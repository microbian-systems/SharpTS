using System.Collections.Generic;
using System.Linq;
using SharpTS.Diagnostics;
using SharpTS.Parsing;
using SharpTS.Tests.Infrastructure;
using SharpTS.TypeSystem;
using SharpTS.TypeSystem.Exceptions;
using Xunit;

namespace SharpTS.Tests.TypeCheckerTests;

/// <summary>
/// Issue #383: a local <c>function</c> declaration's inferred return type must be available to
/// calls that appear textually <em>before</em> the declaration in the same scope. Previously the
/// checker hoisted such functions with an <c>any</c> return placeholder and only inferred the real
/// type when the body was reached, so an earlier call typed as <c>any</c> and silently skipped
/// assignment/return checks. Hoisting now speculatively pre-resolves the return type.
/// </summary>
public class ForwardLocalFunctionReturnTypeTests
{
    [Fact]
    public void NestedForwardCall_AssignedToAnnotatedVar_IsTs2322Error()
    {
        var source = """
            function h() {
                var z: number = make();
                function make() { return "hi"; }
            }
            """;
        var ex = Assert.ThrowsAny<TypeCheckException>(() => TestHarness.RunInterpreted(source));
        Assert.Equal("TS2322", ex.Diagnostic.TsCode);
    }

    [Fact]
    public void TopLevelForwardCall_AssignedToAnnotatedVar_IsTs2322Error()
    {
        var source = """
            var z: number = make();
            function make() { return "hi"; }
            """;
        var ex = Assert.ThrowsAny<TypeCheckException>(() => TestHarness.RunInterpreted(source));
        Assert.Equal("TS2322", ex.Diagnostic.TsCode);
    }

    [Fact]
    public void ForwardCall_DeclaredBefore_StillReports_Control()
    {
        // The already-working ordering: the function is declared before the call.
        var source = """
            function h() {
                function make() { return "hi"; }
                var z: number = make();
            }
            """;
        var ex = Assert.ThrowsAny<TypeCheckException>(() => TestHarness.RunInterpreted(source));
        Assert.Equal("TS2322", ex.Diagnostic.TsCode);
    }

    [Fact]
    public void ForwardCall_CompatibleType_IsNotError()
    {
        // The inferred return type (string) matches the annotation — no false positive.
        var source = """
            function h() {
                var z: string = make();
                function make() { return "hi"; }
            }
            """;
        TestHarness.RunInterpreted(source);
    }

    [Fact]
    public void ForwardCall_InReturnPosition_IsTs2322Error()
    {
        // The pre-resolved return type also drives the enclosing function's return check.
        var source = """
            function h(): number {
                return make();
                function make() { return "hi"; }
            }
            """;
        var ex = Assert.ThrowsAny<TypeCheckException>(() => TestHarness.RunInterpreted(source));
        Assert.Equal("TS2322", ex.Diagnostic.TsCode);
    }

    [Fact]
    public void ForwardCall_NumericInference_AssignedToString_IsTs2322Error()
    {
        var source = """
            function h() {
                var z: string = compute();
                function compute() { return 1 + 2; }
            }
            """;
        var ex = Assert.ThrowsAny<TypeCheckException>(() => TestHarness.RunInterpreted(source));
        Assert.Equal("TS2322", ex.Diagnostic.TsCode);
    }

    [Fact]
    public void Issue378Interaction_HoistedVarFromForwardCall_IsTs2403Error()
    {
        // The residual #378 shape: a hoisted `var` whose first nested initializer is a
        // forward-referenced local function call. The inferred return type (string) now flows into
        // the hoisted binding so a later `var z: number;` conflicts.
        var source = """
            function h(c: boolean) {
                if (c) { var z = make(); }
                var z: number;
                function make() { return "hi"; }
            }
            """;
        var ex = Assert.ThrowsAny<TypeCheckException>(() => TestHarness.RunInterpreted(source));
        Assert.Equal("TS2403", ex.Diagnostic.TsCode);
    }

    [Fact]
    public void MutuallyRecursiveForwardFunctions_DoNotLoop()
    {
        // Mutual recursion between two un-annotated forward-referenced functions must terminate
        // during speculative inference (each sees the other's `any` placeholder mid-flight) rather
        // than recursing infinitely. Behavior is otherwise unchanged.
        var source = """
            function h(): boolean {
                return isEven(4);
                function isEven(n: number) { return n === 0 ? true : isOdd(n - 1); }
                function isOdd(n: number) { return n === 0 ? false : isEven(n - 1); }
            }
            console.log(h());
            """;
        Assert.Equal("true", TestHarness.RunInterpreted(source).Trim());
    }

    [Fact]
    public void RecursiveForwardFunction_DoesNotCrash()
    {
        // A self-recursive forward function: speculative inference must not recurse infinitely.
        var source = """
            function h() {
                var z: number = fact(5);
                function fact(n: number) {
                    if (n <= 1) { return 1; }
                    return n * fact(n - 1);
                }
                return z;
            }
            """;
        // fact infers `number`, so assigning to a number var is fine — no error.
        TestHarness.RunInterpreted(source);
    }

    [Fact]
    public void ForwardFunctionInnerError_ReportedExactlyOnce()
    {
        // Speculative hoist-time inference checks the forward function's body with diagnostics
        // suppressed; the real declaration pass reports them. An error inside the body must surface
        // exactly once — never swallowed, never duplicated.
        var source = """
            function h() {
                var z: string = make();
                function make() { let bad: number = "x"; return "hi"; }
            }
            """;
        var diagnostics = CheckWithRecovery(source);
        Assert.Equal(1, diagnostics.Count(d => d.TsCode == "TS2322"));
    }

    [Fact]
    public void ForwardCallAndInnerError_BothReported_Once()
    {
        // The forward-reference assignment error and an error inside the forward function are
        // independent diagnostics: both surface, each once.
        var source = """
            function h() {
                var z: number = make();
                function make() { let bad: string = 5; return "hi"; }
            }
            """;
        var diagnostics = CheckWithRecovery(source);
        // `z = make()` (string→number) and `bad = 5` (number→string): two TS2322, no duplicates.
        Assert.Equal(2, diagnostics.Count(d => d.TsCode == "TS2322"));
    }

    [Fact]
    public void ForwardCall_RuntimeBehaviorUnchanged()
    {
        // Pre-resolution is a checker-only change: runtime hoisting/execution is unaffected.
        var source = """
            function h(): string {
                return make();
                function make() { return "hi"; }
            }
            console.log(h());
            """;
        Assert.Equal("hi", TestHarness.RunInterpreted(source).Trim());
    }

    private static IReadOnlyList<Diagnostic> CheckWithRecovery(string source)
    {
        var parseResult = new Parser(new Lexer(source).ScanTokens()).Parse();
        Assert.True(parseResult.IsSuccess);
        return new TypeChecker(maxErrors: 50).CheckWithRecovery(parseResult.Statements).Diagnostics;
    }
}
