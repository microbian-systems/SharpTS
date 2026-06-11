using SharpTS.Parsing;
using SharpTS.Tests.Infrastructure;
using SharpTS.TypeSystem.Exceptions;
using Xunit;

namespace SharpTS.Tests.TypeCheckerTests;

/// <summary>
/// Type-AST migration slice (docs/plans/type-ast-design.md): variable annotations resolve
/// node-first for named/literal/array/union constructs, with string fallback elsewhere. These
/// tests pin (a) that the node path actually engages, and (b) that node-resolved types behave
/// identically to string-resolved ones.
/// </summary>
public class TypeNodeSliceTests
{
    [Fact]
    public void NodePath_EngagesForSupportedAnnotations()
    {
        TypeNodeStats.Reset();
        TestHarness.RunInterpreted("""
            var a: number = 1;
            var b: string[] = ["x"];
            var c: "on" | "off" = "on";
            var d: number | string = 1;
            """);
        Assert.True(TypeNodeStats.NodeHits >= 4,
            $"expected the node path for all four annotations, got {TypeNodeStats.NodeHits}");
    }

    [Fact]
    public void NodePath_FallsBackForUnsupportedConstructs()
    {
        TypeNodeStats.Reset();
        TestHarness.RunInterpreted("""
            var f: { a: number } = { a: 1 };
            """);
        Assert.True(TypeNodeStats.StringFallbacks >= 1,
            $"expected the object-type annotation to fall back, got {TypeNodeStats.StringFallbacks}");
    }

    [Fact]
    public void NodePath_EngagesForFunctionTypes()
    {
        TypeNodeStats.Reset();
        TestHarness.RunInterpreted("""
            var f: (x: number) => string = (x) => "s";
            var g: () => void = () => {};
            var h: (x: number, y?: string, ...rest: boolean[]) => number = (x) => x;
            const k: (cb: (n: number) => void) => void = (cb) => cb(1);
            """);
        Assert.True(TypeNodeStats.NodeHits >= 4,
            $"expected the node path for all four function-type annotations, got {TypeNodeStats.NodeHits}");
        Assert.Equal(0, TypeNodeStats.StringFallbacks);
    }

    [Fact]
    public void NodeResolved_FunctionTypeEnforcesParamAndReturn()
    {
        Assert.ThrowsAny<TypeCheckException>(() => TestHarness.RunInterpreted("""
            var f: (x: number) => string = (x: string) => x;
            """));
        Assert.ThrowsAny<TypeCheckException>(() => TestHarness.RunInterpreted("""
            var f: () => string = () => 1;
            """));
    }

    [Fact]
    public void NodeResolved_FunctionTypeArityHonorsOptionalAndRest()
    {
        // A two-required-param target rejects a source requiring three; optional/rest params
        // must not count toward the node-built signature's required arity.
        TestHarness.RunInterpreted("""
            var f: (a: number, b: number, c?: number) => void = (a: number, b: number) => {};
            """);
        Assert.ThrowsAny<TypeCheckException>(() => TestHarness.RunInterpreted("""
            var g: (a: number) => void = (a: number, b: number, c: number) => {};
            """));
    }

    [Fact]
    public void NodeResolved_ConstructorTypeIsConstructable()
    {
        TypeNodeStats.Reset();
        TestHarness.RunInterpreted("""
            class Widget { constructor(public id: number) {} }
            var make: new (id: number) => Widget = Widget;
            const w: Widget = new make(1);
            """);
        Assert.True(TypeNodeStats.NodeHits >= 1,
            $"expected the constructor-type annotation on the node path, got {TypeNodeStats.NodeHits}");
    }

    [Fact]
    public void NodeResolved_UnionEnforcesMembers()
    {
        Assert.ThrowsAny<TypeCheckException>(() => TestHarness.RunInterpreted("""
            var c: "on" | "off" = "neither";
            """));
    }

    [Fact]
    public void NodeResolved_ArrayEnforcesElementType()
    {
        Assert.ThrowsAny<TypeCheckException>(() => TestHarness.RunInterpreted("""
            var b: string[] = [1];
            """));
    }

    [Fact]
    public void NodeResolved_NamedTypesResolveInScope()
    {
        // Type parameters and class names must resolve identically to the string path —
        // the node path delegates bare names to the same single-name resolution.
        TestHarness.RunInterpreted("""
            class Base { foo: string; }
            function f<T>(x: T) {
                var y: T = x;
                var z: Base = new Base();
                var u: Base | null = null;
            }
            """);
    }

    [Fact]
    public void NodeResolved_UnionNormalizesAnyAndNever()
    {
        TestHarness.RunInterpreted("""
            var a: any | never = null;
            """);
    }
}
