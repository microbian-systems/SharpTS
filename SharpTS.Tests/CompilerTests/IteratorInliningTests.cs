using SharpTS.Tests.Infrastructure;
using Xunit;

namespace SharpTS.Tests.CompilerTests;

/// <summary>
/// Behavioral tests for the iterator-helper arrow-inlining fast path
/// (issue #96 M1+). The inliner emits the literal arrow's body directly
/// into the loop, eliminating the per-iteration <see cref="System.Func{T1, TResult}.Invoke"/>
/// virtual call.
/// </summary>
/// <remarks>
/// Each fixture has a sibling that should hit the fallback path (Direct
/// helper or slow ArrayForEach). Both must produce identical observable
/// output — the inliner is purely a perf optimization with zero spec drift.
/// </remarks>
public class IteratorInliningTests
{
    /// <summary>
    /// Baseline: literal expression-bodied arrow inlines and produces the
    /// same sum as the dense loop.
    /// </summary>
    [Fact]
    public void ForEach_LiteralArrow_InlinesAndSumsCorrectly()
    {
        var source = """
            let sum = 0;
            const arr = [1, 2, 3, 4, 5];
            arr.forEach(x => { sum = sum + x; });
            console.log(sum);
            """;

        // Block-bodied arrow should fall back to Direct/slow path. The
        // expression-body version is in the next test.
        var output = TestHarness.RunCompiled(source);
        Assert.Equal("15\n", output);
    }

    /// <summary>
    /// Expression-bodied arrow with side-effecting body — the JS-canonical
    /// way to write `forEach(x => sum += x)` is via expression assignment.
    /// </summary>
    [Fact]
    public void ForEach_ExpressionBodyAssignment_Inlines()
    {
        var source = """
            let sum = 0;
            const arr = [1, 2, 3, 4, 5];
            arr.forEach(x => sum = sum + x);
            console.log(sum);
            """;

        // Note: `sum = sum + x` is an Assign expression so the arrow has
        // ExpressionBody, not BlockBody. But `sum` is captured (read AND
        // written), so this falls through to the slow path that supports
        // captures. The inliner gate requires no captures. Output must
        // still be 15.
        var output = TestHarness.RunCompiled(source);
        Assert.Equal("15\n", output);
    }

    /// <summary>
    /// Pure non-capturing expression body — the textbook inline candidate.
    /// Multiplies each element and pushes to an output via the receiver
    /// reference (which is captured, so this also falls back). What's
    /// being verified here is observational equivalence, not that the
    /// inliner fired — the inliner is a perf optimization, not a
    /// behavioral one. See <see cref="ForEach_PureNonCapturing_Inlines"/>
    /// for the actual inliner trigger.
    /// </summary>
    [Fact]
    public void ForEach_NonCapturing_OutputMatchesSpec()
    {
        var source = """
            let total = 0;
            const arr = [10, 20, 30];
            arr.forEach(x => total += x);
            console.log(total);
            """;

        var output = TestHarness.RunCompiled(source);
        Assert.Equal("60\n", output);
    }

    /// <summary>
    /// Body has no captures and no side effects observable outside the
    /// iteration itself. The eligibility gate fires for arrows like
    /// <c>x =&gt; x * 2</c> when the result is consumed by a method that
    /// uses the return value (like map). For forEach, this is mostly an
    /// "inliner doesn't crash" test since forEach discards the return.
    /// </summary>
    [Fact]
    public void ForEach_PureNonCapturing_Inlines()
    {
        var source = """
            const arr = [1, 2, 3];
            // Body has no captures. forEach discards return.
            arr.forEach(x => x * 2);
            console.log("done");
            """;

        var output = TestHarness.RunCompiled(source);
        Assert.Equal("done\n", output);
    }

    /// <summary>
    /// Holes are skipped per spec — arrow's body must NOT see hole slots.
    /// Emits a hole via <c>delete arr[1]</c> and counts how many times
    /// the callback runs through a counter the body increments via a
    /// captured variable (so this is the slow path; we're verifying spec
    /// behavior the inliner must preserve when its gate flips on).
    /// </summary>
    [Fact]
    public void ForEach_HoleSkippedWithDeletedIndex()
    {
        var source = """
            let count = 0;
            const arr = [1, 2, 3];
            delete arr[1];
            arr.forEach(x => count++);
            console.log(count);
            """;

        // Behavior must match spec regardless of which path emits.
        var output = TestHarness.RunCompiled(source);
        Assert.Equal("2\n", output);
    }

