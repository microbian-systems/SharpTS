using SharpTS.Tests.Infrastructure;
using SharpTS.TypeSystem.Exceptions;
using Xunit;

namespace SharpTS.Tests.SharedTests;

/// <summary>
/// Regression tests for #467 (sibling of #428): a namespace-scoped <c>const</c> / <c>export const</c>
/// was parsed via <c>VarDeclaration()</c> with the default <c>isConst: false</c>, so it became a
/// mutable <c>Stmt.Var</c> — losing const-ness (the literal type widened and reassignment went
/// unflagged). The parser now passes <c>isConst: true</c>, and the type checker / interpreter grew
/// dedicated <c>Stmt.Const</c> arms so the binding is still a visible namespace member.
/// </summary>
public class NamespaceConstNarrowingTests
{
    // --- Runtime: exported namespace consts are accessible with the right values (both modes) ---

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void NamespaceExportConst_IsAccessibleAtRuntime(ExecutionMode mode)
    {
        var source = """
            namespace N { export const x = 5; export let y = 10; }
            console.log(N.x);
            console.log(N.y);
            """;
        Assert.Equal("5\n10\n", TestHarness.Run(source, mode));
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void NamespaceExportConst_StringAndNumber_Runtime(ExecutionMode mode)
    {
        var source = """
            namespace Cfg { export const name = "app"; export const version = 2; }
            console.log(Cfg.name + " " + Cfg.version);
            """;
        Assert.Equal("app 2\n", TestHarness.Run(source, mode));
    }

    // --- Type checker: const-ness is preserved (literal narrowing + reassignment flagged) ---

    [Fact]
    public void NamespaceExportConst_HasLiteralType()
    {
        // `export const x = 5` gives `N.x` the literal type `5` (not the widened `number`),
        // so assigning it to a `5`-typed binding type-checks. This failed before the fix.
        TestHarness.RunInterpreted("""
            namespace N { export const x = 5; }
            const exact: 5 = N.x;
            console.log(exact);
            """);
    }

    [Fact]
    public void NamespaceExportConst_LiteralType_RejectsWrongLiteral()
    {
        // `N.x` is `5`, not `number`, so it is not assignable to the unrelated literal `6`.
        Assert.ThrowsAny<TypeCheckException>(() =>
            TestHarness.RunInterpreted("""
                namespace N { export const x = 5; }
                const bad: 6 = N.x;
                """));
    }

    [Fact]
    public void NamespaceConst_ReassignmentInBody_IsFlagged()
    {
        // A namespace-scoped `const` is read-only; reassigning it in the body is an error.
        Assert.ThrowsAny<TypeCheckException>(() =>
            TestHarness.RunInterpreted("namespace N { export const x = 5; x = 6; }"));
    }

    [Fact]
    public void NamespaceNonExportedConst_HasLiteralType()
    {
        // The parser change applies to non-exported consts too: `x` narrows to `5` and so is
        // assignable to the `5`-typed `y` within the same namespace body.
        TestHarness.RunInterpreted("namespace N { const x = 5; const y: 5 = x; }");
    }
}
