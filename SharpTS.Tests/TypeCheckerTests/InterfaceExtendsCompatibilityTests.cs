using SharpTS.Tests.Infrastructure;
using SharpTS.TypeSystem.Exceptions;
using Xunit;

namespace SharpTS.Tests.TypeCheckerTests;

/// <summary>
/// TS2430 (#188 Step 3): a member redeclared by a derived interface must be assignable to the
/// corresponding member of every extended interface. Mirrors tsc on the
/// <c>{call,construct}SignatureAssignabilityInInheritance*</c> conformance tests.
/// </summary>
public class InterfaceExtendsCompatibilityTests
{
    [Fact]
    public void IncompatibleRedeclaredMember_IsError()
    {
        var source = """
            interface Base { a: (x: number) => number; }
            interface I extends Base {
                a: (x: number) => string;
            }
            """;
        Assert.ThrowsAny<TypeCheckException>(() => TestHarness.RunInterpreted(source));
    }

    [Fact]
    public void CompatibleNarrowedMember_IsAccepted()
    {
        var source = """
            class Base { foo: string; }
            class Derived extends Base { bar: string; }
            interface A { a: (x: number) => Base; }
            interface I extends A {
                a: (x: number) => Derived;
            }
            """;
        TestHarness.RunInterpreted(source); // covariant return narrowing is fine
    }

    [Fact]
    public void IncompatibleRedeclaredGenericMember_IsError()
    {
        // The derived member's free T (from the interface's own type parameter) must not satisfy
        // the base's bound signature parameter.
        var source = """
            interface A { a3: <T>(x: T) => void; }
            interface I3<T> extends A {
                a3: (x: T) => T;
            }
            """;
        Assert.ThrowsAny<TypeCheckException>(() => TestHarness.RunInterpreted(source));
    }

    [Fact]
    public void GenericMemberRedeclaredCompatibly_IsAccepted()
    {
        var source = """
            interface A { a: <T>(x: T) => T[]; }
            interface I extends A {
                a: <S>(x: S) => S[];
            }
            """;
        TestHarness.RunInterpreted(source);
    }

    [Fact]
    public void MemberNotRedeclared_IsInheritedWithoutCheck()
    {
        var source = """
            interface A { a: (x: number) => number; b: string; }
            interface I extends A {
                c: boolean;
            }
            """;
        TestHarness.RunInterpreted(source);
    }
}
