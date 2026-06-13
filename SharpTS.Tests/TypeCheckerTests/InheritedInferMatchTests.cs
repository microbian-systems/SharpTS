using SharpTS.Tests.Infrastructure;
using SharpTS.TypeSystem.Exceptions;
using Xunit;

namespace SharpTS.Tests.TypeCheckerTests;

/// <summary>
/// Conditional-type <c>infer</c> matching against a class instance must see the class's full
/// structural surface — public fields, getters, AND methods, merged across the entire superclass
/// chain (#461 own methods, #492 inherited members). Previously the property source
/// (<c>ExtractPropertiesWithTypes</c>) read only a class's OWN fields and getters, so a structural
/// extends clause like <c>{ val: infer R }</c> silently took its false branch when <c>val</c> was a
/// method or was inherited.
///
/// Each test pins the resolved conditional via an annotation: if <c>J&lt;C&gt;</c> resolves to the
/// true branch (the inferred member type) the matching-typed assignment is accepted and the
/// mismatched one throws; if it wrongly took the false branch (<c>"no"</c>) the expectations invert.
/// Regular (non-<c>declare</c>) classes are used deliberately — <c>declare class</c> currently drops
/// its <c>extends</c> clause entirely (a separate, broader bug tracked outside this fix), which would
/// mask the member-source behavior under test here.
/// </summary>
public class InheritedInferMatchTests
{
    // ---- inherited FIELD (#492) ----

    [Fact]
    public void InheritedField_ResolvesToInferredType()
    {
        // val is declared on Base; Sub inherits it. J<Sub> must be "correct", so "correct" assigns.
        var source = """
            class Base { val: "correct" = "correct"; }
            class Sub extends Base { extra(): void {} }
            type J<T> = T extends { val: infer R } ? R : "no";
            let x: J<Sub> = "correct";
            """;
        TestHarness.RunInterpreted(source);
    }

    [Fact]
    public void InheritedField_WrongAssignment_IsTypeError()
    {
        // J<Sub> is "correct" (inherited), so a number violates it.
        var source = """
            class Base { val: "correct" = "correct"; }
            class Sub extends Base { extra(): void {} }
            type J<T> = T extends { val: infer R } ? R : "no";
            let x: J<Sub> = 42;
            """;
        Assert.ThrowsAny<TypeCheckException>(() => TestHarness.RunInterpreted(source));
    }

    // ---- inherited GETTER (#492) ----

    [Fact]
    public void InheritedGetter_ResolvesToInferredType()
    {
        var source = """
            class Base { get val(): "correct" { return "correct"; } }
            class Sub extends Base { extra(): void {} }
            type J<T> = T extends { val: infer R } ? R : "no";
            let x: J<Sub> = "correct";
            """;
        TestHarness.RunInterpreted(source);
    }

    // ---- inherited METHOD (#492 method variant) ----

    [Fact]
    public void InheritedMethod_ResolvesToInferredReturnType()
    {
        // toJSON is an inherited method; { toJSON(): infer R } must bind R to its return type.
        var source = """
            class Base { toJSON(): "correct" { return "correct"; } }
            class Sub extends Base { extra(): void {} }
            type J<T> = T extends { toJSON(): infer R } ? R : "no";
            let x: J<Sub> = "correct";
            """;
        TestHarness.RunInterpreted(source);
    }

    [Fact]
    public void InheritedMethod_WrongAssignment_IsTypeError()
    {
        var source = """
            class Base { toJSON(): "correct" { return "correct"; } }
            class Sub extends Base { extra(): void {} }
            type J<T> = T extends { toJSON(): infer R } ? R : "no";
            let x: J<Sub> = 42;
            """;
        Assert.ThrowsAny<TypeCheckException>(() => TestHarness.RunInterpreted(source));
    }

    // ---- own METHOD (#461 — the prerequisite this fix also closes) ----

    [Fact]
    public void OwnMethod_ResolvesToInferredReturnType()
    {
        // A class's OWN method must be matchable too — the property source omitted methods entirely.
        var source = """
            class MyClass { toJSON(): "correct" { return "correct"; } }
            type J<T> = T extends { toJSON(): infer R } ? R : "no";
            let x: J<MyClass> = "correct";
            """;
        TestHarness.RunInterpreted(source);
    }

