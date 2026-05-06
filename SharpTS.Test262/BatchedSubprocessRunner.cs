using System.Collections.Concurrent;
using System.Diagnostics;

namespace SharpTS.Test262;

/// <summary>
/// Drives a pool of persistent <c>SharpTS.Test262.Worker</c> subprocesses, each
/// fed test paths from a shared work queue and emitting JSON-line results back
/// over stdout. The parent aggregates results into a single dictionary keyed
/// by absolute path.
/// </summary>
/// <remarks>
/// <para>
/// Persistent workers (vs. per-batch kill+respawn from issue #109's earlier
/// design) keep JIT and ALC state warm across the whole regen — `dotnet`
/// process startup is paid <see cref="WorkerCount"/> times total instead of
/// once per batch. The previous batched orchestrator hit a ~1.3× speedup
/// ceiling at N=4 because per-batch subprocess spawn + JIT warmup ate most
/// of the parallel gains; persistent workers break that ceiling.
/// </para>
/// <para>
/// Crash recovery: each worker tracks the paths it has been given but hasn't
/// emitted a result for ("in-flight"). On worker death (reader sees EOF) those
/// paths are pushed back onto the shared queue so a surviving worker picks
/// them up. If the worker dies mid-test (one path in-flight), that single path
/// is bucketed as <c>RuntimeError:worker-crashed</c> instead of being requeued
/// — otherwise a pathological test could ping-pong between workers forever.
/// </para>
/// <para>
/// Idle watchdog: a parent thread monitors each worker's last-result timestamp.
/// If a worker hasn't emitted a result in <see cref="IdleResultBudget"/>, the
/// parent presumes the in-worker per-test timeout failed (e.g., pure-CPU loop
/// that never reaches a cancellation check), kills the worker, and lets the
/// remaining pool drain the queue.
/// </para>
/// </remarks>
public sealed class BatchedSubprocessRunner
{
    /// <summary>
    /// Number of worker subprocesses to run concurrently. Each worker pulls
    /// from a shared queue; with N persistent workers, total compile + JIT
    /// throughput scales close to linearly with N until disk/JIT contention
    /// flattens the curve. Capped to keep memory under control: each worker
    /// is ~100 MB resident, so N=4 ≈ 400 MB peak. Env var
    /// <c>SHARPTS_TEST262_WORKERS</c> overrides; <c>=1</c> reproduces the old
    /// single-worker semantics for bisecting flakes. Default is half the
    /// logical CPUs (clamped to [1, 8]).
    /// </summary>
    public int WorkerCount { get; init; } = ResolveDefaultWorkerCount();

    /// <summary>
    /// Maximum gap between result lines from a single worker before the parent
    /// declares it stalled and kills it. The worker has its own per-test 5-s
    /// timeout via <c>$Runtime._cancelRequested</c>; this is the outer fallback
    /// for tests where in-worker cancellation never fires (pure-CPU loops, GC
    /// stalls, deadlocks). Generous enough to absorb a slow JIT warm-up after
    /// startup but tight enough that a wedged worker doesn't hold up the regen.
    /// </summary>
    public TimeSpan IdleResultBudget { get; init; } = TimeSpan.FromSeconds(60);

