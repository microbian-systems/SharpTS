using SharpTS.Tests.Infrastructure;
using Xunit;

namespace SharpTS.Tests.SharedTests;

/// <summary>
/// An async arrow that WRITES a variable captured from its enclosing async function. The captured
/// local is promoted into the function's (reference-type) display class and the arrow mutates it
/// through `outer.functionDC.field`, because a boxed value-type state machine cannot be mutated in
/// place by verifiable IL — `unbox` yields a readonly managed pointer, so the old `unbox`+`stfld`
/// failed IL verification and could drop the write in complex state machines (#625).
/// </summary>
public class AsyncArrowCapturedWriteTests
{
    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void AsyncArrow_WritesCapturedVariable(ExecutionMode mode)
    {
        var source = """
            async function main() {
              let n = 0;
              const w = async () => { n = 5; };
              await w();
              console.log(n);
            }
            main();
            """;

        Assert.Equal("5\n", TestHarness.Run(source, mode));
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void AsyncArrow_CompoundWriteToCapturedVariable(ExecutionMode mode)
    {
        var source = """
            async function main() {
              let order = "";
              const w = async () => { order += "a"; };
              await w();
              order += "b";
              await w();
              console.log(order);
            }
            main();
            """;

        Assert.Equal("aba\n", TestHarness.Run(source, mode));
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void AsyncArrow_WritesCapturedVariableAfterAwait(ExecutionMode mode)
    {
        var source = """
            async function main() {
              let n = 0;
              const w = async () => { n = await Promise.resolve(7); };
              await w();
              console.log(n);
            }
            main();
            """;

        Assert.Equal("7\n", TestHarness.Run(source, mode));
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void AsyncArrow_MultipleCapturedWrites(ExecutionMode mode)
    {
        var source = """
            async function main() {
              let a = 1, b = 2;
              const w = async () => { a = a + b; b = a * 2; };
              await w();
              console.log(a + " " + b);
            }
            main();
            """;

        Assert.Equal("3 6\n", TestHarness.Run(source, mode));
    }

    [Fact]
    public void AsyncArrow_WritesCapturedVariable_PassesILVerification()
    {
        // The whole point of #625: the emitted store must be VERIFIABLE, not merely JIT-accepted.
        var source = """
            async function main() {
              let n = 0;
              const w = async () => { n = 5; };
              await w();
              console.log(n);
            }
            main();
            """;

        var (errors, output) = TestHarness.CompileVerifyAndRun(source);

        Assert.Empty(errors);
        Assert.Equal("5\n", output);
    }

    [Fact]
    public void AsyncArrow_ReadOnlyCapture_StillPassesILVerification()
    {
        // Read-only captures keep using the state-machine field load path; guard against the #625
        // change regressing them.
        var source = """
            async function main() {
              let s = "hi";
              const r = async () => { console.log(s); };
              await r();
            }
            main();
            """;

        var (errors, output) = TestHarness.CompileVerifyAndRun(source);

        Assert.Empty(errors);
        Assert.Equal("hi\n", output);
    }
}