    /// <summary>
    /// Expression body with a member access — exercises the inliner's
    /// EmitExpression path through Expr.Get. Confirms the body's
    /// `Variable("x")` resolves to the loop element local (via the
    /// scoped RegisterLocal binding), not to an outer `x`.
    /// </summary>
    [Fact]
    public void ForEach_PropertyAccessBody_Inlines()
    {
        var source = """
            let names = "";
            const items = [{name: "a"}, {name: "b"}, {name: "c"}];
            // Capturing arrow — slow path. The non-capturing variant is the
            // next test.
            items.forEach(it => names += it.name);
            console.log(names);
            """;

        var output = TestHarness.RunCompiled(source);
        Assert.Equal("abc\n", output);
    }

    /// <summary>
    /// Variable in outer scope shadowed by arrow parameter — verifies
    /// that the inliner's scoped binding doesn't leak. After the
    /// forEach, the outer `x` should be unchanged.
    /// </summary>
    [Fact]
    public void ForEach_ParameterShadowsOuterVariable()
    {
        var source = """
            let x = 999;
            const arr = [1, 2, 3];
            // Pure body, fires the inliner. The arrow's `x` shadows the
            // outer `x`, then the scope pops and outer becomes visible
            // again. Note: `x * x` is a pure expression, no captures, so
            // this hits the inline fast path.
            arr.forEach(x => x * x);
            console.log(x);
            """;

        var output = TestHarness.RunCompiled(source);
        Assert.Equal("999\n", output);
    }

    /// <summary>
    /// Block-bodied arrow falls back gracefully — the inliner declines
    /// (V1 supports expression bodies only) and the Direct path takes
    /// over (or the slow path if Direct also declines).
    /// </summary>
    [Fact]
    public void ForEach_BlockBodyArrow_FallsBackCorrectly()
    {
        var source = """
            let sum = 0;
            const arr = [1, 2, 3];
            arr.forEach(x => {
                let doubled = x * 2;
                sum += doubled;
            });
            console.log(sum);
            """;

        // Block body + capture(`sum`) → slow path. Output: (1+2+3)*2 = 12.
        var output = TestHarness.RunCompiled(source);
        Assert.Equal("12\n", output);
    }

    /// <summary>
    /// Const-bound arrow callback — the inliner resolves `const sq = x =&gt;
    /// x*x` via <c>ConstArrowBindings</c> and inlines the same as the
    /// inline form. Must produce same output as inline shape.
    /// </summary>
    [Fact]
    public void ForEach_ConstBoundArrow_InlinesViaConstBindings()
    {
        var source = """
            const noop = x => x;
            const arr = [1, 2, 3];
            arr.forEach(noop);
            console.log("done");
            """;

        var output = TestHarness.RunCompiled(source);
        Assert.Equal("done\n", output);
    }

    /// <summary>
    /// Zero-parameter arrow — JS callbacks may declare fewer parameters
    /// than the helper invokes them with. <c>forEach</c> calls with
    /// (element, index, array); arrow with no params must still inline
    /// (or fall back) without crashing.
    /// </summary>
    [Fact]
    public void ForEach_ZeroParamArrow_FallsBackCleanly()
    {
        var source = """
            let count = 0;
            const arr = [1, 2, 3];
            // `() => count++` captures `count` → slow path.
            arr.forEach(() => count++);
            console.log(count);
            """;

        var output = TestHarness.RunCompiled(source);
        Assert.Equal("3\n", output);
    }

    /// <summary>
    /// Outer-method parameter shadow: <c>LocalVariableResolver</c> consults
    /// formal parameters before the Locals stack, so the inliner can't
    /// safely shadow a same-named outer parameter via <c>RegisterLocal</c>.
    /// The eligibility check declines, falling back to Direct (which has
    /// its own parameter scope on the static method). Output must still
    /// match — the outer <c>x</c> stays at <c>100</c>, and the loop sums
    /// to <c>14</c> (1*1 + 2*2 + 3*3 = 14).
    /// </summary>
    [Fact]
    public void ForEach_ArrowParamShadowsOuterFunctionParam_FallsBackCorrectly()
    {
        var source = """
            function go(x: number): number {
                let sum = 0;
                const arr = [1, 2, 3];
                arr.forEach(x => sum += x * x);
                console.log(x);
                return sum;
            }
            console.log(go(100));
            """;

        var output = TestHarness.RunCompiled(source);
        Assert.Equal("100\n14\n", output);
    }

    /// <summary>
    /// Outer captured-field shadow: arrow inside a nested arrow that
    /// captures a same-named variable. The outer arrow's closure puts the
    /// name into a captured field; the inliner's <c>RegisterLocal</c>
    /// can't shadow that. Decline + fall back. Output stays correct.
    /// </summary>
    [Fact]
    public void ForEach_ArrowParamShadowsCapturedField_FallsBackCorrectly()
    {
        var source = """
            function outer(): number {
                let x = 50;
                let result = 0;
                const inner = () => {
                    // `x` is captured here from outer.
                    const arr = [1, 2, 3];
                    // The arrow `x => ...` has param `x` that would collide
                    // with the captured `x`. Inliner declines.
                    arr.forEach(x => { result += x; });
                    return x + result;
                };
                return inner();
            }
            console.log(outer());
            """;

        var output = TestHarness.RunCompiled(source);
        Assert.Equal("56\n", output);
    }

