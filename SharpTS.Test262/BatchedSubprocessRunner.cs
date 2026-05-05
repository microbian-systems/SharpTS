using System.Diagnostics;

namespace SharpTS.Test262;

/// <summary>
/// Drives a sequence of batched subprocess invocations of
/// SharpTS.Test262.Worker, aggregating results and recovering from worker
/// crashes (which is the whole point — pathological tests in a batch can
/// OOM the worker process; the parent simply spawns the next batch).
/// See issue #109.
/// </summary>
public sealed class BatchedSubprocessRunner
{
    /// <summary>
    /// Number of tests per worker subprocess. Smaller batches = more startup
    /// overhead but tighter memory ceiling AND smaller blast radius if a
    /// pathological test crashes the worker (the rest of its batch is bucketed
    /// as <c>RuntimeError:worker-crashed</c>). 25 keeps crash collateral to a
    /// few dozen tests at a time; bigger sizes amortize startup but a single
    /// runaway can lose 50+ results. Process-spawn at 25 is ~520 batches ×
    /// ~500ms ≈ 4 min of overhead — acceptable for a long regen.
    ///
    /// Sized down from 50 to 25 in #79 because async tests with stuck event
    /// loops accumulate orphan tasks inside a worker, dragging the *rest* of
    /// the batch's tests through degraded scheduling. Faster worker turnover
    /// lets the OS reclaim those orphans more often.
    /// </summary>
    public int BatchSize { get; init; } = 25;

    /// <summary>
    /// Maximum wall-clock time a single batch is allowed to run before the
    /// parent gives up on the worker and bucket-fills the unfinished tests
    /// as <c>RuntimeError:worker-stalled</c>. Without this, an async test
    /// whose orphan event-loop thread keeps the worker's stdout pipe open
    /// would hang the parent's <see cref="StreamReader.ReadLine"/> indefinitely.
    /// </summary>
    public TimeSpan BatchWallClockBudget { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Maximum gap between worker stdout lines before the parent considers
    /// the worker stalled. Per-test timeout is enforced inside the worker;
    /// this is the outer fallback for the case where the worker itself has
    /// wedged (deadlocked on a runtime mutex, GC-thrashing, etc.) and isn't
    /// emitting results.
    /// </summary>
    public TimeSpan IdleStdoutBudget { get; init; } = TimeSpan.FromSeconds(60);

    public string Test262Root { get; }
    public string Mode { get; }
    public TimeSpan TestTimeout { get; }
    public string? SkipFeaturesFile { get; }
    public string WorkerExecutable { get; }

    public BatchedSubprocessRunner(
        string test262Root,
        Test262ExecutionMode mode,
        TimeSpan testTimeout,
        string? skipFeaturesFile,
        string workerExecutable)
    {
        Test262Root = test262Root;
        Mode = mode.ToString();
        TestTimeout = testTimeout;
        SkipFeaturesFile = skipFeaturesFile;
        WorkerExecutable = workerExecutable;
    }

    /// <summary>
    /// Runs every test in <paramref name="absolutePaths"/> through one or more
    /// worker subprocesses. Returns a dictionary keyed by absolute path to
    /// encoded bucket. Tests that the worker fails to emit (because the
    /// subprocess crashed mid-batch) are bucketed as
    /// <c>RuntimeError:worker-crashed</c>.
    /// </summary>
    public IReadOnlyDictionary<string, string> RunAll(
        IReadOnlyList<string> absolutePaths,
        Action<int, int>? progress = null)
    {
        var results = new Dictionary<string, string>(StringComparer.Ordinal);
        int total = absolutePaths.Count;
        int completed = 0;

        for (int batchStart = 0; batchStart < total; batchStart += BatchSize)
        {
            int batchEnd = Math.Min(batchStart + BatchSize, total);
            var batch = new List<string>(batchEnd - batchStart);
            for (int i = batchStart; i < batchEnd; i++) batch.Add(absolutePaths[i]);

            var batchResults = RunBatch(batch);
            foreach (var (path, bucket) in batchResults)
                results[path] = bucket;

            // Anything in the batch the worker didn't emit → crashed mid-run.
            foreach (var path in batch)
            {
                if (!results.ContainsKey(path))
                    results[path] = "RuntimeError:worker-crashed";
            }

            completed = batchEnd;
            progress?.Invoke(completed, total);
        }

        return results;
    }

    /// <summary>
    /// Spawns one worker subprocess and feeds it a batch via stdin. Reads
    /// JSON-lines results from stdout. Returns whatever was emitted; the
    /// caller bucket-fills the rest as crashes.
    /// </summary>
    private IReadOnlyList<(string Path, string Bucket)> RunBatch(IReadOnlyList<string> paths)
    {
        var argList = new List<string>
        {
            WorkerExecutable,
            "--test262-root", Test262Root,
            "--mode", Mode,
            "--timeout-seconds", ((int)TestTimeout.TotalSeconds).ToString(System.Globalization.CultureInfo.InvariantCulture),
        };
        if (!string.IsNullOrEmpty(SkipFeaturesFile))
        {
            argList.Add("--skip-features-file");
            argList.Add(SkipFeaturesFile);
        }

        var psi = new ProcessStartInfo("dotnet")
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var arg in argList) psi.ArgumentList.Add(arg);

        using var proc = Process.Start(psi)
            ?? throw new InvalidOperationException("failed to start Test262 worker");

        // Feed stdin on a background thread; otherwise a tiny worker buffer
        // can deadlock against the parent reading stdout. Stdout reading
        // happens on the main thread.
        var stdinTask = Task.Run(() =>
        {
            try
            {
                foreach (var path in paths)
                    proc.StandardInput.WriteLine(path);
                proc.StandardInput.Close();
            }
            catch
            {
                // Worker may have crashed; ignore — we'll see EOF on stdout.
            }
        });

        // Read stdout on a background thread so the main thread can enforce
        // wall-clock and idle-stdout budgets. ReadLine() is blocking with no
        // timeout argument, so the only way to bound it is to abandon the
        // reader and kill the process.
        var results = new List<(string, string)>();
        var resultsLock = new object();
        var lastLineAt = DateTime.UtcNow;
        var doneSeen = false;
        var readerTask = Task.Run(() =>
        {
            try
            {
                string? line;
                while ((line = proc.StandardOutput.ReadLine()) != null)
                {
                    lastLineAt = DateTime.UtcNow;
                    if (line.StartsWith("{\"_done\":"))
                    {
                        doneSeen = true;
                        break;
                    }
                    var parsed = ParseResultLine(line);
                    if (parsed.HasValue)
                    {
                        lock (resultsLock) results.Add(parsed.Value);
                    }
                }
            }
            catch
            {
                // Stream closed or worker killed mid-read — RunAll will
                // bucket-fill missing tests as worker-crashed.
            }
        });

        var batchDeadline = DateTime.UtcNow + BatchWallClockBudget;
        bool stalled = false;
        while (!readerTask.IsCompleted)
        {
            if (readerTask.Wait(500)) break;
            var now = DateTime.UtcNow;
            if (now > batchDeadline)
            {
                stalled = true;
                break;
            }
            if (now - lastLineAt > IdleStdoutBudget)
            {
                stalled = true;
                break;
            }
        }

        if (stalled || !doneSeen)
        {
            // Force-kill so the next batch starts clean. The reader's blocked
            // ReadLine() unblocks once the pipe closes.
            try { proc.Kill(entireProcessTree: true); } catch { }
        }
        try { proc.WaitForExit(2000); } catch { }
        if (!proc.HasExited)
        {
            try { proc.Kill(entireProcessTree: true); } catch { }
        }
        readerTask.Wait(TimeSpan.FromSeconds(2));
        stdinTask.Wait(TimeSpan.FromSeconds(1));

        lock (resultsLock) return results.ToList();
    }

