using SharpTS.Tests.Infrastructure;
using SharpTS.TypeSystem.Exceptions;
using Xunit;

namespace SharpTS.Tests.SharedTests;

/// <summary>
/// Tests for computed symbol-keyed class methods — `class C { [Symbol.iterator]() {…} }`, the
/// generator form `*[Symbol.iterator]()` and the async form `async *[Symbol.asyncIterator]()`
/// (#592 parser/type-checker/interpreter; #647 compiled mode). Cases proven in both back ends use
/// <see cref="ExecutionModes.All"/>; cases pending compiled support use
/// <see cref="ExecutionModes.InterpretedOnly"/>.
/// </summary>
public class ComputedSymbolMethodTests
{
    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void ObjectReturningIterator_ForOf_Works(ExecutionMode mode)
    {
        var source = """
            class Range {
              [Symbol.iterator]() {
                let i = 0;
                return { next() { return i < 2 ? { value: i++, done: false } : { value: 0, done: true }; } };
              }
            }
            for (const x of new Range()) console.log(x);
            """;

        Assert.Equal("0\n1\n", TestHarness.Run(source, mode));
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void GeneratorIterator_ForOfAndSpread_Works(ExecutionMode mode)
    {
        var source = """
            class Range { *[Symbol.iterator]() { yield 1; yield 2; } }
            for (const x of new Range()) console.log(x);
            console.log([...new Range()].length);
            """;

        Assert.Equal("1\n2\n2\n", TestHarness.Run(source, mode));
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void GeneratorIterator_CapturesThis(ExecutionMode mode)
    {
        var source = """
            class Range {
              constructor(private start: number, private end: number) {}
              *[Symbol.iterator](): Iterator<number> {
                for (let i = this.start; i < this.end; i++) yield i;
              }
            }
            let sum = 0;
            for (const n of new Range(1, 5)) sum += n;
            console.log(sum);
            """;

        Assert.Equal("10\n", TestHarness.Run(source, mode));
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void GeneratorIterator_ElementTypeIsNumber(ExecutionMode mode)
    {
        // The spread must yield number[], not Iterator<number>[] — the factory's iterator
        // return type must not be double-wrapped into Generator<Iterator<number>>.
        var source = """
            class Range { *[Symbol.iterator](): Iterator<number> { yield 1; yield 2; yield 3; } }
            const arr: number[] = [...new Range()];
            console.log(arr.length);
            """;

        Assert.Equal("3\n", TestHarness.Run(source, mode));
    }

    // Compiled mode: a non-symbol computed key (`[KEY]()` with KEY a string) would need a
    // dynamically-named .NET method; the symbol-method registry only backs symbol keys, and named
    // access doesn't consult it. Interpreter-only; tracked as a follow-up.
    [Theory]
    [MemberData(nameof(ExecutionModes.InterpretedOnly), MemberType = typeof(ExecutionModes))]
    public void NonSymbolComputedMethodKey_FoldsToNamedMethod(ExecutionMode mode)
    {
        var source = """
            const KEY = "dyn";
            class C { [KEY]() { return 42; } }
            console.log((new C() as any).dyn());
            """;

        Assert.Equal("42\n", TestHarness.Run(source, mode));
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void AsyncGeneratorIterator_ForAwait_Works(ExecutionMode mode)
    {
        var source = """
            class ARange { async *[Symbol.asyncIterator]() { yield 10; yield 20; } }
            async function main() {
              for await (const x of new ARange()) console.log(x);
            }
            main();
            """;

        Assert.Equal("10\n20\n", TestHarness.Run(source, mode));
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void GenericIterableClass_Works(ExecutionMode mode)
    {
        // Exercises the symbol registry's generic-class handling (registry keyed by the open
        // definition; receiver/MethodInfo closed for invoke).
        var source = """
            class Box<T> {
              constructor(private items: T[]) {}
              *[Symbol.iterator](): Iterator<T> { for (const x of this.items) yield x; }
            }
            for (const n of new Box<number>([1, 2, 3])) console.log(n);
            """;

        Assert.Equal("1\n2\n3\n", TestHarness.Run(source, mode));
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void InheritedComputedIterator_Works(ExecutionMode mode)
    {
        // A subclass inherits the base's [Symbol.iterator] via the registry's base-chain walk.
        var source = """
            class Base { *[Symbol.iterator]() { yield 1; yield 2; } }
            class Derived extends Base {}
            console.log([...new Derived()].length);
            for (const x of new Derived()) console.log(x);
            """;

        Assert.Equal("2\n1\n2\n", TestHarness.Run(source, mode));
    }

    // Reading a symbol method as a value (`obj[Symbol.iterator]`) returns a receiver-bound callable in
    // both back ends, so a standalone `obj[Symbol.iterator]()` call keeps `this` (#755: compiled mode
    // now binds the receiver as `new $TSFunction(obj, method)`, mirroring the string-keyed method read).
    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void DirectSymbolMethodAccess_ReturnsCallable(ExecutionMode mode)
    {
        var source = """
            class R { *[Symbol.iterator]() { yield 5; } }
            const it = (new R() as any)[Symbol.iterator]();
            console.log(it.next().value);
            """;

        Assert.Equal("5\n", TestHarness.Run(source, mode));
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void DirectSymbolMethodAccess_BindsThis(ExecutionMode mode)
    {
        // The returned callable must carry `this` — a standalone `obj[Symbol.iterator]()` reads
        // `this.v` from the receiver (#755).
        var source = """
            class R { v = 9; *[Symbol.iterator]() { yield this.v; yield this.v + 1; } }
            const it = (new R() as any)[Symbol.iterator]();
            console.log(it.next().value);
            console.log(it.next().value);
            """;

        Assert.Equal("9\n10\n", TestHarness.Run(source, mode));
    }

    // Compiled mode: class *expressions* go through a separate emit path that doesn't yet wire
    // computed symbol-keyed methods (class declarations do). Tracked as a follow-up.
    [Theory]
    [MemberData(nameof(ExecutionModes.InterpretedOnly), MemberType = typeof(ExecutionModes))]
    public void ClassExpression_ComputedSymbolMethod_Works(ExecutionMode mode)
    {
        var source = """
            const Range = class { *[Symbol.iterator]() { yield 7; yield 8; } };
            for (const x of new Range()) console.log(x);
            """;

        Assert.Equal("7\n8\n", TestHarness.Run(source, mode));
    }

    [Fact]
    public void PlainClass_StillNotIterable_ReportsError()
    {
        // A class with no [Symbol.iterator] is still rejected as non-iterable (TS2488 / #593).
        var source = """
            class Plain { x = 1; }
            for (const y of new Plain()) console.log(y);
            """;

        Assert.ThrowsAny<TypeCheckException>(() => TestHarness.RunInterpreted(source));
    }
}
