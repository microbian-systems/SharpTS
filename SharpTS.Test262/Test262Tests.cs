using System.Text;
using Xunit;
using Xunit.Abstractions;

namespace SharpTS.Test262;

/// <summary>
/// Serialize the baseline facts — compiled mode redirects Console.Out under a
/// process-wide lock, and running two big runs concurrently would fight over
/// it and risk cross-contamination.
/// </summary>
[CollectionDefinition("Test262Baseline", DisableParallelization = true)]
public class Test262BaselineCollection { }

/// <summary>
/// Milestone 2: subset coverage with a committed baseline.
///
/// Flow:
///   1. Enumerate every <c>.js</c> under the subset folders.
///   2. Run each file through <see cref="Test262Runner"/>.
///   3. Compare outcomes to <c>baselines/{interpreted|compiled}.txt</c>.
///   4. Fail the fact if any regression (good → bad) or new pass (bad → good)
///      is detected; soft-report bucket changes.
///
/// Env-var switches:
///   <c>SHARPTS_TEST262_UPDATE_BASELINE=1</c> — write baselines instead of diffing.
///   <c>SHARPTS_TEST262_WIDE_SWEEP=1</c>      — use <c>wide-sweep.json</c>;
///                                              write <c>wide-sweep-report.md</c> instead of diffing.
/// </summary>
[Collection("Test262Baseline")]
public class Test262Tests
{
    private readonly ITestOutputHelper _output;

    public Test262Tests(ITestOutputHelper output) => _output = output;

    [Fact]
    public void InterpretedBaseline() => RunBaseline(Test262ExecutionMode.Interpreted);

    [Fact]
    public void CompiledBaseline() => RunBaseline(Test262ExecutionMode.Compiled);

    private void RunBaseline(Test262ExecutionMode mode)
    {
        var test262Root = Test262Paths.TryFindRoot();
        var projectDir = Test262Paths.TryFindProjectDir();
        if (test262Root is null || projectDir is null)
        {
            _output.WriteLine("external/test262 or SharpTS.Test262/ not found — run `git submodule update --init external/test262`");
            return;
        }

        var wideSweep = GetBool("SHARPTS_TEST262_WIDE_SWEEP");
        var updateBaseline = GetBool("SHARPTS_TEST262_UPDATE_BASELINE");

        var configDir = Path.Combine(projectDir, "config");
        var configFile = Path.Combine(configDir, wideSweep ? "wide-sweep.json" : "subset.json");
        var config = Test262Config.Load(configFile);
        var skipFeatures = config.LoadSkipFeatures(configDir);

        var modeFolders = config.GetFoldersForMode(mode);
        var files = EnumerateTestFiles(test262Root, modeFolders);
        _output.WriteLine($"[{mode}] enumerated {files.Count} test files from {modeFolders.Count} folders");

        var runner = new Test262Runner(test262Root, config.Timeout, skipFeatures);
        var current = new SortedDictionary<string, string>(StringComparer.Ordinal);
        var counts = new Dictionary<Test262Outcome, int>();

        var started = DateTime.UtcNow;
        foreach (var (relPath, absPath) in files)
        {
            var result = runner.RunOne(absPath, mode);
            current[relPath] = Test262Baseline.EncodeBucket(result);
            counts.TryGetValue(result.Outcome, out var c);
            counts[result.Outcome] = c + 1;
        }
        var elapsed = DateTime.UtcNow - started;

        var summary = FormatSummary(counts, files.Count, elapsed);
        _output.WriteLine(summary);

        if (wideSweep)
        {
            var reportPath = Path.Combine(projectDir, "wide-sweep-report.md");
            WriteWideSweepReport(reportPath, mode, config, counts, current, elapsed);
            _output.WriteLine($"[{mode}] wrote {reportPath}");
            return;
        }

        var baselinePath = Path.Combine(projectDir, "baselines",
            mode == Test262ExecutionMode.Interpreted ? "interpreted.txt" : "compiled.txt");

        if (updateBaseline || !File.Exists(baselinePath))
        {
            Test262Baseline.Write(baselinePath, current.Select(kv => (kv.Key, kv.Value)));
            _output.WriteLine($"[{mode}] wrote baseline → {baselinePath}");
            return;
        }

        var baseline = Test262Baseline.Read(baselinePath);
        var diff = Test262BaselineDiffer.Diff(baseline, current);
        LogDiff(mode, diff);

        if (diff.HasHardFailures || diff.NewEntries.Count > 0 || diff.RemovedEntries.Count > 0)
        {
            Assert.Fail(
                $"[{mode}] baseline drift: " +
                $"{diff.NewRegressions.Count} regressions, {diff.NewPasses.Count} new passes, " +
                $"{diff.NewEntries.Count} new entries, {diff.RemovedEntries.Count} removed entries. " +
                $"Re-run with SHARPTS_TEST262_UPDATE_BASELINE=1 to update.");
        }
    }

