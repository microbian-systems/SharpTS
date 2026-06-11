using SharpTS.Tests.Infrastructure;
using Xunit;

namespace SharpTS.Tests.SharedTests;

/// <summary>
/// Tests for computed accessor names in class bodies (#261):
/// get [Symbol.toStringTag]() / static get [Symbol.species]().
/// Symbol-keyed accessors run interpreted only (compiled support tracked by #266);
/// literal computed keys fold to ordinary names in the parser and run in both modes.
/// </summary>
public class ComputedAccessorNameTests
{
    [Theory]
    [MemberData(nameof(ExecutionModes.InterpretedOnly), MemberType = typeof(ExecutionModes))]
    public void StaticSymbolGetter_SpeciesPattern(ExecutionMode mode)
    {
        var source = """
            class MyP extends Promise<any> {
                static get [Symbol.species]() { return Promise; }
            }
            console.log((MyP as any)[Symbol.species] === Promise);
            """;

        var output = TestHarness.Run(source, mode);
        Assert.Equal("true\n", output);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.InterpretedOnly), MemberType = typeof(ExecutionModes))]
    public void InstanceSymbolGetterAndSetter(ExecutionMode mode)
    {
        var source = """
            class Tagged {
                stored: any = null;
                get [Symbol.toStringTag]() { return "Tagged!"; }
                set [Symbol.toPrimitive](v: any) { this.stored = v; }
            }
            const t = new Tagged();
            console.log((t as any)[Symbol.toStringTag]);
            (t as any)[Symbol.toPrimitive] = 42;
            console.log(t.stored);
            """;

        var output = TestHarness.Run(source, mode);
        Assert.Equal("Tagged!\n42\n", output);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.InterpretedOnly), MemberType = typeof(ExecutionModes))]
    public void StaticSymbolGetter_InheritedThroughSubclassChain(ExecutionMode mode)
    {
        var source = """
            class Base {
                static get [Symbol.species]() { return Base; }
            }
            class Sub extends Base {}
            console.log((Sub as any)[Symbol.species] === Base);
            """;

        var output = TestHarness.Run(source, mode);
        Assert.Equal("true\n", output);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.InterpretedOnly), MemberType = typeof(ExecutionModes))]
    public void SymbolAccessor_InClassExpression(ExecutionMode mode)
    {
        var source = """
            const C = class {
                get [Symbol.toStringTag]() { return "expr"; }
            };
            console.log((new C() as any)[Symbol.toStringTag]);
            """;

        var output = TestHarness.Run(source, mode);
        Assert.Equal("expr\n", output);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void LiteralComputedKeys_FoldToOrdinaryNames(ExecutionMode mode)
    {
        var source = """
            class Lit {
                get ["foo"]() { return 7; }
            }
            console.log(new Lit().foo);
            """;

        var output = TestHarness.Run(source, mode);
        Assert.Equal("7\n", output);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void StringAndKeywordAccessorNames_Parse(ExecutionMode mode)
    {
        var source = """
            class Kw {
                get "quoted"() { return 1; }
                get type() { return 2; }
            }
            console.log(new Kw()["quoted"], new Kw().type);
            """;

        var output = TestHarness.Run(source, mode);
        Assert.Equal("1 2\n", output);
    }
}
