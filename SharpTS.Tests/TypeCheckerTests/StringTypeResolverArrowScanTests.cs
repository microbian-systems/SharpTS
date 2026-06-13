using SharpTS.Tests.Infrastructure;
using SharpTS.TypeSystem.Exceptions;
using Xunit;

namespace SharpTS.Tests.TypeCheckerTests;

/// <summary>
/// The string-based type resolver (<c>TypeChecker.TypeParsing.cs</c>) tracks bracket-nesting depth
/// to find top-level operators. Several scanners treated <b>every</b> <c>&gt;</c> as a closing
/// bracket — including the <c>&gt;</c> of an arrow <c>=&gt;</c>, which has no matching <c>&lt;</c> —
/// so a single function type in a string-resolved composite drove the depth negative and subsequent
/// top-level tokens were missed (#462). The fix applies the established <c>=&gt;</c> guard
/// (<c>i == 0 || s[i - 1] != '='</c>) to the six previously-unguarded scanners
/// (<c>SplitTupleElements</c>, <c>TryParseIndexedAccessType</c>, <c>FindTopLevelKeyword</c>,
/// <c>FindTopLevelChar</c>, <c>FindConditionalElseColon</c>, <c>FindTopLevelAs</c>), matching the
/// sibling scanners that already guarded it.
/// </summary>
public class StringTypeResolverArrowScanTests
{
    // ---- SplitTupleElements: a tuple element that is itself a function type ----

    [Fact]
    public void TupleWithFunctionElement_RejectsNonFunction()
    {
        // Without the guard, the ',' separating the two elements is read at negative depth (the '>'
        // of '() => number' having consumed a phantom bracket), so the tuple is mis-split and
        // element 0's function type is lost — a string is then wrongly accepted in slot 0.
        var source = """
            type Pair = [() => number, string];
            const bad: Pair = ["notfn", "x"];
            """;
        Assert.ThrowsAny<TypeCheckException>(() => TestHarness.RunInterpreted(source));
    }

    [Fact]
    public void TupleWithFunctionElement_AcceptsMatchingTuple()
    {
        var source = """
            type Pair = [() => number, string];
            const ok: Pair = [() => 1, "x"];
            """;
        TestHarness.RunInterpreted(source);
    }

    // ---- TryParseIndexedAccessType: indexing an inline object whose member is a function type ----

    [Fact]
    public void IndexedAccessIntoArrowMember_ResolvesToFunctionType()
    {
        // `{ a: () => number }["a"]` is `() => number`; the arrow inside the object previously drove
        // the index-bracket scan negative so the `["a"]` was never found and the whole type garbled
        // to something a bare number satisfied.
        var source = """
            type X = { a: () => number }["a"];
            const bad: X = 5;
            """;
        Assert.ThrowsAny<TypeCheckException>(() => TestHarness.RunInterpreted(source));
    }

    [Fact]
    public void IndexedAccessIntoArrowMember_AcceptsFunction()
    {
        var source = """
            type X = { a: () => number }["a"];
            const ok: X = () => 1;
            """;
        TestHarness.RunInterpreted(source);
    }

    // ---- conditional detection over a function-typed extends clause, via the string path ----
    // A generic alias instantiation expands through string substitution (TypeChecker.Generics.cs),
    // forcing the conditional through FindTopLevelKeyword(" extends ") / FindTopLevelChar('?') /
    // FindConditionalElseColon(':'). A top-level `=> ` in the check or extends type used to hide the
    // `extends`/`?`/`:` from those scans, so the conditional was not recognized and collapsed to `any`.

    [Fact]
    public void ConditionalFunctionExtends_StringPath_InfersReturnType()
    {
        // Ret<() => string> resolves to string; returning a number violates it.
        var source = """
            type Ret<T> = T extends () => infer U ? U : "no";
            function f(): Ret<() => string> { return 42; }
            """;
        Assert.ThrowsAny<TypeCheckException>(() => TestHarness.RunInterpreted(source));
    }

    [Fact]
    public void ConditionalFunctionExtends_StringPath_AcceptsInferredReturn()
    {
        var source = """
            type Ret<T> = T extends () => infer U ? U : "no";
            function f(): Ret<() => string> { return "hello"; }
            """;
        TestHarness.RunInterpreted(source);
    }

    [Fact]
    public void ConditionalFunctionExtends_NonFunction_TakesFalseBranch()
    {
        // `string` is not callable, so U never binds and the conditional resolves to its false
        // branch ("no"); a number return violates that literal.
        var source = """
            type Ret<T> = T extends () => infer U ? U : "no";
            function f(): Ret<string> { return 42; }
            """;
        Assert.ThrowsAny<TypeCheckException>(() => TestHarness.RunInterpreted(source));
    }

    [Fact]
    public void NestedConditionalWithArrows_SelectsOuterElseBranch()
    {
        // The nested conditional in the true branch contributes its own '?'/':' pair; the outer
        // else-colon scan (FindConditionalElseColon) must stay ternary-depth aware AND skip the
        // arrow's '>'. Ret<() => number> -> A=number -> (number extends string ? ...) is false ->
        // "other"; returning "s" (the inner THEN literal) must be rejected.
        var source = """
            type Ret<T> = T extends () => infer A ? (A extends string ? "s" : "other") : "no";
            function f(): Ret<() => number> { return "s"; }
            """;
        Assert.ThrowsAny<TypeCheckException>(() => TestHarness.RunInterpreted(source));
    }

    [Fact]
    public void NestedConditionalWithArrows_AcceptsSelectedBranch()
    {
        var source = """
            type Ret<T> = T extends () => infer A ? (A extends string ? "s" : "other") : "no";
            function f(): Ret<() => number> { return "other"; }
            """;
        TestHarness.RunInterpreted(source);
    }
}
