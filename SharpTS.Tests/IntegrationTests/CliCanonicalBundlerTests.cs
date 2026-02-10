using Xunit;

namespace SharpTS.Tests.IntegrationTests;

/// <summary>
/// Integration tests for the canonical bundler CLI path (--bundler canonical).
/// </summary>
public class CliCanonicalBundlerTests
{
    [Fact]
    public void Compile_TargetExe_BundlerCanonical_UsesCanonicalBundler()
    {
        using var tempDir = CliTestHelper.CreateTempDirectory();
        var scriptPath = tempDir.CreateFile("app.ts", CliFixtures.SimpleHelloWorld);

        var result = CliTestHelper.RunCli($"-c \"{scriptPath}\" -t exe --bundler canonical", tempDir.Path);

        Assert.Equal(0, result.ExitCode);
        Assert.True(File.Exists(tempDir.GetPath("app.exe")));

        // When --bundler canonical is specified, must use canonical bundler
        Assert.Contains("canonical bundler", result.StandardOutput);
        Assert.DoesNotContain("SDK bundler", result.StandardOutput);
    }

    [Fact]
    public void Compile_TargetExe_BundlerCanonical_ExeActuallyWorks()
    {
        using var tempDir = CliTestHelper.CreateTempDirectory();
        var scriptPath = tempDir.CreateFile("hello.ts", CliFixtures.SimpleHelloWorld);

        var compileResult = CliTestHelper.RunCli($"-c \"{scriptPath}\" -t exe --bundler canonical", tempDir.Path);

        // Must use canonical bundler
        Assert.Equal(0, compileResult.ExitCode);
        Assert.Contains("canonical bundler", compileResult.StandardOutput);

        // The exe must actually run correctly
        var exePath = tempDir.GetPath("hello.exe");
        var psi = new System.Diagnostics.ProcessStartInfo(exePath)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            WorkingDirectory = tempDir.Path
        };

        using var process = System.Diagnostics.Process.Start(psi)!;
        var output = process.StandardOutput.ReadToEnd();
        process.WaitForExit();

        Assert.Equal(0, process.ExitCode);
        Assert.Contains("Hello, World!", output);
    }

    [Fact]
    public void Compile_TargetExe_BundlerCanonical_NumericScript_ExeActuallyWorks()
    {
        using var tempDir = CliTestHelper.CreateTempDirectory();
        var scriptPath = tempDir.CreateFile("numeric.ts", CliFixtures.NumericScript);

        var compileResult = CliTestHelper.RunCli($"-c \"{scriptPath}\" -t exe --bundler canonical", tempDir.Path);
        Assert.Equal(0, compileResult.ExitCode);

        var exePath = tempDir.GetPath("numeric.exe");
        Assert.True(File.Exists(exePath));

        var psi = new System.Diagnostics.ProcessStartInfo(exePath)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            WorkingDirectory = tempDir.Path
        };

        using var process = System.Diagnostics.Process.Start(psi)!;
        var output = process.StandardOutput.ReadToEnd();
        process.WaitForExit();

        Assert.Equal(0, process.ExitCode);
        Assert.Contains("15", output); // 1+2+3+4+5 = 15
    }

    [Fact]
    public void Compile_TargetExe_BundlerCanonical_QuietMode_NoBundlerMessage()
    {
        using var tempDir = CliTestHelper.CreateTempDirectory();
        var scriptPath = tempDir.CreateFile("app.ts", CliFixtures.SimpleHelloWorld);

        var result = CliTestHelper.RunCli($"-c \"{scriptPath}\" -t exe --bundler canonical --quiet", tempDir.Path);

        Assert.Equal(0, result.ExitCode);
        Assert.True(File.Exists(tempDir.GetPath("app.exe")));

        // Quiet mode should not show any output
        Assert.DoesNotContain("Compiled to", result.StandardOutput);
        Assert.DoesNotContain("bundler", result.StandardOutput);
    }

    [Fact]
    public void Compile_TargetExe_BundlerCanonical_CustomOutput_Works()
    {
        using var tempDir = CliTestHelper.CreateTempDirectory();
        var scriptPath = tempDir.CreateFile("app.ts", CliFixtures.SimpleHelloWorld);
        var outputPath = tempDir.GetPath("custom.exe");

        var result = CliTestHelper.RunCli($"-c \"{scriptPath}\" -t exe -o \"{outputPath}\" --bundler canonical", tempDir.Path);

        Assert.Equal(0, result.ExitCode);
        Assert.True(File.Exists(outputPath));
        Assert.Contains("canonical bundler", result.StandardOutput);
    }
}
