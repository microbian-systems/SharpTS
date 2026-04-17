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
}
