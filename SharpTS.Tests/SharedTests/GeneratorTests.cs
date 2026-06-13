using SharpTS.Tests.Infrastructure;
using Xunit;

namespace SharpTS.Tests.SharedTests;

/// <summary>
/// Shared generator tests that run against both interpreter and compiler.
/// These tests verify core generator functionality that should work identically in both modes.
/// </summary>
public class GeneratorTests
{
    #region For...Of Integration

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void Generator_ForOfLoop_IteratesAllValues(ExecutionMode mode)
    {
        var source = """
            function* nums() {
                yield 10;
                yield 20;
                yield 30;
            }

            for (let n of nums()) {
                console.log(n);
            }
            """;

        var output = TestHarness.Run(source, mode);
        Assert.Equal("10\n20\n30\n", output);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void Generator_WithParameters_UsesParameters(ExecutionMode mode)
    {
        var source = """
            function* range(start: number, end: number) {
                for (let i = start; i <= end; i++) {
                    yield i;
                }
            }

            for (let x of range(5, 8)) {
                console.log(x);
            }
            """;

        var output = TestHarness.Run(source, mode);
        Assert.Equal("5\n6\n7\n8\n", output);
    }

    #endregion

    #region Yield* Delegation

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void Generator_YieldStarArray_DelegatesCorrectly(ExecutionMode mode)
    {
        var source = """
            function* withArray() {
                yield* [1, 2, 3];
            }

            for (let x of withArray()) {
                console.log(x);
            }
            """;

        var output = TestHarness.Run(source, mode);
        Assert.Equal("1\n2\n3\n", output);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void Generator_YieldStarGenerator_DelegatesCorrectly(ExecutionMode mode)
    {
        var source = """
            function* inner() {
                yield 2;
                yield 3;
            }

            function* outer() {
                yield 1;
                yield* inner();
                yield 4;
            }

            for (let x of outer()) {
                console.log(x);
            }
            """;

        var output = TestHarness.Run(source, mode);
        Assert.Equal("1\n2\n3\n4\n", output);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void Generator_YieldStar_ForwardsSentValue(ExecutionMode mode)
    {
        // ECMA-262 §14.4.14: the value passed to the outer's next(v) is forwarded
        // into the delegated iterator's next(v), so the inner yield resumes with it
        // rather than undefined (issue #476).
        var source = """
            function* inner() {
                const a = yield 1;
                console.log("inner got", a);
            }
            function* outer() {
                yield* inner();
            }
            const it = outer();
            it.next();
            it.next(99);
            """;

        var output = TestHarness.Run(source, mode);
        Assert.Equal("inner got 99\n", output);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void Generator_YieldStar_ForwardsAcrossYields_AndYieldsCompletionValue(ExecutionMode mode)
    {
        // Each next(v) feeds the inner's successive yields, and the delegate's
        // return value becomes the value of the `yield*` expression.
        var source = """
            function* inner() {
                const a = yield "a";
                const b = yield "b";
                return `inner:${a},${b}`;
            }
            function* outer() {
                const ret = yield* inner();
                console.log("ret", ret);
                yield "after";
            }
            const it = outer();
            console.log(it.next().value);
            console.log(it.next(1).value);
            console.log(it.next(2).value);
            console.log(it.next(3).done);
            """;

        var output = TestHarness.Run(source, mode);
        Assert.Equal("a\nb\nret inner:1,2\nafter\ntrue\n", output);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void Generator_YieldStar_Nested_ForwardsSentValue(ExecutionMode mode)
    {
        // A resume value must thread through every level of nested delegation.
        var source = """
            function* a() {
                const x = yield 1;
                console.log("a got", x);
            }
            function* b() { yield* a(); }
            function* c() { yield* b(); }
            const it = c();
            it.next();
            it.next(42);
            """;

        var output = TestHarness.Run(source, mode);
        Assert.Equal("a got 42\n", output);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void Generator_YieldStar_InExpression_ForwardsSentValue(ExecutionMode mode)
    {
        // yield* as a sub-expression (operand-spill path) still forwards the resume
        // value and yields the delegate's return value into the surrounding expression.
        var source = """
            function* inner() {
                const x = yield 1;
                return x + 10;
            }
            function* outer() {
                const v = "R:" + (yield* inner());
                console.log(v);
            }
            const it = outer();
            it.next();
            it.next(5);
            """;

        var output = TestHarness.Run(source, mode);
        Assert.Equal("R:15\n", output);
    }

    #endregion

    #region Control Flow

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void Generator_WhileLoop_YieldsMultipleTimes(ExecutionMode mode)
    {
        var source = """
            function* countdown(n: number) {
                while (n > 0) {
                    yield n;
                    n--;
                }
            }

            for (let x of countdown(3)) {
                console.log(x);
            }
            """;

        var output = TestHarness.Run(source, mode);
        Assert.Equal("3\n2\n1\n", output);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void Generator_IfStatement_ConditionalYield(ExecutionMode mode)
    {
        var source = """
            function* conditionalYield(includeSecond: boolean) {
                yield 1;
                if (includeSecond) {
                    yield 2;
                }
                yield 3;
            }

            console.log("With second:");
            for (let x of conditionalYield(true)) {
                console.log(x);
            }

            console.log("Without second:");
            for (let x of conditionalYield(false)) {
                console.log(x);
            }
            """;

        var output = TestHarness.Run(source, mode);
        Assert.Equal("With second:\n1\n2\n3\nWithout second:\n1\n3\n", output);
    }

    #endregion

    #region Closures

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void Generator_Closure_CapturesVariables(ExecutionMode mode)
    {
        var source = """
            function* makeSequence(multiplier: number) {
                for (let i = 1; i <= 3; i++) {
                    yield i * multiplier;
                }
            }

            for (let x of makeSequence(10)) {
                console.log(x);
            }
            """;

        var output = TestHarness.Run(source, mode);
        Assert.Equal("10\n20\n30\n", output);
    }

    #endregion

    #region Iterator Protocol (.next())

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void Generator_BasicYield_ReturnsValues(ExecutionMode mode)
    {
        var source = """
            function* counter() {
                yield 1;
                yield 2;
                yield 3;
            }

            let gen = counter();
            console.log(gen.next().value);
            console.log(gen.next().value);
            console.log(gen.next().value);
            console.log(gen.next().done);
            """;

        var output = TestHarness.Run(source, mode);
        Assert.Equal("1\n2\n3\ntrue\n", output);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void Generator_EmptyGenerator_ReturnsDoneImmediately(ExecutionMode mode)
    {
        var source = """
            function* empty() {}

            let gen = empty();
            let result = gen.next();
            console.log(result.done);
            """;

        var output = TestHarness.Run(source, mode);
        Assert.Equal("true\n", output);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void Generator_IteratorResult_HasCorrectStructure(ExecutionMode mode)
    {
        var source = """
            function* single() {
                yield 42;
            }

            let gen = single();
            let first = gen.next();
            let second = gen.next();

            console.log("First value:", first.value);
            console.log("First done:", first.done);
            console.log("Second done:", second.done);
            """;

        var output = TestHarness.Run(source, mode);
        Assert.Equal("First value: 42\nFirst done: false\nSecond done: true\n", output);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void Generator_MultipleInstances_IndependentState(ExecutionMode mode)
    {
        var source = """
            function* counter() {
                yield 1;
                yield 2;
                yield 3;
            }

            let gen1 = counter();
            let gen2 = counter();

            console.log(gen1.next().value);
            console.log(gen2.next().value);
            console.log(gen1.next().value);
            console.log(gen2.next().value);
            """;

        var output = TestHarness.Run(source, mode);
        Assert.Equal("1\n1\n2\n2\n", output);
    }

    #endregion

    #region Resume Values (next(v)) — issue #452

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void Generator_NextWithValue_DeliveredToYield(ExecutionMode mode)
    {
        // ECMA-262 §27.5.3.3: `yield expr` evaluates to the argument of the
        // resuming next(v). The first next() only starts the body.
        var source = """
            function* g() {
                const x = yield 1;
                console.log("got", x);
                const y = yield 2;
                console.log("got", y);
                return 99;
            }
            const it = g();
            console.log("first", it.next().value);
            console.log("r1", it.next(42).value);
            console.log("r2", it.next(43).value);
            console.log("done", it.next().done);
            """;

        var output = TestHarness.Run(source, mode);
        Assert.Equal("first 1\ngot 42\nr1 2\ngot 43\nr2 99\ndone true\n", output);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void Generator_AccumulatorPattern_UsesSentValues(ExecutionMode mode)
    {
        // The classic two-way generator: each next(n) feeds the running total.
        var source = """
            function* adder() {
                let total = 0;
                while (true) {
                    const n = yield total;
                    total += n;
                }
            }
            const a = adder();
            console.log(a.next().value);
            console.log(a.next(5).value);
            console.log(a.next(10).value);
            console.log(a.next(3).value);
            """;

        var output = TestHarness.Run(source, mode);
        Assert.Equal("0\n5\n15\n18\n", output);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void Generator_AccumulatorPattern_CompoundAssignAcrossYield(ExecutionMode mode)
    {
        // #497: the same two-way accumulator, but the running total is read *before* the yield
        // as the LHS of `+=`, so it is loop-carried across the suspension. The sibling test
        // Generator_AccumulatorPattern_UsesSentValues sidesteps this by reading `total` after
        // the yield. Pre-fix the compiled state machine left `total` in an IL local that the
        // MoveNext re-entry wiped, so each resume recomputed `0 + n` (5, 10, 3) instead of the
        // running total. The analyzer now hoists loop-body locals of any yielding loop.
        var source = """
            function* adder() {
                let total = 0;
                while (true) {
                    total += yield total;
                }
            }
            const a = adder();
            console.log(a.next().value);
            console.log(a.next(5).value);
            console.log(a.next(10).value);
            console.log(a.next(3).value);
            """;

        var output = TestHarness.Run(source, mode);
        Assert.Equal("0\n5\n15\n18\n", output);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void Generator_AccumulatorPattern_CompoundAssignAcrossYield_DoWhile(ExecutionMode mode)
    {
        // #497: do-while is one of the loop forms that lacked loop-body hoisting pre-fix.
        var source = """
            function* adder() {
                let total = 0;
                do {
                    total += yield total;
                } while (true);
            }
            const a = adder();
            console.log(a.next().value);
            console.log(a.next(5).value);
            console.log(a.next(10).value);
            console.log(a.next(3).value);
            """;

        var output = TestHarness.Run(source, mode);
        Assert.Equal("0\n5\n15\n18\n", output);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void Generator_AccumulatorPattern_CompoundAssignAcrossYield_For(ExecutionMode mode)
    {
        // #497: an unbounded for(;;) likewise lacked loop-body hoisting pre-fix.
        var source = """
            function* adder() {
                let total = 0;
                for (;;) {
                    total += yield total;
                }
            }
            const a = adder();
            console.log(a.next().value);
            console.log(a.next(5).value);
            console.log(a.next(10).value);
            console.log(a.next(3).value);
            """;

        var output = TestHarness.Run(source, mode);
        Assert.Equal("0\n5\n15\n18\n", output);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void Generator_LoopCarriedLocal_ReadBeforeYield_SurvivesAcrossIterations(ExecutionMode mode)
    {
        // #497 (general shape): a counted for-loop whose induction-adjacent local is read in the
        // loop body before the yield. The accumulator must persist across the loop back-edge.
        var source = """
            function* g() {
                let sum = 0;
                for (let i = 0; i < 3; i++) {
                    sum = sum + (yield sum);
                }
                return sum;
            }
            const it = g();
            console.log(it.next().value);    // 0
            console.log(it.next(10).value);  // 10
            console.log(it.next(20).value);  // 30
            console.log(it.next(30).value);  // 60 (return)
            console.log(it.next().done);     // true
            """;

        var output = TestHarness.Run(source, mode);
        Assert.Equal("0\n10\n30\n60\ntrue\n", output);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void Generator_BareNext_ResumesWithUndefined(ExecutionMode mode)
    {
        var source = """
            function* g() {
                const x = yield 1;
                console.log(x, typeof x);
            }
            const it = g();
            it.next();
            it.next();
            """;

        var output = TestHarness.Run(source, mode);
        Assert.Equal("undefined undefined\n", output);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void Generator_NextWithNull_ResumesWithNull(ExecutionMode mode)
    {
        // next(null) must deliver null (typeof "object"), distinct from a bare
        // next() which resumes with undefined.
        var source = """
            function* g() {
                const x = yield 1;
                console.log(x, typeof x);
            }
            const it = g();
            it.next();
            it.next(null);
            """;

        var output = TestHarness.Run(source, mode);
        Assert.Equal("null object\n", output);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void Generator_ForOf_ResumesYieldWithUndefined(ExecutionMode mode)
    {
        // for...of drives the generator without passing a value, so a yield used
        // as an expression resolves to undefined (not null).
        var source = """
            function* g() {
                const x = yield 10;
                console.log("resumed", x, typeof x);
            }
            for (const v of g()) {
                console.log("yielded", v);
            }
            """;

        var output = TestHarness.Run(source, mode);
        Assert.Equal("yielded 10\nresumed undefined undefined\n", output);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void Generator_SentValue_UsedInExpression(ExecutionMode mode)
    {
        var source = """
            function* g() {
                const doubled = (yield 1) * 2;
                console.log("doubled", doubled);
            }
            const it = g();
            it.next();
            it.next(21);
            """;

        var output = TestHarness.Run(source, mode);
        Assert.Equal("doubled 42\n", output);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void Generator_InstanceMethod_NextWithValue_AndThis(ExecutionMode mode)
    {
        var source = """
            class Echo {
                prefix: string = "msg:";
                *run() {
                    const first = yield "ready";
                    console.log(this.prefix + first);
                    const second = yield "more";
                    console.log(this.prefix + second);
                }
            }
            const g = new Echo().run();
            console.log(g.next().value);
            g.next("hello");
            g.next("world");
            """;

        var output = TestHarness.Run(source, mode);
        Assert.Equal("ready\nmsg:hello\nmsg:world\n", output);
    }

    #endregion

    #region Yield* with String

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void Generator_YieldStarString_DelegatesCharacters(ExecutionMode mode)
    {
        var source = """
            function* chars() {
                yield* "hi";
            }

            for (let c of chars()) {
                console.log(c);
            }
            """;

        var output = TestHarness.Run(source, mode);
        Assert.Equal("h\ni\n", output);
    }

    #endregion

    #region Yield* with Map and Set

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void Generator_YieldStarMap_DelegatesEntries(ExecutionMode mode)
    {
        var source = """
            function* mapIter() {
                let m = new Map<string, number>();
                m.set("a", 1);
                m.set("b", 2);
                yield* m;
            }

            for (let pair of mapIter()) {
                console.log(pair[0] + ":" + pair[1]);
            }
            """;

        var output = TestHarness.Run(source, mode);
        Assert.Equal("a:1\nb:2\n", output);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void Generator_YieldStarSet_DelegatesValues(ExecutionMode mode)
    {
        var source = """
            function* setIter() {
                let s = new Set<number>();
                s.add(100);
                s.add(200);
                yield* s;
            }

            for (let v of setIter()) {
                console.log(v);
            }
            """;

        var output = TestHarness.Run(source, mode);
        Assert.Equal("100\n200\n", output);
    }

    #endregion

    #region return()/throw() resume suspended generator (ECMA-262 §27.5.3.4) — issue #478

    // These are interpreter-only: a try/finally combined with yield currently emits invalid
    // IL in compiled mode (tracked in issue #477), so the compiled path can't be observed yet.

    [Theory]
    [MemberData(nameof(ExecutionModes.InterpretedOnly), MemberType = typeof(ExecutionModes))]
    public void Generator_Return_RunsFinally_AndReportsValue(ExecutionMode mode)
    {
        // The repro from issue #478: return(v) on a suspended generator resumes it as an
        // abrupt completion, running the active finally block, then settles { value, done }.
        var source = """
            function* g() {
                try {
                    yield 1;
                    yield 2;
                } finally {
                    console.log("finally ran");
                }
            }
            const it = g();
            const a = it.next();
            console.log(a.value, a.done);
            const b = it.return(99);
            console.log(b.value, b.done);
            const c = it.next();
            console.log(c.value, c.done);
            """;

        var output = TestHarness.Run(source, mode);
        // Node: 1 false / finally ran / 99 true / undefined true
        Assert.Equal("1 false\nfinally ran\n99 true\nundefined true\n", output);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.InterpretedOnly), MemberType = typeof(ExecutionModes))]
    public void Generator_Throw_CaughtByTryCatch_Continues(ExecutionMode mode)
    {
        // throw(e) injects the error at the yield point; an enclosing catch handles it and
        // the generator keeps running (and may yield again).
        var source = """
            function* g() {
                try {
                    yield 1;
                    yield 2;
                } catch (e) {
                    console.log("caught " + e);
                    yield 99;
                }
            }
            const it = g();
            console.log(it.next().value);
            const r = it.throw("boom");
            console.log(r.value, r.done);
            const done = it.next();
            console.log(done.value, done.done);
            """;

        var output = TestHarness.Run(source, mode);
        Assert.Equal("1\ncaught boom\n99 false\nundefined true\n", output);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.InterpretedOnly), MemberType = typeof(ExecutionModes))]
    public void Generator_Throw_RunsFinally_ThenPropagates(ExecutionMode mode)
    {
        // throw(e) with only a finally (no catch): the finally runs, then the error propagates
        // to the throw() caller.
        var source = """
            function* g() {
                try {
                    yield 1;
                } finally {
                    console.log("finally C");
                }
            }
            const it = g();
            it.next();
            try {
                it.throw("boomC");
            } catch (e) {
                console.log("outer caught " + e);
            }
            """;

        var output = TestHarness.Run(source, mode);
        Assert.Equal("finally C\nouter caught boomC\n", output);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.InterpretedOnly), MemberType = typeof(ExecutionModes))]
    public void Generator_Return_RunsNestedFinallyInnerToOuter(ExecutionMode mode)
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
            const it = g();
            it.next();
            const r = it.return(5);
            console.log(r.value, r.done);
            """;

        var output = TestHarness.Run(source, mode);
        Assert.Equal("inner\nouter\n5 true\n", output);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.InterpretedOnly), MemberType = typeof(ExecutionModes))]
    public void Generator_Return_FinallyThatYields_DefersCompletion(ExecutionMode mode)
    {
        // A finally that yields suspends the pending return; the return value is delivered
        // only once the finally completes on a later next().
        var source = """
            function* g() {
                try {
                    yield 1;
                } finally {
                    yield 99;
                }
            }
            const it = g();
            it.next();
            const a = it.return(5);
            console.log(a.value, a.done);
            const b = it.next();
            console.log(b.value, b.done);
            """;

        var output = TestHarness.Run(source, mode);
        Assert.Equal("99 false\n5 true\n", output);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.InterpretedOnly), MemberType = typeof(ExecutionModes))]
    public void Generator_Return_FinallyReturnOverridesValue(ExecutionMode mode)
    {
        // A finally that returns its own value overrides the value passed to return().
        var source = """
            function* g() {
                try {
                    yield 1;
                } finally {
                    return 7;
                }
            }
            const it = g();
            it.next();
            const r = it.return(99);
            console.log(r.value, r.done);
            """;

        var output = TestHarness.Run(source, mode);
        Assert.Equal("7 true\n", output);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.InterpretedOnly), MemberType = typeof(ExecutionModes))]
    public void Generator_Return_FinallyThrowOverridesReturn(ExecutionMode mode)
    {
        // A finally that throws overrides the pending return with the thrown error.
        var source = """
            function* g() {
                try {
                    yield 1;
                } finally {
                    throw "finally-err";
                }
            }
            const it = g();
            it.next();
            try {
                it.return(5);
            } catch (e) {
                console.log("caught " + e);
            }
            """;

        var output = TestHarness.Run(source, mode);
        Assert.Equal("caught finally-err\n", output);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.InterpretedOnly), MemberType = typeof(ExecutionModes))]
    public void Generator_ReturnAndThrow_OnNotStarted_DoNotRunBody(ExecutionMode mode)
    {
        // return()/throw() on a generator that hasn't started close it without running the
        // body, so the finally never runs (ECMA-262 §27.5.3.4).
        var source = """
            function* g() {
                try {
                    yield 1;
                } finally {
                    console.log("should NOT run");
                }
            }
            const a = g();
            const r = a.return(8);
            console.log(r.value, r.done);
            const b = g();
            try {
                b.throw("x");
            } catch (e) {
                console.log("threw " + e);
            }
            """;

        var output = TestHarness.Run(source, mode);
        Assert.Equal("8 true\nthrew x\n", output);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.InterpretedOnly), MemberType = typeof(ExecutionModes))]
    public void Generator_NextAfterCompletion_ReportsUndefined(ExecutionMode mode)
    {
        // Once a generator finishes, its completion value is delivered exactly once; further
        // next() calls report { value: undefined, done: true }.
        var source = """
            function* g() {
                yield 1;
                return 42;
            }
            const it = g();
            it.next();
            const done = it.next();
            console.log(done.value, done.done);
            const after = it.next();
            console.log(after.value, after.done);
            """;

        var output = TestHarness.Run(source, mode);
        Assert.Equal("42 true\nundefined true\n", output);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.InterpretedOnly), MemberType = typeof(ExecutionModes))]
    public void Generator_UncaughtThrowInBody_PropagatesToNext(ExecutionMode mode)
    {
        // An uncaught throw inside the body surfaces to the next() caller rather than being
        // swallowed (regression guard for the worker's abnormal-completion handling).
        var source = """
            function* g() {
                yield 1;
                throw "bodyboom";
            }
            const it = g();
            it.next();
            try {
                it.next();
            } catch (e) {
                console.log("caught " + e);
            }
            """;

        var output = TestHarness.Run(source, mode);
        Assert.Equal("caught bodyboom\n", output);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.InterpretedOnly), MemberType = typeof(ExecutionModes))]
    public void Generator_BareReturn_ReportsUndefinedValue(ExecutionMode mode)
    {
        // A no-argument return() resumes with undefined.
        var source = """
            function* g() {
                yield 1;
                yield 2;
            }
            const it = g();
            it.next();
            const r = it.return();
            console.log(r.value, r.done);
            """;

        var output = TestHarness.Run(source, mode);
        Assert.Equal("undefined true\n", output);
    }

    #endregion

    #region Re-entrant next()/return()/throw() — "already running" (ECMA-262 §27.5.3.3) — issues #515, #521

    // ECMA-262 §27.5.3.3 (GeneratorValidate): calling next/return/throw on a generator whose state
    // is `executing` throws a TypeError ("Generator is already running"). The only way to reach
    // that state from a guest call is re-entrancy — the body advancing itself. Before the fix the
    // interpreter's thread-coroutine deadlocked (#515) and the compiled state machine recursed back
    // into MoveNext and overflowed the stack (#521; return/throw silently corrupted state). Both are
    // now guarded.
    //
    // The interpreted and compiled cases are tested from separate sources rather than shared:
    //   * Interpreted tests use the `let it; it = g()` self-reference form.
    //   * Compiled tests resolve the receiver through a live object property (`h.it`), because
    //     compiled generators capture closure variables by value (#541) — a self-assigned `it` is
    //     snapshotted as `undefined`, so the `let it; it = g()` form never reaches the guard.
    //   * `instanceof TypeError` is asserted only when the error is caught OUTSIDE the generator; a
    //     separate pre-existing bug (#543) makes `instanceof` report false for an error caught
    //     inside a compiled generator body (its `.name`/`.message` are still correct).
    // The yield*-delegation case doesn't rely on the captured self-reference (the inner generator is
    // created after the outer is assigned), so it runs in both modes from a single source.

    [Theory]
    [MemberData(nameof(ExecutionModes.InterpretedOnly), MemberType = typeof(ExecutionModes))]
    public void Generator_ReentrantNext_ThrowsTypeErrorThenResumes(ExecutionMode mode)
    {
        // The re-entrant next() throws a catchable TypeError; once caught, the generator is still
        // suspended-able and resumes normally (the guard must not corrupt its running state).
        var source = """
            let it: any;
            function* g() {
                try { it.next(); }
                catch (e: any) { console.log(e instanceof TypeError, e.name, e.message); }
                yield 1;
            }
            it = g();
            const r = it.next();
            console.log(r.value, r.done);
            """;

        var output = TestHarness.Run(source, mode);
        Assert.Equal("true TypeError Generator is already running\n1 false\n", output);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.InterpretedOnly), MemberType = typeof(ExecutionModes))]
    public void Generator_ReentrantNext_UncaughtPropagatesToResumingCaller(ExecutionMode mode)
    {
        // An uncaught re-entrant next() completes the generator abnormally and the TypeError
        // surfaces to the outer next() that resumed it — matching Node's uncaught behavior.
        var source = """
            let it: any;
            function* g() { it.next(); yield 1; }
            it = g();
            try { it.next(); }
            catch (e: any) { console.log("outer caught", e.message); }
            """;

        var output = TestHarness.Run(source, mode);
        Assert.Equal("outer caught Generator is already running\n", output);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.InterpretedOnly), MemberType = typeof(ExecutionModes))]
    public void Generator_ReentrantReturn_ThrowsTypeErrorThenResumes(ExecutionMode mode)
    {
        var source = """
            let it: any;
            function* g() {
                try { it.return(0); }
                catch (e: any) { console.log("return ->", e.message); }
                yield 1;
            }
            it = g();
            const r = it.next();
            console.log(r.value, r.done);
            """;

        var output = TestHarness.Run(source, mode);
        Assert.Equal("return -> Generator is already running\n1 false\n", output);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.InterpretedOnly), MemberType = typeof(ExecutionModes))]
    public void Generator_ReentrantThrow_ThrowsTypeErrorThenResumes(ExecutionMode mode)
    {
        // The "already running" guard takes precedence over the injected throw(e): the caller's
        // error never reaches the body — it gets a TypeError instead.
        var source = """
            let it: any;
            function* g() {
                try { it.throw("boom"); }
                catch (e: any) { console.log("throw ->", e.message); }
                yield 1;
            }
            it = g();
            const r = it.next();
            console.log(r.value, r.done);
            """;

        var output = TestHarness.Run(source, mode);
        Assert.Equal("throw -> Generator is already running\n1 false\n", output);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void Generator_ReentrantThroughYieldStar_ThrowsTypeError(ExecutionMode mode)
    {
        // The outer generator is still `executing` while it delegates via yield*, so an inner
        // generator that calls the outer's next() must observe "already running". This case doesn't
        // depend on the captured self-reference, so it runs in both modes (#515 interp + #521 compiled).
        var source = """
            let outer: any;
            function* inner() {
                try { outer.next(); }
                catch (e: any) { console.log("deleg ->", e.message); }
                yield 5;
            }
            function* g() { yield* inner(); }
            outer = g();
            const r = outer.next();
            console.log(r.value, r.done);
            """;

        var output = TestHarness.Run(source, mode);
        Assert.Equal("deleg -> Generator is already running\n5 false\n", output);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.InterpretedOnly), MemberType = typeof(ExecutionModes))]
    public void GeneratorExpression_ReentrantNext_ThrowsTypeError(ExecutionMode mode)
    {
        // A generator function *expression* uses a distinct runtime class (SharpTSArrowGenerator);
        // its guard is exercised here. `var` (not `let`) sidesteps an unrelated type-checker scoping
        // bug for generator function expressions that close over block-scoped variables.
        var source = """
            var it: any;
            const g = function*() {
                try { it.next(); }
                catch (e: any) { console.log("expr ->", e.message); }
                yield 7;
            };
            it = g();
            const r = it.next();
            console.log(r.value, r.done);
            """;

        var output = TestHarness.Run(source, mode);
        Assert.Equal("expr -> Generator is already running\n7 false\n", output);
    }

    // --- Compiled variants (#521): genuine re-entrancy via a live object-property receiver,
    // so the guard is reached despite the by-value closure capture (#541). ---

    [Theory]
    [MemberData(nameof(ExecutionModes.CompiledOnly), MemberType = typeof(ExecutionModes))]
    public void Generator_ReentrantNext_Compiled_ThrowsTypeErrorThenResumes(ExecutionMode mode)
    {
        // The re-entrant next() throws a catchable TypeError; once caught, the generator is still
        // suspended-able and resumes normally (the guard must not corrupt its running state).
        var source = """
            const h: any = {};
            function* g() {
                try { h.it.next(); }
                catch (e: any) { console.log(e.name, e.message); }
                yield 1;
            }
            h.it = g();
            const r = h.it.next();
            console.log(r.value, r.done);
            """;

        var output = TestHarness.Run(source, mode);
        Assert.Equal("TypeError Generator is already running\n1 false\n", output);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.CompiledOnly), MemberType = typeof(ExecutionModes))]
    public void Generator_ReentrantNext_Compiled_UncaughtPropagatesToResumingCaller(ExecutionMode mode)
    {
        // An uncaught re-entrant next() completes the generator abnormally and the TypeError
        // surfaces to the outer next() that resumed it. Caught outside the body, so instanceof works.
        var source = """
            const h: any = {};
            function* g() { h.it.next(); yield 1; }
            h.it = g();
            try { h.it.next(); }
            catch (e: any) { console.log(e instanceof TypeError, e.name, e.message); }
            """;

        var output = TestHarness.Run(source, mode);
        Assert.Equal("true TypeError Generator is already running\n", output);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.CompiledOnly), MemberType = typeof(ExecutionModes))]
    public void Generator_ReentrantReturn_Compiled_ThrowsTypeErrorThenResumes(ExecutionMode mode)
    {
        var source = """
            const h: any = {};
            function* g() {
                try { h.it.return(0); }
                catch (e: any) { console.log("return ->", e.message); }
                yield 1;
            }
            h.it = g();
            const r = h.it.next();
            console.log(r.value, r.done);
            """;

        var output = TestHarness.Run(source, mode);
        Assert.Equal("return -> Generator is already running\n1 false\n", output);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.CompiledOnly), MemberType = typeof(ExecutionModes))]
    public void Generator_ReentrantThrow_Compiled_ThrowsTypeErrorThenResumes(ExecutionMode mode)
    {
        // The "already running" guard takes precedence over the injected throw(e): the caller's
        // error never reaches the body — it gets a TypeError instead.
        var source = """
            const h: any = {};
            function* g() {
                try { h.it.throw("boom"); }
                catch (e: any) { console.log("throw ->", e.message); }
                yield 1;
            }
            h.it = g();
            const r = h.it.next();
            console.log(r.value, r.done);
            """;

        var output = TestHarness.Run(source, mode);
        Assert.Equal("throw -> Generator is already running\n1 false\n", output);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.CompiledOnly), MemberType = typeof(ExecutionModes))]
    public void GeneratorExpression_ReentrantNext_Compiled_ThrowsTypeError(ExecutionMode mode)
    {
        // A generator function *expression* uses the same state-machine builder; its guard is
        // exercised here. `var` (not `const`) sidesteps an unrelated scoping bug for generator
        // function expressions that close over block-scoped variables.
        var source = """
            var h: any = {};
            var g = function*() {
                try { h.it.next(); }
                catch (e: any) { console.log("expr ->", e.message); }
                yield 7;
            };
            h.it = g();
            var r = h.it.next();
            console.log(r.value, r.done);
            """;

        var output = TestHarness.Run(source, mode);
        Assert.Equal("expr -> Generator is already running\n7 false\n", output);
    }

    #endregion
}
