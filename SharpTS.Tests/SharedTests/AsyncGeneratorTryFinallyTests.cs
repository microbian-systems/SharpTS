using SharpTS.Tests.Infrastructure;
using Xunit;

namespace SharpTS.Tests.SharedTests;

/// <summary>
/// Regression tests for #559: a compiled <em>async</em> generator with a <c>yield</c>/<c>await</c>
/// inside <c>try</c>/<c>catch</c>/<c>finally</c> previously mishandled non-local exits that cross the
/// protected region. <c>break</c>/<c>continue</c> leaving the try emitted invalid IL
/// (<c>InvalidProgramException</c> in <c>MoveNextAsync</c>), and <c>return</c> / a <c>throw</c> from a
/// catch skipped the enclosing <c>finally</c>. This is the async analog of #500 (plain generator); the
/// fix ports the same unified exit-scope + pending-action dispatch into
/// <c>AsyncGeneratorMoveNextEmitter</c> so every non-local exit runs the enclosing <c>finally</c>(s)
/// before transferring control. See <c>AsyncGeneratorMoveNextEmitter.Statements.TryCatch.cs</c>.
///
/// <para>
/// COMPILED-ONLY. The interpreter eagerly drains an async generator's body: its internal side effects
/// (a finally's <c>console.log</c>, a catch's logging) run before the consumer's <c>for await…of</c>
/// body observes the yielded values, and a throwing async generator drops the consumer's processing of
/// already-yielded values entirely. That ordering / value-delivery divergence is pre-existing and
/// independent of try/finally control flow — it affects any async generator with observable internal
/// effects — and is tracked separately (#564 ordering, #566 manual next() rejection). These tests
/// therefore assert the compiled path,
/// where #559 lives and where output matches Node. The IL-verification cases at the bottom — emitted
/// IL must verify — are the heart of the fix.
/// </para>
/// </summary>
public class AsyncGeneratorTryFinallyTests
{
    [Theory]
    [MemberData(nameof(ExecutionModes.CompiledOnly), MemberType = typeof(ExecutionModes))]
    public void BreakOutOfTryFinally_RunsFinallyBeforeBreaking(ExecutionMode mode)
    {
        // The exact #559 repro: break leaving the try must run the finally first (was invalid IL).
        var source = """
            async function* g() {
              while (true) {
                try { yield 1; break; } finally { console.log("FIN"); }
              }
            }
            async function main() { for await (const v of g()) console.log("v" + v); }
            main();
            """;

        Assert.Equal("v1\nFIN\n", TestHarness.Run(source, mode));
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.CompiledOnly), MemberType = typeof(ExecutionModes))]
    public void ContinueOutOfTryFinally_RunsFinallyThatIteration(ExecutionMode mode)
    {
        // `continue` from inside the try must run the finally before the next iteration, and the code
        // after the continue must be skipped on that iteration only (was invalid IL).
        var source = """
            async function* g() {
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
            async function main() { for await (const v of g()) console.log("got" + v); }
            main();
            """;

        Assert.Equal("got0\nafter0\nfin0\ngot1\nfin1\ngot2\nafter2\nfin2\n", TestHarness.Run(source, mode));
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.CompiledOnly), MemberType = typeof(ExecutionModes))]
    public void ThrowFromCatch_RunsFinallyThenPropagates(ExecutionMode mode)
    {
        // The exact #559 repro: a throw inside the catch must still run the finally before the
        // exception propagates out of the generator to the consumer (finally was skipped).
        var source = """
            async function* g() {
              try { yield 1; throw "a"; } catch (e) { throw "b"; } finally { console.log("FIN"); }
            }
            async function main() {
              try { for await (const v of g()) console.log("v" + v); } catch (e) { console.log("outer " + e); }
            }
            main();
            """;

        Assert.Equal("v1\nFIN\nouter b\n", TestHarness.Run(source, mode));
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.CompiledOnly), MemberType = typeof(ExecutionModes))]
    public void ReturnFromCatch_RunsFinallyBeforeCompleting(ExecutionMode mode)
    {
        // A `return` from the catch body must run the finally; the yield after the try must not run.
        var source = """
            async function* g() {
              try { yield 1; throw "x"; } catch (e) { return; } finally { console.log("FIN"); }
              yield 99;
            }
            async function main() { for await (const v of g()) console.log("v" + v); }
            main();
            """;

        Assert.Equal("v1\nFIN\n", TestHarness.Run(source, mode));
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.CompiledOnly), MemberType = typeof(ExecutionModes))]
    public void ReturnInsideTryFinally_RunsFinallyBeforeCompleting(ExecutionMode mode)
    {
        // `return` inside the try must run the finally before the generator completes; the statement
        // after the return must not execute (was invalid IL — the return's `ret` sat in a mini block).
        var source = """
            async function* g() {
              try {
                yield 1;
                return;
                yield 99;
              } finally {
                console.log("fin");
              }
            }
            async function main() { for await (const v of g()) console.log("v" + v); }
            main();
            """;

        Assert.Equal("v1\nfin\n", TestHarness.Run(source, mode));
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.CompiledOnly), MemberType = typeof(ExecutionModes))]
    public void ReturnInsideNestedTryFinally_RunsAllEnclosingFinallys(ExecutionMode mode)
    {
        var source = """
            async function* g() {
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
            async function main() { for await (const v of g()) console.log("v" + v); }
            main();
            """;

        Assert.Equal("v1\ninner\nouter\n", TestHarness.Run(source, mode));
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.CompiledOnly), MemberType = typeof(ExecutionModes))]
    public void BreakThroughNestedFinallys_RunsInnerThenOuter(ExecutionMode mode)
    {
        // A break that leaves two enclosing trys runs both finallys, innermost first.
        var source = """
            async function* g() {
              while (true) {
                try {
                  try { yield 1; break; } finally { console.log("inner"); }
                } finally { console.log("outer"); }
              }
            }
            async function main() { for await (const v of g()) console.log("v" + v); }
            main();
            """;

        Assert.Equal("v1\ninner\nouter\n", TestHarness.Run(source, mode));
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.CompiledOnly), MemberType = typeof(ExecutionModes))]
    public void LabeledBreakToOuterLoop_RunsInterveningFinally(ExecutionMode mode)
    {
        // A labeled break targeting the outer loop runs the finally of the inner loop's try.
        var source = """
            async function* g() {
              outer: for (let i = 0; i < 3; i++) {
                for (let j = 0; j < 3; j++) {
                  try { yield i * 10 + j; if (j === 1) break outer; } finally { console.log("fin" + i + j); }
                }
              }
            }
            async function main() { for await (const v of g()) console.log("v" + v); }
            main();
            """;

        Assert.Equal("v0\nfin00\nv1\nfin01\n", TestHarness.Run(source, mode));
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.CompiledOnly), MemberType = typeof(ExecutionModes))]
    public void LabeledContinueToOuterLoop_RunsInterveningFinally(ExecutionMode mode)
    {
        // The labeled-`continue` sibling of LabeledBreakToOuterLoop (#586/#589). `continue outer` must
        // run the inner loop's finally and then advance the *outer* loop — skipping the rest of the
        // inner loop — rather than continuing the inner loop. The same EnterLoop pending-label adoption
        // (#586) that resolves the labeled break drives this path; without behavioral coverage the
        // continue direction could regress silently (the labeled-break test alone would not catch it).
        var source = """
            async function* g() {
              outer: for (let i = 0; i < 2; i++) {
                for (let j = 0; j < 3; j++) {
                  try { yield i * 10 + j; if (j === 0) continue outer; } finally { console.log("fin" + i + j); }
                }
              }
            }
            async function main() { for await (const v of g()) console.log("v" + v); }
            main();
            """;

        Assert.Equal("v0\nfin00\nv10\nfin10\n", TestHarness.Run(source, mode));
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.CompiledOnly), MemberType = typeof(ExecutionModes))]
    public void BreakToLoopBetweenTwoTrys_RunsOnlyInnerFinally(ExecutionMode mode)
    {
        // The break targets a loop that sits *between* two trys: only the finally inside that loop
        // runs at the break; the outer finally runs once, later, when the generator completes.
        var source = """
            async function* g() {
              try {
                for (let i = 0; i < 3; i++) {
                  try { yield i; if (i === 1) break; } finally { console.log("inner" + i); }
                }
                console.log("after-loop");
              } finally { console.log("OUTER"); }
            }
            async function main() { for await (const v of g()) console.log("v" + v); }
            main();
            """;

        Assert.Equal("v0\ninner0\nv1\ninner1\nafter-loop\nOUTER\n", TestHarness.Run(source, mode));
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.CompiledOnly), MemberType = typeof(ExecutionModes))]
    public void BreakWithYieldingFinally_DrivesFinallyThenBreaks(ExecutionMode mode)
    {
        // The finally that runs on the break path itself yields; the break completes only after the
        // finally's yields are driven, then control resumes after the loop.
        var source = """
            async function* g() {
              while (true) {
                try { yield 1; break; } finally { yield 2; }
              }
              yield 3;
            }
            async function main() { for await (const v of g()) console.log("v" + v); }
            main();
            """;

        Assert.Equal("v1\nv2\nv3\n", TestHarness.Run(source, mode));
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.CompiledOnly), MemberType = typeof(ExecutionModes))]
    public void ReturnInTry_WithYieldingFinally_CompletesAfterFinallyYields(ExecutionMode mode)
    {
        // The finally itself yields, suspending MoveNextAsync between the `return` and the completion.
        // The pending-return state must survive that suspension (it lives in a field, not a local), so
        // the generator completes after the finally rather than running `yield 99`.
        var source = """
            async function* g() {
              try {
                yield 1;
                return;
              } finally {
                yield 2;
              }
              yield 99;
            }
            async function main() { for await (const v of g()) console.log("v" + v); }
            main();
            """;

        Assert.Equal("v1\nv2\n", TestHarness.Run(source, mode));
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.CompiledOnly), MemberType = typeof(ExecutionModes))]
    public void ThrowFromFinally_RunsEnclosingFinallyThenPropagates(ExecutionMode mode)
    {
        // A throw raised inside a finally body must still run the enclosing finally before it
        // propagates to the consumer.
        var source = """
            async function* g() {
              try {
                try { yield 1; } finally { throw "boom"; }
              } finally { console.log("OUTER"); }
            }
            async function main() {
              try { for await (const v of g()) console.log("v" + v); } catch (e) { console.log("caught " + e); }
            }
            main();
            """;

        Assert.Equal("v1\nOUTER\ncaught boom\n", TestHarness.Run(source, mode));
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.CompiledOnly), MemberType = typeof(ExecutionModes))]
    public void BreakOutOfInnerCatchlessTry_RunsOuterFinally(ExecutionMode mode)
    {
        // The break leaves an inner try/catch that has no finally, nested in an outer try-with-
        // finally; the outer finally must still run on the way out.
        var source = """
            async function* g() {
              while (true) {
                try {
                  try { yield 1; break; } catch (e) {}
                } finally { console.log("OUTERFIN"); }
              }
            }
            async function main() { for await (const v of g()) console.log("v" + v); }
            main();
            """;

        Assert.Equal("v1\nOUTERFIN\n", TestHarness.Run(source, mode));
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.CompiledOnly), MemberType = typeof(ExecutionModes))]
    public void YieldStarInTryFinally_DelegatesThenRunsFinally(ExecutionMode mode)
    {
        var source = """
            async function* inner() { yield 2; yield 3; }
            async function* g() {
              try {
                yield 1;
                yield* inner();
                yield 4;
              } finally {
                console.log("fin");
              }
            }
            async function main() { for await (const v of g()) console.log("v" + v); }
            main();
            """;

        Assert.Equal("v1\nv2\nv3\nv4\nfin\n", TestHarness.Run(source, mode));
    }

    // ---- await inside the protected region (async-generator-specific) ----

    [Theory]
    [MemberData(nameof(ExecutionModes.CompiledOnly), MemberType = typeof(ExecutionModes))]
    public void AwaitThenReturnInTryFinally_RunsFinally(ExecutionMode mode)
    {
        // An await suspension precedes the return inside the try; the finally must still run.
        var source = """
            async function* g() {
              try { await Promise.resolve(0); yield 1; return; } finally { console.log("FIN"); }
            }
            async function main() { for await (const v of g()) console.log("v" + v); }
            main();
            """;

        Assert.Equal("v1\nFIN\n", TestHarness.Run(source, mode));
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.CompiledOnly), MemberType = typeof(ExecutionModes))]
    public void AwaitThenBreakOutOfTryFinally_RunsFinally(ExecutionMode mode)
    {
        var source = """
            async function* g() {
              while (true) {
                try { const x = await Promise.resolve(5); yield x; break; } finally { console.log("FIN"); }
              }
            }
            async function main() { for await (const v of g()) console.log("v" + v); }
            main();
            """;

        Assert.Equal("v5\nFIN\n", TestHarness.Run(source, mode));
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.CompiledOnly), MemberType = typeof(ExecutionModes))]
    public void AwaitThenContinueOutOfTryFinally_RunsFinallyEachIteration(ExecutionMode mode)
    {
        var source = """
            async function* g() {
              for (let i = 0; i < 2; i++) {
                try { await Promise.resolve(0); yield i; continue; } finally { console.log("fin" + i); }
              }
            }
            async function main() { for await (const v of g()) console.log("v" + v); }
            main();
            """;

        Assert.Equal("v0\nfin0\nv1\nfin1\n", TestHarness.Run(source, mode));
    }

    // ---- IL-verification guards (the heart of #559: emitted IL must verify) ----

    [Theory]
    [InlineData("async function* g() { try { yield 1; yield 2; } finally { console.log('f'); } } async function main(){ for await (const v of g()) {} } main();")]
    [InlineData("async function* g() { try { yield 1; throw 'x'; } catch (e) { console.log(e); } } async function main(){ for await (const v of g()) {} } main();")]
    [InlineData("async function* g() { try { yield 1; } catch (e) {} finally { console.log('f'); } yield 2; } async function main(){ for await (const v of g()) {} } main();")]
    [InlineData("async function* g() { for (let i=0;i<2;i++){ try { yield i; } finally { console.log(i); } } } async function main(){ for await (const v of g()) {} } main();")]
    [InlineData("async function* g() { try { try { yield 1; } finally { console.log('a'); } } finally { console.log('b'); } } async function main(){ for await (const v of g()) {} } main();")]
    [InlineData("async function* g() { try { yield 1; return; } finally { console.log('f'); } } async function main(){ for await (const v of g()) {} } main();")]
    [InlineData("async function* g() { try { yield 1; } finally { yield 2; } } async function main(){ for await (const v of g()) {} } main();")]
    [InlineData("async function* g() { while (true) { try { yield 1; break; } finally { console.log('f'); } } } async function main(){ for await (const v of g()) {} } main();")]
    [InlineData("async function* inner(){ yield 2; } async function* g() { try { yield 1; yield* inner(); } finally { console.log('f'); } } async function main(){ for await (const v of g()) {} } main();")]
    // #559 control-flow shapes: continue, throw-from-catch, return-from-catch, nested-finally break,
    // labeled break, labeled continue (#586/#589), break to a loop sitting between two trys, and a
    // yielding finally on the break path.
    [InlineData("async function* g() { for (let i=0;i<2;i++){ try { yield i; continue; } finally { console.log('f'); } } } async function main(){ for await (const v of g()) {} } main();")]
    [InlineData("async function* g() { try { yield 1; throw 'a'; } catch (e) { throw 'b'; } finally { console.log('f'); } } async function main(){ try { for await (const v of g()) {} } catch (e) {} } main();")]
    [InlineData("async function* g() { try { yield 1; throw 'a'; } catch (e) { return; } finally { console.log('f'); } } async function main(){ for await (const v of g()) {} } main();")]
    [InlineData("async function* g() { while (true) { try { try { yield 1; break; } finally { console.log('a'); } } finally { console.log('b'); } } } async function main(){ for await (const v of g()) {} } main();")]
    [InlineData("async function* g() { outer: for(let i=0;i<2;i++){ for(let j=0;j<2;j++){ try { yield j; break outer; } finally { console.log('f'); } } } } async function main(){ for await (const v of g()) {} } main();")]
    [InlineData("async function* g() { outer: for(let i=0;i<2;i++){ for(let j=0;j<2;j++){ try { yield j; continue outer; } finally { console.log('f'); } } } } async function main(){ for await (const v of g()) {} } main();")]
    [InlineData("async function* g() { try { for(let i=0;i<2;i++){ try { yield i; break; } finally { console.log('a'); } } } finally { console.log('b'); } } async function main(){ for await (const v of g()) {} } main();")]
    [InlineData("async function* g() { while (true) { try { yield 1; break; } finally { yield 2; } } } async function main(){ for await (const v of g()) {} } main();")]
    // await suspensions crossing the protected region alongside the non-local exits.
    [InlineData("async function* g() { try { await Promise.resolve(0); yield 1; return; } finally { console.log('f'); } } async function main(){ for await (const v of g()) {} } main();")]
    [InlineData("async function* g() { while (true) { try { await Promise.resolve(0); yield 1; break; } finally { console.log('f'); } } } async function main(){ for await (const v of g()) {} } main();")]
    public void AsyncGeneratorTryFinallyWithSuspension_EmitsVerifiableIL(string source)
    {
        var errors = TestHarness.CompileAndVerifyOnly(source);
        Assert.Empty(errors);
    }
}
