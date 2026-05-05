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

// Configure stdout for line-buffered output so the parent observes results
// in real time and can detect a hung worker by stalled output.
Console.Out.Flush();

int count = 0;
string? path;
while ((path = Console.In.ReadLine()) != null)
{
    path = path.Trim();
    if (path.Length == 0) continue;

    var result = runner.RunOne(path, mode);
    var bucket = result.SkipReason is null
        ? result.Outcome.ToString()
        : $"{result.Outcome}:{result.SkipReason}";

    Console.Out.WriteLine(JsonLine(path, bucket));
    Console.Out.Flush();
    count++;
}

Console.Out.WriteLine($"{{\"_done\":true,\"count\":{count}}}");
Console.Out.Flush();
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
