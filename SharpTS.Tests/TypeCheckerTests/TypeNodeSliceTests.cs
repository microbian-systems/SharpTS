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
            var f: (x: number) => string = (x) => "s";
            """);
        Assert.True(TypeNodeStats.StringFallbacks >= 1,
            $"expected the function-type annotation to fall back, got {TypeNodeStats.StringFallbacks}");
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
