using SharpTS.Tests.Infrastructure;
using SharpTS.TypeSystem.Exceptions;
using Xunit;

namespace SharpTS.Tests.TypeCheckerTests;

/// <summary>
/// A class may list the built-in iterable-protocol interfaces (<c>Iterable&lt;T&gt;</c>,
/// <c>AsyncIterable&lt;T&gt;</c>, <c>Iterator&lt;T&gt;</c>, …) in its <c>implements</c> clause (#756).
/// These lib interfaces are not registered as named environment entries — they resolve only via the
/// generic-type path — so the implements-clause lookup must fall back to a structural satisfaction
/// check rather than reporting "is not an interface". Validation is type-checker-only, so these run
/// interpreted (the type checker runs identically in both back ends).
/// </summary>
public class ImplementsIterableProtocolTests
{
    [Fact]
    public void Class_ImplementsIterable_GeneratorMethod_Accepted()
    {
        // The #756 repro: a generator [Symbol.iterator] makes the class satisfy Iterable<number>.
        var source = """
            class Range implements Iterable<number> {
              *[Symbol.iterator](): Iterator<number> { yield 1; yield 2; }
            }
            for (const x of new Range()) console.log(x);
            """;

        Assert.Equal("1\n2\n", TestHarness.RunInterpreted(source));
    }

    [Fact]
    public void Class_ImplementsIterable_ObjectReturningIterator_Accepted()
    {
        // A non-generator [Symbol.iterator] returning a manual iterator object also satisfies it.
        var source = """
            class Range implements Iterable<number> {
              [Symbol.iterator](): Iterator<number> {
                let i = 0;
                return { next() { return i < 2 ? { value: i++, done: false } : { value: 0, done: true }; } };
              }
            }
            console.log([...new Range()].length);
            """;

        Assert.Equal("2\n", TestHarness.RunInterpreted(source));
    }

    [Fact]
    public void Class_ImplementsIterable_NoTypeArgs_Accepted()
    {
        // `implements Iterable` with no type argument defaults to Iterable<any> (like tsc's Iterable<unknown>).
        var source = """
            class R implements Iterable<number> { *[Symbol.iterator]() { yield 7; } }
            console.log([...new R()][0]);
            """;

        Assert.Equal("7\n", TestHarness.RunInterpreted(source));
    }

    [Fact]
    public void Class_ImplementsAsyncIterable_Accepted()
    {
        // The async parallel: async *[Symbol.asyncIterator]() satisfies AsyncIterable<number>.
        var source = """
            class ARange implements AsyncIterable<number> {
              async *[Symbol.asyncIterator]() { yield 10; yield 20; }
            }
            async function main() { for await (const x of new ARange()) console.log(x); }
            main();
            """;

        Assert.Equal("10\n20\n", TestHarness.RunInterpreted(source));
    }

    [Fact]
    public void Class_ImplementsIterableAndUserInterface_Accepted()
    {
        // A built-in protocol interface mixes with a user interface in the same implements clause.
        var source = """
            interface Named { name: string; }
            class R implements Iterable<number>, Named {
              name = "r";
              *[Symbol.iterator]() { yield 1; }
            }
            console.log(new R().name);
            """;

        Assert.Equal("r\n", TestHarness.RunInterpreted(source));
    }

    [Fact]
    public void ClassExpression_ImplementsIterable_Accepted()
    {
        // Class expressions resolve the implements clause through a separate path — also wired (#756).
        var source = """
            const C = class implements Iterable<number> { *[Symbol.iterator]() { yield 3; yield 4; } };
            for (const x of new C()) console.log(x);
            """;

        Assert.Equal("3\n4\n", TestHarness.RunInterpreted(source));
    }

    [Fact]
    public void Class_ImplementsIterable_NotIterable_Rejected()
    {
        // A class with no [Symbol.iterator] does not satisfy Iterable<number> (TS2420).
        var source = """
            class Bad implements Iterable<number> { x = 1; }
            console.log(new Bad().x);
            """;

        Assert.ThrowsAny<TypeCheckException>(() => TestHarness.RunInterpreted(source));
    }

    [Fact]
    public void Class_ImplementsAsyncIterable_NotAsyncIterable_Rejected()
    {
        var source = """
            class Bad implements AsyncIterable<number> { x = 1; }
            console.log(new Bad().x);
            """;

        Assert.ThrowsAny<TypeCheckException>(() => TestHarness.RunInterpreted(source));
    }

    [Fact]
    public void Class_ImplementsIterable_SyncNotAsync_Rejected()
    {
        // A sync iterable is not an AsyncIterable — the structural async probe must reject it.
        var source = """
            class R implements AsyncIterable<number> { *[Symbol.iterator]() { yield 1; } }
            console.log("x");
            """;

        Assert.ThrowsAny<TypeCheckException>(() => TestHarness.RunInterpreted(source));
    }

    [Fact]
    public void Class_ImplementsIterable_ElementMismatch_Rejected()
    {
        // The element type is checked when known: yielding number does not satisfy Iterable<string>.
        var source = """
            class R implements Iterable<string> { *[Symbol.iterator](): Iterator<number> { yield 1; } }
            console.log("x");
            """;

        Assert.ThrowsAny<TypeCheckException>(() => TestHarness.RunInterpreted(source));
    }

    [Fact]
    public void Class_ImplementsNonInterface_StillRejected()
    {
        // Regression: a non-interface binding in implements position is still "is not an interface".
        var source = """
            const NotIface = 5;
            class C implements NotIface {}
            console.log("x");
            """;

        Assert.ThrowsAny<TypeCheckException>(() => TestHarness.RunInterpreted(source));
    }

    [Fact]
    public void Class_ImplementsUserInterface_MissingMember_StillRejected()
    {
        // Regression: a user interface still validates member-by-member.
        var source = """
            interface Foo { bar(): number; }
            class C implements Foo { baz() { return 1; } }
            console.log("x");
            """;

        Assert.ThrowsAny<TypeCheckException>(() => TestHarness.RunInterpreted(source));
    }
}
