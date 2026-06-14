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
/// cross-mode parity guard. #500 extended the compiled scheme so that every non-local exit
/// (<c>break</c>/<c>continue</c> leaving the try, and a <c>throw</c> or <c>return</c> from a catch
/// or finally body) runs the enclosing <c>finally</c>(s) before transferring control — previously
/// only <c>return</c> from the try body did. Cases that depend on still-open gaps are documented
/// inline and intentionally not asserted here: generator <c>.throw()</c>/<c>.return()</c> injecting
/// through a suspended <c>try</c> (#478) and the completion value of a normally-finished generator
/// (#499) are out of scope.
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

    // ---- #500: non-local exits other than a try-body return must run the enclosing finally ----

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void BreakOutOfTryFinally_RunsFinallyBeforeBreaking(ExecutionMode mode)
    {
        // The exact #500 repro: break leaving the try must run the finally first.
        var source = """
            function* g() {
              while (true) {
                try { yield 1; break; } finally { console.log("FIN"); }
              }
            }
            for (const v of g()) console.log(v);
            """;

        Assert.Equal("1\nFIN\n", TestHarness.Run(source, mode));
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void ContinueOutOfTryFinally_RunsFinallyThatIteration(ExecutionMode mode)
    {
        // `continue` from inside the try must run the finally before jumping to the next iteration,
        // and the code after the continue must be skipped on that iteration only.
        var source = """
            function* g() {
              for (let i = 0; i < 3; i++) {
                try {
                  yield i;
                  if (i === 1) continue;
                  console.log("after" + i);
                } finally {
                  console.log("fin" + i);
                }
              }
            }
            for (const v of g()) console.log("got" + v);
            """;

        Assert.Equal("got0\nafter0\nfin0\ngot1\nfin1\ngot2\nafter2\nfin2\n", TestHarness.Run(source, mode));
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void ThrowFromCatch_RunsFinallyThenPropagates(ExecutionMode mode)
    {
        // The exact #500 repro: a throw inside the catch must still run the finally before the
        // exception propagates out of the generator to the consumer.
        var source = """
            function* g() {
              try { yield 1; throw "a"; } catch (e) { throw "b"; } finally { console.log("FIN"); }
            }
            try { for (const v of g()) console.log(v); } catch (e) { console.log("outer " + e); }
            """;

        Assert.Equal("1\nFIN\nouter b\n", TestHarness.Run(source, mode));
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void ReturnFromCatch_RunsFinallyBeforeCompleting(ExecutionMode mode)
    {
        // A `return` from the catch body must run the finally; the yield after the try must not run.
        var source = """
            function* g() {
              try { yield 1; throw "x"; } catch (e) { return; } finally { console.log("FIN"); }
              yield 99;
            }
            for (const v of g()) console.log(v);
            """;

        Assert.Equal("1\nFIN\n", TestHarness.Run(source, mode));
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void BreakThroughNestedFinallys_RunsInnerThenOuter(ExecutionMode mode)
    {
        // A break that leaves two enclosing trys runs both finallys, innermost first.
        var source = """
            function* g() {
              while (true) {
                try {
                  try { yield 1; break; } finally { console.log("inner"); }
                } finally { console.log("outer"); }
              }
            }
            for (const v of g()) console.log("v" + v);
            """;

        Assert.Equal("v1\ninner\nouter\n", TestHarness.Run(source, mode));
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void LabeledBreakToOuterLoop_RunsInterveningFinally(ExecutionMode mode)
    {
        // A labeled break targeting the outer loop runs the finally of the inner loop's try.
        var source = """
            function* g() {
              outer: for (let i = 0; i < 3; i++) {
                for (let j = 0; j < 3; j++) {
                  try { yield i * 10 + j; if (j === 1) break outer; } finally { console.log("fin" + i + j); }
                }
              }
            }
            for (const v of g()) console.log("v" + v);
            """;

        Assert.Equal("v0\nfin00\nv1\nfin01\n", TestHarness.Run(source, mode));
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void BreakToLoopBetweenTwoTrys_RunsOnlyInnerFinally(ExecutionMode mode)
    {
        // The break targets a loop that sits *between* two trys: only the finally inside that loop
        // runs at the break; the outer finally runs once, later, when the generator completes.
        var source = """
            function* g() {
              try {
                for (let i = 0; i < 3; i++) {
                  try { yield i; if (i === 1) break; } finally { console.log("inner" + i); }
                }
                console.log("after-loop");
              } finally { console.log("OUTER"); }
            }
            for (const v of g()) console.log("v" + v);
            """;

        Assert.Equal("v0\ninner0\nv1\ninner1\nafter-loop\nOUTER\n", TestHarness.Run(source, mode));
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void BreakWithYieldingFinally_DrivesFinallyThenBreaks(ExecutionMode mode)
    {
        // The finally that runs on the break path itself yields; the break completes only after the
        // finally's yields are driven, then control resumes after the loop.
        var source = """
            function* g() {
              while (true) {
                try { yield 1; break; } finally { yield 2; }
              }
              yield 3;
            }
            for (const v of g()) console.log("v" + v);
            """;

        Assert.Equal("v1\nv2\nv3\n", TestHarness.Run(source, mode));
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void ThrowFromFinally_RunsEnclosingFinallyThenPropagates(ExecutionMode mode)
    {
        // A throw raised inside a finally body must still run the enclosing finally before it
        // propagates to the consumer.
        var source = """
            function* g() {
              try {
                try { yield 1; } finally { throw "boom"; }
              } finally { console.log("OUTER"); }
            }
            try { for (const v of g()) console.log("v" + v); } catch (e) { console.log("caught " + e); }
            """;

        Assert.Equal("v1\nOUTER\ncaught boom\n", TestHarness.Run(source, mode));
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void BreakOutOfInnerCatchlessTry_RunsOuterFinally(ExecutionMode mode)
    {
        // The break leaves an inner try/catch that has no finally, nested in an outer try-with-
        // finally; the outer finally must still run on the way out.
        var source = """
            function* g() {
              while (true) {
                try {
                  try { yield 1; break; } catch (e) {}
                } finally { console.log("OUTERFIN"); }
              }
            }
            for (const v of g()) console.log("v" + v);
            """;

        Assert.Equal("v1\nOUTERFIN\n", TestHarness.Run(source, mode));
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

    // ---- #554: return/break/continue inside a NO-yield try (real IL exception block) ----
    // When no yield crosses the protected region, EmitSimpleTryCatch opens a *real* IL exception
    // block. A `return`/`break`/`continue` leaving it must therefore use `Leave` (which runs the
    // finally) rather than an illegal `ret`/`br` — previously these crashed MoveNext with
    // InvalidProgramException (return → ReturnFromTry, break/continue → BranchOutOfTry).

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void BareReturnInNoYieldTryFinally_RunsFinally(ExecutionMode mode)
    {
        // The exact #554 repro: a yield precedes the try, but the try itself contains no yield.
        var source = """
            function* g() { yield 0; try { return; } finally { console.log("f"); } }
            for (const v of g()) console.log(v);
            """;

        Assert.Equal("0\nf\n", TestHarness.Run(source, mode));
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void ValueReturnInNoYieldTryFinally_RunsFinallyThenCompletesWithValue(ExecutionMode mode)
    {
        // The finally runs on the return path, and the returned value is the completion value.
        var source = """
            function* g() { yield 0; try { return 7; } finally { console.log("f"); } }
            const it = g();
            let r = it.next(); console.log(r.value + "/" + r.done);
            r = it.next(); console.log(r.value + "/" + r.done);
            r = it.next(); console.log(r.value + "/" + r.done);
            """;

        Assert.Equal("0/false\nf\n7/true\nundefined/true\n", TestHarness.Run(source, mode));
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void BreakInNoYieldTryFinally_RunsFinallyBeforeBreaking(ExecutionMode mode)
    {
        // The second #554 repro: break leaves a no-yield try, running its finally first.
        var source = """
            function* g() { yield 0; while (true) { try { break; } finally { console.log("f"); } } }
            for (const v of g()) console.log(v);
            """;

        Assert.Equal("0\nf\n", TestHarness.Run(source, mode));
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void ContinueInNoYieldTryFinally_RunsFinallyEachIteration(ExecutionMode mode)
    {
        // The yield sits outside the try, so the try (holding the continue) takes the real-IL path.
        var source = """
            function* g() {
              for (let i = 0; i < 3; i++) {
                yield i;
                try { continue; } finally { console.log("f" + i); }
              }
            }
            for (const v of g()) console.log("v" + v);
            """;

        Assert.Equal("v0\nf0\nv1\nf1\nv2\nf2\n", TestHarness.Run(source, mode));
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void ReturnInNoYieldTry_NestedInYieldingFinally_RunsAllThenKeepsValue(ExecutionMode mode)
    {
        // The inner try has no yield (real IL block); it is nested in an outer try whose finally
        // yields (flag-based). The inner return must `Leave` the real block — running its no-yield
        // finally — into the outer flag cleanup, drive the outer yielding finally, then complete with
        // the returned value (a real try never encloses a flag try, so the finally ordering holds).
        var source = """
            function* g() {
              try {
                yield 1;
                try { return 5; } finally { console.log("x"); }
              } finally { yield 2; }
            }
            const it = g();
            let r = it.next(); console.log(r.value + "/" + r.done);
            r = it.next(); console.log(r.value + "/" + r.done);
            r = it.next(); console.log(r.value + "/" + r.done);
            """;

        Assert.Equal("1/false\nx\n2/false\n5/true\n", TestHarness.Run(source, mode));
    }

    // ---- #555: a `return <value>` whose finally yields must keep its completion value ----

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void ReturnValueWithYieldingFinally_RestoresCompletionValue(ExecutionMode mode)
    {
        // The exact #555 repro: the finally yields 9 (overwriting Current), but the final next() must
        // still report the returned 5 — the value is stashed at the return and restored after the
        // finally has run. (Compiled previously reported the yielded 9 as the completion value.)
        var source = """
            function* g() { try { return 5; } finally { yield 9; } }
            const it = g();
            let r = it.next(); console.log(r.value + "/" + r.done);
            r = it.next(); console.log(r.value + "/" + r.done);
            r = it.next(); console.log(r.value + "/" + r.done);
            """;

        Assert.Equal("9/false\n5/true\nundefined/true\n", TestHarness.Run(source, mode));
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void ReturnValueInYieldingTry_NonYieldingFinally_KeepsValue(ExecutionMode mode)
    {
        // Sibling of #555: the try yields (flag-based path) and returns a value; the finally does not
        // yield. The completion value must be the returned 5, not the last yielded value.
        var source = """
            function* g() { try { yield 1; return 5; } finally { console.log("f"); } }
            const it = g();
            let r = it.next(); console.log(r.value + "/" + r.done);
            r = it.next(); console.log(r.value + "/" + r.done);
            r = it.next(); console.log(r.value + "/" + r.done);
            """;

        Assert.Equal("1/false\nf\n5/true\nundefined/true\n", TestHarness.Run(source, mode));
    }

    // ---- #599: an exception propagating through a YIELDING finally must not be lost ----

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void UncaughtThrow_ThroughYieldingFinally_StillPropagates(ExecutionMode mode)
    {
        // The exact #599 repro: the try throws after a yield, and the (catch-less) finally yields.
        // The finally's suspension previously wiped the IL local holding the captured exception, so
        // the post-finally rethrow saw null and the throw was swallowed. The exception must survive
        // the suspension and surface to the consumer after the finally completes.
        var source = """
            function* g() { try { yield 1; throw "boom"; } finally { yield 2; } }
            const it = g();
            console.log(JSON.stringify(it.next()));
            console.log(JSON.stringify(it.next()));
            try { console.log(JSON.stringify(it.next())); } catch (e) { console.log("caught:" + e); }
            """;

        Assert.Equal(
            "{\"value\":1,\"done\":false}\n{\"value\":2,\"done\":false}\ncaught:boom\n",
            TestHarness.Run(source, mode));
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void RethrownFromInnerCatch_ThroughYieldingFinally_StillPropagates(ExecutionMode mode)
    {
        // #599's second shape: an inner try/catch (a sync segment of the outer try body) rethrows;
        // the captured exception must survive the outer yielding finally and propagate.
        var source = """
            function* g() {
              try { try { throw new Error("e1"); } catch (e) { throw new Error("e2"); } }
              finally { yield 7; }
            }
            const it = g();
            console.log(JSON.stringify(it.next()));
            try { it.next(); } catch (e) { console.log("caught:" + e.message); }
            """;

        Assert.Equal("{\"value\":7,\"done\":false}\ncaught:e2\n", TestHarness.Run(source, mode));
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void UncaughtThrow_ThroughNestedYieldingFinally_NotClobbered(ExecutionMode mode)
    {
        // A finally that yields and itself contains a (separate) try/finally that also yields. Each
        // construct gets its own caught-exception field, so the inner construct's persistence cannot
        // clobber the outer's captured exception — it must still rethrow after both finallys run.
        var source = """
            function* g() { try { throw "e"; } finally { try {} finally { yield 1; } } }
            const it = g();
            console.log(JSON.stringify(it.next()));
            try { it.next(); } catch (e) { console.log("caught:" + e); }
            """;

        Assert.Equal("{\"value\":1,\"done\":false}\ncaught:e\n", TestHarness.Run(source, mode));
    }

    // ---- #598: return/break/continue lexically inside a no-yield finally body ----
    // None of ret/br/Leave may exit a .NET finally region, so these constructs must take the
    // flag-based path even with no yield (the finally-side analog of #554's try/catch-body fix).

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void ReturnInsideNoYieldFinally_Completes(ExecutionMode mode)
    {
        // The exact #598 repro: a `return` inside a no-yield finally. The generator has a yield
        // elsewhere so it is a state machine; the try/finally itself crosses no yield.
        var source = """
            function* g() { yield 0; try {} finally { return 5; } }
            const it = g();
            console.log(JSON.stringify(it.next()));
            console.log(JSON.stringify(it.next()));
            console.log(JSON.stringify(it.next()));
            """;

        Assert.Equal(
            "{\"value\":0,\"done\":false}\n{\"value\":5,\"done\":true}\n{\"done\":true}\n",
            TestHarness.Run(source, mode));
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void BreakInsideNoYieldFinally_ExitsLoop(ExecutionMode mode)
    {
        // #598's break repro: a `break` inside a no-yield finally exits the enclosing loop.
        var source = """
            function* g() { yield 0; while (true) { try {} finally { break; } } yield 1; }
            for (const v of g()) console.log(v);
            """;

        Assert.Equal("0\n1\n", TestHarness.Run(source, mode));
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void ContinueInsideNoYieldFinally_SkipsRest(ExecutionMode mode)
    {
        var source = """
            function* g() {
              for (let i = 0; i < 3; i++) {
                try {} finally { if (i === 1) continue; console.log("body" + i); }
              }
              yield 9;
            }
            for (const v of g()) console.log("y" + v);
            """;

        Assert.Equal("body0\nbody2\ny9\n", TestHarness.Run(source, mode));
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void ReturnInNoYieldFinally_OverridesPendingException(ExecutionMode mode)
    {
        // The try throws but the finally returns: per JS semantics the abrupt return overrides the
        // exception (it is swallowed). The real-IL path could not express this at all (#598).
        var source = """
            function* g() { yield 0; try { throw "boom"; } finally { return 5; } }
            const it = g();
            console.log(JSON.stringify(it.next()));
            console.log(JSON.stringify(it.next()));
            """;

        Assert.Equal(
            "{\"value\":0,\"done\":false}\n{\"value\":5,\"done\":true}\n",
            TestHarness.Run(source, mode));
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void LabeledBreakInsideNoYieldFinally_ExitsOuterLoop(ExecutionMode mode)
    {
        var source = """
            function* g() {
              yield 0;
              outer: while (true) { while (true) { try {} finally { break outer; } } }
              yield 1;
            }
            for (const v of g()) console.log(v);
            """;

        Assert.Equal("0\n1\n", TestHarness.Run(source, mode));
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void ReturnInNestedNoYieldFinally_RunsOuterFinally(ExecutionMode mode)
    {
        // A return in an inner finally must still run the enclosing finally before completing.
        var source = """
            function* g() {
              yield 0;
              try { try {} finally { return 1; } } finally { console.log("outer-fin"); }
            }
            const it = g();
            console.log(JSON.stringify(it.next()));
            console.log(JSON.stringify(it.next()));
            """;

        Assert.Equal(
            "{\"value\":0,\"done\":false}\nouter-fin\n{\"value\":1,\"done\":true}\n",
            TestHarness.Run(source, mode));
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
    // #500 control-flow shapes: continue, throw-from-catch, return-from-catch, nested-finally break,
    // labeled break, break to a loop sitting between two trys, and a yielding finally on the break path.
    [InlineData("function* g() { for (let i=0;i<2;i++){ try { yield i; continue; } finally { console.log('f'); } } } for (const v of g()) {}")]
    [InlineData("function* g() { try { yield 1; throw 'a'; } catch (e) { throw 'b'; } finally { console.log('f'); } } try { for (const v of g()) {} } catch (e) {}")]
    [InlineData("function* g() { try { yield 1; throw 'a'; } catch (e) { return; } finally { console.log('f'); } } for (const v of g()) {}")]
    [InlineData("function* g() { while (true) { try { try { yield 1; break; } finally { console.log('a'); } } finally { console.log('b'); } } } for (const v of g()) {}")]
    [InlineData("function* g() { outer: for(let i=0;i<2;i++){ for(let j=0;j<2;j++){ try { yield j; break outer; } finally { console.log('f'); } } } } for (const v of g()) {}")]
    [InlineData("function* g() { try { for(let i=0;i<2;i++){ try { yield i; break; } finally { console.log('a'); } } } finally { console.log('b'); } } for (const v of g()) {}")]
    [InlineData("function* g() { while (true) { try { yield 1; break; } finally { yield 2; } } } for (const v of g()) {}")]
    // #554: return/break/continue inside a NO-yield try (real IL exception block) — must `Leave`, not
    // `ret`/`br`. The yield lives elsewhere in the body so it is still a generator state machine.
    [InlineData("function* g() { yield 0; try { return; } finally { console.log('f'); } } for (const v of g()) {}")]
    [InlineData("function* g() { yield 0; try { return 7; } finally { console.log('f'); } } for (const v of g()) {}")]
    [InlineData("function* g() { yield 0; while (true) { try { break; } finally { console.log('f'); } } } for (const v of g()) {}")]
    [InlineData("function* g() { for (let i=0;i<2;i++){ yield i; try { continue; } finally { console.log('f'); } } } for (const v of g()) {}")]
    [InlineData("function* g() { yield 0; try { return 3; } catch (e) {} } for (const v of g()) {}")]
    [InlineData("function* g() { yield 0; try { try { return 1; } finally { console.log('a'); } } finally { console.log('b'); } } for (const v of g()) {}")]
    // #554/#555: a no-yield (real IL) inner try nested in an outer yielding (flag-based) finally.
    [InlineData("function* g() { try { yield 1; try { return 5; } finally { console.log('x'); } } finally { yield 2; } } for (const v of g()) {}")]
    [InlineData("function* g() { while (true) { try { yield 1; try { break; } finally { console.log('x'); } } finally { yield 2; } } } for (const v of g()) {}")]
    // #555: a `return <value>` whose finally yields.
    [InlineData("function* g() { try { return 5; } finally { yield 9; } } for (const v of g()) {}")]
    // #599: an exception crossing a yielding finally — the captured exception is persisted to a field.
    [InlineData("function* g() { try { yield 1; throw 'boom'; } finally { yield 2; } } try { for (const v of g()) {} } catch (e) {}")]
    [InlineData("function* g() { try { try { throw 'e1'; } catch (e) { throw 'e2'; } } finally { yield 7; } } try { for (const v of g()) {} } catch (e) {}")]
    [InlineData("function* g() { try { throw 'e'; } finally { try {} finally { yield 1; } } } try { for (const v of g()) {} } catch (e) {}")]
    // #598: return/break/continue lexically inside a no-yield finally — must take the flag-based path
    // (none of ret/br/Leave may exit a real .NET finally region → LeaveOutOfFinally otherwise).
    [InlineData("function* g() { yield 0; try {} finally { return 5; } } for (const v of g()) {}")]
    [InlineData("function* g() { yield 0; try { throw 'x'; } finally { return 5; } } try { for (const v of g()) {} } catch (e) {}")]
    [InlineData("function* g() { yield 0; while (true) { try {} finally { break; } } yield 1; } for (const v of g()) {}")]
    [InlineData("function* g() { for (let i=0;i<2;i++){ yield i; try {} finally { continue; } } } for (const v of g()) {}")]
    [InlineData("function* g() { yield 0; outer: while(true){ while(true){ try {} finally { break outer; } } } } for (const v of g()) {}")]
    [InlineData("function* g() { yield 0; try { try {} finally { return 1; } } finally { console.log('o'); } } for (const v of g()) {}")]
    // #526: the external return()/throw() injection check is emitted at every yield resume, and the
    // yield* forwarding check at every yield* resume — both must verify across these shapes.
    [InlineData("function* inner(){ try { yield 1; } finally { console.log('x'); } } function* g(){ yield* inner(); } for (const v of g()) {}")]
    [InlineData("function* inner(){ try { yield 1; } finally {} } function* mid(){ try { yield* inner(); } finally {} } function* g(){ yield* mid(); } for (const v of g()) {}")]
    [InlineData("function* g(){ try { yield 1; } catch (e) { yield 2; } finally { yield 3; } } for (const v of g()) {}")]
    public void GeneratorTryFinallyWithYield_EmitsVerifiableIL(string source)
    {
        var errors = TestHarness.CompileAndVerifyOnly(source);
        Assert.Empty(errors);
    }
}
