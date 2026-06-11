using SharpTS.Tests.Infrastructure;
using Xunit;

namespace SharpTS.Tests.SharedTests;

/// <summary>
/// Tests for expando statics on class constructors via bracket assignment (#262).
/// Node allows arbitrary string/symbol-keyed statics on class objects.
/// </summary>
public class ClassExpandoStaticTests
{
    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void StringKeyedExpandoStatic_RoundTrips(ExecutionMode mode)
    {
        var source = """
            class C {}
            (C as any)["foo"] = 1;
            console.log((C as any)["foo"] === 1);
            """;

        var output = TestHarness.Run(source, mode);
        Assert.Equal("true\n", output);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void SymbolKeyedExpandoStatic_RoundTrips(ExecutionMode mode)
    {
        var source = """
            class C {}
            (C as any)[Symbol.species] = 2;
            console.log((C as any)[Symbol.species] === 2);
            console.log((C as any)[Symbol.iterator] === undefined);
            """;

        var output = TestHarness.Run(source, mode);
        Assert.Equal("true\ntrue\n", output);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void ExpandoStatics_InAsyncContext_RoundTrip(ExecutionMode mode)
    {
        var source = """
            class C {}
            async function go() {
                (C as any)["bar"] = 3;
                (C as any)[Symbol.toPrimitive] = 4;
                console.log((C as any)["bar"] === 3);
                console.log((C as any)[Symbol.toPrimitive] === 4);
            }
            go();
            """;

        var output = TestHarness.Run(source, mode);
        Assert.Equal("true\ntrue\n", output);
    }

    // Compiled mode does not yet walk the constructor parent chain for expando
    // statics (#265); Node inherits them via Object.getPrototypeOf(D) === C.
    [Theory]
    [MemberData(nameof(ExecutionModes.InterpretedOnly), MemberType = typeof(ExecutionModes))]
    public void ExpandoStatics_InheritedThroughSubclassChain(ExecutionMode mode)
    {
        var source = """
            class C {}
            class D extends C {}
            (C as any)["foo"] = 1;
            (C as any)[Symbol.species] = 2;
            console.log((D as any)["foo"] === 1);
            console.log((D as any)[Symbol.species] === 2);
            """;

        var output = TestHarness.Run(source, mode);
        Assert.Equal("true\ntrue\n", output);
    }
}
