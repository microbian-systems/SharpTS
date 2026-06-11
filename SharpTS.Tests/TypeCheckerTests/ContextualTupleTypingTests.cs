using SharpTS.Tests.Infrastructure;
using SharpTS.TypeSystem.Exceptions;
using Xunit;

namespace SharpTS.Tests.TypeCheckerTests;

/// <summary>
/// Contextual tuple typing for array literals (a narrow slice of tsc's
/// checkExpressionWithContextualType): an array literal checked against a tuple — or a
/// union containing tuples — infers a tuple type instead of T[]. The context propagates
/// through ternary branches and groupings. Pinned by the GH39357 case in
/// assignmentCompatWithDiscriminatedUnion.
/// </summary>
public class ContextualTupleTypingTests
{
    [Fact]
    public void ArrayLiteral_AgainstTupleAnnotation_InfersTuple()
    {
        TestHarness.RunInterpreted("""
            declare const ab: "a" | "b";
            const t: ["a" | "b", number] = [ab, 1];
            """);
    }

    [Fact]
    public void ArrayLiteral_AgainstTupleUnionAnnotation_InfersTuple()
    {
        TestHarness.RunInterpreted("""
            type A = ["a", number] | ["b", number] | ["c", string];
            const a: A = ["c", ""];
            """);
    }

    [Fact]
    public void ContextPropagatesThroughTernary()
    {
        TestHarness.RunInterpreted("""
            type A = ["a", number] | ["c", string];
            declare const cond: boolean;
            const a: A = cond ? ["a", 1] : ["c", ""];
            """);
    }

    [Fact]
    public void ContextPropagatesThroughGrouping()
    {
        TestHarness.RunInterpreted("""
            type A = ["a", number] | ["c", string];
            const a: A = (["c", ""]);
            """);
    }

    [Fact]
    public void GH39357_DiscriminatedTupleUnion_WithOrNarrowing()
    {
        TestHarness.RunInterpreted("""
            type A = ["a", number] | ["b", number] | ["c", string];
            type B = "a" | "b" | "c";
            declare const b: B;
            const a: A = b === "a" || b === "b" ? [b, 1] : ["c", ""];
            """);
    }

    [Fact]
    public void WrongElementType_StillRejected()
    {
        Assert.ThrowsAny<TypeCheckException>(() => TestHarness.RunInterpreted("""
            type A = ["a", number] | ["c", string];
            const a: A = ["a", "not-a-number"];
            """));
    }

    [Fact]
    public void ArityMismatch_StillRejected()
    {
        Assert.ThrowsAny<TypeCheckException>(() => TestHarness.RunInterpreted("""
            type A = ["a", number] | ["c", string];
            const a: A = ["a", 1, 2];
            """));
    }

    [Fact]
    public void ContextAppliesToReturnStatements()
    {
        TestHarness.RunInterpreted("""
            type A = ["a", number] | ["c", string];
            declare const cond: boolean;
            function make(): A {
                return cond ? ["a", 1] : ["c", ""];
            }
            """);
    }

    [Fact]
    public void NestedTupleContext_Propagates()
    {
        TestHarness.RunInterpreted("""
            type Pair = ["p", ["q", number]];
            const p: Pair = ["p", ["q", 1]];
            """);
    }

    [Fact]
    public void NonTupleContext_KeepsArrayInference()
    {
        TestHarness.RunInterpreted("""
            const xs: number[] = [1, 2, 3];
            declare const cond: boolean;
            const ys: number[] = cond ? [1] : [2, 3];
            """);
    }

    [Fact]
    public void TupleValueFlowsAtRuntime()
    {
        // Indexing the union directly (m[0]) hits a separate CheckGetIndex gap (#311),
        // so the runtime shape is observed via JSON.stringify instead.
        var result = TestHarness.RunInterpreted("""
            type A = ["a", number] | ["c", string];
            function make(cond: boolean): A {
                return cond ? ["a", 1] : ["c", "z"];
            }
            console.log(JSON.stringify(make(true)));
            console.log(JSON.stringify(make(false)));
            """);
        Assert.Equal("[\"a\",1]\n[\"c\",\"z\"]\n", result);
    }
}