    private static int ResolveDefaultWorkerCount()
    {
        var env = Environment.GetEnvironmentVariable("SHARPTS_TEST262_WORKERS");
        if (!string.IsNullOrEmpty(env) && int.TryParse(env, out var n) && n > 0)
            return Math.Clamp(n, 1, 16);
        return Math.Clamp(Environment.ProcessorCount / 2, 1, 8);
    }

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
    /// Runs every test in <paramref name="absolutePaths"/> through the worker
    /// pool. Returns a dictionary keyed by absolute path to encoded bucket.
    /// Tests for which no worker emits a result (worker died, watchdog killed,
    /// pool drained early) are bucketed as <c>RuntimeError:worker-crashed</c>.
    /// </summary>
    public IReadOnlyDictionary<string, string> RunAll(
        IReadOnlyList<string> absolutePaths,
        Action<int, int>? progress = null)
    {
        int total = absolutePaths.Count;
        if (total == 0) return new Dictionary<string, string>(StringComparer.Ordinal);

        var workQueue = new ConcurrentQueue<string>(absolutePaths);

        var results = new Dictionary<string, string>(StringComparer.Ordinal);
        var resultsLock = new object();
        int completed = 0;

        void OnResult(string path, string bucket)
        {
            lock (resultsLock)
            {
                if (results.ContainsKey(path)) return; // late-arriving duplicate
                results[path] = bucket;
                completed++;
                progress?.Invoke(completed, total);
            }
        }

        int workerCount = Math.Min(WorkerCount, total);
        var workers = new PersistentWorker[workerCount];
        try
        {
            for (int i = 0; i < workerCount; i++)
            {
                workers[i] = new PersistentWorker(BuildWorkerArgs(), workQueue, OnResult, IdleResultBudget, i);
                workers[i].Start();
            }
            foreach (var w in workers) w.WaitForCompletion();
        }
        finally
        {
            foreach (var w in workers) w?.Dispose();
        }

        // Final pass: bucket-fill anything no worker emitted (queue drained
        // because all workers crashed before reaching it, or watchdogs killed
        // the last live worker mid-stride).
        lock (resultsLock)
        {
            foreach (var path in absolutePaths)
            {
                if (!results.ContainsKey(path))
                    results[path] = "RuntimeError:worker-crashed";
            }
        }

        return results;
    }

    private string[] BuildWorkerArgs()
    {
        var args = new List<string>
        {
            WorkerExecutable,
            "--test262-root", Test262Root,
            "--mode", Mode,
            "--timeout-seconds", ((int)TestTimeout.TotalSeconds).ToString(System.Globalization.CultureInfo.InvariantCulture),
        };
        if (!string.IsNullOrEmpty(SkipFeaturesFile))
        {
            args.Add("--skip-features-file");
            args.Add(SkipFeaturesFile);
        }
        return args.ToArray();
    }

    /// <summary>
    /// One persistent worker subprocess plus its writer/reader threads. Owns
    /// the lifetime of the underlying <see cref="Process"/> and tears it down
    /// on dispose. The writer pulls paths from the shared queue; the reader
    /// emits results back via the supplied callback.
    /// </summary>
    private sealed class PersistentWorker : IDisposable
    {
        private readonly Process _proc;
        private readonly ConcurrentQueue<string> _queue;
        private readonly Action<string, string> _onResult;
        private readonly TimeSpan _idleBudget;
        private readonly int _id;

        // In-flight queue: paths sent to this worker for which we haven't yet
        // seen a result. Used to (a) detect orphans on worker death, (b) push
        // them back to the shared queue (or bucket them as crashed if a single
        // path keeps wedging successive workers).
        private readonly Queue<string> _inFlight = new();
        private readonly object _inFlightLock = new();

        // Updated by the reader on each line. Watchdog reads it. volatile so
        // the watchdog thread sees the latest value without a fence.
        private long _lastResultTicks = DateTime.UtcNow.Ticks;

        private Task? _writerTask;
        private Task? _readerTask;
        private Task? _watchdogTask;
        private readonly CancellationTokenSource _shutdownCts = new();
        private bool _killedByWatchdog;

