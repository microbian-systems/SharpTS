using SharpTS.Tests.Infrastructure;
using Xunit;

namespace SharpTS.Tests.SharedTests;

/// <summary>
/// Regression tests for compiler limitations surfaced by the Phase 3c `path`
/// migration attempt. Both are pre-existing latent bugs that weren't hit by
/// any previous stdlib migration — path's shape (rest-parameter APIs,
/// namespace-like `posix`/`win32` sub-objects) is what exposes them.
/// </summary>
/// <remarks>
/// These tests are currently <see cref="FactAttribute.Skip"/>-ped so they
/// document the limitation without counting as a failing test. Removing
/// <c>Skip</c> once either bug is fixed will cause the corresponding case
/// to light up green.
/// </remarks>
public class CrossModuleRestParamTests
{
    [Theory(Skip = "Known compiler bug: a function with `...rest` parameters dispatched " +
        "across a module boundary receives no args packaged into the rest array. " +
        "Same-module calls work. Blocks path migration (path.join/resolve use rest).")]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void RestParam_AcrossModuleImport(ExecutionMode mode)
    {
        var files = new Dictionary<string, string>
        {
            ["lib.ts"] = """
                export function joinIt(...parts: string[]): string {
                    return parts.join(',');
                }
                """,
            ["main.ts"] = """
                import { joinIt } from './lib';
                console.log(joinIt('a', 'b', 'c'));
                """
        };

        var output = TestHarness.RunModules(files, "main.ts", mode);
        Assert.Equal("a,b,c\n", output);
    }

    [Theory(Skip = "Known compiler bug: calling a method stored in an object literal " +
        "exported from a module throws NullReferenceException in compiled mode " +
        "(the wrapper loses its invocation target). Blocks path.posix/path.win32 sub-object APIs.")]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void ObjectLiteralMethod_AcrossModuleImport(ExecutionMode mode)
    {
        var files = new Dictionary<string, string>
        {
            ["lib.ts"] = """
                export const ns = {
                    val: 'hi',
                    greet(name: string): string { return 'hello ' + name; },
                };
                """,
            ["main.ts"] = """
                import { ns } from './lib';
                console.log(ns.val);
                console.log(ns.greet('world'));
                """
        };

        var output = TestHarness.RunModules(files, "main.ts", mode);
        Assert.Equal("hi\nhello world\n", output);
    }
}
