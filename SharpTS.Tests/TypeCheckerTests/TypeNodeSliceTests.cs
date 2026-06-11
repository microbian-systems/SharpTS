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
        // Mapped-type alias bodies have no node form yet, so the reference falls back.
        TypeNodeStats.Reset();
        TestHarness.RunInterpreted("""
            type RO<T> = { [K in keyof T]: T[K] };
            var r: RO<{ a: number }> = { a: 1 };
            """);
        Assert.True(TypeNodeStats.StringFallbacks >= 1,
            $"expected the mapped-alias annotation to fall back, got {TypeNodeStats.StringFallbacks}");
    }

    [Fact]
    public void NodePath_EngagesForGenericAliasReferences()
    {
        TypeNodeStats.Reset();
        TestHarness.RunInterpreted("""
            type Box<T> = { value: T };
            type PairOf<A, B> = [A, B];
            type Handler<T> = (item: T) => void;
            var b: Box<number> = { value: 1 };
            var p: PairOf<string, number> = ["x", 1];
            var h: Handler<string> = (s: string) => {};
            """);
        Assert.True(TypeNodeStats.NodeHits >= 3,
            $"expected the node path for all three alias annotations, got {TypeNodeStats.NodeHits}");
        Assert.Equal(0, TypeNodeStats.StringFallbacks);
    }

    [Fact]
    public void NodeResolved_GenericAliasEnforcesSubstitutedArguments()
    {
        Assert.ThrowsAny<TypeCheckException>(() => TestHarness.RunInterpreted("""
            type Box<T> = { value: T };
            var b: Box<number> = { value: "no" };
            """));
        Assert.ThrowsAny<TypeCheckException>(() => TestHarness.RunInterpreted("""
            type Handler<T> = (item: T) => void;
            var h: Handler<string> = (n: number) => {};
            """));
        // Wrong arity carries the string path's TS2314.
        Assert.ThrowsAny<TypeCheckException>(() => TestHarness.RunInterpreted("""
            type Box<T> = { value: T };
            var b: Box<number, string> = { value: 1 };
            """));
    }

    [Fact]
    public void NodeResolved_RecursiveAliasStillConverges()
    {
        // Self-referential alias: the recursion placeholder must fire on the node path the
        // same way it does on the string path (no stack overflow, no spurious error).
        TestHarness.RunInterpreted("""
            type Tree<T> = { value: T; children: Tree<T>[] };
            var t: Tree<number> = { value: 1, children: [{ value: 2, children: [] }] };
            """);
    }

    [Fact]
    public void NodePath_EngagesForGenericReferences()
    {
        TypeNodeStats.Reset();
        TestHarness.RunInterpreted("""
            class Pair<A, B> { constructor(public first: A, public second: B) {} }
            var xs: Array<number> = [1, 2];
            var p: Promise<string> = Promise.resolve("x");
            var pr: Pair<string, number> = new Pair<string, number>("x", 1);
            var part: Partial<{ a: number; b: string }> = { a: 1 };
            """);
        Assert.True(TypeNodeStats.NodeHits >= 4,
            $"expected the node path for all four generic annotations, got {TypeNodeStats.NodeHits}");
        Assert.Equal(0, TypeNodeStats.StringFallbacks);
    }

    [Fact]
    public void NodeResolved_GenericReferencesEnforceArguments()
    {
        Assert.ThrowsAny<TypeCheckException>(() => TestHarness.RunInterpreted("""
            var xs: Array<number> = ["not a number"];
            """));
        Assert.ThrowsAny<TypeCheckException>(() => TestHarness.RunInterpreted("""
            class Pair<A, B> { constructor(public first: A, public second: B) {} }
            var p: Pair<string, number> = new Pair<number, string>(1, "x");
            """));
        // Utility-type expansion through the node path: Partial makes members optional,
        // but still rejects mistyped ones.
        Assert.ThrowsAny<TypeCheckException>(() => TestHarness.RunInterpreted("""
            var part: Partial<{ a: number }> = { a: "no" };
            """));
    }

    [Fact]
    public void NodePath_EngagesForObjectAndTupleTypes()
    {
        TypeNodeStats.Reset();
        TestHarness.RunInterpreted("""
            var o: { name: string; age?: number } = { name: "x" };
            var m: { greet(x: number): string } = { greet: (x: number) => "hi" };
            var ix: { [k: string]: number } = { a: 1 };
            var t: [string, number?] = ["x"];
            var nt: [first: string, rest: number] = ["x", 1];
            """);
        Assert.True(TypeNodeStats.NodeHits >= 5,
            $"expected the node path for all five annotations, got {TypeNodeStats.NodeHits}");
        Assert.Equal(0, TypeNodeStats.StringFallbacks);
    }

    [Fact]
    public void NodeResolved_ObjectTypeEnforcesMembers()
    {
        Assert.ThrowsAny<TypeCheckException>(() => TestHarness.RunInterpreted("""
            var o: { name: string } = { name: 1 };
            """));
        Assert.ThrowsAny<TypeCheckException>(() => TestHarness.RunInterpreted("""
            var o: { name: string } = {};
            """));
        // Optional members may be absent but not mistyped.
        TestHarness.RunInterpreted("""
            var o: { name: string; age?: number } = { name: "x" };
            """);
        Assert.ThrowsAny<TypeCheckException>(() => TestHarness.RunInterpreted("""
            var o: { name: string; age?: number } = { name: "x", age: "old" };
            """));
    }

    [Fact]
    public void NodeResolved_TupleEnforcesArityAndElementTypes()
    {
        Assert.ThrowsAny<TypeCheckException>(() => TestHarness.RunInterpreted("""
            var t: [string, number] = ["x"];
            """));
        Assert.ThrowsAny<TypeCheckException>(() => TestHarness.RunInterpreted("""
            var t: [string, number] = [1, "x"];
            """));
        TestHarness.RunInterpreted("""
            var t: [string, ...number[]] = ["x", 1, 2, 3];
            """);
    }

    [Fact]
    public void NodeResolved_IndexSignatureEnforcesValueType()
    {
        Assert.ThrowsAny<TypeCheckException>(() => TestHarness.RunInterpreted("""
            var ix: { [k: string]: number } = { a: "not a number" };
            """));
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

    [Fact]
    public void NodePath_EngagesForIntersectionKeyofIndexedAndTypeof()
    {
        TypeNodeStats.Reset();
        TestHarness.RunInterpreted("""
            type A = { a: number };
            type B = { b: string };
            var ab: A & B = { a: 1, b: "x" };
            var k: keyof A = "a";
            var v: A["a"] = 1;
            const origin = { n: 5 };
            var t: typeof origin = origin;
            """);
        Assert.True(TypeNodeStats.NodeHits >= 4,
            $"expected the node path for intersection/keyof/indexed/typeof, got {TypeNodeStats.NodeHits}");
        Assert.Equal(0, TypeNodeStats.StringFallbacks);
    }

    [Fact]
    public void NodeResolved_IntersectionMergesMembers()
    {
        // Both members' properties are required in the merged type.
        Assert.ThrowsAny<TypeCheckException>(() => TestHarness.RunInterpreted("""
            type A = { a: number };
            type B = { b: string };
            var ab: A & B = { a: 1 };
            """));
    }

    [Fact]
    public void NodeResolved_KeyofEnforcesKeys()
    {
        Assert.ThrowsAny<TypeCheckException>(() => TestHarness.RunInterpreted("""
            type A = { a: number; b: string };
            var k: keyof A = "c";
            """));
    }

    [Fact]
    public void NodeResolved_IndexedAccessEnforcesValueType()
    {
        Assert.ThrowsAny<TypeCheckException>(() => TestHarness.RunInterpreted("""
            type A = { a: number };
            var v: A["a"] = "not a number";
            """));
    }

    [Fact]
    public void NodePath_EngagesForConditionalAliasWithInfer()
    {
        // The alias definition is now a ConditionalTypeNode carrying an InferTypeNode, so the
        // reference expands through the node path (no string fallback).
        TypeNodeStats.Reset();
        TestHarness.RunInterpreted("""
            type Elem<T> = T extends (infer U)[] ? U : never;
            var e: Elem<number[]> = 1;
            """);
        Assert.True(TypeNodeStats.NodeHits >= 1,
            $"expected the conditional/infer alias on the node path, got {TypeNodeStats.NodeHits}");
        Assert.Equal(0, TypeNodeStats.StringFallbacks);
    }

    [Fact]
    public void NodeResolved_ConditionalEvaluatesToSelectedBranch()
    {
        // IsNum<number> resolves to the true branch ("yes"), so "no" must be rejected — the same
        // verdict the string path produces (EvaluateConditionalType is path-independent).
        Assert.ThrowsAny<TypeCheckException>(() => TestHarness.RunInterpreted("""
            type IsNum<T> = T extends number ? "yes" : "no";
            var r: IsNum<number> = "no";
            """));
        // The false branch is selected for a non-number argument.
        Assert.ThrowsAny<TypeCheckException>(() => TestHarness.RunInterpreted("""
            type IsNum<T> = T extends number ? "yes" : "no";
            var r: IsNum<string> = "yes";
            """));
    }
}
