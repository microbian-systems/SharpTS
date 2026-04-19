using SharpTS.Tests.Infrastructure;
using Xunit;

namespace SharpTS.Tests.SharedTests;

/// <summary>
/// Regression tests for cross-module top-level name collisions. The compiler
/// previously shared a single <c>_topLevelStaticVars</c> dict across every
/// module — so a <c>const foo</c> in one module would shadow an exported
/// <c>function foo()</c> in another, even when the importer aliased the
/// named import. Any stdlib migration whose exports match common user
/// variable names (e.g. <c>os.platform</c>, <c>querystring.parse</c>) would
/// hit this.
/// </summary>
public class CrossModuleNameCollisionTests
{
    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void ExportedFunctionSurvivesConstShadowingInImporter(ExecutionMode mode)
    {
        var files = new Dictionary<string, string>
        {
            ["lib.ts"] = """
                export function foo(): string { return 'from-lib'; }
                """,
            ["main.ts"] = """
                import { foo as libFoo } from './lib';
                const foo = libFoo();
                console.log(foo);
                """
        };

        var output = TestHarness.RunModules(files, "main.ts", mode);
        Assert.Equal("from-lib\n", output);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void LibModuleReferencesOwnExportedFunctionDespiteImporterConflict(ExecutionMode mode)
    {
        // lib.ts should see its OWN `foo` via hoisting even when main.ts has
        // an unrelated `const foo`. The test passes only when the scoping
        // fix correctly isolates each module's top-level bindings.
        var files = new Dictionary<string, string>
        {
            ["lib.ts"] = """
                export function foo(): string { return 'ok'; }
                console.log('lib:', typeof foo);
                """,
            ["main.ts"] = """
                import { foo as libFoo } from './lib';
                const foo = libFoo();
                console.log('main:', foo);
                """
        };

        var output = TestHarness.RunModules(files, "main.ts", mode);
        Assert.Equal("lib: function\nmain: ok\n", output);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void TwoModulesDeclaringSameConstName(ExecutionMode mode)
    {
        var files = new Dictionary<string, string>
        {
            ["a.ts"] = """
                const x = 'from-a';
                export function getX(): string { return x; }
                """,
            ["b.ts"] = """
                const x = 'from-b';
                export function getX(): string { return x; }
                """,
            ["main.ts"] = """
                import { getX as getA } from './a';
                import { getX as getB } from './b';
                console.log(getA(), getB());
                """
        };

        var output = TestHarness.RunModules(files, "main.ts", mode);
        Assert.Equal("from-a from-b\n", output);
    }
}