    // ---- deeper chain ----

    [Fact]
    public void TwoLevelInheritance_ResolvesToInferredType()
    {
        // val lives two levels up; the whole Superclass chain is merged.
        var source = """
            class A { val: "correct" = "correct"; }
            class B extends A {}
            class C extends B { extra(): void {} }
            type J<T> = T extends { val: infer R } ? R : "no";
            let x: J<C> = "correct";
            """;
        TestHarness.RunInterpreted(source);
    }

    [Fact]
    public void DerivedMemberShadowsInherited()
    {
        // The derived declaration wins over the inherited one (own-wins precedence).
        var source = """
            class Base { val: "base" = "base"; }
            class Sub extends Base { val: "derived" = "derived"; }
            type J<T> = T extends { val: infer R } ? R : "no";
            let x: J<Sub> = "derived";
            """;
        TestHarness.RunInterpreted(source);
    }

    [Fact]
    public void DerivedMemberShadowsInherited_InheritedValueRejected()
    {
        var source = """
            class Base { val: "base" = "base"; }
            class Sub extends Base { val: "derived" = "derived"; }
            type J<T> = T extends { val: infer R } ? R : "no";
            let x: J<Sub> = "base";
            """;
        Assert.ThrowsAny<TypeCheckException>(() => TestHarness.RunInterpreted(source));
    }

    // ---- generic check type (the receiver is itself a generic instantiation) ----

    [Fact]
    public void GenericInstantiationCheckType_SubstitutesAndInfers()
    {
        // J<Box<number>> binds R to the substituted field type (number).
        var source = """
            class Box<T> { value: T; constructor(v: T) { this.value = v; } }
            type J<T> = T extends { value: infer R } ? R : "no";
            let x: J<Box<number>> = 123;
            """;
        TestHarness.RunInterpreted(source);
    }

    [Fact]
    public void GenericInstantiationCheckType_WrongAssignment_IsTypeError()
    {
        var source = """
            class Box<T> { value: T; constructor(v: T) { this.value = v; } }
            type J<T> = T extends { value: infer R } ? R : "no";
            let x: J<Box<number>> = "nope";
            """;
        Assert.ThrowsAny<TypeCheckException>(() => TestHarness.RunInterpreted(source));
    }

    // ---- exclusions / false-branch controls ----

    [Fact]
    public void PrivateInheritedMember_IsExcluded_TakesFalseBranch()
    {
        // A private inherited member is not part of the structural surface, so the match fails and
        // J<Sub> is the false branch "no" — which "no" satisfies.
        var source = """
            class Base { private val: "correct" = "correct"; }
            class Sub extends Base { extra(): void {} }
            type J<T> = T extends { val: infer R } ? R : "no";
            let x: J<Sub> = "no";
            """;
        TestHarness.RunInterpreted(source);
    }

    [Fact]
    public void PrivateInheritedMember_TrueBranchValueRejected()
    {
        // Confirms the above really took the false branch: "correct" is not assignable to "no".
        var source = """
            class Base { private val: "correct" = "correct"; }
            class Sub extends Base { extra(): void {} }
            type J<T> = T extends { val: infer R } ? R : "no";
            let x: J<Sub> = "correct";
            """;
        Assert.ThrowsAny<TypeCheckException>(() => TestHarness.RunInterpreted(source));
    }

    [Fact]
    public void AbsentMember_TakesFalseBranch()
    {
        // No val anywhere in the hierarchy → false branch.
        var source = """
            class Base { other: number = 1; }
            class Sub extends Base {}
            type J<T> = T extends { val: infer R } ? R : "no";
            let x: J<Sub> = "no";
            """;
        TestHarness.RunInterpreted(source);
    }

    // ---- string-resolver (as-cast) path, to confirm the fix is independent of resolution route ----

    [Fact]
    public void AsCast_InheritedField_ResolvesToInferredType()
    {
        var source = """
            class Base { val: "correct" = "correct"; }
            class Sub extends Base {}
            type J<T> = T extends { val: infer R } ? R : "no";
            const s: "correct" = (null as any as J<Sub>);
            """;
        TestHarness.RunInterpreted(source);
    }
}
