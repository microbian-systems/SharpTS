using Xunit;
using Xunit.Abstractions;

namespace SharpTS.Test262;

/// <summary>
/// Milestone 1 acceptance: one Test262 file runs end-to-end in both execution
/// modes with correct outcome classification. We do not assert <c>Pass</c> —
/// SharpTS is not 100% spec-compliant and the point of this suite is to
/// surface exactly where it isn't. The bar is that plumbing works:
/// neither mode lands in <see cref="Test262Outcome.HarnessError"/>.
/// </summary>
public class SmokeTest
{
    private readonly ITestOutputHelper _output;

    public SmokeTest(ITestOutputHelper output) => _output = output;

    /// <summary>
    /// Issue #79 smoke: one async-flagged test must execute end-to-end in
    /// both modes, hitting <c>$DONE</c> and bucketing as <c>Pass</c>. The
    /// chosen file (<c>Promise/resolve/arg-non-thenable</c>) follows the
    /// canonical <c>.then($DONE, $DONE)</c> shape and exercises both the
    /// host-callable interpreter path and the JS-shim compiled path.
    /// </summary>
    [Theory]
    [InlineData(Test262ExecutionMode.Interpreted)]
    [InlineData(Test262ExecutionMode.Compiled)]
    public void Promise_resolve_argNonThenable_AsyncDone(Test262ExecutionMode mode)
    {
        var root = Test262Paths.TryFindRoot();
        if (root is null)
        {
            _output.WriteLine("external/test262 not initialized");
            return;
        }
        var testPath = Path.Combine(
            Test262Paths.TestDir(root),
            "built-ins", "Promise", "resolve", "arg-non-thenable.js");
        Assert.True(File.Exists(testPath));

        var runner = new Test262Runner(root, TimeSpan.FromSeconds(15));
        var result = runner.RunOne(testPath, mode);
        _output.WriteLine($"{mode} → {result.Outcome}");
        if (result.Message is not null) _output.WriteLine($"  message: {result.Message}");
        if (result.SkipReason is not null) _output.WriteLine($"  skip: {result.SkipReason}");

        Assert.NotEqual(Test262Outcome.Skipped, result.Outcome);
        Assert.NotEqual(Test262Outcome.HarnessError, result.Outcome);
    }

    /// <summary>
    /// Diagnostic only — runs a fixed ~500-test subset through
    /// <see cref="BatchedSubprocessRunner"/> with worker counts 1, 2, 4, 8 and
    /// reports wall-clock per run. Lets us see whether perf scales linearly
    /// with worker count, plateaus, or degrades — the prior 4-worker compile
    /// regen only got 1.32× over serial, suggesting either disk/JIT
    /// contention, straggler batches, or orchestrator overhead.
    /// </summary>
    [Fact]
    public void Diagnostic_ParallelScaling()
    {
        var root = Test262Paths.TryFindRoot();
        if (root is null) return;
        var workerDll = Test262Paths.TryFindWorkerDll();
        if (workerDll is null)
        {
            _output.WriteLine("worker DLL not found — build SharpTS.Test262.Worker first");
            return;
        }

        // Math folder: ~1949 tests, mostly fast (no async timeouts), good signal
        // for compile + JIT + load throughput. Take 500 to keep total runtime
        // manageable across four passes.
        var testDir = Path.Combine(Test262Paths.TestDir(root), "built-ins", "Math");
        var paths = Directory.EnumerateFiles(testDir, "*.js", SearchOption.AllDirectories)
            .Where(f => !f.EndsWith("_FIXTURE.js"))
            .OrderBy(f => f, StringComparer.Ordinal)
            .Take(500)
            .ToList();
        _output.WriteLine($"profiling subset: {paths.Count} tests");

        // Warm up: spawn one worker to JIT the worker assembly + load the
        // Test262 harness on disk. Without this, run #1 pays cold-start cost
        // that runs #2-4 don't.
        var warmup = new BatchedSubprocessRunner(
            root, Test262ExecutionMode.Compiled, TimeSpan.FromSeconds(5), null, workerDll)
        { WorkerCount = 1 };
        warmup.RunAll(paths.Take(25).ToList());

        foreach (var n in new[] { 1, 2, 4, 8 })
        {
            var runner = new BatchedSubprocessRunner(
                root, Test262ExecutionMode.Compiled, TimeSpan.FromSeconds(5), null, workerDll)
            { WorkerCount = n };

            var sw = System.Diagnostics.Stopwatch.StartNew();
            var results = runner.RunAll(paths);
            sw.Stop();

            var msPerTest = sw.Elapsed.TotalMilliseconds / paths.Count;
            _output.WriteLine($"N={n}: {sw.Elapsed.TotalSeconds:F1}s total, {msPerTest:F1} ms/test, {results.Count} results");
        }
    }

