using SharpTS.Tests.Infrastructure;
using Xunit;

namespace SharpTS.Tests.SharedTests;

/// <summary>
/// Issue #533: a function (or any closure) declared BEFORE a <c>let</c>/<c>const</c> in the same
/// scope may reference that binding in its body. The body only executes once the binding is
/// initialized, so TypeScript — which collects every lexical binding in a scope before checking any
/// function body — accepts the forward reference. SharpTS previously rejected it with a spurious
/// "Undefined variable" type error because it checked function bodies in source order and only
/// pre-hoisted <c>var</c>/<c>class</c>. The fix hoists <c>let</c>/<c>const</c> the same way.
///
/// A *direct* (non-deferred) use-before-declaration is still an error — surfaced at runtime, the
/// same way a forward <c>class</c> reference is — see <see cref="DirectUseBeforeDeclaration_StillThrows"/>.
/// </summary>
public class LexicalForwardReferenceTests
{
    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void FunctionDeclaration_ReferencesLaterLet(ExecutionMode mode)
    {
        var source = """
            function f() { return x; }
            let x = 1;
            console.log(f());
            """;

        Assert.Equal("1", TestHarness.Run(source, mode).Trim());
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void FunctionDeclaration_ReferencesLaterConst(ExecutionMode mode)
    {
        var source = """
            function f() { return z; }
            const z = 5;
            console.log(f());
            """;

        Assert.Equal("5", TestHarness.Run(source, mode).Trim());
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void Generator_ReferencesLaterLet(ExecutionMode mode)
    {
        // The exact second repro from the issue (a generator declared before its let binding).
        var source = """
            function* g() { yield x; yield x + 1; }
            let x = 1;
            console.log([...g()].join(","));
            """;

        Assert.Equal("1,2", TestHarness.Run(source, mode).Trim());
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void ArrowConst_ReferencesLaterLet(ExecutionMode mode)
    {
        var source = """
            const getIt = () => _val;
            let _val = 42;
            console.log(getIt());
            """;

        Assert.Equal("42", TestHarness.Run(source, mode).Trim());
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void AnnotatedLet_ForwardReferenced_TypeChecks(ExecutionMode mode)
    {
        var source = """
            function f(): string { return s; }
            let s: string = "hi";
            console.log(f());
            """;

        Assert.Equal("hi", TestHarness.Run(source, mode).Trim());
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void InnerFunction_ReferencesLaterLet_InEnclosingBody(ExecutionMode mode)
    {
        // The forward binding lives in the enclosing FUNCTION body, not module scope.
        var source = """
            function outer() {
                function f() { return y; }
                let y = 99;
                return f();
            }
            console.log(outer());
            """;

        Assert.Equal("99", TestHarness.Run(source, mode).Trim());
    }

    [Fact]
    public void ShadowingConst_BindsToInnerDeclaration_Interpreted()
    {
        // The inner `const x` shadows the outer one throughout `wrapper`, including in f's closure.
        // Hoisting the inner binding makes f resolve to it (value "shadow"), which is what runs.
        // Interpreted-only: compiled mode mis-binds a captured *shadowing* block-scoped name to the
        // wrong display-class slot (resolves to null) — a pre-existing defect, orthogonal to #533
        // (it reproduces with f declared AFTER the inner const, i.e. no forward reference). See #562.
        var source = """
            const x = 99;
            function wrapper() {
                function f() { return x; }
                const x = "shadow";
                return f();
            }
            console.log(wrapper());
            """;

        Assert.Equal("shadow", TestHarness.RunInterpreted(source).Trim());
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void MutualRecursion_ViaConstArrows_StillWorks(ExecutionMode mode)
    {
        // Guards that hoisting let/const as `any` does not clobber the precise function type that
        // HoistConstFunctionExpressions registers for a `const f = () => …` arrow (the IsDefinedLocally
        // guard). Each arrow forward-references the other.
        var source = """
            const isEven = (n: number): boolean => n === 0 ? true : isOdd(n - 1);
            const isOdd = (n: number): boolean => n === 0 ? false : isEven(n - 1);
            console.log(isEven(10) + " " + isOdd(7));
            """;

        Assert.Equal("true true", TestHarness.Run(source, mode).Trim());
    }

    [Fact]
    public void NamespaceMemberFunction_ReferencesLaterConst_Interpreted()
    {
        // A namespace member function declared before a namespace-level const now type-checks the
        // same way the in-order variant already did. Interpreted-only: compiled mode cannot resolve
        // a namespace-level variable from a namespace member function at all (any order, var/let/const)
        // — a pre-existing, orthogonal namespace-emission defect tracked in #567.
        var source = """
            namespace N {
                export function f() { return val; }
                const val = 7;
                export const result = f();
            }
            console.log(N.result);
            """;

        Assert.Equal("7", TestHarness.RunInterpreted(source).Trim());
    }

    [Fact]
    public void DirectUseBeforeDeclaration_StillThrows()
    {
        // Not deferred behind a closure: the read runs before `let x`, so the binding does not exist
        // yet. This is still an error — now at runtime, exactly how a forward class reference behaves
        // — never silently `undefined`.
        var source = """
            console.log(x);
            let x = 1;
            """;

        var ex = Assert.ThrowsAny<Exception>(() => TestHarness.RunInterpreted(source));
        Assert.Contains("Undefined variable", ex.Message);
    }
}