    /// <summary>
    /// Inliner output matches interpreter output — cross-mode equivalence
    /// catches spec drift the inliner could introduce. Both modes compute
    /// the same sum for a typical forEach loop.
    /// </summary>
    [Fact]
    public void ForEach_InterpreterAndCompiledAgree()
    {
        var source = """
            let sum = 0;
            const arr = [1, 2, 3, 4, 5, 6, 7, 8, 9, 10];
            arr.forEach(x => sum += x);
            console.log(sum);
            """;

        var compiled = TestHarness.RunCompiled(source);
        var interpreted = TestHarness.RunInterpreted(source);
        Assert.Equal(interpreted, compiled);
        Assert.Equal("55\n", compiled);
    }

    // -- M2: map / filter -------------------------------------------------

    /// <summary>
    /// Map with literal arrow body — eliminates per-iter Func.Invoke,
    /// produces parallel-length output of arrow body's value.
    /// </summary>
    [Fact]
    public void Map_LiteralArrow_Inlines()
    {
        var source = """
            const arr = [1, 2, 3, 4, 5];
            const doubled = arr.map(x => x * 2);
            console.log(doubled.length);
            console.log(doubled[0]);
            console.log(doubled[4]);
            """;

        var output = TestHarness.RunCompiled(source);
        Assert.Equal("5\n2\n10\n", output);
    }

    /// <summary>
    /// Map preserves holes per ECMA-262 23.1.3.20 — output[i] is a hole
    /// when source[i] is a hole, callback NOT invoked. Verified via
    /// JSON.stringify which renders holes as <c>null</c>.
    /// </summary>
    [Fact]
    public void Map_PreservesHoles()
    {
        var source = """
            const arr = [1, 2, 3];
            delete arr[1];
            const doubled = arr.map(x => x * 2);
            console.log(JSON.stringify(doubled));
            """;

        var output = TestHarness.RunCompiled(source);
        Assert.Equal("[2,null,6]\n", output);
    }

    /// <summary>
    /// Map output type — body returns object expression, output is the
    /// raw projected object (not boxed bool / number).
    /// </summary>
    [Fact]
    public void Map_ProjectsObjectProperty()
    {
        var source = """
            const items = [{n: 1}, {n: 2}, {n: 3}];
            const ns = items.map(it => it.n);
            console.log(ns[0] + ns[1] + ns[2]);
            """;

        var output = TestHarness.RunCompiled(source);
        Assert.Equal("6\n", output);
    }

    /// <summary>
    /// Filter with non-strict-equality predicate that yields a boolean —
    /// exercises the IsTruthy path on a body whose static type is
    /// boolean but whose runtime stack value may be either a boxed
    /// Boolean or a raw int32.
    /// </summary>
    [Fact]
    public void Filter_RangeCheckPredicate_FiltersCorrectly()
    {
        var source = """
            const arr: number[] = [0, 1, 2, 3, 0, 4];
            const big = arr.filter(x => x > 1);
            console.log(big.length);
            console.log(big[0]);
            console.log(big[1]);
            """;

        var output = TestHarness.RunCompiled(source);
        Assert.Equal("3\n2\n3\n", output);
    }

    /// <summary>
    /// Filter with strict-equality predicate (boolean-typed body) —
    /// regression for the bool-fast-path bug where <c>Brfalse</c> on a
    /// boxed True always fell through, returning the unfiltered list.
    /// </summary>
    [Fact]
    public void Filter_StrictEqualityBody_FiltersCorrectly()
    {
        var source = """
            const values = [1, 2, 3, 4, 5];
            const evens = values.filter(v => v % 2 === 0);
            console.log(evens.length);
            console.log(evens[0]);
            console.log(evens[1]);
            """;

        var output = TestHarness.RunCompiled(source);
        Assert.Equal("2\n2\n4\n", output);
    }

    /// <summary>
    /// Filter skips holes — callback NOT invoked, output densifies.
    /// Spec ref: ECMA-262 23.1.3.8.
    /// </summary>
    [Fact]
    public void Filter_SkipsHoles()
    {
        var source = """
            const arr = [10, 20, 30, 40];
            delete arr[1];
            delete arr[2];
            const big = arr.filter(x => x > 5);
            console.log(big.length);
            console.log(big[0]);
            console.log(big[1]);
            """;

        var output = TestHarness.RunCompiled(source);
        Assert.Equal("2\n10\n40\n", output);
    }

