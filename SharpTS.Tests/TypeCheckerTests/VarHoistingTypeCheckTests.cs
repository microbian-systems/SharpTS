using SharpTS.Tests.Infrastructure;
using Xunit;

namespace SharpTS.Tests.TypeCheckerTests;

/// <summary>
/// Tests that the type checker correctly handles JavaScript var hoisting semantics.
/// Var declarations are hoisted to the top of their enclosing scope, so forward
/// references from within function bodies should not produce false "Undefined variable" errors.
/// </summary>
public class VarHoistingTypeCheckTests
{
    [Fact]
    public void ForwardVarReference_InFunctionBody_Succeeds()
    {
        var source = """
            function f() { return x; }
            var x = 1;
            console.log(f());
            """;

        var output = TestHarness.RunInterpreted(source);
        Assert.Equal("1", output.Trim());
    }

    [Fact]
    public void ForwardVarReference_InFunctionExpression_Succeeds()
    {
        var source = """
            var getIt = function() { return _val; };
            var _val = 42;
            console.log(getIt());
            """;

        var output = TestHarness.RunInterpreted(source);
        Assert.Equal("42", output.Trim());
    }

    [Fact]
    public void ForwardVarReference_InArrowFunction_Succeeds()
    {
        var source = """
            var getIt = () => _val;
            var _val = 42;
            console.log(getIt());
            """;

        var output = TestHarness.RunInterpreted(source);
        Assert.Equal("42", output.Trim());
    }

    [Fact]
    public void ForwardVarReference_InObjectLiteralGetter_Succeeds()
    {
        // Simulates the Babel CJS interop pattern used by uuid and other packages
        var source = """
            var obj = {
                get value() { return _foo; }
            };
            var _foo = "bar";
            console.log(obj.value);
            """;

        var output = TestHarness.RunInterpreted(source);
        Assert.Equal("bar", output.Trim());
    }

    [Fact]
    public void ForwardVarReference_DuplicateDeclarations_Succeeds()
    {
        var source = """
            function f() { return x; }
            var x = 1;
            var x = 2;
            console.log(f());
            """;

        var output = TestHarness.RunInterpreted(source);
        Assert.Equal("2", output.Trim());
    }

    [Fact]
    public void ForwardLetReference_InFunctionBody_StillFails()
    {
        var source = """
            function f() { return y; }
            let y = 1;
            """;

        var ex = Assert.ThrowsAny<Exception>(() => TestHarness.RunInterpreted(source));
        Assert.Contains("Undefined variable", ex.Message);
    }

    [Fact]
    public void ForwardConstReference_InFunctionBody_StillFails()
    {
        var source = """
            function f() { return z; }
            const z = 1;
            """;

        var ex = Assert.ThrowsAny<Exception>(() => TestHarness.RunInterpreted(source));
        Assert.Contains("Undefined variable", ex.Message);
    }
}
