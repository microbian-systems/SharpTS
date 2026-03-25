using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using SharpTS.Tests.Infrastructure;
using Xunit;

namespace SharpTS.Tests.CompilerTests;

/// <summary>
/// Tests for the --ref-asm flag functionality.
/// Verifies that compiled assemblies reference System.Runtime (not System.Private.CoreLib)
/// and can be used as compile-time references by other C# projects.
/// </summary>
public class ReferenceAssemblyTests
{
    /// <summary>
    /// Verifies that an async function compiled with --ref-asm references System.Runtime.
    /// </summary>
    [Fact]
    public void RefAsm_AsyncFunction_ReferencesSystemRuntime()
    {
        var source = """
            async function getData(): Promise<string> {
                return "hello";
            }

            async function main() {
                const result = await getData();
                console.log(result);
            }

            main();
            """;

        var (tempDir, dllPath) = TestHarness.CompileWithRefAsm(source);
        try
        {
            var refs = GetAssemblyReferences(dllPath);
            Assert.Contains(refs, r => r == "System.Runtime");
            Assert.DoesNotContain(refs, r => r == "System.Private.CoreLib");
        }
        finally
        {
            CleanupTempDir(tempDir);
        }
    }

    /// <summary>
    /// Verifies that a generator function compiled with --ref-asm references System.Runtime.
    /// </summary>
    [Fact]
    public void RefAsm_Generator_ReferencesSystemRuntime()
    {
        var source = """
            function* generateNumbers(): Generator<number> {
                yield 1;
                yield 2;
                yield 3;
            }

            for (const n of generateNumbers()) {
                console.log(n);
            }
            """;

        var (tempDir, dllPath) = TestHarness.CompileWithRefAsm(source);
        try
        {
            var refs = GetAssemblyReferences(dllPath);
            Assert.Contains(refs, r => r == "System.Runtime");
            Assert.DoesNotContain(refs, r => r == "System.Private.CoreLib");
        }
        finally
        {
            CleanupTempDir(tempDir);
        }
    }

    /// <summary>
    /// Verifies that combined async+generator code compiled with --ref-asm references System.Runtime.
    /// </summary>
    [Fact]
    public void RefAsm_AsyncGenerator_ReferencesSystemRuntime()
    {
        var source = """
            async function delay(ms: number): Promise<void> {
                return;
            }

            function* range(start: number, end: number): Generator<number> {
                for (let i = start; i < end; i++) {
                    yield i;
                }
            }

            async function main() {
                for (const n of range(1, 4)) {
                    await delay(0);
                    console.log(n);
                }
            }

            main();
            """;

        var (tempDir, dllPath) = TestHarness.CompileWithRefAsm(source);
        try
        {
            var refs = GetAssemblyReferences(dllPath);
            Assert.Contains(refs, r => r == "System.Runtime");
            Assert.DoesNotContain(refs, r => r == "System.Private.CoreLib");
        }
        finally
        {
            CleanupTempDir(tempDir);
        }
    }

    /// <summary>
    /// Verifies that --ref-asm compiled code still executes correctly at runtime.
    /// </summary>
    [Fact]
    public void RefAsm_RuntimeExecution_Works()
    {
        var source = """
            async function getData(): Promise<string> {
                return "async works";
            }

            function* genNumbers(): Generator<number> {
                yield 1;
                yield 2;
            }

            async function main() {
                const data = await getData();
                console.log(data);

                for (const n of genNumbers()) {
                    console.log(n);
                }
            }

            main();
            """;

        var (tempDir, dllPath) = TestHarness.CompileWithRefAsm(source);
        try
        {
            var output = TestHarness.ExecuteCompiledDll(dllPath);
            Assert.Equal("async works\n1\n2\n", output);
        }
        finally
        {
            CleanupTempDir(tempDir);
        }
    }

    /// <summary>
    /// Helper to get assembly reference names from a PE file.
    /// </summary>
    private static List<string> GetAssemblyReferences(string dllPath)
    {
        using var stream = File.OpenRead(dllPath);
        using var peReader = new PEReader(stream);
        var metadataReader = peReader.GetMetadataReader();

        var refs = new List<string>();
        foreach (var refHandle in metadataReader.AssemblyReferences)
        {
            var reference = metadataReader.GetAssemblyReference(refHandle);
            var name = metadataReader.GetString(reference.Name);
            refs.Add(name);
        }
        return refs;
    }

    private static void CleanupTempDir(string tempDir)
    {
        try
        {
            Directory.Delete(tempDir, true);
        }
        catch
        {
            // Ignore cleanup errors
        }
    }
}
