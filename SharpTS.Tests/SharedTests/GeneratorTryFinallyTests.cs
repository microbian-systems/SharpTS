using SharpTS.Tests.Infrastructure;
using Xunit;

namespace SharpTS.Tests.SharedTests;

/// <summary>
/// Regression tests for #477: a compiled generator with a <c>yield</c> inside <c>try</c>/
/// <c>catch</c>/<c>finally</c> previously emitted invalid IL (<c>InvalidProgramException</c> in
/// <c>MoveNext</c>) — the state-dispatch switch branched into the protected region and the
/// <c>yield</c>'s <c>ret</c> sat inside it, both illegal. The fix emits a flag-based scheme: the
/// try body's synchronous runs are wrapped in mini IL try/catch blocks that record any exception
/// into a flag, while the yields (and any non-local exits) are emitted at the top level so their
/// resume labels are reachable and their <c>ret</c>/<c>br</c> are legal.
///
/// Run against both modes; the interpreter already behaved correctly, so these double as a
/// cross-mode parity guard. Cases that depend on still-open gaps are documented inline and
/// intentionally not asserted here: generator <c>.throw()</c>/<c>.return()</c> injecting through
/// a suspended <c>try</c> (#478), <c>break</c>/<c>continue</c> running an enclosing <c>finally</c>
/// (#500), and the completion value of a normally-finished generator (#499) are out of scope.
/// </summary>
public class GeneratorTryFinallyTests
{
    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void YieldInTryFinally_RunsFinallyAfterBody(ExecutionMode mode)
    {
        // The exact repro from #477.
        var source = """
            function* g() {
              try {
                yield 1;
                yield 2;
              } finally {
                console.log("fin");
              }
            }
            for (const v of g()) console.log(v);
            """;

        Assert.Equal("1\n2\nfin\n", TestHarness.Run(source, mode));
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void YieldThenThrowInTry_CaughtWithValue(ExecutionMode mode)
    {
        // The yield resumes, then a synchronous throw in the same try body is caught — and the
        // thrown value reaches the catch parameter (it is hoisted to a field because it is used
        // after the yield; storing it only to a fresh local previously lost it).
        var source = """
            function* g() {
              try {
                yield 1;
                throw "boom";
              } catch (e) {
                console.log("caught " + e);
              }
            }
            for (const v of g()) console.log(v);
            """;

        Assert.Equal("1\ncaught boom\n", TestHarness.Run(source, mode));
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void YieldInTry_CatchAndFinally_ThenYieldAfter(ExecutionMode mode)
    {
        var source = """
            function* g() {
              try {
                yield 1;
              } catch (e) {
                console.log("c");
              } finally {
                console.log("fin");
              }
              yield 9;
            }
            for (const v of g()) console.log(v);
            """;

        Assert.Equal("1\nfin\n9\n", TestHarness.Run(source, mode));
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void YieldInTryFinally_InsideLoop_RunsFinallyEachIteration(ExecutionMode mode)
    {
        var source = """
            function* g() {
              for (let i = 0; i < 3; i++) {
                try {
                  yield i;
                } finally {
                  console.log("f" + i);
                }
              }
            }
            for (const v of g()) console.log(v);
            """;

        Assert.Equal("0\nf0\n1\nf1\n2\nf2\n", TestHarness.Run(source, mode));
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void NestedTryFinally_WithYield_RunsBothFinallysInnerThenOuter(ExecutionMode mode)
    {
        var source = """
            function* g() {
              try {
                try {
                  yield 1;
                } finally {
                  console.log("inner");
                }
              } finally {
                console.log("outer");
              }
            }
            for (const v of g()) console.log(v);
            """;

        Assert.Equal("1\ninner\nouter\n", TestHarness.Run(source, mode));
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void YieldStarInTryFinally_DelegatesThenRunsFinally(ExecutionMode mode)
    {
        var source = """
            function* inner() { yield 2; yield 3; }
            function* g() {
              try {
                yield 1;
                yield* inner();
                yield 4;
              } finally {
                console.log("fin");
              }
            }
            for (const v of g()) console.log(v);
            """;

        Assert.Equal("1\n2\n3\n4\nfin\n", TestHarness.Run(source, mode));
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void FinallyThatYields_IsDriven(ExecutionMode mode)
    {
        var source = """
            function* g() {
              try {
                yield 1;
              } finally {
                yield 2;
                yield 3;
              }
            }
            for (const v of g()) console.log(v);
            """;

        Assert.Equal("1\n2\n3\n", TestHarness.Run(source, mode));
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void ReturnInsideTryFinally_RunsFinallyBeforeCompleting(ExecutionMode mode)
    {
        // `return` inside the try must run the finally before the generator completes; the
        // statement after the return must not execute.
        var source = """
            function* g() {
              try {
                yield 1;
                return;
                yield 99;
              } finally {
                console.log("fin");
              }
            }
            for (const v of g()) console.log(v);
            """;

        Assert.Equal("1\nfin\n", TestHarness.Run(source, mode));
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void ReturnInsideNestedTryFinally_RunsAllEnclosingFinallys(ExecutionMode mode)
    {
        var source = """
            function* g() {
              try {
                try {
                  yield 1;
                  return;
                } finally {
                  console.log("inner");
                }
              } finally {
                console.log("outer");
              }
              yield 99;
            }
            for (const v of g()) console.log(v);
            """;

        Assert.Equal("1\ninner\nouter\n", TestHarness.Run(source, mode));
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void ReturnInTry_WithYieldingFinally_CompletesAfterFinallyYields(ExecutionMode mode)
    {
        // The finally itself yields, suspending MoveNext between the `return` and the completion
        // check. The pending-return state must survive that suspension (it lives in a field, not a
        // local), so the generator completes after the finally rather than running `yield 99`.
        var source = """
            function* g() {
              try {
                yield 1;
                return;
              } finally {
                yield 2;
              }
              yield 99;
            }
            for (const v of g()) console.log(v);
            """;

        Assert.Equal("1\n2\n", TestHarness.Run(source, mode));
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void NextValue_DeliveredToYieldInsideTry(ExecutionMode mode)
    {
        // The value passed to next() must reach a yield expression sitting inside a try.
        var source = """
            function* g() {
              try {
                const x = yield 1;
                console.log("got " + x);
              } finally {
                console.log("fin");
              }
            }
            const it = g();
            it.next();
            it.next(10);
            """;

        Assert.Equal("got 10\nfin\n", TestHarness.Run(source, mode));
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void HoistedCatchParam_SimplePath_ReceivesThrownValue(ExecutionMode mode)
    {
        // Catch parameter used after a yield is hoisted to a field; the try here has no yield
        // (simple IL try/catch path). Storing the caught value only to a local previously lost
        // it — this guards the field-aware bind for the simple path too.
        var source = """
            function* g() {
              yield 0;
              try {
                throw "boom";
              } catch (e) {
                yield e;
              }
            }
            for (const v of g()) console.log(v);
            """;

        Assert.Equal("0\nboom\n", TestHarness.Run(source, mode));
    }

    // ---- IL-verification guards (the heart of #477: emitted IL must verify) ----

    [Theory]
    [InlineData("function* g() { try { yield 1; yield 2; } finally { console.log('f'); } } for (const v of g()) {}")]
    [InlineData("function* g() { try { yield 1; throw 'x'; } catch (e) { console.log(e); } } for (const v of g()) {}")]
    [InlineData("function* g() { try { yield 1; } catch (e) {} finally { console.log('f'); } yield 2; } for (const v of g()) {}")]
    [InlineData("function* g() { for (let i=0;i<2;i++){ try { yield i; } finally { console.log(i); } } } for (const v of g()) {}")]
    [InlineData("function* g() { try { try { yield 1; } finally { console.log('a'); } } finally { console.log('b'); } } for (const v of g()) {}")]
    [InlineData("function* g() { try { yield 1; return; } finally { console.log('f'); } } for (const v of g()) {}")]
    [InlineData("function* g() { try { yield 1; } finally { yield 2; } } for (const v of g()) {}")]
    [InlineData("function* g() { while (true) { try { yield 1; break; } finally { console.log('f'); } } } for (const v of g()) {}")]
    [InlineData("function* inner(){ yield 2; } function* g() { try { yield 1; yield* inner(); } finally { console.log('f'); } } for (const v of g()) {}")]
    public void GeneratorTryFinallyWithYield_EmitsVerifiableIL(string source)
    {
        var errors = TestHarness.CompileAndVerifyOnly(source);
        Assert.Empty(errors);
    }
}
