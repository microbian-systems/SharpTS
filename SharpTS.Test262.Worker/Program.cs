using System.Text;
using SharpTS.Test262;

// Subprocess worker for Test262 batch execution.
//
// Protocol:
//   Args:    --test262-root <path>
//            --mode <Compiled|Interpreted>
//            [--timeout-seconds <N>]
//            [--skip-features-file <path>]
//   Stdin:   one absolute test path per line, EOF ends the batch
//   Stdout:  one result per line: {"path":"<input>","bucket":"<encoded>"}
//            terminated by {"_done":true,"count":<N>} sentinel
//   Stderr:  unused (errors propagate to RuntimeError bucket; subprocess
//            crashes are detected by the parent via missing sentinel)
//
// Designed so a pathological test that allocates gigabytes (or otherwise
// kills the process) only loses its enclosing batch — the parent can
// recover by spawning a fresh worker for the next batch. See issue #109.

string? test262Root = null;
var mode = Test262ExecutionMode.Compiled;
var timeoutSeconds = 5;
string? skipFeaturesFile = null;

for (int i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--test262-root":
            test262Root = args[++i];
            break;
        case "--mode":
            mode = Enum.Parse<Test262ExecutionMode>(args[++i]);
            break;
        case "--timeout-seconds":
            timeoutSeconds = int.Parse(args[++i]);
            break;
        case "--skip-features-file":
            skipFeaturesFile = args[++i];
            break;
        default:
            Console.Error.WriteLine($"unknown arg: {args[i]}");
            return 64;
    }
}

if (string.IsNullOrEmpty(test262Root))
{
    Console.Error.WriteLine("--test262-root required");
    return 64;
}

var skipFeatures = LoadSkipFeatures(skipFeaturesFile);
var runner = new Test262Runner(test262Root, TimeSpan.FromSeconds(timeoutSeconds), skipFeatures);

// CRITICAL: capture the original stdout NOW, before any test runs. The runner's
// compiled-mode test execution does Console.SetOut(stringWriter) to capture
// per-test JS console output, then Console.SetOut(original) to restore. On
// Timeout tests the cancel-and-wait grace period ends after 1s but the
// orphan Task that holds the redirected Out can keep running — meaning the
// worker's `Console.Out.WriteLine(JsonLine(...))` for the result of the
// timed-out test would land in the orphan's StringWriter, not the parent's
// pipe. Subsequent JSON-line results would also leak until the orphan
// finally calls SetOut(originalOut) — losing entire batches of results
// silently and desyncing the parent's inFlight queue.
//
// Capturing the original TextWriter once and writing all worker output via
// that reference makes the JSON-line emit independent of process-wide
// Console.Out state.
var workerStdout = Console.Out;

// Optional file-based diagnostic trace for parallel-regen flake hunting.
// Set SHARPTS_TEST262_WORKER_TRACE=<path> from the parent (orchestrator does
// this when SHARPTS_TEST262_TRACE is enabled) to log every test the worker
// processes plus periodic memory snapshots.
StreamWriter? trace = null;
{
    var p = Environment.GetEnvironmentVariable("SHARPTS_TEST262_WORKER_TRACE");
    if (!string.IsNullOrEmpty(p))
        trace = new StreamWriter(p, append: true) { AutoFlush = true };
}
void TraceLog(string format, params object[] args)
{
    trace?.WriteLine($"{DateTime.UtcNow:HH:mm:ss.fff} {string.Format(format, args)}");
}
TraceLog("startup pid={0} mode={1} timeout={2}s", Environment.ProcessId, mode, timeoutSeconds);

workerStdout.Flush();

int count = 0;
string? path;
while ((path = Console.In.ReadLine()) != null)
{
    path = path.Trim();
    if (path.Length == 0) continue;

    var swTest = System.Diagnostics.Stopwatch.StartNew();
    var result = runner.RunOne(path, mode);
    swTest.Stop();
    var bucket = result.SkipReason is null
        ? result.Outcome.ToString()
        : $"{result.Outcome}:{result.SkipReason}";

    workerStdout.WriteLine(JsonLine(path, bucket));
    workerStdout.Flush();
    count++;
    if (trace is not null)
    {
        TraceLog("test #{0} {1}ms {2} :: {3}", count, swTest.ElapsedMilliseconds, bucket, Path.GetFileName(path));
        if (count % 100 == 0)
        {
            using var proc = System.Diagnostics.Process.GetCurrentProcess();
            TraceLog("MEM at #{0}: working={1}MB private={2}MB",
                count,
                proc.WorkingSet64 / 1_048_576,
                proc.PrivateMemorySize64 / 1_048_576);
        }
    }
}

TraceLog("EOF after {0} tests; emitting _done", count);
workerStdout.WriteLine($"{{\"_done\":true,\"count\":{count}}}");
workerStdout.Flush();
trace?.Dispose();
return 0;

static HashSet<string> LoadSkipFeatures(string? path)
{
    var set = new HashSet<string>(StringComparer.Ordinal);
    if (string.IsNullOrEmpty(path) || !File.Exists(path)) return set;
    foreach (var raw in File.ReadAllLines(path))
    {
        var line = raw.Trim();
        if (line.Length == 0 || line.StartsWith('#')) continue;
        set.Add(line);
    }
    return set;
}

static string JsonLine(string path, string bucket)
{
    var sb = new StringBuilder(path.Length + bucket.Length + 32);
    sb.Append("{\"path\":\"");
    JsonEscape(sb, path);
    sb.Append("\",\"bucket\":\"");
    JsonEscape(sb, bucket);
    sb.Append("\"}");
    return sb.ToString();
}

static void JsonEscape(StringBuilder sb, string s)
{
    foreach (var c in s)
    {
        switch (c)
        {
            case '"': sb.Append("\\\""); break;
            case '\\': sb.Append("\\\\"); break;
            case '\n': sb.Append("\\n"); break;
            case '\r': sb.Append("\\r"); break;
            case '\t': sb.Append("\\t"); break;
            default:
                if (c < 0x20)
                    sb.Append($"\\u{(int)c:X4}");
                else
                    sb.Append(c);
                break;
        }
    }
}
