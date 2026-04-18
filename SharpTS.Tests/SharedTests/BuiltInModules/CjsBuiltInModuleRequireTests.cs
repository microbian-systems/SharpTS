using SharpTS.Tests.Infrastructure;
using Xunit;

namespace SharpTS.Tests.SharedTests.BuiltInModules;

/// <summary>
/// Tests that CJS require() works for built-in modules in both interpreter and compiled modes.
/// These validate the EmitCjsBuiltInModuleObject code path in compiled mode,
/// which creates namespace objects for built-in modules accessed via require().
/// </summary>
public class CjsBuiltInModuleRequireTests
{
    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void Cjs_Require_Path_Join(ExecutionMode mode)
    {
        var files = new Dictionary<string, string>
        {
            ["main.cjs"] = """
                const path = require('path');
                console.log(typeof path.join);
                const result = path.join('foo', 'bar');
                console.log(result.includes('foo') && result.includes('bar'));
                """
        };
        var output = TestHarness.RunModules(files, "main.cjs", mode);
        Assert.Equal("function\ntrue\n", output);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void Cjs_Require_Assert_Ok(ExecutionMode mode)
    {
        var files = new Dictionary<string, string>
        {
            ["main.cjs"] = """
                const assert = require('assert');
                assert.ok(true);
                console.log('passed');
                """
        };
        var output = TestHarness.RunModules(files, "main.cjs", mode);
        Assert.Equal("passed\n", output);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void Cjs_Require_Tty_Isatty(ExecutionMode mode)
    {
        var files = new Dictionary<string, string>
        {
            ["main.cjs"] = """
                const tty = require('tty');
                console.log(typeof tty.isatty);
                console.log(tty.isatty(999));
                """
        };
        var output = TestHarness.RunModules(files, "main.cjs", mode);
        Assert.Equal("function\nfalse\n", output);
    }

    /// <summary>
    /// Tests require() of multiple built-in modules in the same CJS file.
    /// </summary>
    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void Cjs_Require_MultipleModules(ExecutionMode mode)
    {
        var files = new Dictionary<string, string>
        {
            ["main.cjs"] = """
                const path = require('path');
                const tty = require('tty');
                console.log(typeof path.join);
                console.log(typeof tty.isatty);
                """
        };
        var output = TestHarness.RunModules(files, "main.cjs", mode);
        Assert.Equal("function\nfunction\n", output);
    }

    /// <summary>
    /// Regression: stdlib ESM modules required from a CJS caller.
    /// </summary>
    /// <remarks>
    /// The 'os' module migrated to stdlib/node/os.ts (embedded stdlib, ESM). CJS require()
    /// of ESM-in-assembly modules needs special handling: interpreter falls back to
    /// ExportsAsObject() when DefaultExport is null (named-exports-only modules have no
    /// default); compiled mode materializes a namespace from the module's export static
    /// fields. Both paths landed with the path migration; this test pins that os's
    /// equivalent shape also works.
    /// </remarks>
    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void Cjs_Require_Os_Platform(ExecutionMode mode)
    {
        var files = new Dictionary<string, string>
        {
            ["main.cjs"] = """
                const os = require('os');
                console.log(typeof os.platform);
                console.log(typeof os.EOL);
                """
        };
        var output = TestHarness.RunModules(files, "main.cjs", mode);
        Assert.Equal("function\nstring\n", output);
    }

    /// <summary>
    /// Regression: querystring (also an embedded stdlib ESM module) via require().
    /// </summary>
    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void Cjs_Require_Querystring_Parse(ExecutionMode mode)
    {
        var files = new Dictionary<string, string>
        {
            ["main.cjs"] = """
                const qs = require('querystring');
                console.log(typeof qs.parse);
                const parsed = qs.parse('a=1&b=2');
                console.log(parsed.a);
                console.log(parsed.b);
                """
        };
        var output = TestHarness.RunModules(files, "main.cjs", mode);
        Assert.Equal("function\n1\n2\n", output);
    }
}
