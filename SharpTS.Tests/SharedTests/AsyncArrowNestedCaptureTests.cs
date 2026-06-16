using SharpTS.Diagnostics.Exceptions;
using SharpTS.Tests.Infrastructure;
using Xunit;

namespace SharpTS.Tests.SharedTests;

/// <summary>
/// An async arrow nested directly inside a TOP-LEVEL (standalone) async arrow that captures a
/// variable from the enclosing arrow's scope. Compiled mode registers the inner arrow as its own
/// standalone state machine; #641 wires the single read-only capture through the stub's leading
/// capture argument (read from the enclosing frame, copied into the inner state machine). Harder
/// shapes — writing a capture, or capturing more than one variable — are rejected with a clear
/// compile error rather than miscompiled (#684).
/// </summary>
public class AsyncArrowNestedCaptureTests
{
    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void NestedAsyncArrow_ReadsSingleCapture(ExecutionMode mode)
    {
        var source = """
            const f = async () => {
              const base = 100;
              const x = await (async () => base + 5)();
              console.log(x);
            };
            f();
            """;

        Assert.Equal("105\n", TestHarness.Run(source, mode));
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void NestedAsyncArrow_ReadsCaptureAndOwnParameter(ExecutionMode mode)
    {
        var source = """
            const f = async () => {
              const base = 100;
              const inner = async (k: number) => base + k;
              const x = await inner(5);
              console.log(x);
            };
            f();
            """;

        Assert.Equal("105\n", TestHarness.Run(source, mode));
    }

    [Fact]
    public void NestedAsyncArrow_ReadsSingleCapture_PassesILVerification()
    {
        var source = """
            const f = async () => {
              const base = 100;
              const x = await (async () => base + 5)();
              console.log(x);
            };
            f();
            """;

        var (errors, output) = TestHarness.CompileVerifyAndRun(source);

        Assert.Empty(errors);
        Assert.Equal("105\n", output);
    }

    [Fact]
    public void NestedAsyncArrow_WritesCapture_RejectedInCompiledMode()
    {
        // Standalone arrows capture by value, so a write cannot propagate back. Compiled mode must
        // reject this rather than silently dropping the write (#684); the interpreter is correct.
        var source = """
            const f = async () => {
              let n = 1;
              const g = async () => { n = await (async () => n + 10)(); };
              await g();
              console.log(n);
            };
            f();
            """;

        Assert.Equal("11\n", TestHarness.RunInterpreted(source));
        Assert.Throws<CompileException>(() => TestHarness.RunCompiled(source));
    }

    [Fact]
    public void NestedAsyncArrow_MultipleCaptures_RejectedInCompiledMode()
    {
        var source = """
            const f = async () => {
              const a = 10, b = 20, c = 30;
              const inner = async () => a + b + c;
              console.log(await inner());
            };
            f();
            """;

        Assert.Equal("60\n", TestHarness.RunInterpreted(source));
        Assert.Throws<CompileException>(() => TestHarness.RunCompiled(source));
    }
}