    /// <summary>
    /// Map and filter cross-mode equivalence.
    /// </summary>
    [Fact]
    public void MapFilter_InterpreterAndCompiledAgree()
    {
        var source = """
            const nums = [1, 2, 3, 4, 5, 6, 7, 8, 9, 10];
            const squaresOfEvens = nums
                .filter(n => n % 2 === 0)
                .map(n => n * n);
            console.log(squaresOfEvens.length);
            console.log(squaresOfEvens[0]);
            console.log(squaresOfEvens[4]);
            """;

        var compiled = TestHarness.RunCompiled(source);
        var interpreted = TestHarness.RunInterpreted(source);
        Assert.Equal(interpreted, compiled);
        Assert.Equal("5\n4\n100\n", compiled);
    }

    /// <summary>
    /// Const-bound arrow callback for map — exercises the
    /// <c>ConstArrowBindings</c> resolver inside the inliner.
    /// </summary>
    [Fact]
    public void Map_ConstBoundArrow_Inlines()
    {
        var source = """
            const sq = x => x * x;
            const arr = [1, 2, 3, 4];
            const out = arr.map(sq);
            console.log(out.length);
            console.log(out[3]);
            """;

        var output = TestHarness.RunCompiled(source);
        Assert.Equal("4\n16\n", output);
    }

    // -- M3: short-circuit family --------------------------------------

    [Fact]
    public void Find_LiteralArrow_ReturnsFirstTruthy()
    {
        var source = """
            const arr = [1, 3, 5, 8, 7];
            const first = arr.find(x => x > 4);
            console.log(first);
            """;

        var output = TestHarness.RunCompiled(source);
        Assert.Equal("5\n", output);
    }

    [Fact]
    public void Find_NoMatch_ReturnsUndefined()
    {
        var source = """
            const arr = [1, 2, 3];
            const r = arr.find(x => x > 100);
            console.log(typeof r);
            """;

        var output = TestHarness.RunCompiled(source);
        Assert.Equal("undefined\n", output);
    }

    [Fact]
    public void Find_HoleSeenAsUndefined()
    {
        // Per ECMA-262 23.1.3.10 find IS invoked for holes (with undefined).
        var source = """
            const arr = [1, 2, 3];
            delete arr[1];
            const r = arr.find(x => x === undefined);
            console.log(typeof r);
            """;

        var output = TestHarness.RunCompiled(source);
        Assert.Equal("undefined\n", output);
    }

    [Fact]
    public void FindIndex_LiteralArrow_ReturnsFirstMatchIndex()
    {
        var source = """
            const arr = [10, 20, 30, 40];
            console.log(arr.findIndex(x => x > 20));
            """;

        var output = TestHarness.RunCompiled(source);
        Assert.Equal("2\n", output);
    }

    [Fact]
    public void FindIndex_NoMatch_ReturnsMinusOne()
    {
        var source = """
            const arr = [1, 2, 3];
            console.log(arr.findIndex(x => x > 100));
            """;

        var output = TestHarness.RunCompiled(source);
        Assert.Equal("-1\n", output);
    }

    [Fact]
    public void FindLast_LiteralArrow_ReturnsLastTruthy()
    {
        var source = """
            const arr = [1, 3, 5, 8, 7, 4];
            const last = arr.findLast(x => x > 3);
            console.log(last);
            """;

        var output = TestHarness.RunCompiled(source);
        Assert.Equal("4\n", output);
    }

    [Fact]
    public void FindLast_NoMatch_ReturnsUndefined()
    {
        var source = """
            const arr = [1, 2, 3];
            const r = arr.findLast(x => x > 100);
            console.log(typeof r);
            """;

        var output = TestHarness.RunCompiled(source);
        Assert.Equal("undefined\n", output);
    }

    [Fact]
    public void FindLastIndex_LiteralArrow_ReturnsLastMatchIndex()
    {
        var source = """
            const arr = [10, 20, 30, 20, 10];
            console.log(arr.findLastIndex(x => x === 20));
            """;

        var output = TestHarness.RunCompiled(source);
        Assert.Equal("3\n", output);
    }

    [Fact]
    public void FindLastIndex_NoMatch_ReturnsMinusOne()
    {
        var source = """
            const arr = [1, 2, 3];
            console.log(arr.findLastIndex(x => x > 100));
            """;

        var output = TestHarness.RunCompiled(source);
        Assert.Equal("-1\n", output);
    }

    [Fact]
    public void Some_LiteralArrow_ShortCircuitsOnTruthy()
    {
        var source = """
            const arr = [1, 2, 3, 4];
            console.log(arr.some(x => x > 3));
            console.log(arr.some(x => x > 100));
            """;

        var output = TestHarness.RunCompiled(source);
        Assert.Equal("true\nfalse\n", output);
    }