    private static bool GetBool(string name)
    {
        var v = Environment.GetEnvironmentVariable(name);
        return v is "1" or "true" or "TRUE";
    }

    /// <summary>
    /// Walks each configured folder for <c>*.js</c> files, excluding the
    /// Test262 convention for non-test fixtures (suffix <c>_FIXTURE.js</c>).
    /// </summary>
    private static List<(string RelPath, string AbsPath)> EnumerateTestFiles(
        string test262Root, IReadOnlyList<string> folders)
    {
        var results = new List<(string, string)>();
        foreach (var folder in folders)
        {
            var absFolder = Path.Combine(test262Root, folder.Replace('/', Path.DirectorySeparatorChar));
            if (!Directory.Exists(absFolder)) continue;
            foreach (var file in Directory.EnumerateFiles(absFolder, "*.js", SearchOption.AllDirectories))
            {
                if (file.EndsWith("_FIXTURE.js", StringComparison.Ordinal)) continue;
                var rel = Path.GetRelativePath(test262Root, file).Replace('\\', '/');
                results.Add((rel, file));
            }
        }
        // Stable enumeration order so baselines don't flap between runs.
        results.Sort((a, b) => StringComparer.Ordinal.Compare(a.Item1, b.Item1));
        return results;
    }

    private static string FormatSummary(Dictionary<Test262Outcome, int> counts, int total, TimeSpan elapsed)
    {
        var sb = new StringBuilder();
        sb.Append($"summary: {total} tests in {elapsed.TotalSeconds:F1}s → ");
        var parts = counts.OrderBy(kv => kv.Key)
            .Select(kv => $"{kv.Key}={kv.Value}");
        sb.Append(string.Join(", ", parts));
        return sb.ToString();
    }

    private void LogDiff(Test262ExecutionMode mode, BaselineDiff diff)
    {
        void Dump(string label, IReadOnlyList<BaselineChange> list, int maxShown = 20)
        {
            if (list.Count == 0) return;
            _output.WriteLine($"[{mode}] {label}: {list.Count}");
            foreach (var c in list.Take(maxShown))
                _output.WriteLine($"  {c.RelPath}: {c.OldBucket ?? "-"} → {c.NewBucket}");
            if (list.Count > maxShown)
                _output.WriteLine($"  ... and {list.Count - maxShown} more");
        }
        Dump("regressions", diff.NewRegressions);
        Dump("new passes", diff.NewPasses);
        Dump("new entries", diff.NewEntries);
        Dump("removed entries", diff.RemovedEntries);
        Dump("bucket changes (soft)", diff.BucketChanges);
    }

    private static void WriteWideSweepReport(
        string path,
        Test262ExecutionMode mode,
        Test262Config config,
        Dictionary<Test262Outcome, int> counts,
        IReadOnlyDictionary<string, string> current,
        TimeSpan elapsed)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# Test262 wide-sweep report — {mode}");
        sb.AppendLine();
        sb.AppendLine($"- generated: {DateTime.UtcNow:u}");
        sb.AppendLine($"- folders: {string.Join(", ", config.Folders)}");
        sb.AppendLine($"- timeout: {config.TimeoutSeconds}s");
        sb.AppendLine($"- elapsed: {elapsed.TotalSeconds:F1}s");
        sb.AppendLine($"- total: {current.Count}");
        sb.AppendLine();
        sb.AppendLine("## Outcomes");
        sb.AppendLine();
        sb.AppendLine("| Bucket | Count |");
        sb.AppendLine("|--------|------:|");
        foreach (var kv in counts.OrderBy(kv => kv.Key))
            sb.AppendLine($"| {kv.Key} | {kv.Value} |");
        File.AppendAllText(path, sb.ToString());
    }
}
