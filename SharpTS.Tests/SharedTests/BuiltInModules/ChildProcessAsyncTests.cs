using System.Runtime.InteropServices;
using SharpTS.Tests.Infrastructure;
using Xunit;

namespace SharpTS.Tests.SharedTests.BuiltInModules;

/// <summary>
/// Tests for the async child_process methods (exec, spawn).
/// These methods return ChildProcess objects and use callbacks/events.
/// </summary>
public class ChildProcessAsyncTests
{
    [Theory]
    [MemberData(nameof(ExecutionModes.InterpretedOnly), MemberType = typeof(ExecutionModes))]
    public void Exec_ReturnsChildProcess(ExecutionMode mode)
    {
        var echoCommand = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? "echo test"
            : "echo test";

        var files = new Dictionary<string, string>
        {
            ["main.ts"] = $$"""
                import { exec } from 'child_process';
                const child = exec('{{echoCommand}}');
                console.log(typeof child === 'object');
                console.log(typeof child.on === 'function');
                """
        };

        var output = TestHarness.RunModules(files, "main.ts", mode);
        Assert.Equal("true\ntrue\n", output);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.InterpretedOnly), MemberType = typeof(ExecutionModes))]
    public void Spawn_ReturnsChildProcess(ExecutionMode mode)
    {
        var command = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? "cmd.exe"
            : "/bin/echo";
        var args = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? "['/c', 'echo', 'spawned']"
            : "['spawned']";

        var files = new Dictionary<string, string>
        {
            ["main.ts"] = $$"""
                import { spawn } from 'child_process';
                const child = spawn('{{command}}', {{args}});
                console.log(typeof child === 'object');
                console.log(typeof child.on === 'function');
                console.log(child.stdout !== null && child.stdout !== undefined);
                console.log(child.stderr !== null && child.stderr !== undefined);
                """
        };

        var output = TestHarness.RunModules(files, "main.ts", mode);
        Assert.Equal("true\ntrue\ntrue\ntrue\n", output);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void ExecSync_StillWorks(ExecutionMode mode)
    {
        // Ensure sync methods still work after adding async methods
        var echoCommand = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? "echo verify"
            : "echo verify";

        var files = new Dictionary<string, string>
        {
            ["main.ts"] = $$"""
                import { execSync } from 'child_process';
                const result = execSync('{{echoCommand}}');
                console.log(result.trim() === 'verify');
                """
        };

        var output = TestHarness.RunModules(files, "main.ts", mode);
        Assert.Equal("true\n", output);
    }
}
