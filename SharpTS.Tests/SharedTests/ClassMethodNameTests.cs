using SharpTS.Tests.Infrastructure;
using Xunit;

namespace SharpTS.Tests.SharedTests;

/// <summary>
/// Tests for class method definitions using identifier names that require
/// disambiguation or reserved-word acceptance: `get`, `set` (contextual
/// keywords that are also accessor openers) and `delete` (reserved word
/// that ES2015+ MethodDefinition accepts as a property name).
/// Runs in both interpreter and compiled modes.
/// </summary>
public class ClassMethodNameTests
{
    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void MethodNamedGet(ExecutionMode mode)
    {
        var source = """
            class Foo {
                get(k: string): string { return "got " + k; }
            }
            const f = new Foo();
            console.log(f.get("x"));
            """;
        var output = TestHarness.Run(source, mode);
        Assert.Equal("got x\n", output);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void MethodNamedSet(ExecutionMode mode)
    {
        var source = """
            class Foo {
                _v: string = "";
                set(k: string, v: string): void { this._v = k + "=" + v; }
                read(): string { return this._v; }
            }
            const f = new Foo();
            f.set("a", "b");
            console.log(f.read());
            """;
        var output = TestHarness.Run(source, mode);
        Assert.Equal("a=b\n", output);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void MethodNamedDelete(ExecutionMode mode)
    {
        var source = """
            class Store {
                count: number = 0;
                delete(k: string): void { this.count++; }
            }
            const s = new Store();
            s.delete("a");
            s.delete("b");
            console.log(s.count);
            """;
        var output = TestHarness.Run(source, mode);
        Assert.Equal("2\n", output);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void GetAccessor_StillWorks(ExecutionMode mode)
    {
        // Regression: `get value()` must still parse as an accessor, not as
        // a method named `get`.
        var source = """
            class Foo {
                _v: number = 42;
                get value(): number { return this._v; }
                set value(v: number) { this._v = v; }
            }
            const f = new Foo();
            console.log(f.value);
            f.value = 99;
            console.log(f.value);
            """;
        var output = TestHarness.Run(source, mode);
        Assert.Equal("42\n99\n", output);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void GetMethod_Coexists_WithGetAccessor_DifferentClasses(ExecutionMode mode)
    {
        var source = """
            class A {
                get(k: string): string { return "a:" + k; }
            }
            class B {
                _v: string = "b";
                get value(): string { return this._v; }
            }
            console.log(new A().get("x"));
            console.log(new B().value);
            """;
        var output = TestHarness.Run(source, mode);
        Assert.Equal("a:x\nb\n", output);
    }

    // Note: fields named `get`/`set` (e.g. `get: number = 10`) remain a
    // parser limitation — the field-declaration branch only matches raw
    // IDENTIFIER and doesn't yet accept contextual-keyword tokens followed
    // by field openers. Methods named `get`/`set` are the practical case
    // that user code and stdlib hit (URLSearchParams etc.); fields with
    // these names are vanishingly rare.
}