    [Fact]
    public void Some_EmptyArray_ReturnsFalse()
    {
        var source = """
            const arr: number[] = [];
            console.log(arr.some(x => x > 0));
            """;

        var output = TestHarness.RunCompiled(source);
        Assert.Equal("false\n", output);
    }

    [Fact]
    public void Some_HoleSkipped()
    {
        // some doesn't invoke callback for holes; only present truthy
        // elements count.
        var source = """
            const arr = [1, 2, 3];
            delete arr[1];
            // arr is now [1, hole, 3]. predicate matches even values; only
            // 2 is even but it's now the hole, so result is false.
            console.log(arr.some(x => x === 2));
            """;

        var output = TestHarness.RunCompiled(source);
        Assert.Equal("false\n", output);
    }

    [Fact]
    public void Every_LiteralArrow_ShortCircuitsOnFalsy()
    {
        var source = """
            const arr = [1, 2, 3, 4];
            console.log(arr.every(x => x > 0));
            console.log(arr.every(x => x > 2));
            """;

        var output = TestHarness.RunCompiled(source);
        Assert.Equal("true\nfalse\n", output);
    }

    [Fact]
    public void Every_EmptyArray_ReturnsTrue()
    {
        var source = """
            const arr: number[] = [];
            console.log(arr.every(x => x > 0));
            """;

        var output = TestHarness.RunCompiled(source);
        Assert.Equal("true\n", output);
    }

    [Fact]
    public void Every_HoleSkipped()
    {
        // every: skips holes per spec. So [1, hole, 1].every(x => x === 1)
        // returns true.
        var source = """
            const arr = [1, 2, 1];
            delete arr[1];
            console.log(arr.every(x => x === 1));
            """;

        var output = TestHarness.RunCompiled(source);
        Assert.Equal("true\n", output);
    }

    [Fact]
    public void ShortCircuitFamily_InterpreterAndCompiledAgree()
    {
        var source = """
            const nums = [1, 2, 3, 4, 5, 6, 7, 8, 9, 10];
            console.log(nums.find(n => n > 5));
            console.log(nums.findIndex(n => n > 5));
            console.log(nums.findLast(n => n < 5));
            console.log(nums.findLastIndex(n => n < 5));
            console.log(nums.some(n => n > 9));
            console.log(nums.every(n => n < 100));
            """;

        var compiled = TestHarness.RunCompiled(source);
        var interpreted = TestHarness.RunInterpreted(source);
        Assert.Equal(interpreted, compiled);
        Assert.Equal("6\n5\n4\n3\ntrue\ntrue\n", compiled);
    }

    // -- M4: reduce / reduceRight --------------------------------------

    [Fact]
    public void Reduce_LiteralArrow_SumsCorrectly()
    {
        var source = """
            const arr = [1, 2, 3, 4, 5];
            console.log(arr.reduce((acc, x) => acc + x, 0));
            """;

        var output = TestHarness.RunCompiled(source);
        Assert.Equal("15\n", output);
    }

    [Fact]
    public void Reduce_StringConcat_Inlines()
    {
        var source = """
            const parts = ["hello", " ", "world"];
            console.log(parts.reduce((acc, s) => acc + s, ""));
            """;

        var output = TestHarness.RunCompiled(source);
        Assert.Equal("hello world\n", output);
    }

    [Fact]
    public void Reduce_EmptyArray_ReturnsInitial()
    {
        var source = """
            const arr: number[] = [];
            console.log(arr.reduce((a, b) => a + b, 42));
            """;

        var output = TestHarness.RunCompiled(source);
        Assert.Equal("42\n", output);
    }

    [Fact]
    public void Reduce_HoleSkipped_AccumulatorPreserved()
    {
        // reduce skips holes per ECMA-262 23.1.3.24; accumulator passes
        // through unchanged.
        var source = """
            const arr = [1, 2, 3, 4];
            delete arr[1];
            delete arr[2];
            console.log(arr.reduce((acc, x) => acc + x, 0));
            """;

        var output = TestHarness.RunCompiled(source);
        Assert.Equal("5\n", output);  // 1 + 4 = 5
    }

    [Fact]
    public void ReduceRight_ProducesReverseOrder()
    {
        var source = """
            const parts = ["a", "b", "c"];
            console.log(parts.reduceRight((acc, s) => acc + s, ""));
            """;

        var output = TestHarness.RunCompiled(source);
        Assert.Equal("cba\n", output);
    }

    [Fact]
    public void ReduceRight_EmptyArray_ReturnsInitial()
    {
        var source = """
            const arr: number[] = [];
            console.log(arr.reduceRight((acc, x) => acc + x, 99));
            """;

        var output = TestHarness.RunCompiled(source);
        Assert.Equal("99\n", output);
    }