    /// <summary>
    /// Parses one JSON-line result. Hand-rolled rather than pulling in
    /// System.Text.Json: protocol is fixed-shape, hot-path on every test,
    /// and avoiding the dep keeps the parent's link surface tiny.
    /// </summary>
    private static (string Path, string Bucket)? ParseResultLine(string line)
    {
        // Expected: {"path":"<escaped>","bucket":"<escaped>"}
        const string pathKey = "\"path\":\"";
        const string bucketKey = "\",\"bucket\":\"";
        int pi = line.IndexOf(pathKey, StringComparison.Ordinal);
        if (pi < 0) return null;
        pi += pathKey.Length;
        int bi = line.IndexOf(bucketKey, pi, StringComparison.Ordinal);
        if (bi < 0) return null;
        var path = JsonUnescape(line[pi..bi]);
        bi += bucketKey.Length;
        int end = line.LastIndexOf("\"}", StringComparison.Ordinal);
        if (end < bi) return null;
        var bucket = JsonUnescape(line[bi..end]);
        return (path, bucket);
    }

    private static string JsonUnescape(string s)
    {
        if (!s.Contains('\\')) return s;
        var sb = new System.Text.StringBuilder(s.Length);
        for (int i = 0; i < s.Length; i++)
        {
            var c = s[i];
            if (c != '\\' || i + 1 >= s.Length) { sb.Append(c); continue; }
            i++;
            switch (s[i])
            {
                case '"': sb.Append('"'); break;
                case '\\': sb.Append('\\'); break;
                case 'n': sb.Append('\n'); break;
                case 'r': sb.Append('\r'); break;
                case 't': sb.Append('\t'); break;
                case 'u':
                    if (i + 4 < s.Length)
                    {
                        var hex = s[(i + 1)..(i + 5)];
                        if (ushort.TryParse(hex, System.Globalization.NumberStyles.HexNumber, null, out var code))
                            sb.Append((char)code);
                        i += 4;
                    }
                    break;
                default: sb.Append(s[i]); break;
            }
        }
        return sb.ToString();
    }
}
