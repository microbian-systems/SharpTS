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
}
