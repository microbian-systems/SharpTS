using System.Diagnostics;
using Xunit;
using Xunit.Abstractions;

namespace SharpTS.Tests.IntegrationTests;

/// <summary>
/// Shared fixture that installs npm packages once for the entire test class.
/// The temp directory persists across all tests and is cleaned up after the last test.
/// </summary>
public class NpmFixture : IDisposable
{
    public string PackageDir { get; }
    public bool NpmAvailable { get; }

    /// <summary>Pinned package versions for reproducibility.</summary>
    private static readonly (string Name, string Version)[] Packages =
    [
        ("ms", "2.1.3"),
        ("uuid", "9.0.1"),
        ("debug", "4.3.4"),
        ("semver", "7.6.0"),
        ("minimatch", "9.0.4"),
        ("yaml", "2.4.1"),
        ("lodash", "4.17.21"),
    ];

    public NpmFixture()
    {
        PackageDir = Path.Combine(Path.GetTempPath(), "sharpts_npm_smoke");
        NpmAvailable = IsNpmOnPath();

        if (!NpmAvailable) return;

        // Reuse existing install if the marker file exists (avoids repeated downloads).
        var marker = Path.Combine(PackageDir, ".sharpts_npm_installed");
        if (File.Exists(marker)) return;

        Directory.CreateDirectory(PackageDir);

        // Initialize package.json so npm install works.
        RunProcess("npm", "init -y", PackageDir);

        // Install all packages in one shot.
        var specs = string.Join(" ", Packages.Select(p => $"{p.Name}@{p.Version}"));
        var result = RunProcess("npm", $"install --save {specs}", PackageDir, timeoutMs: 120_000);
        if (result.ExitCode != 0)
            throw new InvalidOperationException(
                $"npm install failed (exit {result.ExitCode}):\n{result.StdErr}");

        File.WriteAllText(marker, DateTime.UtcNow.ToString("O"));
    }

    public void Dispose()
    {
        // Intentionally leave the directory for caching across test runs.
        // CI can wipe temp if needed.
    }

    private static bool IsNpmOnPath()
    {
        try
        {
            var result = RunProcess("npm", "--version", Path.GetTempPath(), timeoutMs: 10_000);
            return result.ExitCode == 0;
        }
        catch { return false; }
    }

    internal static (int ExitCode, string StdOut, string StdErr) RunProcess(
        string fileName, string arguments, string workingDir, int timeoutMs = 60_000)
    {
        // On Windows, .cmd/.bat scripts (like npm.cmd) require cmd.exe to execute
        // when UseShellExecute is false.
        string actualFile = fileName;
        string actualArgs = arguments;
        if (OperatingSystem.IsWindows() && fileName == "npm")
        {
            actualFile = "cmd.exe";
            actualArgs = $"/c npm {arguments}";
        }

        var psi = new ProcessStartInfo(actualFile, actualArgs)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            WorkingDirectory = workingDir
        };

        using var process = Process.Start(psi)!;
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();

        if (!process.WaitForExit(timeoutMs))
        {
            process.Kill();
            throw new TimeoutException($"{fileName} {arguments} exceeded {timeoutMs}ms");
        }

        return (process.ExitCode, stdoutTask.Result, stderrTask.Result);
    }
}

/// <summary>
/// Smoke tests that validate SharpTS against real npm packages.
/// Requires npm on PATH; tests skip gracefully otherwise.
/// Filter: dotnet test --filter "Category=npm"
/// </summary>
[Trait("Category", "npm")]
public class RealPackageSmokeTests : IClassFixture<NpmFixture>
{
    private readonly NpmFixture _npm;
    private readonly ITestOutputHelper _output;

    public RealPackageSmokeTests(NpmFixture npm, ITestOutputHelper output)
    {
        _npm = npm;
        _output = output;
    }

    private void SkipIfNoNpm()
    {
        Skip.If(!_npm.NpmAvailable, "npm is not available on PATH");
    }

    private CliTestHelper.CliResult RunInterpreter(string scriptPath)
    {
        var result = CliTestHelper.RunCli($"\"{scriptPath}\"", _npm.PackageDir, TimeSpan.FromSeconds(60));
        _output.WriteLine($"[interpreter] exit={result.ExitCode}");
        _output.WriteLine($"[interpreter] stdout:\n{result.StandardOutput}");
        if (!string.IsNullOrEmpty(result.StandardError))
            _output.WriteLine($"[interpreter] stderr:\n{result.StandardError}");
        return result;
    }

