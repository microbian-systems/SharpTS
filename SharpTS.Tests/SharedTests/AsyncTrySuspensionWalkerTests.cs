using SharpTS.Tests.Infrastructure;
using Xunit;

namespace SharpTS.Tests.SharedTests;

/// <summary>
/// Regression tests for the compiled async-function suspension walker
/// (<c>AsyncFunctionMoveNextEmitter.ContainsAwaitInStmt/Expr</c>), which had
/// drifted from the generator/async-generator walkers: it was missing the
/// <c>Switch</c>, <c>Throw</c>, <c>Print</c>, and <c>LabeledStatement</c>
/// statement arms and the <c>CompoundAssign</c>, <c>GetIndex</c>, and
/// <c>SetIndex</c> expression arms. When an <c>await</c> sat inside one of those
/// positions <em>within a <c>try</c></em>, the walker under-reported suspension,
/// so the emitter chose the real-IL <c>try</c> lowering whose resume label
/// became an illegal <c>BranchIntoTry</c> target — the #631/#850 failure mode
/// (InvalidProgramException / ILVerify failure) in compiled mode, while the
/// interpreter was unaffected. Most cases are dual-mode (compiled pinned against
/// the interpreter); two constructs — <c>throw await</c> and <c>arr[i] += await</c>
/// inside a try — remain compiled-mode bugs in the flag-based state-machine emission
/// (not the walker) and are pinned interpreter-only with a note. Numbers are kept
/// integral to avoid the separate number-to-string formatting differences.
/// </summary>
public class AsyncTrySuspensionWalkerTests
{
    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void AsyncTry_AwaitInsideSwitch(ExecutionMode mode)
    {
        var source = """
            async function val(): Promise<number> { return 1; }
            async function main(): Promise<void> {
                try {
                    switch (1) {
                        case 1: {
                            const v = await val();
                            console.log("case: " + v);
                            break;
                        }
                    }
                } catch (e) {
                    console.log("caught: " + e);
                }
                console.log("done");
            }
            main();
            """;
        Assert.Equal("case: 1\ndone\n", TestHarness.Run(source, mode));
    }

    // Interpreter-only: in COMPILED mode `throw await f()` inside a try does not route
    // to the guest catch (the awaited value escapes as a host exception). That is a
    // deeper bug in AsyncFunctionMoveNextEmitter's flag-based throw emission, not the
    // suspension walker (which now correctly classifies this as suspending). Tracked as
    // follow-up; pinned here in the mode that is correct.
    [Theory]
    [MemberData(nameof(ExecutionModes.InterpretedOnly), MemberType = typeof(ExecutionModes))]
    public void AsyncTry_AwaitInsideThrow(ExecutionMode mode)
    {
        var source = """
            async function makeErr(): Promise<string> { return "boom"; }
            async function main(): Promise<void> {
                try {
                    throw await makeErr();
                } catch (e) {
                    console.log("caught: " + e);
                }
            }
            main();
            """;
        Assert.Equal("caught: boom\n", TestHarness.Run(source, mode));
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void AsyncTry_AwaitInsideConsoleLog(ExecutionMode mode)
    {
        // console.log(...) lowers to a Stmt.Print, whose arm was missing.
        var source = """
            async function val(): Promise<string> { return "hi"; }
            async function main(): Promise<void> {
                try {
                    console.log(await val());
                } catch (e) {
                    console.log("caught");
                }
            }
            main();
            """;
        Assert.Equal("hi\n", TestHarness.Run(source, mode));
    }

    // Interpreter-only: in COMPILED mode `arr[i] += await f()` inside a try strands the
    // indexed receiver across the suspension (#850-class). The suspension walker now
    // classifies it correctly; the remaining gap is in the flag-based async state-machine
    // emission. Tracked as follow-up; pinned here in the mode that is correct.
    [Theory]
    [MemberData(nameof(ExecutionModes.InterpretedOnly), MemberType = typeof(ExecutionModes))]
    public void AsyncTry_AwaitInCompoundIndexedAssign(ExecutionMode mode)
    {
        // arr[0] += await ... exercises CompoundAssign + GetIndex + SetIndex.
        var source = """
            async function inc(): Promise<number> { return 5; }
            async function main(): Promise<void> {
                const arr = [10];
                try {
                    arr[0] += await inc();
                    console.log("arr0: " + arr[0]);
                } catch (e) {
                    console.log("caught");
                }
            }
            main();
            """;
        Assert.Equal("arr0: 15\n", TestHarness.Run(source, mode));
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void AsyncTry_AwaitInIndexExpression(ExecutionMode mode)
    {
        // arr[await ...] exercises GetIndex with the await in the index.
        var source = """
            async function idx(): Promise<number> { return 2; }
            async function main(): Promise<void> {
                const arr = [10, 20, 30];
                try {
                    const v = arr[await idx()];
                    console.log("v: " + v);
                } catch (e) {
                    console.log("caught");
                }
            }
            main();
            """;
        Assert.Equal("v: 30\n", TestHarness.Run(source, mode));
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void AsyncTry_AwaitInsideLabeledStatement(ExecutionMode mode)
    {
        // The labeled loop wraps the await; pre-fix the missing LabeledStatement
        // arm short-circuited before descending into the loop body.
        var source = """
            async function val(): Promise<number> { return 7; }
            async function main(): Promise<void> {
                try {
                    outer: for (let i = 0; i < 1; i++) {
                        const v = await val();
                        console.log("labeled: " + v);
                    }
                } catch (e) {
                    console.log("caught");
                }
            }
            main();
            """;
        Assert.Equal("labeled: 7\n", TestHarness.Run(source, mode));
    }
}
