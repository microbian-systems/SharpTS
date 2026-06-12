using SharpTS.Tests.Infrastructure;
using SharpTS.TypeSystem.Exceptions;
using Xunit;

namespace SharpTS.Tests.TypeCheckerTests;

/// <summary>
/// Round-3 fixes from the #226 cluster survey: namespace var hoisting (self-referential
/// annotations/initializers), enum member accesses typing as the enum, TS2403 var
/// redeclarations, and first-match overload resolution.
/// </summary>
public class Round3ConformanceTests
{
    [Fact]
    public void NamespaceVar_SelfReferentialTypeofAndInitializer_Resolve()
    {
        var source = """
            namespace M {
                var a: { foo: typeof a; };
                var b: { foo: typeof b; };
                a = b;
            }
            """;
        TestHarness.RunInterpreted(source);
    }

    [Fact]
    public void EnumMemberAccess_TypesAsTheEnum_CrossEnumAssignmentErrors()
    {
        var source = """
            enum E { A, B, C }
            enum F { D = 4, E, F }
            var e = E.A;
            var f = F.D;
            function use() { e = f; }
            """;
        Assert.ThrowsAny<TypeCheckException>(() => TestHarness.RunInterpreted(source));
    }

    [Fact]
    public void NumericLiteral_NotAssignableToEnum_EvenWhenMemberValue()
    {
        var source = """
            enum E { A, B, C }
            var e = E.A;
            function use() { e = 1; }
            """;
        Assert.ThrowsAny<TypeCheckException>(() => TestHarness.RunInterpreted(source));
    }

    [Fact]
    public void WidenedNumber_StillAssignableToNumericEnum()
    {
        var source = """
            enum E { A }
            var n: number;
            var e: E;
            function use() { e = n; n = e; }
            """;
        TestHarness.RunInterpreted(source);
    }

    [Fact]
    public void VarRedeclaration_DifferentType_IsTs2403Error()
    {
        var source = """
            enum E { A }
            declare function f1(x: E): E;
            declare function f2(x: string): string;
            function use() {
                var r = f1(E.A);
                var r = f2("s");
            }
            """;
        Assert.ThrowsAny<TypeCheckException>(() => TestHarness.RunInterpreted(source));
    }

    [Fact]
    public void VarRedeclaration_SameType_IsAccepted()
    {
        var source = """
            enum E { A }
            declare function f1(x: E): E;
            function use() {
                var r = f1(E.A);
                var r = f1(E.A);
            }
            """;
        TestHarness.RunInterpreted(source);
    }

    [Fact]
    public void VarRedeclaration_AnnotationOnly_DifferentType_IsTs2403Error()
    {
        // Issue #336: an annotation-only duplicate (`var z: T;` with no initializer) was dropped
        // by VarHoister to a bare no-op, losing the annotation, so TS2403 never fired.
        var source = """
            function h() {
                var z: string;
                var z: number;
            }
            """;
        var ex = Assert.ThrowsAny<TypeCheckException>(() => TestHarness.RunInterpreted(source));
        Assert.Equal("TS2403", ex.Diagnostic.TsCode);
    }

    [Fact]
    public void VarRedeclaration_AnnotationOnly_SameType_IsAccepted()
    {
        var source = """
            function h() {
                var z: string;
                var z: string;
            }
            """;
        TestHarness.RunInterpreted(source);
    }

    [Fact]
    public void VarRedeclaration_AnnotationOnly_AtTopLevel_IsTs2403Error()
    {
        var source = """
            var z: string;
            var z: number;
            """;
        var ex = Assert.ThrowsAny<TypeCheckException>(() => TestHarness.RunInterpreted(source));
        Assert.Equal("TS2403", ex.Diagnostic.TsCode);
    }

    [Fact]
    public void VarRedeclaration_AnnotationOnly_AfterInitializer_IsTs2403Error()
    {
        // First declaration carries the type via inference; annotation-only duplicate still checks.
        var source = """
            function h() {
                var z = "hi";
                var z: number;
            }
            """;
        var ex = Assert.ThrowsAny<TypeCheckException>(() => TestHarness.RunInterpreted(source));
        Assert.Equal("TS2403", ex.Diagnostic.TsCode);
    }

    [Fact]
    public void VarShadowing_InNestedFunction_IsNotRedeclaration()
    {
        var source = """
            var x = 1;
            function inner() {
                var x = "different type, different scope";
            }
            """;
        TestHarness.RunInterpreted(source);
    }

    [Fact]
    public void OverloadResolution_PicksFirstDeclaredMatch()
    {
        // tsc takes the first matching overload in declaration order, not the most specific.
        var source = """
            declare function pick(x: unknown): string;
            declare function pick(x: number): number;
            function use() {
                let s: string = pick(42);
            }
            """;
        TestHarness.RunInterpreted(source);
    }
}