    private (int ExitCode, string Output) CompileAndRun(string scriptPath)
    {
        var compile = CliTestHelper.RunCli($"-c \"{scriptPath}\"", _npm.PackageDir, TimeSpan.FromSeconds(60));
        _output.WriteLine($"[compile] exit={compile.ExitCode}");
        if (compile.ExitCode != 0)
        {
            var msg = compile.StandardOutput + compile.StandardError;
            _output.WriteLine($"[compile] output:\n{msg}");
            return (compile.ExitCode, msg);
        }

        var dllName = Path.GetFileNameWithoutExtension(scriptPath) + ".dll";
        var dllPath = Path.Combine(_npm.PackageDir, dllName);
        if (!File.Exists(dllPath))
            return (-1, $"DLL not found at {dllPath}");

        var (exitCode, stdOut, stdErr) = NpmFixture.RunProcess("dotnet", $"\"{dllPath}\"", _npm.PackageDir);
        var output = stdOut + stdErr;
        _output.WriteLine($"[run] exit={exitCode}");
        _output.WriteLine($"[run] output:\n{output}");
        return (exitCode, CliTestHelper.NormalizeOutput(output));
    }

    private string CreateScript(string name, string content)
    {
        var path = Path.Combine(_npm.PackageDir, name);
        File.WriteAllText(path, content);
        return path;
    }

    // ──────────────────────────────────────────────────────────────
    // ms — tiny duration parser (~150 LOC, zero deps)
    // ──────────────────────────────────────────────────────────────

    [SkippableFact]
    public void Ms_Interpreter()
    {
        SkipIfNoNpm();
        var script = CreateScript("test_ms.cjs", """
            const ms = require('ms');
            console.log(ms('2 days'));
            console.log(ms('1h'));
            console.log(ms(60000));
            """);

        var result = RunInterpreter(script);
        Assert.Equal(0, result.ExitCode);
        Assert.Contains("172800000", result.StandardOutput);
        Assert.Contains("3600000", result.StandardOutput);
        Assert.Contains("1m", result.StandardOutput);
    }

    [SkippableFact]
    public void Ms_Compiled()
    {
        SkipIfNoNpm();
        var script = CreateScript("test_ms_c.cjs", """
            const ms = require('ms');
            console.log(ms('2 days'));
            console.log(ms('1h'));
            console.log(ms(60000));
            """);

        var (exit, output) = CompileAndRun(script);
        Assert.Equal(0, exit);
        Assert.Contains("172800000", output);
        Assert.Contains("3600000", output);
        Assert.Contains("1m", output);
    }

    // ──────────────────────────────────────────────────────────────
    // uuid — UUID generation, tests crypto interop
    // ──────────────────────────────────────────────────────────────

    [SkippableFact]
    public void Uuid_Interpreter()
    {
        SkipIfNoNpm();
        var script = CreateScript("test_uuid.cjs", """
            const { v4 } = require('uuid');
            const id = v4();
            console.log(typeof id);
            console.log(id.length);
            """);

        var result = RunInterpreter(script);
        Assert.Equal(0, result.ExitCode);
        Assert.Contains("string", result.StandardOutput);
        Assert.Contains("36", result.StandardOutput);
    }

    [SkippableFact(Skip = "Blocked on interpreter issues")]
    public void Uuid_Compiled()
    {
        SkipIfNoNpm();
        var script = CreateScript("test_uuid_c.cjs", """
            const { v4 } = require('uuid');
            const id = v4();
            console.log(typeof id);
            console.log(id.length);
            """);

        var (exit, output) = CompileAndRun(script);
        Assert.Equal(0, exit);
        Assert.Contains("string", output);
        Assert.Contains("36", output);
    }

    // ──────────────────────────────────────────────────────────────
    // debug — logging utility (depends on ms)
    // ──────────────────────────────────────────────────────────────

    [SkippableFact]
    public void Debug_Interpreter()
    {
        SkipIfNoNpm();
        var script = CreateScript("test_debug.cjs", """
            const debug = require('debug');
            const log = debug('test');
            console.log(typeof debug);
            console.log(typeof log);
            """);

        var result = RunInterpreter(script);
        Assert.Equal(0, result.ExitCode);
        Assert.Contains("function", result.StandardOutput);
    }

    [SkippableFact(Skip = "Blocked on interpreter issues")]
    public void Debug_Compiled()
    {
        SkipIfNoNpm();
        var script = CreateScript("test_debug_c.cjs", """
            const debug = require('debug');
            const log = debug('test');
            console.log(typeof debug);
            console.log(typeof log);
            """);

        var (exit, output) = CompileAndRun(script);
        Assert.Equal(0, exit);
        Assert.Contains("function", output);
    }

    // ──────────────────────────────────────────────────────────────
    // semver — semantic version parsing
    // ──────────────────────────────────────────────────────────────

    [SkippableFact]
    public void Semver_Interpreter()
    {
        SkipIfNoNpm();
        var script = CreateScript("test_semver.cjs", """
            const semver = require('semver');
            console.log(semver.valid('1.2.3'));
            console.log(semver.gt('1.2.3', '1.2.0'));
            console.log(semver.satisfies('1.2.3', '>=1.0.0'));
            """);

        var result = RunInterpreter(script);
        Assert.Equal(0, result.ExitCode);
        Assert.Contains("1.2.3", result.StandardOutput);
        Assert.Contains("true", result.StandardOutput);
    }

