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

    // NOTE: require('os') in compiled mode fails because OsModuleEmitter has no
    // RegisterBuiltInModuleMethod registrations — the TSFunction wrappers are null.
    // This is a pre-existing gap: modules that only implemented IBuiltInModuleEmitter
    // for ESM direct-call dispatch (os, crypto, etc.) don't have the runtime helper
    // methods needed for first-class function wrappers in CJS mode.
    // Tracked as a separate issue from tty implementation.
}
