using SharpTS.Tests.Infrastructure;
using Xunit;

namespace SharpTS.Tests.SharedTests;

/// <summary>
/// Regression tests for default parameter values on module-exported functions.
/// Before fix: $TSFunction.Invoke (used on every cross-module call) padded missing
/// args with null and dispatched to the full-arity method without running the
/// default initializer, so imported callers saw null instead of the default.
/// </summary>
public class DefaultParameterTests
{
    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void DefaultParam_StringValue_AppliedAcrossModuleImport(ExecutionMode mode)
    {
        var files = new Dictionary<string, string>
        {
            ["lib.ts"] = """
                export function greet(name: string, greeting: string = 'Hello'): string {
                    return greeting + ', ' + name;
                }
                """,
            ["main.ts"] = """
                import { greet } from './lib';
                console.log(greet('World'));
                console.log(greet('World', 'Hi'));
                """
        };

        var output = TestHarness.RunModules(files, "main.ts", mode);
        Assert.Equal("Hello, World\nHi, World\n", output);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void DefaultParam_MultipleDefaults_AppliedAcrossModuleImport(ExecutionMode mode)
    {
        var files = new Dictionary<string, string>
        {
            ["lib.ts"] = """
                export function build(base: string, sep: string = '-', tag: string = 'v1'): string {
                    return base + sep + tag;
                }
                """,
            ["main.ts"] = """
                import { build } from './lib';
                console.log(build('app'));
                console.log(build('app', '_'));
                console.log(build('app', '_', 'prod'));
                """
        };

        var output = TestHarness.RunModules(files, "main.ts", mode);
        Assert.Equal("app-v1\napp_v1\napp_prod\n", output);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void DefaultParam_DirectCall_StillWorks(ExecutionMode mode)
    {
        // Direct same-module calls must continue to work — the overload-generator
        // path is unchanged by this fix.
        var source = """
            function greet(name: string, greeting: string = 'Hello'): string {
                return greeting + ', ' + name;
            }
            console.log(greet('World'));
            console.log(greet('World', 'Hi'));
            """;

        var output = TestHarness.Run(source, mode);
        Assert.Equal("Hello, World\nHi, World\n", output);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void DefaultParam_NumericValue_DirectCall(ExecutionMode mode)
    {
        // Numeric (value-type) defaults still work for direct same-module calls via
        // OverloadGenerator. Module-imported callers with value-type defaults are a
        // known limitation — the inline null-check pattern skips value types.
        var source = """
            function pad(width: number = 4): number {
                return width * 2;
            }
            console.log(pad());
            console.log(pad(7));
            """;

        var output = TestHarness.Run(source, mode);
        Assert.Equal("8\n14\n", output);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void DefaultParam_ExplicitUndefined_FiresDefault(ExecutionMode mode)
    {
        // Per JS spec, passing explicit `undefined` must trigger the default.
        // Compiled mode used to pad missing args with the $Undefined singleton
        // (a non-null object), and the callee's entry check was a plain brtrue
        // that treated non-null as "skip the default" — so explicit-undefined
        // callers (and any caller routed through $TSFunction.Invoke) received
        // the sentinel instead of the default value.
        var source = """
            function withDefault(x: any, arr: any = [1, 2, 3]): any {
                return arr;
            }
            console.log(JSON.stringify(withDefault('x')));
            console.log(JSON.stringify(withDefault('x', undefined)));
            """;

        var output = TestHarness.Run(source, mode);
        Assert.Equal("[1,2,3]\n[1,2,3]\n", output);
    }

    [Fact]
    public void DefaultParam_AcrossModuleBoundary_FiresDefault_Compiled()
    {
        // Surfaced by the debug → supports-color smoke test: a function with a
        // default parameter living in a separate CJS module, called during that
        // module's init with only the leading arg, used to crash in compiled
        // mode with `InvalidCastException: '$Undefined' → List<object>` because
        // the default-param prologue only fired on null, not on the $Undefined
        // singleton that the cross-module caller padded missing args with.
        var files = new Dictionary<string, string>
        {
            ["lib.cjs"] = """
                function hasFlag(flag, argv = [10, 20, 30]) {
                    return argv.indexOf(flag);
                }
                module.exports = { result: hasFlag('x') };
                """,
            ["main.cjs"] = """
                const lib = require('./lib.cjs');
                console.log(lib.result);
                """,
        };
        var output = TestHarness.RunModules(files, "main.cjs", ExecutionMode.Compiled);
        Assert.Equal("-1\n", output);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void DefaultParam_FunctionExpression_OmittedArg_AppliesDefault(ExecutionMode mode)
    {
        // #646: a function *expression* (or arrow) used to drop the default in compiled mode,
        // leaving the parameter at its CLR zero value (0 for number). The Stmt.Function
        // declaration path already worked; the Expr.ArrowFunction path did not.
        var source = """
            const f = function (x: number, y: number = 3) { return x + y; };
            console.log(f(4));
            console.log(f(4, 10));
            """;
        Assert.Equal("7\n14\n", TestHarness.Run(source, mode));
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void DefaultParam_ArrowFunction_OmittedArg_AppliesDefault(ExecutionMode mode)
    {
        // #646
        var source = """
            const af = (x: number, y: number = 3) => x + y;
            console.log(af(4));
            console.log(af(4, 10));
            """;
        Assert.Equal("7\n14\n", TestHarness.Run(source, mode));
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void DefaultParam_AsyncArrow_OmittedArg_AppliesDefault(ExecutionMode mode)
    {
        // #646 / #635: the async function-expression / async-arrow path inherits the same
        // arrow default application.
        var source = """
            const af = async (x: number, y: number = 3) => x + y;
            af(4).then(v => console.log(v));
            """;
        Assert.Equal("7\n", TestHarness.Run(source, mode));
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void DefaultParam_ReferencesEarlierParam_Arrow(ExecutionMode mode)
    {
        // #698: a later default may reference any earlier parameter. The arrow / function-expression
        // path used to reject this at type-check time with "Undefined variable 'a'".
        var source = """
            const g = (a: number, b: number = a * 2) => a + b;
            console.log(g(5));
            console.log(g(5, 1));
            """;
        Assert.Equal("15\n6\n", TestHarness.Run(source, mode));
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void DefaultParam_ReferencesEarlierParam_FunctionDeclaration(ExecutionMode mode)
    {
        // #698: function-declaration earlier-param default. Compiled mode used to crash at runtime
        // ("Undefined variable 'a'") inside the OverloadGenerator forwarding stub, which emitted the
        // default expression without making the provided parameters resolvable.
        var source = """
            function f(a: number, b: number = a) { return a + b; }
            console.log(f(5));
            console.log(f(5, 1));
            """;
        Assert.Equal("10\n6\n", TestHarness.Run(source, mode));
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void DefaultParam_ReferencesEarlierParam_StringValue(ExecutionMode mode)
    {
        // #698: reference-typed earlier-param default (exercises a different conversion path
        // than the numeric/value-typed case).
        var source = """
            function f(a: string, b: string = a) { return a + b; }
            console.log(f("x"));
            console.log(f("x", "y"));
            """;
        Assert.Equal("xx\nxy\n", TestHarness.Run(source, mode));
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void DefaultParam_ReferencesEarlierDefaultedParam_Chain(ExecutionMode mode)
    {
        // #698: a default may reference an earlier *defaulted* parameter, e.g. c = a + b where b is
        // itself defaulted. Compiled mode resolves this via cascading overload forwarding — each
        // overload fills exactly one default and forwards to the next-higher arity, so every prior
        // parameter (provided or defaulted) is a real argument of the method evaluating the next default.
        var source = """
            function f(a: number, b: number = 1, c: number = a + b) { return a + b + c; }
            console.log(f(5));
            console.log(f(5, 2));
            console.log(f(5, 2, 3));
            """;
        Assert.Equal("12\n14\n10\n", TestHarness.Run(source, mode));
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.InterpretedOnly), MemberType = typeof(ExecutionModes))]
    public void DefaultParam_ReferencesEarlierParam_Method(ExecutionMode mode)
    {
        // #698: a method default referencing an earlier parameter type-checks and evaluates correctly
        // in the interpreter. (Compiled class methods ignore default parameter values entirely — a
        // broader, separately tracked gap — so this is interpreter-only.)
        var source = """
            class C { m(a: number, b: number = a * 2) { return a + b; } }
            console.log(new C().m(5));
            console.log(new C().m(5, 1));
            """;
        Assert.Equal("15\n6\n", TestHarness.Run(source, mode));
    }
}