    [SkippableFact(Skip = "Blocked on interpreter issues")]
    public void Semver_Compiled()
    {
        SkipIfNoNpm();
        var script = CreateScript("test_semver_c.cjs", """
            const semver = require('semver');
            console.log(semver.valid('1.2.3'));
            console.log(semver.gt('1.2.3', '1.2.0'));
            console.log(semver.satisfies('1.2.3', '>=1.0.0'));
            """);

        var (exit, output) = CompileAndRun(script);
        Assert.Equal(0, exit);
        Assert.Contains("1.2.3", output);
        Assert.Contains("true", output);
    }

    // ──────────────────────────────────────────────────────────────
    // minimatch — glob pattern matcher
    // ──────────────────────────────────────────────────────────────

    [SkippableFact]
    public void Minimatch_Interpreter()
    {
        SkipIfNoNpm();
        var script = CreateScript("test_minimatch.cjs", """
            const { minimatch } = require('minimatch');
            console.log(minimatch('foo.js', '*.js'));
            console.log(minimatch('bar.txt', '*.js'));
            """);

        var result = RunInterpreter(script);
        Assert.Equal(0, result.ExitCode);
        Assert.Contains("true", result.StandardOutput);
        Assert.Contains("false", result.StandardOutput);
    }

    [SkippableFact(Skip = "Blocked on interpreter issues")]
    public void Minimatch_Compiled()
    {
        SkipIfNoNpm();
        var script = CreateScript("test_minimatch_c.cjs", """
            const { minimatch } = require('minimatch');
            console.log(minimatch('foo.js', '*.js'));
            console.log(minimatch('bar.txt', '*.js'));
            """);

        var (exit, output) = CompileAndRun(script);
        Assert.Equal(0, exit);
        Assert.Contains("true", output);
        Assert.Contains("false", output);
    }

    // ──────────────────────────────────────────────────────────────
    // yaml — YAML parser
    // ──────────────────────────────────────────────────────────────

    [SkippableFact(Skip = "Blocked on CJS cross-module class resolution (yaml internal 'Pair' class)")]
    public void Yaml_Interpreter()
    {
        SkipIfNoNpm();
        var script = CreateScript("test_yaml.cjs", """
            const YAML = require('yaml');
            const obj = YAML.parse('a: 1\nb: 2');
            console.log(obj.a);
            console.log(obj.b);
            console.log(typeof YAML.stringify);
            """);

        var result = RunInterpreter(script);
        Assert.Equal(0, result.ExitCode);
        Assert.Contains("1", result.StandardOutput);
        Assert.Contains("2", result.StandardOutput);
        Assert.Contains("function", result.StandardOutput);
    }

    [SkippableFact(Skip = "Blocked on interpreter issues")]
    public void Yaml_Compiled()
    {
        SkipIfNoNpm();
        var script = CreateScript("test_yaml_c.cjs", """
            const YAML = require('yaml');
            const obj = YAML.parse('a: 1\nb: 2');
            console.log(obj.a);
            console.log(obj.b);
            console.log(typeof YAML.stringify);
            """);

        var (exit, output) = CompileAndRun(script);
        Assert.Equal(0, exit);
        Assert.Contains("1", output);
        Assert.Contains("2", output);
        Assert.Contains("function", output);
    }

    // ──────────────────────────────────────────────────────────────
    // lodash — utility kitchen sink
    // ──────────────────────────────────────────────────────────────

    [SkippableFact(Skip = "ASI resolved; now blocked on other parse errors in lodash source")]
    public void Lodash_Interpreter()
    {
        SkipIfNoNpm();
        var script = CreateScript("test_lodash.cjs", """
            const _ = require('lodash');
            console.log(typeof _);
            console.log(_.chunk([1, 2, 3, 4], 2));
            console.log(_.flatten([[1, 2], [3, 4]]));
            """);

        var result = RunInterpreter(script);
        Assert.Equal(0, result.ExitCode);
        Assert.Contains("function", result.StandardOutput);
    }

    [SkippableFact(Skip = "Blocked on interpreter issues")]
    public void Lodash_Compiled()
    {
        SkipIfNoNpm();
        var script = CreateScript("test_lodash_c.cjs", """
            const _ = require('lodash');
            console.log(typeof _);
            console.log(_.chunk([1, 2, 3, 4], 2));
            console.log(_.flatten([[1, 2], [3, 4]]));
            """);

        var (exit, output) = CompileAndRun(script);
        Assert.Equal(0, exit);
        Assert.Contains("function", output);
    }
}
