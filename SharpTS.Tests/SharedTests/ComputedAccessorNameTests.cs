using SharpTS.Tests.Infrastructure;
using Xunit;

namespace SharpTS.Tests.SharedTests;

/// <summary>
/// Tests for computed accessor names in class bodies (#261):
/// get [Symbol.toStringTag]() / static get [Symbol.species]().
/// Symbol-keyed accessors on class declarations run in both modes (compiled
/// support added in #266; module-local Symbol keys confirmed by #282); on class
/// expressions they stay interpreted-only (#281). Literal computed keys fold to
/// ordinary names in the parser.
/// </summary>
public class ComputedAccessorNameTests
{
    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
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
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
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
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
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

    // Class expressions use a separate emission path with no .cctor registration
    // hook; compiled symbol-accessor support there is tracked by #281.
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

    // A get and set accessor for the SAME symbol must coexist (they merge into one
    // registry slot) and the .cctor registration must not disturb static field init.
    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void SymbolGetterAndSetter_SameKey_WithStaticField(ExecutionMode mode)
    {
        var source = """
            class Box {
                static tag: number = 7;
                value: any = 0;
                get [Symbol.toPrimitive]() { return this.value; }
                set [Symbol.toPrimitive](v: any) { this.value = v * 2; }
            }
            const b = new Box();
            (b as any)[Symbol.toPrimitive] = 21;
            console.log((b as any)[Symbol.toPrimitive]);
            console.log(Box.tag);
            """;

        var output = TestHarness.Run(source, mode);
        Assert.Equal("42\n7\n", output);
    }

    // Regression for #282: a MODULE-LOCAL Symbol key (not a well-known symbol)
    // must register and dispatch in compiled mode. The key expression is
    // evaluated in the lazily-run class .cctor, which executes after the
    // top-level binding is assigned, so `key` resolves correctly. Getter and
    // setter share one registry slot.
    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void SymbolAccessor_ModuleLocalKey(ExecutionMode mode)
    {
        var source = """
            const key = Symbol("k");
            class Box {
                _v: number = 10;
                get [key]() { return this._v; }
                set [key](x: number) { this._v = x; }
            }
            const b = new Box() as any;
            console.log(b[key]);
            b[key] = 42;
            console.log(b[key]);
            """;

        var output = TestHarness.Run(source, mode);
        Assert.Equal("10\n42\n", output);
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
