using SharpTS.Tests.Infrastructure;
using Xunit;

namespace SharpTS.Tests;

/// <summary>
/// Tests for ES2020 globalThis support.
/// </summary>
public class GlobalThisTests
{
    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void GlobalThis_Math_MatchesDirect(ExecutionMode mode)
    {
        var result1 = TestHarness.Run("""
            // globalThis.Math.PI works like Math.PI
            console.log(globalThis.Math.PI === Math.PI);
            """, mode);
        Assert.Contains("true", result1);

        var result2 = TestHarness.Run("""
            // globalThis.Math.floor works like Math.floor
            console.log(globalThis.Math.floor(3.7));
            """, mode);
        Assert.Contains("3", result2);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void GlobalThis_Console_Works(ExecutionMode mode)
    {
        var result = TestHarness.Run("""
            // globalThis.console.log works
            globalThis.console.log("Hello from globalThis");
            """, mode);
        Assert.Contains("Hello from globalThis", result);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void GlobalThis_SelfReference(ExecutionMode mode)
    {
        var result = TestHarness.Run("""
            // Self-reference: globalThis.globalThis
            console.log(globalThis.globalThis === globalThis);
            """, mode);
        Assert.Contains("true", result);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void GlobalThis_Assignment(ExecutionMode mode)
    {
        var result = TestHarness.Run("""
            // Assignment to globalThis
            globalThis.myValue = 42;
            console.log(globalThis.myValue);
            """, mode);
        Assert.Contains("42", result);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void GlobalThis_IndexAccess(ExecutionMode mode)
    {
        var result = TestHarness.Run("""
            // Index access with string literal
            globalThis["testProp"] = "hello";
            console.log(globalThis["testProp"]);
            """, mode);
        Assert.Contains("hello", result);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void GlobalThis_DynamicIndex(ExecutionMode mode)
    {
        var result = TestHarness.Run("""
            // Dynamic index access
            const name: string = "Math";
            console.log(typeof globalThis[name]);
            """, mode);
        Assert.Contains("object", result);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void GlobalThis_BuiltInConstants(ExecutionMode mode)
    {
        var r1 = TestHarness.Run("""
            // globalThis.undefined equals undefined
            console.log(globalThis.undefined === undefined);
            """, mode);
        Assert.Contains("true", r1);

        var r2 = TestHarness.Run("""
            // globalThis.NaN is NaN
            console.log(Number.isNaN(globalThis.NaN));
            """, mode);
        Assert.Contains("true", r2);

        var r3 = TestHarness.Run("""
            // globalThis.Infinity is Infinity
            console.log(globalThis.Infinity === Infinity);
            """, mode);
        Assert.Contains("true", r3);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void GlobalThis_ModuleScopedVarsNotAccessible(ExecutionMode mode)
    {
        var result = TestHarness.Run("""
            // User-defined variables are NOT accessible via globalThis (module semantics)
            let x: number = 1;
            console.log(globalThis.x === undefined);
            """, mode);
        Assert.Contains("true", result);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void GlobalThis_ChainedSelfReference(ExecutionMode mode)
    {
        var result = TestHarness.Run("""
            // Chained self-reference: globalThis.globalThis.Math.PI
            console.log(globalThis.globalThis.Math.PI === Math.PI);
            """, mode);
        Assert.Contains("true", result);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void GlobalThis_Process(ExecutionMode mode)
    {
        var result = TestHarness.Run("""
            // Process access through globalThis
            console.log(typeof globalThis.process);
            """, mode);
        Assert.Contains("object", result);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void GlobalThis_ParseInt(ExecutionMode mode)
    {
        var result = TestHarness.Run("""
            // globalThis.parseInt works
            console.log(globalThis.parseInt("42"));
            """, mode);
        Assert.Contains("42", result);
    }
}
