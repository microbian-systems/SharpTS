using SharpTS.Tests.Infrastructure;
using Xunit;

namespace SharpTS.Tests.SharedTests;

/// <summary>
/// Tests for async generators (async function*) and async iteration (for await...of).
/// Runs against both interpreter and compiler.
/// Note: Tests use single-arg console.log or string concatenation to avoid
/// a multi-arg console.log limitation in compiled async functions.
/// </summary>
public class AsyncGeneratorTests
{
    #region Basic Async Generator Tests

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void AsyncGenerator_BasicYield_ReturnsValues(ExecutionMode mode)
    {
        var source = """
            async function* asyncCounter() {
                yield 1;
                yield 2;
                yield 3;
            }

            async function main() {
                const gen = asyncCounter();
                let result = await gen.next();
                console.log(result.value + " " + result.done);
                result = await gen.next();
                console.log(result.value + " " + result.done);
                result = await gen.next();
                console.log(result.value + " " + result.done);
                result = await gen.next();
                console.log(result.value + " " + result.done);
            }

            main();
            """;

        var output = TestHarness.Run(source, mode);
        // After the last yield, next() reports { value: undefined, done: true } — not null (#481/#540).
        Assert.Equal("1 false\n2 false\n3 false\nundefined true\n", output);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void AsyncGenerator_EmptyGenerator_ReturnsDoneImmediately(ExecutionMode mode)
    {
        var source = """
            async function* empty() {}

            async function main() {
                const gen = empty();
                const result = await gen.next();
                console.log(result.done);
            }

            main();
            """;

        var output = TestHarness.Run(source, mode);
        Assert.Equal("true\n", output);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void AsyncGenerator_WithAwait_AwaitsBeforeYield(ExecutionMode mode)
    {
        var source = """
            async function delay(value: number): Promise<number> {
                return value * 10;
            }

            async function* asyncGen() {
                const a = await delay(1);
                yield a;
                const b = await delay(2);
                yield b;
                const c = await delay(3);
                yield c;
            }

            async function main() {
                for await (const val of asyncGen()) {
                    console.log(val);
                }
            }

            main();
            """;

        var output = TestHarness.Run(source, mode);
        Assert.Equal("10\n20\n30\n", output);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void AsyncGenerator_WithParameters_UsesParameters(ExecutionMode mode)
    {
        var source = """
            async function* range(start: number, end: number) {
                for (let i = start; i <= end; i++) {
                    yield i;
                }
            }

            async function main() {
                for await (const n of range(5, 8)) {
                    console.log(n);
                }
            }

            main();
            """;

        var output = TestHarness.Run(source, mode);
        Assert.Equal("5\n6\n7\n8\n", output);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void AsyncGenerator_SingleYield_Works(ExecutionMode mode)
    {
        var source = """
            async function* single() {
                yield 42;
            }

            async function main() {
                const gen = single();
                const r1 = await gen.next();
                console.log(r1.value + " " + r1.done);
                const r2 = await gen.next();
                console.log(r2.value + " " + r2.done);
            }

            main();
            """;

        var output = TestHarness.Run(source, mode);
        // The result after the single yield is { value: undefined, done: true } — not null (#481/#540).
        Assert.Equal("42 false\nundefined true\n", output);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void AsyncGenerator_YieldInLoop_Works(ExecutionMode mode)
    {
        var source = """
            async function* countdown(start: number) {
                while (start > 0) {
                    yield start;
                    start = start - 1;
                }
            }

            async function main() {
                for await (const n of countdown(3)) {
                    console.log(n);
                }
            }

            main();
            """;

        var output = TestHarness.Run(source, mode);
        Assert.Equal("3\n2\n1\n", output);
    }

    #endregion

    #region for await...of Tests

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void ForAwaitOf_AsyncGenerator_IteratesValues(ExecutionMode mode)
    {
        var source = """
            async function* asyncNumbers() {
                yield 1;
                yield 2;
                yield 3;
            }

            async function main() {
                for await (const num of asyncNumbers()) {
                    console.log(num);
                }
            }

            main();
            """;

        var output = TestHarness.Run(source, mode);
        Assert.Equal("1\n2\n3\n", output);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void ForAwaitOf_AsyncGeneratorWithAwait_IteratesAwaitedValues(ExecutionMode mode)
    {
        var source = """
            async function delay(value: number): Promise<number> {
                return value * 2;
            }

            async function* asyncGen() {
                yield await delay(5);
                yield await delay(10);
                yield await delay(15);
            }

            async function main() {
                for await (const val of asyncGen()) {
                    console.log(val);
                }
            }

            main();
            """;

        var output = TestHarness.Run(source, mode);
        Assert.Equal("10\n20\n30\n", output);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void ForAwaitOf_WithBreak_StopsIteration(ExecutionMode mode)
    {
        var source = """
            async function* numbers() {
                yield 0;
                yield 1;
                yield 2;
                yield 3;
                yield 4;
                yield 5;
            }

            async function main() {
                for await (const num of numbers()) {
                    console.log(num);
                    if (num >= 3) break;
                }
                console.log("done");
            }

            main();
            """;

        var output = TestHarness.Run(source, mode);
        Assert.Equal("0\n1\n2\n3\ndone\n", output);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void ForAwaitOf_WithContinue_SkipsIteration(ExecutionMode mode)
    {
        var source = """
            async function* numbers() {
                yield 1;
                yield 2;
                yield 3;
                yield 4;
                yield 5;
            }

            async function main() {
                for await (const num of numbers()) {
                    if (num % 2 === 0) continue;
                    console.log(num);
                }
            }

            main();
            """;

        var output = TestHarness.Run(source, mode);
        Assert.Equal("1\n3\n5\n", output);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void ForAwaitOf_EmptyAsyncGenerator_NoIterations(ExecutionMode mode)
    {
        var source = """
            async function* empty() {}

            async function main() {
                let count = 0;
                for await (const _ of empty()) {
                    count++;
                }
                console.log("iterations: " + count);
            }

            main();
            """;

        var output = TestHarness.Run(source, mode);
        Assert.Equal("iterations: 0\n", output);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void ForAwaitOf_MultipleLoops_Works(ExecutionMode mode)
    {
        var source = """
            async function* gen() {
                yield 1;
                yield 2;
            }

            async function main() {
                for await (const x of gen()) {
                    console.log("first: " + x);
                }
                for await (const y of gen()) {
                    console.log("second: " + y);
                }
            }

            main();
            """;

        var output = TestHarness.Run(source, mode);
        Assert.Equal("first: 1\nfirst: 2\nsecond: 1\nsecond: 2\n", output);
    }

    #endregion

    #region Async Generator .return() and .throw() Tests

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void AsyncGenerator_Return_ClosesGenerator(ExecutionMode mode)
    {
        var source = """
            async function* asyncGen() {
                yield 1;
                yield 2;
                yield 3;
            }

            async function main() {
                const gen = asyncGen();
                const r1 = await gen.next();
                console.log(r1.value);
                const returnResult = await gen.return(42);
                console.log(returnResult.value + " " + returnResult.done);
                const nextResult = await gen.next();
                console.log(nextResult.done);
            }

            main();
            """;

        var output = TestHarness.Run(source, mode);
        Assert.Equal("1\n42 true\ntrue\n", output);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void AsyncGenerator_Throw_ThrowsError(ExecutionMode mode)
    {
        var source = """
            async function* asyncGen() {
                yield 1;
                yield 2;
            }

            async function main() {
                const gen = asyncGen();
                const r1 = await gen.next();
                console.log(r1.value);
                try {
                    await gen.throw("Test error");
                } catch (e) {
                    console.log("Caught: " + e);
                }
            }

            main();
            """;

        var output = TestHarness.Run(source, mode);
        Assert.Equal("1\nCaught: Test error\n", output);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void AsyncGenerator_ReturnWithoutValue_ReturnsUndefined(ExecutionMode mode)
    {
        // ECMA-262 §27.6.1.3: AsyncGenerator.prototype.return(value) with value absent resolves to
        // { value: undefined, done: true } — an omitted argument is undefined, not null (#618).
        var source = """
            async function* gen() {
                yield 1;
                yield 2;
            }

            async function main() {
                const g = gen();
                await g.next();
                const r = await g.return();
                console.log(r.value + " " + r.done);
            }

            main();
            """;

        var output = TestHarness.Run(source, mode);
        Assert.Equal("undefined true\n", output);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void AsyncGenerator_ThrowWithoutValue_RejectsWithUndefined(ExecutionMode mode)
    {
        // throw() with no argument rejects with undefined, not null (#618).
        var source = """
            async function* gen() { yield 1; yield 2; }
            async function main() {
                const g = gen();
                await g.next();
                try { await g.throw(); } catch (e) { console.log("rejected " + e); }
            }
            main();
            """;

        var output = TestHarness.Run(source, mode);
        Assert.Equal("rejected undefined\n", output);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void AsyncGenerator_BareReturn_CompletesWithUndefined(ExecutionMode mode)
    {
        // A value-less `return;` completes with undefined, not null; `return null;` still reports null (#540).
        var source = """
            async function* bare() { yield 1; return; }
            async function* retNull() { yield 1; return null; }
            async function main() {
                const a = bare();
                await a.next();
                console.log("bare " + (await a.next()).value);
                const b = retNull();
                await b.next();
                console.log("retNull " + (await b.next()).value);
            }
            main();
            """;

        var output = TestHarness.Run(source, mode);
        Assert.Equal("bare undefined\nretNull null\n", output);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void AsyncGenerator_ManualNextOnThrowingGenerator_RejectsCatchably(ExecutionMode mode)
    {
        // A throwing async generator settles its final next() as a rejection (catchable via await),
        // delivering the preceding yields first, rather than propagating unhandled (#566).
        var source = """
            async function* g() { yield 1; throw "b"; }
            async function main() {
                const it = g();
                const r1 = await it.next();
                let err = "none";
                try { await it.next(); } catch (e) { err = "" + e; }
                console.log(r1.value + " err=" + err);
            }
            main();
            """;

        var output = TestHarness.Run(source, mode);
        Assert.Equal("1 err=b\n", output);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void AsyncGenerator_RejectedAwaitInTry_CaughtByOwnCatch(ExecutionMode mode)
    {
        // A rejected await inside a try in an async generator is caught by that try's catch, which
        // binds the rejection reason; the generator then continues to its next yield (#617).
        var source = """
            async function* g() {
                try { await Promise.reject("boom"); } catch (e: any) { console.log("caught " + e); }
                yield 1;
            }
            async function main() { for await (const v of g()) console.log("v" + v); }
            main();
            """;

        var output = TestHarness.Run(source, mode);
        Assert.Equal("caught boom\nv1\n", output);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void AsyncGenerator_ThrowInTryAfterYield_CatchBindsValue(ExecutionMode mode)
    {
        // A throw in a flag-based try (one containing a yield) is caught and the catch parameter binds
        // the thrown value, not null — the catch param is stored to its hoisted field (#617/#477 analog).
        // The caught value is *yielded* (not logged) so the assertion is independent of the interpreter's
        // eager-drain side-effect ordering (#564): yielded values keep their order in both modes.
        var source = """
            async function* g() {
                try { yield 0; throw "x"; } catch (e: any) { yield "caught:" + e; }
                yield 1;
            }
            async function main() { for await (const v of g()) console.log(v); }
            main();
            """;

        var output = TestHarness.Run(source, mode);
        Assert.Equal("0\ncaught:x\n1\n", output);
    }

    #endregion

    #region yield* Delegation Tests

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void AsyncGenerator_YieldStar_DelegatesToSyncIterable(ExecutionMode mode)
    {
        var source = """
            async function* asyncGen() {
                yield* [1, 2, 3];
            }

            async function main() {
                for await (const val of asyncGen()) {
                    console.log(val);
                }
            }

            main();
            """;

        var output = TestHarness.Run(source, mode);
        Assert.Equal("1\n2\n3\n", output);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void AsyncGenerator_YieldStar_DelegatesToAsyncGenerator(ExecutionMode mode)
    {
        var source = """
            async function* inner() {
                yield "a";
                yield "b";
            }

            async function* outer() {
                yield "start";
                yield* inner();
                yield "end";
            }

            async function main() {
                for await (const val of outer()) {
                    console.log(val);
                }
            }

            main();
            """;

        var output = TestHarness.Run(source, mode);
        Assert.Equal("start\na\nb\nend\n", output);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void AsyncGenerator_YieldStar_EmptyIterable(ExecutionMode mode)
    {
        var source = """
            async function* gen() {
                yield 1;
                yield* [];
                yield 2;
            }

            async function main() {
                for await (const val of gen()) {
                    console.log(val);
                }
            }

            main();
            """;

        var output = TestHarness.Run(source, mode);
        Assert.Equal("1\n2\n", output);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void AsyncGenerator_YieldStar_NestedDelegation(ExecutionMode mode)
    {
        var source = """
            async function* level1() {
                yield 1;
            }

            async function* level2() {
                yield* level1();
                yield 2;
            }

            async function* level3() {
                yield* level2();
                yield 3;
            }

            async function main() {
                for await (const val of level3()) {
                    console.log(val);
                }
            }

            main();
            """;

        var output = TestHarness.Run(source, mode);
        Assert.Equal("1\n2\n3\n", output);
    }

    #endregion

    #region Return Value Tests

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void AsyncGenerator_ReturnsValue_IncludedInFinalResult(ExecutionMode mode)
    {
        var source = """
            async function* genWithReturn() {
                yield 1;
                yield 2;
                return "final";
            }

            async function main() {
                const gen = genWithReturn();
                let result = await gen.next();
                console.log(result.value + " " + result.done);
                result = await gen.next();
                console.log(result.value + " " + result.done);
                result = await gen.next();
                console.log(result.value + " " + result.done);
            }

            main();
            """;

        var output = TestHarness.Run(source, mode);
        Assert.Equal("1 false\n2 false\nfinal true\n", output);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void AsyncGenerator_ImplicitReturn_ReturnsUndefined(ExecutionMode mode)
    {
        var source = """
            async function* gen() {
                yield 1;
            }

            async function main() {
                const g = gen();
                await g.next();
                const r = await g.next();
                console.log(r.value + " " + r.done);
            }

            main();
            """;

        var output = TestHarness.Run(source, mode);
        // A generator that runs off the end completes with `undefined`, not null (#481/#540).
        Assert.Equal("undefined true\n", output);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void AsyncGenerator_EarlyReturn_SkipsRemainingYields(ExecutionMode mode)
    {
        var source = """
            async function* gen(earlyReturn: boolean) {
                yield 1;
                if (earlyReturn) return "early";
                yield 2;
                return "normal";
            }

            async function main() {
                const g = gen(true);
                let r = await g.next();
                console.log(r.value + " " + r.done);
                r = await g.next();
                console.log(r.value + " " + r.done);
            }

            main();
            """;

        var output = TestHarness.Run(source, mode);
        Assert.Equal("1 false\nearly true\n", output);
    }

    #endregion

    #region Edge Cases

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void AsyncGenerator_InNamespace_Works(ExecutionMode mode)
    {
        var source = """
            namespace Utils {
                export async function* counter(max: number) {
                    for (let i = 1; i <= max; i++) {
                        yield i;
                    }
                }
            }

            async function main() {
                for await (const n of Utils.counter(3)) {
                    console.log(n);
                }
            }

            main();
            """;

        var output = TestHarness.Run(source, mode);
        Assert.Equal("1\n2\n3\n", output);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void AsyncGenerator_WithStringYields_Works(ExecutionMode mode)
    {
        var source = """
            async function* greetings() {
                yield "hello";
                yield "world";
            }

            async function main() {
                for await (const s of greetings()) {
                    console.log(s);
                }
            }

            main();
            """;

        var output = TestHarness.Run(source, mode);
        Assert.Equal("hello\nworld\n", output);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void AsyncGenerator_YieldingObjects_Works(ExecutionMode mode)
    {
        var source = """
            async function* objects() {
                yield { name: "Alice", age: 30 };
                yield { name: "Bob", age: 25 };
            }

            async function main() {
                for await (const obj of objects()) {
                    console.log(obj.name + " " + obj.age);
                }
            }

            main();
            """;

        var output = TestHarness.Run(source, mode);
        Assert.Equal("Alice 30\nBob 25\n", output);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void AsyncGenerator_YieldingArrays_Works(ExecutionMode mode)
    {
        var source = """
            async function* arrays() {
                yield [1, 2, 3];
                yield [4, 5, 6];
            }

            async function main() {
                for await (const arr of arrays()) {
                    console.log(arr.join(","));
                }
            }

            main();
            """;

        var output = TestHarness.Run(source, mode);
        Assert.Equal("1,2,3\n4,5,6\n", output);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void AsyncGenerator_ConditionalYield_Works(ExecutionMode mode)
    {
        var source = """
            async function* conditional(includeMiddle: boolean) {
                yield 1;
                if (includeMiddle) {
                    yield 2;
                }
                yield 3;
            }

            async function main() {
                console.log("With middle:");
                for await (const n of conditional(true)) {
                    console.log(n);
                }
                console.log("Without middle:");
                for await (const n of conditional(false)) {
                    console.log(n);
                }
            }

            main();
            """;

        var output = TestHarness.Run(source, mode);
        Assert.Equal("With middle:\n1\n2\n3\nWithout middle:\n1\n3\n", output);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void AsyncGenerator_YieldNull_Works(ExecutionMode mode)
    {
        var source = """
            async function* gen() {
                yield null;
                yield 1;
                yield null;
            }

            async function main() {
                for await (const val of gen()) {
                    console.log(val);
                }
            }

            main();
            """;

        var output = TestHarness.Run(source, mode);
        Assert.Equal("null\n1\nnull\n", output);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void AsyncGenerator_YieldBoolean_Works(ExecutionMode mode)
    {
        var source = """
            async function* gen() {
                yield true;
                yield false;
            }

            async function main() {
                for await (const val of gen()) {
                    console.log(val);
                }
            }

            main();
            """;

        var output = TestHarness.Run(source, mode);
        Assert.Equal("true\nfalse\n", output);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void AsyncGenerator_AwaitInYieldExpression_Works(ExecutionMode mode)
    {
        var source = """
            async function getValue(): Promise<number> {
                return 42;
            }

            async function* gen() {
                yield await getValue();
            }

            async function main() {
                for await (const val of gen()) {
                    console.log(val);
                }
            }

            main();
            """;

        var output = TestHarness.Run(source, mode);
        Assert.Equal("42\n", output);
    }

    #endregion

    #region yield await Regression Tests

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void AsyncGenerator_MultipleAwaitsBetweenYields_Works(ExecutionMode mode)
    {
        var source = """
            async function add(a: number, b: number): Promise<number> {
                return a + b;
            }

            async function* gen() {
                const x = await add(1, 2);
                const y = await add(x, 3);
                yield y;
                const z = await add(y, 4);
                yield z;
            }

            async function main() {
                for await (const val of gen()) {
                    console.log(val);
                }
            }

            main();
            """;

        var output = TestHarness.Run(source, mode);
        Assert.Equal("6\n10\n", output);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void AsyncGenerator_MultipleYieldAwait_InSequence(ExecutionMode mode)
    {
        var source = """
            async function getValue(n: number): Promise<number> {
                return n * 10;
            }

            async function* gen() {
                yield await getValue(1);
                yield await getValue(2);
                yield await getValue(3);
            }

            async function main() {
                for await (const val of gen()) {
                    console.log(val);
                }
            }

            main();
            """;

        var output = TestHarness.Run(source, mode);
        Assert.Equal("10\n20\n30\n", output);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void AsyncGenerator_MixedYieldAndYieldAwait(ExecutionMode mode)
    {
        var source = """
            async function getValue(n: number): Promise<number> {
                return n * 10;
            }

            async function* gen() {
                yield 1;
                yield await getValue(2);
                yield 3;
                yield await getValue(4);
                yield 5;
            }

            async function main() {
                for await (const val of gen()) {
                    console.log(val);
                }
            }

            main();
            """;

        var output = TestHarness.Run(source, mode);
        Assert.Equal("1\n20\n3\n40\n5\n", output);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void AsyncGenerator_YieldAwaitThenStandaloneAwait(ExecutionMode mode)
    {
        var source = """
            async function getValue(n: number): Promise<number> {
                return n * 10;
            }

            async function* gen() {
                yield await getValue(1);
                const x = await getValue(2);
                yield x;
                yield await getValue(3);
            }

            async function main() {
                for await (const val of gen()) {
                    console.log(val);
                }
            }

            main();
            """;

        var output = TestHarness.Run(source, mode);
        Assert.Equal("10\n20\n30\n", output);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void AsyncGenerator_YieldAwaitWithComputation(ExecutionMode mode)
    {
        var source = """
            async function getValue(n: number): Promise<number> {
                return n;
            }

            async function* gen() {
                yield await getValue(5) + 1;
                yield await getValue(10) * 2;
            }

            async function main() {
                for await (const val of gen()) {
                    console.log(val);
                }
            }

            main();
            """;

        var output = TestHarness.Run(source, mode);
        Assert.Equal("6\n20\n", output);
    }

    #endregion

    #region Completion / resume value semantics — #481, #540

    [Theory]
    [MemberData(nameof(ExecutionModes.CompiledOnly), MemberType = typeof(ExecutionModes))]
    public void AsyncGenerator_ResumedYield_EvaluatesToUndefined(ExecutionMode mode)
    {
        // The resumed `yield` expression evaluates to undefined (no value sent), not null (#481, the
        // async analog of #443). Previously the compiled emitter loaded CLR null here.
        //
        // COMPILED-ONLY: #481 is a compiled-emitter bug. The interpreter eagerly drains the body and
        // does not bind a variable whose initializer is a yield (`const r = yield 1`) at all — it
        // throws "Undefined variable 'r'" — a separate pre-existing eager-drain gap, not what #481 fixes.
        var source = """
            async function* ag() { const r = yield 1; console.log("r=" + r); }
            async function main() { for await (const v of ag()) {} }
            main();
            """;

        Assert.Equal("r=undefined\n", TestHarness.Run(source, mode));
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.CompiledOnly), MemberType = typeof(ExecutionModes))]
    public void AsyncGenerator_YieldStarCompletion_EvaluatesToUndefined(ExecutionMode mode)
    {
        // The completion value of `yield* inner()` (when the delegate has no explicit return value) is
        // undefined, not null (#481). COMPILED-ONLY for the same eager-drain reason as the resumed-yield
        // test: the interpreter does not bind `const x = yield* …`.
        var source = """
            async function* inner() { yield 2; yield 3; }
            async function* g() { const x = yield* inner(); console.log("x=" + x); yield 4; }
            async function main() { for await (const v of g()) console.log("v" + v); }
            main();
            """;

        Assert.Equal("v2\nv3\nx=undefined\nv4\n", TestHarness.Run(source, mode));
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void AsyncGenerator_NextAfterDone_ReportsUndefinedNotStaleReturn(ExecutionMode mode)
    {
        // The return value is delivered exactly once; every later next() reports undefined, not the
        // stale completion value replayed forever (#540, async analog of #499/#480).
        var source = """
            async function* ag() { yield 1; return 42; }
            async function main() {
                const it = ag();
                console.log((await it.next()).value);
                console.log((await it.next()).value);
                console.log((await it.next()).value);
                console.log((await it.next()).value);
            }
            main();
            """;

        Assert.Equal("1\n42\nundefined\nundefined\n", TestHarness.Run(source, mode));
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void AsyncGenerator_NextAfterReturn_ReportsUndefinedNotLastYielded(ExecutionMode mode)
    {
        // After return(v) delivers { value: v, done: true }, a later next() reports undefined — the
        // last *yielded* value must not replay (#540).
        var source = """
            async function* ag() { yield 1; yield 2; }
            async function main() {
                const it = ag();
                console.log((await it.next()).value);
                console.log(JSON.stringify(await it.return(99)));
                console.log((await it.next()).value);
            }
            main();
            """;

        Assert.Equal("1\n{\"value\":99,\"done\":true}\nundefined\n", TestHarness.Run(source, mode));
    }

    #endregion

    #region Re-entrant next() — "already running" guard (#542)

    [Theory]
    [MemberData(nameof(ExecutionModes.CompiledOnly), MemberType = typeof(ExecutionModes))]
    public void AsyncGenerator_ReentrantNext_Compiled_RejectsInsteadOfStackOverflow(ExecutionMode mode)
    {
        // A compiled async generator whose body advances itself previously recursed into MoveNextAsync
        // until the stack overflowed (RangeError: Maximum call stack size exceeded). ECMA-262 §27.6.3
        // queues such a request, but under this synchronous drive a real queue would deadlock the only
        // reachable re-entrancy case (the body awaiting its own request), so the guard rejects with a
        // catchable TypeError instead of crashing (#542). Observed here via a `for await…of` consumer,
        // whose next()-drive surfaces the rejection to the enclosing try/catch.
        //
        // COMPILED-ONLY: the interpreter eagerly drains the body, so its re-entrant next() returns a
        // (non-conformant) done result rather than recursing — it never hit the stack overflow this
        // guards against, and asserting that divergent eager-drain output here would not test #542.
        var source = """
            const h: any = {};
            async function* g() { await h.it.next(); yield 1; }
            h.it = g();
            async function main() {
              try {
                for await (const v of h.it) { console.log("v" + v); }
              } catch (e: any) {
                console.log("caught: " + e.name + " " + e.message);
              }
            }
            main();
            """;

        Assert.Equal("caught: TypeError Async generator is already running\n", TestHarness.Run(source, mode));
    }

    #endregion
}
