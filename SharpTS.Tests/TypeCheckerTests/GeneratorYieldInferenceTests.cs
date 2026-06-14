using SharpTS.Tests.Infrastructure;
using SharpTS.TypeSystem.Exceptions;
using Xunit;

namespace SharpTS.Tests.TypeCheckerTests;

/// <summary>
/// A generator function with no explicit <c>Generator&lt;T&gt;</c> return annotation now infers its yield
/// type from the operand types of its <c>yield</c> / <c>yield*</c> expressions, instead of reusing the
/// (usually empty → <c>void</c>) inferred RETURN type (#548). Once the yield type is real, <c>for...of</c>
/// binds it directly rather than degrading to <c>any</c>, and a nested generator no longer compiles to an
/// invalid <c>IEnumerator&lt;System.Void&gt;</c> (#532).
/// </summary>
public class GeneratorYieldInferenceTests
{
    // ---- #548: yield type derived from yield / yield* operands (was always void) ----

    [Fact]
    public void Generator_InfersYieldType_FromYieldExpressions()
    {
        // The headline #548 example: g() infers Generator<number> (was Generator<void>), so the spread is
        // number[] and reading an element as number type-checks.
        var source = """
            function* g() { yield 1; yield 2; }
            const arr: number[] = [...g()];
            const n: number = arr[0];
            """;
        TestHarness.RunInterpreted(source);
    }

    [Fact]
    public void Generator_InferredYield_IsNotVoid_WrongConsumerRejected()
    {
        // The inferred element is number, so assigning it to string is a genuine TS2322 — previously the
        // element was void and the error named the wrong type.
        var source = """
            function* g() { yield 1; }
            const s: string = [...g()][0];
            """;
        var ex = Assert.ThrowsAny<TypeCheckException>(() => TestHarness.RunInterpreted(source));
        Assert.Equal("TS2322", ex.Diagnostic.TsCode);
    }

    [Fact]
    public void Generator_ForOf_BindsInferredYieldType()
    {
        // VisitForOf now extracts Generator.YieldType (it deliberately did not before, because a
        // delegating-only generator inferred a void yield — now fixed).
        var source = """
            function* g() { yield 1; yield 2; }
            for (const x of g()) { const n: number = x; }
            """;
        TestHarness.RunInterpreted(source);
    }

    [Fact]
    public void Generator_ForOf_WrongElementUse_Rejected()
    {
        var source = """
            function* g() { yield 1; }
            for (const x of g()) { const s: string = x; }
            """;
        Assert.ThrowsAny<TypeCheckException>(() => TestHarness.RunInterpreted(source));
    }

    [Fact]
    public void Generator_YieldStar_ContributesDelegatedElementType()
    {
        // yield* over an iterable contributes the delegate's element type to the enclosing yield type.
        var source = """
            function* g() { yield* [1, 2, 3]; }
            const arr: number[] = [...g()];
            """;
        TestHarness.RunInterpreted(source);
    }

    [Fact]
    public void Generator_YieldStar_ElementMismatch_Rejected()
    {
        var source = """
            function* g() { yield* [1, 2, 3]; }
            const arr: string[] = [...g()];
            """;
        Assert.ThrowsAny<TypeCheckException>(() => TestHarness.RunInterpreted(source));
    }

    [Fact]
    public void Generator_MixedYields_UnionsYieldType()
    {
        var source = """
            function* g() { yield 1; yield "a"; }
            const arr: (number | string)[] = [...g()];
            """;
        TestHarness.RunInterpreted(source);
    }

    [Fact]
    public void Generator_ReturnValue_DoesNotBecomeYieldType()
    {
        // The `return` value is the (discarded) TReturn, not the yield type: a generator that only yields
        // numbers but returns a string still infers Generator<number>, so the string return must not make
        // the elements assignable to string.
        var source = """
            function* g() { yield 1; return "done"; }
            const s: string = [...g()][0];
            """;
        Assert.ThrowsAny<TypeCheckException>(() => TestHarness.RunInterpreted(source));
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void Generator_InferredYield_IteratesInBothModes(ExecutionMode mode)
    {
        // Type-level change must not perturb interpretation or IL codegen.
        var source = """
            function* g() { yield 10; yield 20; yield 30; }
            let sum = 0;
            for (const x of g()) { sum += x; }
            console.log(sum);
            """;
        Assert.Equal("60\n", TestHarness.Run(source, mode));
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void Generator_EmptyYieldsNever_RunsInBothModes(ExecutionMode mode)
    {
        // An empty generator infers Generator<never>; spreading it yields an empty array. The never element
        // (which maps to System.Void) must fall back to an object collection slot rather than crashing IL
        // emission with "System.Void may not be used as a type argument".
        var source = """
            function outer() { function* g() {} return [...g()]; }
            console.log(outer().length);
            """;
        Assert.Equal("0\n", TestHarness.Run(source, mode));
    }

    // ---- #532: nested generator declaration infers its yield type (compiled no longer System.Void) ----

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void NestedGenerator_InfersYieldType_RunsInBothModes(ExecutionMode mode)
    {
        // The nested generator's yield type is inferred (number), so the outer function returns number[]
        // and the compiled path no longer emits an invalid IEnumerator<System.Void> (#532).
        var source = """
            function outer() {
              function* g() { yield 42; }
              return [...g()];
            }
            console.log(outer()[0]);
            """;
        Assert.Equal("42\n", TestHarness.Run(source, mode));
    }

    [Fact]
    public void NestedGenerator_InferredYield_SatisfiesAnnotatedReturn()
    {
        var source = """
            function outer(): number[] {
              function* g() { yield 42; }
              return [...g()];
            }
            """;
        TestHarness.RunInterpreted(source);
    }

    // NOTE: generator METHODS with an inferred return type now also compute Generator<yieldType> rather
    // than Generator<void> (the same fix in CheckClassBody), but the result is not yet observable at a call
    // site: `new C().values()` resolves an inferred method's return to the unresolved `<inferred>`
    // placeholder regardless of generator-ness — a pre-existing method-return-inference propagation gap
    // (#658). A test here would exercise that unrelated bug, so it is intentionally omitted.
}