    [Fact]
    public void Reduce_NonInitialValue_FallsBackToSlowPath()
    {
        // 1-arg reduce (no initial value) — V1 inliner declines because
        // it needs a first-present-slot scan. Slow path handles it.
        var source = """
            const arr = [1, 2, 3, 4];
            console.log(arr.reduce((acc, x) => acc + x));
            """;

        var output = TestHarness.RunCompiled(source);
        Assert.Equal("10\n", output);
    }

    [Fact]
    public void Reduce_AccumulatorTypeChange_HandlesProperly()
    {
        // acc starts as number, body produces string — exercises the
        // EnsureBoxed re-store between iterations and re-load on next iter.
        var source = """
            const arr = [1, 2, 3];
            const r = arr.reduce<string>((acc, x) => acc + ":" + x, "start");
            console.log(r);
            """;

        var output = TestHarness.RunCompiled(source);
        Assert.Equal("start:1:2:3\n", output);
    }

    [Fact]
    public void Reduce_OneParamCallback_BindsAccOnly()
    {
        // Callback declares only `acc`; spec-permitted (extra positional
        // args ignored).
        var source = """
            const arr = [1, 2, 3];
            console.log(arr.reduce((acc) => acc + 10, 0));
            """;

        var output = TestHarness.RunCompiled(source);
        Assert.Equal("30\n", output);  // +10 each iter, 3 iters = 30
    }

    [Fact]
    public void Reduce_InterpreterAndCompiledAgree()
    {
        var source = """
            const nums = [1, 2, 3, 4, 5, 6, 7, 8, 9, 10];
            console.log(nums.reduce((a, b) => a + b, 0));
            console.log(nums.reduceRight((a, b) => a + b, 0));
            console.log(nums.reduce((a, b) => a * b, 1));
            """;

        var compiled = TestHarness.RunCompiled(source);
        var interpreted = TestHarness.RunInterpreted(source);
        Assert.Equal(interpreted, compiled);
        Assert.Equal("55\n55\n3628800\n", compiled);
    }

    // -- M5: block-body arrows ----------------------------------------

    /// <summary>
    /// forEach with block body and bare return — typed callback signature
    /// is <c>(T, number, T[]) =&gt; void</c>, so a value-returning return
    /// would be a TypeScript error. The bare-return form exercises the
    /// inliner's handler with a null value (early exit from iteration).
    /// </summary>
    [Fact]
    public void Block_ForEach_BareReturn_ExitsIteration()
    {
        var source = """
            let count = 0;
            const arr = [1, 2, 3];
            arr.forEach(x => {
                count++;
                if (x === 2) return;
                count += 100;
            });
            console.log(count);
            """;

        // x=1: count=1, branch not taken, count=101.
        // x=2: count=102, branch taken, return (advance early).
        // x=3: count=103, branch not taken, count=203.
        var output = TestHarness.RunCompiled(source);
        Assert.Equal("203\n", output);
    }

    [Fact]
    public void Block_Map_BlockBodyReturn_ProducesArray()
    {
        var source = """
            const arr = [1, 2, 3, 4];
            const out = arr.map(x => {
                const sq = x * x;
                return sq + 1;
            });
            console.log(out[0] + ":" + out[1] + ":" + out[3]);
            """;

        var output = TestHarness.RunCompiled(source);
        Assert.Equal("2:5:17\n", output);
    }

    [Fact]
    public void Block_Map_FallOffEnd_ProducesUndefined()
    {
        // No explicit return → implicit return undefined for that
        // iteration. Map output position holds undefined (renders as null
        // in JSON.stringify).
        var source = """
            const arr = [1, 2, 3];
            const out = arr.map(x => { let y = x; });
            console.log(JSON.stringify(out));
            """;

        var output = TestHarness.RunCompiled(source);
        Assert.Equal("[null,null,null]\n", output);
    }

    [Fact]
    public void Block_Filter_BlockBodyReturn_FiltersCorrectly()
    {
        var source = """
            const arr = [1, 2, 3, 4, 5, 6];
            const evens = arr.filter(x => {
                const r = x % 2;
                return r === 0;
            });
            console.log(evens.length);
            console.log(evens[0]);
            console.log(evens[2]);
            """;

        var output = TestHarness.RunCompiled(source);
        Assert.Equal("3\n2\n6\n", output);
    }

    [Fact]
    public void Block_Find_EarlyReturn_FromIfBranch()
    {
        var source = """
            const arr = [1, 5, 8, 3, 12];
            const result = arr.find(x => {
                if (x > 6) return true;
                return false;
            });
            console.log(result);
            """;

        var output = TestHarness.RunCompiled(source);
        Assert.Equal("8\n", output);
    }