    /// <summary>
    /// Diagnostic only — measures per-test latency for compiled vs interpreted
    /// modes on a trivial Math test, instrumenting the major phases (parse,
    /// type-check, IL emit, save, ALC load, invoke). Lets us pinpoint where
    /// the per-test cost concentrates so we know what's worth optimizing.
    /// </summary>
    [Fact]
    public void Diagnostic_PerTestTimingBreakdown()
    {
        var root = Test262Paths.TryFindRoot();
        if (root is null) return;
        var testPath = Path.Combine(Test262Paths.TestDir(root),
            "built-ins", "Math", "abs", "S15.8.2.1_A1.js");
        if (!File.Exists(testPath)) return;

        var skip = new HashSet<string>(StringComparer.Ordinal);
        var runner = new Test262Runner(root, TimeSpan.FromSeconds(5), skip);

        // Warmup so JIT + filesystem caches are hot.
        for (int i = 0; i < 3; i++)
        {
            runner.RunOne(testPath, Test262ExecutionMode.Compiled);
            runner.RunOne(testPath, Test262ExecutionMode.Interpreted);
        }

        const int iterations = 30;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        for (int i = 0; i < iterations; i++)
            runner.RunOne(testPath, Test262ExecutionMode.Compiled);
        sw.Stop();
        var compiledMs = sw.Elapsed.TotalMilliseconds / iterations;
        _output.WriteLine($"Compiled: {compiledMs:F1} ms/test (over {iterations} iterations)");

        sw.Restart();
        for (int i = 0; i < iterations; i++)
            runner.RunOne(testPath, Test262ExecutionMode.Interpreted);
        sw.Stop();
        var interpretedMs = sw.Elapsed.TotalMilliseconds / iterations;
        _output.WriteLine($"Interpreted: {interpretedMs:F1} ms/test (over {iterations} iterations)");

        _output.WriteLine($"Compiled-mode overhead vs interpreted: {compiledMs - interpretedMs:F1} ms ({compiledMs / interpretedMs:F1}x)");
    }

