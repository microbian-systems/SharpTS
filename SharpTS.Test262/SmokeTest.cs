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
    /// Pinpoints the exact line in species-ctor.js that throws after prop-desc.js,
    /// by progressively shrinking the source.
    /// </summary>
    [Fact(Skip = "diagnostic only — kept for repro of the issue#101 cross-test prototype leak")]
    public void Diagnostic_SpeciesCtorAfterPropDesc()
    {
        var root = Test262Paths.TryFindRoot();
        if (root is null) return;
        var testDir = Path.Combine(
            Test262Paths.TestDir(root),
            "built-ins", "RegExp", "prototype", "Symbol.split");
        var prior = Path.Combine(testDir, "prop-desc.js");

        // Various inline scripts to try AFTER running prop-desc.js.
        var snippets = new (string label, string src)[]
        {
            ("just regex literal", "var re = /x/iy;"),
            ("set re.constructor", "var re = /x/iy; re.constructor = function() {};"),
            ("read RegExp.prototype", "var p = RegExp.prototype; console.log(typeof p);"),
            ("read [Symbol.split]", "var p = RegExp.prototype[Symbol.split]; console.log(typeof p);"),
            ("regex then read [Symbol.split]", "var re = /x/iy; var p = RegExp.prototype[Symbol.split]; console.log(typeof p);"),
            ("regex then RegExp.prototype", "var re = /x/iy; var p = RegExp.prototype; console.log(typeof p, p);"),
            ("regex; m=...; typeof m STRICT", "var re = /x/iy; var m = RegExp.prototype[Symbol.split]; if (typeof m !== 'function') throw new Error('m typeof: ' + typeof m); console.log('ok');"),
            ("regex; m=...; m.call typeof", "var re = /x/iy; var m = RegExp.prototype[Symbol.split]; var c = m.call; console.log(typeof c);"),
            ("after prop-desc, m.call call", "var re = /x/iy; var r = RegExp.prototype[Symbol.split].call(re, 'abcde'); console.log(r);"),
            ("regex; nothing else", "var re = /x/iy;"),
            ("just save+typeof split", "var m = RegExp.prototype[Symbol.split]; var c = m.call; console.log(typeof c);"),
            ("verify m+then call", "var m = RegExp.prototype[Symbol.split]; if (typeof m !== 'function') throw new Error('m is ' + typeof m); var c = m.call; if (typeof c !== 'function') throw new Error('c is ' + typeof c); console.log('ok');"),
            ("just verify m alone", "var m = RegExp.prototype[Symbol.split]; if (typeof m !== 'function') throw new Error('m is ' + typeof m); console.log('ok');"),
            ("c=m.call; log done", "var m = RegExp.prototype[Symbol.split]; var c = m.call; console.log('done');"),
            ("c=m.call no log", "var m = RegExp.prototype[Symbol.split]; var c = m.call;"),
            ("expr m.call no var", "var m = RegExp.prototype[Symbol.split]; m.call;"),
            ("var c = m.call; typeof c", "var m = RegExp.prototype[Symbol.split]; var c = m.call; typeof c;"),
            ("typeof_c_alone", "var m = RegExp.prototype[Symbol.split]; var c = m.call; var t = typeof c;"),
            ("let m c", "let m = RegExp.prototype[Symbol.split]; let c = m.call; console.log(typeof c);"),
            ("inline m.call", "console.log(typeof RegExp.prototype[Symbol.split].call);"),
            ("inline +var m", "var m = RegExp.prototype[Symbol.split]; console.log(typeof m.call);"),
            ("inline2 + 2 var m", "var m = RegExp.prototype[Symbol.split]; var n = m; console.log(typeof n.call);"),
            ("ifelse with m alone", "var m = RegExp.prototype[Symbol.split]; if (m) { console.log('ok'); }"),
            ("ifelse with m.call", "var m = RegExp.prototype[Symbol.split]; if (m.call) { console.log('ok'); }"),
            ("var c = (m.call)", "var m = RegExp.prototype[Symbol.split]; var c = (m.call); console.log(typeof c);"),
            ("c = m['call']", "var m = RegExp.prototype[Symbol.split]; var c = m['call']; console.log(typeof c);"),
            ("var c only m", "var m = RegExp.prototype[Symbol.split]; var c = m; console.log(typeof c);"),
            ("var c=m, then m.call", "var m = RegExp.prototype[Symbol.split]; var c = m; if (typeof c !== 'function') throw new Error('c is ' + typeof c); console.log('ok');"),
            ("invoke Symbol.split direct", "var re = /x/iy; var r = re[Symbol.split]('abcde'); console.log(r);"),
            ("Array.push.call", "var arr = []; Array.prototype.push.call(arr, 1); console.log(arr.length);"),
            ("hasOwnProperty.call", "var o = {a:1}; var r = Object.prototype.hasOwnProperty.call(o, 'a'); console.log(r);"),
            ("Function.prototype.call exists", "console.log(typeof Function.prototype.call);"),
            ("Function.prototype.call.bind", "var f = Function.prototype.call.bind(Object.prototype.hasOwnProperty); console.log(typeof f);"),
            ("save method then call", "var re = /x/iy; var m = RegExp.prototype[Symbol.split]; var r = m.call(re, 'abcde'); console.log(r);"),
            ("save method then check typeof", "var re = /x/iy; var m = RegExp.prototype[Symbol.split]; console.log(typeof m, typeof m.call);"),
            ("invoke Symbol.split", "var re = /x/iy; var r = RegExp.prototype[Symbol.split].call(re, 'abcde'); console.log(r);"),
            ("species-ctor body", "var re = /x/iy; re.constructor = function() {}; re.constructor[Symbol.species] = function() { return /[db]/y; }; var r = RegExp.prototype[Symbol.split].call(re, 'abcde'); console.log(r);"),
        };

        foreach (var (label, src) in snippets)
        {
            var runner = new Test262Runner(root, TimeSpan.FromSeconds(15));
            runner.RunOne(prior, Test262ExecutionMode.Interpreted);
            var tmp = Path.GetTempFileName() + ".js";
            File.WriteAllText(tmp, src);
            try
            {
                var result = runner.RunOne(tmp, Test262ExecutionMode.Interpreted);
                _output.WriteLine($"[{label}] → {result.Outcome}: {result.Message}");
            }
            finally { File.Delete(tmp); }
        }
    }

    /// <summary>
    /// Minimal repro: writes the species-ctor pattern inline so we don't need
    /// the harness. Confirms the bug is in the JS pattern itself, not in
    /// harness loading or shared state.
    /// </summary>
    [Fact(Skip = "diagnostic only — kept for repro of the issue#101 cross-test prototype leak")]
    public void Diagnostic_SpeciesCtorMinimal()
    {
        var root = Test262Paths.TryFindRoot();
        if (root is null) return;
        // Write a temp .js with the species-ctor pattern.
        var src = "var re = /x/iy; re.constructor = function() {}; re.constructor[Symbol.species] = function() { return /[db]/y; }; var r = RegExp.prototype[Symbol.split].call(re, 'abcde'); console.log(r);";
        var tmp = Path.GetTempFileName() + ".js";
        File.WriteAllText(tmp, src);
        var runner = new Test262Runner(root, TimeSpan.FromSeconds(15));
        var result = runner.RunOne(tmp, Test262ExecutionMode.Interpreted);
        _output.WriteLine($"outcome: {result.Outcome}");
        if (result.Message is not null) _output.WriteLine($"message: {result.Message}");
        File.Delete(tmp);
    }

    /// <summary>
    /// Diagnostic for #101: species-ctor.js passes manually but reports
    /// RuntimeError in the regen baseline. Runs the test through Test262Runner
    /// directly to capture the exact outcome + message and confirm whether
    /// it's a runner-state issue or a code-path bug.
    /// </summary>
    [Fact]
    public void Diagnostic_SpeciesGetErr()
    {
        var root = Test262Paths.TryFindRoot();
        if (root is null) return;
        var path = Path.Combine(Test262Paths.TestDir(root),
            "built-ins", "RegExp", "prototype", "Symbol.split", "species-ctor-species-get-err.js");
        var runner = new Test262Runner(root, TimeSpan.FromSeconds(15));
        var result = runner.RunOne(path, Test262ExecutionMode.Interpreted);
        _output.WriteLine($"{result.Outcome}: {result.Message}");
    }

    [Fact]
    public void Diagnostic_RegExpRegressions()
    {
        var root = Test262Paths.TryFindRoot();
        if (root is null) return;
        var paths = new[]
        {
            Path.Combine(Test262Paths.TestDir(root), "built-ins", "RegExp", "S15.10.4.1_A6_T1.js"),
            Path.Combine(Test262Paths.TestDir(root), "built-ins", "RegExp", "prototype", "Symbol.match", "g-coerce-result-err.js"),
            Path.Combine(Test262Paths.TestDir(root), "built-ins", "RegExp", "prototype", "Symbol.matchAll", "this-get-flags.js"),
        };
        foreach (var path in paths)
        {
            var runner = new Test262Runner(root, TimeSpan.FromSeconds(15));
            var result = runner.RunOne(path, Test262ExecutionMode.Interpreted);
            _output.WriteLine($"{Path.GetFileName(path)} → {result.Outcome}: {result.Message}");
        }
    }

    [Fact]
    public void Diagnostic_CoerceGlobal()
    {
        var root = Test262Paths.TryFindRoot();
        if (root is null) return;
        var path = Path.Combine(Test262Paths.TestDir(root),
            "built-ins", "RegExp", "prototype", "Symbol.replace", "coerce-global.js");
        var runner = new Test262Runner(root, TimeSpan.FromSeconds(15));
        var result = runner.RunOne(path, Test262ExecutionMode.Interpreted);
        _output.WriteLine($"{result.Outcome}: {result.Message}");
    }

    [Fact]
    public void Diagnostic_RegExpExecRegression()
    {
        var root = Test262Paths.TryFindRoot();
        if (root is null) return;
        var tests = new[] { "S15.10.6.2_A6.js", "S15.10.6.2_A8.js", "S15.10.6.2_A9.js", "S15.10.6.2_A10.js", "S15.10.6.2_A11.js" };
        foreach (var t in tests)
        {
            var path = Path.Combine(Test262Paths.TestDir(root), "built-ins", "RegExp", "prototype", "exec", t);
            var runner = new Test262Runner(root, TimeSpan.FromSeconds(15));
            var result = runner.RunOne(path, Test262ExecutionMode.Interpreted);
            _output.WriteLine($"{t} → {result.Outcome}: {result.Message}");
        }
    }

    [Fact(Skip = "diagnostic only — kept for repro of the issue#101 cross-test prototype leak")]
    public void Diagnostic_SpeciesCtor()
    {
        var root = Test262Paths.TryFindRoot();
        if (root is null) return;
        var testDir = Path.Combine(
            Test262Paths.TestDir(root),
            "built-ins", "RegExp", "prototype", "Symbol.split");
        // Run all Symbol.split tests in alphabetical order using a single
        // Test262Runner (matching the baseline path). If species-ctor.js
        // passes standalone but fails in this batch, some earlier test is
        // polluting state.
        var files = Directory.EnumerateFiles(testDir, "*.js")
            .OrderBy(f => f, StringComparer.Ordinal)
            .ToList();
        var runner = new Test262Runner(root, TimeSpan.FromSeconds(15));
        var failures = new List<string>();
        foreach (var f in files)
        {
            var result = runner.RunOne(f, Test262ExecutionMode.Interpreted);
            var name = Path.GetFileName(f);
            if (result.Outcome == Test262Outcome.RuntimeError)
                failures.Add($"{name}: {result.Message}");
            if (name.Contains("species-ctor.js") || name == "species-ctor-y.js")
                _output.WriteLine($"{name} → {result.Outcome}: {result.Message}");
        }
        _output.WriteLine($"runtime errors: {failures.Count}");
        foreach (var f in failures.Take(20)) _output.WriteLine($"  {f}");
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