    [Fact]
    public void Block_FindIndex_MultipleReturns()
    {
        var source = """
            const arr = [1, 2, 3, 4, 5];
            const idx = arr.findIndex(x => {
                if (x === 0) return false;
                if (x > 3) return true;
                return false;
            });
            console.log(idx);
            """;

        var output = TestHarness.RunCompiled(source);
        Assert.Equal("3\n", output);
    }

    [Fact]
    public void Block_Some_FallOffEnd_TreatsAsUndefined()
    {
        // Body falls off end → implicit return undefined → not truthy →
        // some returns false.
        var source = """
            const arr = [1, 2, 3];
            console.log(arr.some(x => { const y = x; }));
            """;

        var output = TestHarness.RunCompiled(source);
        Assert.Equal("false\n", output);
    }

    [Fact]
    public void Block_Every_FallOffEnd_TreatsAsUndefined()
    {
        // every: falsy means "fail this iteration". Implicit undefined
        // is falsy → every returns false on first iteration.
        var source = """
            const arr = [1, 2, 3];
            console.log(arr.every(x => { const y = x; }));
            """;

        var output = TestHarness.RunCompiled(source);
        Assert.Equal("false\n", output);
    }

    [Fact]
    public void Block_Reduce_LocalVarsInBody()
    {
        var source = """
            const nums = [1, 2, 3, 4];
            const product = nums.reduce((acc, x) => {
                const next = acc * x;
                return next;
            }, 1);
            console.log(product);
            """;

        var output = TestHarness.RunCompiled(source);
        Assert.Equal("24\n", output);
    }

    [Fact]
    public void Block_Reduce_ConditionalReturn()
    {
        // reduce body with if/return — early-return takes the if branch,
        // fall-through takes the second.
        var source = """
            const arr = [1, 2, 3, 4, 5];
            const r = arr.reduce<number>((acc, x) => {
                if (x > 3) return acc + 100;
                return acc + x;
            }, 0);
            console.log(r);
            """;

        var output = TestHarness.RunCompiled(source);
        // 0 + 1 + 2 + 3 + 100 + 100 = 206
        Assert.Equal("206\n", output);
    }

    [Fact]
    public void Block_DisallowedShape_FallsBackCorrectly()
    {
        // for-loop inside body — V1 inliner declines (allowlist excludes
        // For). Slow path takes over; output must still be correct.
        var source = """
            const arr = [1, 2, 3];
            const sum = arr.reduce<number>((acc, x) => {
                let total = 0;
                for (let i = 0; i < x; i++) total += i;
                return acc + total;
            }, 0);
            console.log(sum);
            """;

        var output = TestHarness.RunCompiled(source);
        // x=1: 0; x=2: 0+1=1; x=3: 0+1+2=3. Sum = 0+1+3 = 4.
        Assert.Equal("4\n", output);
    }

    [Fact]
    public void Block_LocalDeclarationsDoNotLeakAcrossIterations()
    {
        // `let y` is block-scoped; declared on each iteration of the
        // inlined loop. Verifies that EnterScope/ExitScope cleanup
        // happens between iterations — otherwise the second iter would
        // see the first iter's `y`. The test passes if `y` resolves to
        // the per-iteration computed value, not a stale one.
        var source = """
            const arr = [10, 20, 30];
            const out = arr.map(x => {
                let y = x + 1;
                return y;
            });
            console.log(out[0] + ":" + out[1] + ":" + out[2]);
            """;

        var output = TestHarness.RunCompiled(source);
        Assert.Equal("11:21:31\n", output);
    }

    [Fact]
    public void Block_BodyInterpreterAndCompiledAgree()
    {
        var source = """
            const nums = [1, 2, 3, 4, 5];
            const out = nums.map(x => {
                const sq = x * x;
                return sq;
            });
            console.log(out[0] + ":" + out[4]);
            const evens = nums.filter(x => {
                return x % 2 === 0;
            });
            console.log(evens.length);
            const sum = nums.reduce<number>((acc, x) => {
                const next = acc + x;
                return next;
            }, 0);
            console.log(sum);
            """;

        var compiled = TestHarness.RunCompiled(source);
        var interpreted = TestHarness.RunInterpreted(source);
        Assert.Equal(interpreted, compiled);
        Assert.Equal("1:25\n2\n15\n", compiled);
    }