    /// <summary>
    /// Diagnostic only — instruments per-phase compiled-mode time over a fixed
    /// 200-test prefix of <c>built-ins/Math</c>. Math is chosen because it has
    /// very few timeouts and pathological allocations, so the timings reflect
    /// the steady-state compile + load + invoke path rather than outliers.
    /// Sequential single-threaded so phase ratios are clean (no contention).
    /// </summary>
    [Fact]
    public void Diagnostic_CompiledPhaseBreakdown()
    {
        var root = Test262Paths.TryFindRoot();
        if (root is null) { _output.WriteLine("external/test262 not initialized"); return; }

        var mathDir = Path.Combine(Test262Paths.TestDir(root), "built-ins", "Math");
        if (!Directory.Exists(mathDir)) { _output.WriteLine($"missing {mathDir}"); return; }

        var paths = Directory.EnumerateFiles(mathDir, "*.js", SearchOption.AllDirectories)
            .Where(f => !f.EndsWith("_FIXTURE.js"))
            .OrderBy(f => f, StringComparer.Ordinal)
            .Take(200)
            .ToList();
        _output.WriteLine($"profiling {paths.Count} tests from Math/");

        var runner = new Test262Runner(root, TimeSpan.FromSeconds(15));

        // Warm up (JIT, file caches, harness cache) so phase 1 isn't full of
        // cold-start cost.
        for (int i = 0; i < 3; i++)
            runner.RunOne(paths[0], Test262ExecutionMode.Compiled);

        DumpRun("collectible ALC (current)", false);
        DumpRun("non-collectible Assembly.Load (diagnostic)", true);

        void DumpRun(string label, bool nonCollectible)
        {
            Test262Runner.UseNonCollectibleLoad = nonCollectible;
            try
            {
                CompiledPhaseStats.Reset();
                var sw = System.Diagnostics.Stopwatch.StartNew();
                foreach (var p in paths)
                    runner.RunOne(p, Test262ExecutionMode.Compiled);
                sw.Stop();

                long total = CompiledPhaseStats.LexParseTicks
                           + CompiledPhaseStats.TypeCheckTicks
                           + CompiledPhaseStats.DeadCodeTicks
                           + CompiledPhaseStats.ILCompileTicks
                           + CompiledPhaseStats.SaveBytesTicks
                           + CompiledPhaseStats.AlcLoadTicks
                           + CompiledPhaseStats.InvokeTicks
                           + CompiledPhaseStats.UnloadTicks
                           + CompiledPhaseStats.PeriodicGcTicks;
                long count = CompiledPhaseStats.Count;
                if (count == 0) { _output.WriteLine($"--- {label}: no tests measured"); return; }

                _output.WriteLine($"--- {label} ---");
                _output.WriteLine($"wall: {sw.Elapsed.TotalSeconds:F2} s   tests: {count}   wall/test: {sw.Elapsed.TotalMilliseconds / count:F2} ms");
                _output.WriteLine($"  (total measured phase ticks: {CompiledPhaseStats.Ms(total):F1} ms)");
                Row("LexParse",        CompiledPhaseStats.LexParseTicks, count, total);
                Row("TypeCheck",       CompiledPhaseStats.TypeCheckTicks, count, total);
                Row("DeadCode",        CompiledPhaseStats.DeadCodeTicks, count, total);
                Row("ILCompile",       CompiledPhaseStats.ILCompileTicks, count, total);
                Row("SaveBytes",       CompiledPhaseStats.SaveBytesTicks, count, total);
                Row("AlcLoad",         CompiledPhaseStats.AlcLoadTicks, count, total);
                Row("  AlcCtor",       CompiledPhaseStats.AlcCtorTicks, count, total);
                Row("  AlcLoadStream", CompiledPhaseStats.AlcLoadFromStreamTicks, count, total);
                Row("  AlcReflect",    CompiledPhaseStats.AlcReflectionTicks, count, total);
                Row("Invoke",          CompiledPhaseStats.InvokeTicks, count, total);
                Row("Unload",          CompiledPhaseStats.UnloadTicks, count, total);
                Row("PeriodicGc",      CompiledPhaseStats.PeriodicGcTicks, count, total);
            }
            finally { Test262Runner.UseNonCollectibleLoad = false; }
        }

        void Row(string name, long ticks, long count, long total)
        {
            var ms = CompiledPhaseStats.Ms(ticks);
            var perTest = ms / count;
            var pct = total == 0 ? 0 : 100.0 * ticks / total;
            _output.WriteLine($"  {name,-16} {ms,9:F1} ms total    {perTest,7:F2} ms/test    {pct,5:F1}%");
        }
    }

    [Theory]
    [InlineData(Test262ExecutionMode.Interpreted)]
    [InlineData(Test262ExecutionMode.Compiled)]
    public void ArrayIsArray_existsAsFunction(Test262ExecutionMode mode)
    {
        var root = Test262Paths.TryFindRoot();
        if (root is null)
        {
            _output.WriteLine("external/test262 not initialized — run `git submodule update --init external/test262`");
            return; // Soft-skip so local builds without the submodule still pass.
        }

        var testPath = Path.Combine(
            Test262Paths.TestDir(root),
            "built-ins", "Array", "isArray", "15.4.3.2-0-1.js");

        Assert.True(File.Exists(testPath), $"Expected Test262 file at {testPath}");

        var runner = new Test262Runner(root);
        var result = runner.RunOne(testPath, mode);

        _output.WriteLine($"{mode} → {result.Outcome}");
        if (result.Message is not null) _output.WriteLine($"  message: {result.Message}");
        if (result.SkipReason is not null) _output.WriteLine($"  skip: {result.SkipReason}");

        Assert.NotEqual(Test262Outcome.HarnessError, result.Outcome);
    }
}
