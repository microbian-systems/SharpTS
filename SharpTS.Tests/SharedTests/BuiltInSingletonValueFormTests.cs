using SharpTS.Tests.Infrastructure;
using Xunit;

namespace SharpTS.Tests.SharedTests;

/// <summary>
/// Regression for #276: in compiled mode, accessing a built-in singleton's
/// method through a <em>value</em> (rather than the bare <c>Math.method(...)</c>
/// syntactic form) yielded <c>undefined</c>. The dedicated static emitters
/// (MathStaticEmitter / JSONStaticEmitter) intercept <c>Math.max(...)</c> at
/// compile time before the receiver is evaluated; when the singleton is used as
/// a value the lookup fell through to the runtime <c>_mathSingleton</c> /
/// <c>_jsonSingleton</c> dictionaries, which were created empty and never
/// populated. They are now lazily populated with the same identity-cached
/// <c>$TSFunction</c> wrappers the value-form static emitters hand out.
/// </summary>
public class BuiltInSingletonValueFormTests
{
    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void Math_AsValue_MethodsDispatch(ExecutionMode mode)
    {
        var source = @"
            const m: any = Math;
            console.log(typeof m.max);
            console.log(m.max(1, 2));
            console.log(m.min(5, 3, 8));
            console.log(m.floor(3.7));
            console.log(m.abs(-4));
        ";
        var output = TestHarness.Run(source, mode);
        Assert.Equal("function\n2\n3\n3\n4\n", output);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void Json_AsValue_MethodsDispatch(ExecutionMode mode)
    {
        var source = @"
            const j: any = JSON;
            console.log(typeof j.stringify);
            console.log(j.stringify({ a: 1 }));
            console.log(j.parse('{""b"":2}').b);
        ";
        var output = TestHarness.Run(source, mode);
        Assert.Equal("function\n{\"a\":1}\n2\n", output);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.CompiledOnly), MemberType = typeof(ExecutionModes))]
    public void Math_AsValue_MethodIdentityIsStable(ExecutionMode mode)
    {
        // The value-form wrapper must be the same identity-cached $TSFunction the
        // bare `Math.max` syntactic form hands out (ECMA-262: built-in methods are
        // single objects). Interpreted mode synthesizes a fresh wrapper per read,
        // so this is scoped to compiled mode.
        var source = @"
            const m: any = Math;
            console.log(m.max === Math.max);
            console.log(m.floor === Math.floor);
        ";
        var output = TestHarness.Run(source, mode);
        Assert.Equal("true\ntrue\n", output);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.CompiledOnly), MemberType = typeof(ExecutionModes))]
    public void Math_AsValue_MethodsAreNonEnumerable(ExecutionMode mode)
    {
        // Math's methods are non-enumerable per ECMA-262 §17, so populating the
        // singleton must install non-enumerable descriptors — `Object.keys(Math)`
        // stays empty.
        var source = @"
            const m: any = Math;
            console.log(Object.keys(m).length);
        ";
        var output = TestHarness.Run(source, mode);
        Assert.Equal("0\n", output);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void Json_StaticMethodIdentityIsStable(ExecutionMode mode)
    {
        // #299: JSON.parse / JSON.stringify are single built-in function objects
        // per ECMA-262 §25.5, so repeated access returns the SAME callable.
        // Previously the interpreter synthesized a fresh wrapper per access while
        // compiled mode was already stable — both now agree.
        var source = @"
            console.log(JSON.stringify === JSON.stringify);
            console.log(JSON.parse === JSON.parse);
        ";
        var output = TestHarness.Run(source, mode);
        Assert.Equal("true\ntrue\n", output);
    }
}