        public PersistentWorker(string[] args, ConcurrentQueue<string> queue,
            Action<string, string> onResult, TimeSpan idleBudget, int id)
        {
            _queue = queue;
            _onResult = onResult;
            _idleBudget = idleBudget;
            _id = id;

            var psi = new ProcessStartInfo("dotnet")
            {
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            foreach (var arg in args) psi.ArgumentList.Add(arg);
            _proc = Process.Start(psi)
                ?? throw new InvalidOperationException("failed to start Test262 worker");
        }

        public void Start()
        {
            _writerTask = Task.Run(WriteLoop);
            _readerTask = Task.Run(ReadLoop);
            _watchdogTask = Task.Run(WatchdogLoop);
        }

        public void WaitForCompletion()
        {
            try { _writerTask?.Wait(); } catch { }
            try { _readerTask?.Wait(); } catch { }
            _shutdownCts.Cancel();
            try { _watchdogTask?.Wait(TimeSpan.FromSeconds(2)); } catch { }
            try { _proc.WaitForExit(2000); } catch { }
        }

        public void Dispose()
        {
            try { _shutdownCts.Cancel(); } catch { }
            try
            {
                if (!_proc.HasExited) _proc.Kill(entireProcessTree: true);
            }
            catch { }
            _proc.Dispose();
            _shutdownCts.Dispose();
        }

        // Writer thread: pulls paths from the shared queue and writes each to
        // the worker's stdin. When the queue empties, closes stdin so the
        // worker's `while (Console.In.ReadLine() != null)` loop exits cleanly,
        // which lets the reader observe EOF and finish.
        private void WriteLoop()
        {
            try
            {
                while (_queue.TryDequeue(out var path))
                {
                    lock (_inFlightLock) _inFlight.Enqueue(path);
                    _proc.StandardInput.WriteLine(path);
                }
                _proc.StandardInput.Close();
            }
            catch
            {
                // Worker died (broken pipe). Reader will detect EOF and the
                // crash-handling path on completion will requeue inFlight.
            }
        }

        // Reader thread: parses JSON-line results, emits via callback, dequeues
        // in-flight. Runs until EOF (worker exited normally or crashed).
        private void ReadLoop()
        {
            try
            {
                string? line;
                while ((line = _proc.StandardOutput.ReadLine()) != null)
                {
                    Interlocked.Exchange(ref _lastResultTicks, DateTime.UtcNow.Ticks);
                    if (line.StartsWith("{\"_done\":"))
                        continue; // legacy sentinel emitted at worker exit; ignore
                    var parsed = ParseResultLine(line);
                    if (!parsed.HasValue) continue;
                    var (path, bucket) = parsed.Value;
                    _onResult(path, bucket);
                    lock (_inFlightLock)
                    {
                        if (_inFlight.Count > 0) _inFlight.Dequeue();
                    }
                }
            }
            catch
            {
                // Pipe broke / killed. Crash handling below.
            }

            // Worker has exited (or its stdout closed). Recover any unfinished
            // paths.
            HandleCompletion();
        }

        // Watchdog thread: kills the worker if no result has arrived within
        // _idleBudget. The kill triggers reader EOF, which routes through
        // HandleCompletion to recover in-flight paths.
        private void WatchdogLoop()
        {
            var token = _shutdownCts.Token;
            while (!token.IsCancellationRequested)
            {
                try { Task.Delay(1000, token).Wait(token); }
                catch { return; }
                if (_proc.HasExited) return;
                var lastTicks = Interlocked.Read(ref _lastResultTicks);
                var idle = DateTime.UtcNow.Ticks - lastTicks;
                if (idle > _idleBudget.Ticks)
                {
                    _killedByWatchdog = true;
                    try { _proc.Kill(entireProcessTree: true); } catch { }
                    return;
                }
            }
        }

        // Recovery on worker exit / crash. If the worker exited with a single
        // path in-flight, that path was almost certainly the cause — bucket it
        // as worker-crashed (or worker-stalled if the watchdog fired) so a
        // pathological test doesn't ping-pong forever between workers. Any
        // additional in-flight paths the worker received but hadn't started
        // yet go back to the shared queue for a surviving worker.
        private void HandleCompletion()
        {
            List<string> inFlight;
            lock (_inFlightLock)
            {
                inFlight = new List<string>(_inFlight);
                _inFlight.Clear();
            }
            if (inFlight.Count == 0) return;

            // Pessimistic: assume the head was the test that killed us.
            // Requeue the rest.
            var head = inFlight[0];
            var bucket = _killedByWatchdog
                ? "RuntimeError:worker-stalled"
                : "RuntimeError:worker-crashed";
            _onResult(head, bucket);
            for (int i = 1; i < inFlight.Count; i++)
                _queue.Enqueue(inFlight[i]);
        }
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