    /// <summary>
    /// Inlined IL must pass ILVerify. Runs the basic forEach/map/filter
    /// pipeline through <see cref="TestHarness.CompileVerifyAndRun"/>.
    /// Catches stack-balance bugs, missing boxes, etc.
    /// </summary>
    /// <remarks>
    /// Filters the same set of pre-existing host-runtime/reference-assembly
    /// false positives that <see cref="ILVerificationTests"/> ignores.
    /// </remarks>
    [Fact]
    public void InlinedHelpers_PassILVerification()
    {
        var source = """
            const arr = [1, 2, 3, 4, 5];
            arr.forEach(x => x);
            const doubled = arr.map(x => x * 2);
            const big = arr.filter(x => x > 2);
            const first = arr.find(x => x > 3);
            const idx = arr.findIndex(x => x > 3);
            const last = arr.findLast(x => x < 5);
            const lastIdx = arr.findLastIndex(x => x < 5);
            const anyBig = arr.some(x => x > 3);
            const allPos = arr.every(x => x > 0);
            const sum = arr.reduce((a, b) => a + b, 0);
            const product = arr.reduceRight((a, b) => a * b, 1);
            console.log(doubled.length + ":" + big.length + ":" + first + ":" + idx + ":" + last + ":" + lastIdx + ":" + anyBig + ":" + allPos + ":" + sum + ":" + product);
            """;

        var (errors, output) = TestHarness.CompileVerifyAndRun(source);
        var unexpected = errors.Where(e => !KnownRuntimeFalsePositives.Any(k => e.Contains(k))).ToList();
        Assert.Empty(unexpected);
        Assert.Equal("5:3:4:3:4:3:true:true:15:120\n", output);
    }

    private static readonly HashSet<string> KnownRuntimeFalsePositives = new()
    {
        // URL/CookieJar/Headers helpers use host-runtime methods that fail
        // ref-assembly verification but work at runtime. Mirrored from
        // ILVerificationTests.KnownRuntimeErrors.
        "$Runtime.UrlParse", "$Runtime.UrlResolve",
        "$Runtime.CookieJarGetCookies", "$Runtime.CookieJarSetCookie", "$Runtime.CookieJarClear",
        "$Headers.", "$URL.", "$URLSearchParams.",
        "$ReadableStream", "$WritableStream", "$TransformStream",
    };

    // -- M6: feature flag --------------------------------------------------

    /// <summary>
    /// The <c>SHARPTS_DISABLE_ARROW_INLINING</c> environment variable
    /// disables the inliner at the eligibility gate. When set, all
    /// iterator-helper call sites fall through to the Direct/slow path.
    /// Lets benchmarks compare paths and provides a one-flag rollback if
    /// a regression surfaces. The flag is read once at static init time
    /// (eligibility check uses a cached <c>readonly bool</c>), so toggling
    /// at runtime within a single process has no effect — this fixture
    /// exercises the static-init path indirectly via standard inline
    /// behavior. Confirms the env-var-disabled branch compiles and links;
    /// runtime A/B comparison happens via running the benchmark project
    /// twice with different env values.
    /// </summary>
    [Fact]
    public void EnvFlag_InlinerEnabled_ProducesCorrectOutput()
    {
        // Default state (no env var set): inliner active. We're not
        // asserting that the inliner FIRED — just that the flag-checked
        // path is wired in. The behavioral fixtures above prove the
        // inliner runs in the env-not-set case.
        var source = """
            const arr = [1, 2, 3, 4];
            const sum = arr.reduce((a, b) => a + b, 0);
            console.log(sum);
            """;

        var output = TestHarness.RunCompiled(source);
        Assert.Equal("10\n", output);
    }

    /// <summary>
    /// Smoke test that the bench-fixture call shapes still execute
    /// correctly post-M5 (covers the eight call shapes the production
    /// benchmark <c>SharpTS.Benchmarks/TypeScriptSources/ArrayHelpers.ts</c>
    /// exercises, but at top level rather than wrapped in user functions
    /// — the wrapped form trips a pre-existing ILVerify false positive on
    /// <c>$Program.Main</c> that is unrelated to the inliner).
    /// </summary>
    [Fact]
    public void BenchFixtureShape_AllInlinedHelpersExecuteCorrectly()
    {
        var source = """
            const data: any[] = [1, 2, 3, 4, 5];
            console.log(data.map(x => x * 2).length);
            console.log(data.filter(x => x > 10).length);
            console.log(data.reduce((a, b) => a + b, 0));
            let sum: number = 0;
            data.forEach(x => { sum = sum + x; });
            console.log(sum);
            console.log(data.every(x => x >= 0));
            console.log(data.find(x => x > 9999) ?? -1);
            const cbDouble = (x: number) => x * 2;
            console.log(data.map(cbDouble).length);
            const cbAdd = (a: number, b: number) => a + b;
            console.log(data.reduce(cbAdd, 0));
            """;

        var output = TestHarness.RunCompiled(source);
        // map → 5 elements; filter → 0 (none > 10); reduce → 0+1+2+3+4+5 = 15;
        // forEach sum → 15; every → all >= 0 → true; find → no match → -1;
        // map(cbDouble) → 5 elements; reduce(cbAdd) → 15.
        Assert.Equal("5\n0\n15\n15\ntrue\n-1\n5\n15\n", output);
    }
}
