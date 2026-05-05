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
