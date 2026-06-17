using SharpTS.Tests.Infrastructure;
using SharpTS.TypeSystem.Exceptions;
using Xunit;

namespace SharpTS.Tests.SharedTests;

/// <summary>
/// Regression tests for #592: a class body can declare a computed symbol-keyed method, including the
/// canonical iterable hook <c>[Symbol.iterator]()</c> (plus the generator <c>*[Symbol.iterator]()</c>
/// and async <c>async *[Symbol.asyncIterator]()</c> forms). Previously the parser rejected the
/// computed <c>[</c> form for methods, so a user-defined iterable class couldn't be written at all.
///
/// Runtime behavior is asserted interpreted-only: compiled-mode dispatch is a tracked follow-up
/// (#647) for which the IL backend deliberately raises a clear compile error (see the bottom test).
/// </summary>
public class ComputedSymbolKeyedMethodTests
{
    // ---- Interpreter runtime ----

    [Fact]
    public void ComputedIterator_ObjectReturningForm_ForOf()
    {
        // The exact repro shape from #592.
        var source = """
            class Range {
              [Symbol.iterator]() {
                let i = 0;
                return { next() { return i < 2 ? { value: i++, done: false } : { value: 0, done: true }; } };
              }
            }
            for (const x of new Range()) console.log(x);
            """;
        Assert.Equal("0\n1\n", TestHarness.RunInterpreted(source));
    }

    [Fact]
    public void ComputedIterator_GeneratorForm_ForOf()
    {
        var source = """
            class GenRange { *[Symbol.iterator]() { yield 10; yield 20; } }
            for (const x of new GenRange()) console.log(x);
            """;
        Assert.Equal("10\n20\n", TestHarness.RunInterpreted(source));
    }

    [Fact]
    public void ComputedAsyncIterator_GeneratorForm_ForAwait()
    {
        var source = """
            class AsyncRange { async *[Symbol.asyncIterator]() { yield 1; yield 2; } }
            async function run() {
              for await (const x of new AsyncRange()) console.log(x);
            }
            run();
            """;
        Assert.Equal("1\n2\n", TestHarness.RunInterpreted(source));
    }

    [Fact]
    public void ComputedIterator_UsesThis()
    {
        var source = """
            class Counter {
              limit = 3;
              *[Symbol.iterator]() { for (let i = 0; i < this.limit; i++) yield i; }
            }
            const out: number[] = [];
            for (const x of new Counter()) out.push(x);
            console.log(out.join(","));
            """;
        Assert.Equal("0,1,2\n", TestHarness.RunInterpreted(source));
    }

    [Fact]
    public void ComputedStringKeyedMethod_FoldsIntoMethodTable()
    {
        // A string-literal computed key is a static name: `["greet"]()` is the method `greet`.
        var source = """
            class W { ["greet"]() { return "hi"; } }
            console.log(new W().greet());
            """;
        Assert.Equal("hi\n", TestHarness.RunInterpreted(source));
    }

    [Fact]
    public void ComputedIterator_SpreadAndArrayFrom()
    {
        var source = """
            class R { *[Symbol.iterator]() { yield 1; yield 2; yield 3; } }
            console.log([...new R()].join(","));
            console.log(Array.from(new R()).length);
            """;
        Assert.Equal("1,2,3\n3\n", TestHarness.RunInterpreted(source));
    }

    [Fact]
    public void ComputedIterator_InheritedFromSuperclass()
    {
        var source = """
            class Base { *[Symbol.iterator]() { yield 7; yield 8; } }
            class Derived extends Base {}
            console.log([...new Derived()].join(","));
            """;
        Assert.Equal("7,8\n", TestHarness.RunInterpreted(source));
    }

    // ---- Type checker ----

    [Fact]
    public void ComputedMethod_Body_IsTypeChecked()
    {
        // A type error inside the computed method body must be reported.
        Assert.ThrowsAny<TypeCheckException>(() =>
            TestHarness.RunInterpreted("""
                class C { [Symbol.iterator]() { const x: number = "bad"; return x; } }
                new C();
                """));
    }

    [Fact]
    public void ComputedIterator_MakesClassStructurallyIterable()
    {
        // The `@@iterator` member registration lets the structural-iterable typing (#485) accept the
        // class as a spread source — this would error ("must be an iterable type") before the fix.
        TestHarness.RunInterpreted("""
            class R { *[Symbol.iterator]() { yield 1; } }
            const arr: number[] = [...new R()];
            console.log(arr.length);
            """);
    }

    [Fact]
    public void TwoComputedMethods_NoSpuriousOverloadError()
    {
        // Two computed methods share the synthetic "<computed>" name internally; they must not be
        // mistaken for duplicate overloads of one method.
        TestHarness.RunInterpreted("""
            class M {
              [Symbol.iterator]() { return { next() { return { value: 0, done: true }; } }; }
              [Symbol.asyncIterator]() { return null; }
            }
            console.log("ok");
            """);
    }

    // ---- Compiled mode: tracked follow-up (#647) raises a clear compile error ----

    [Fact]
    public void ComputedMethod_Compiled_RaisesClearCompileError()
    {
        var ex = Assert.ThrowsAny<Exception>(() =>
            TestHarness.RunCompiled("class R { [Symbol.iterator]() { return { next() { return { value: 0, done: true }; } }; } } new R();"));
        Assert.Contains("computed symbol-keyed class methods", ex.Message);
    }
}
