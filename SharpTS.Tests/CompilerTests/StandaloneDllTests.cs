using Xunit;

namespace SharpTS.Tests.CompilerTests;

/// <summary>
/// Tests that ensure compiled DLLs remain standalone (no SharpTS.dll dependency).
/// </summary>
public class StandaloneDllTests
{
    /// <summary>
    /// Scans Compilation/ source files to ensure no direct typeof() references to
    /// SharpTS types that would embed assembly references in emitted IL.
    ///
    /// WRONG: typeof(RuntimeTypes).GetMethod(...) - embeds SharpTS.dll reference
    /// RIGHT: EmitReflectionCall(il, "SharpTS.Compilation.RuntimeTypes, SharpTS", ...) - runtime lookup
    /// </summary>
    [Fact]
    public void CompilationFiles_ShouldNotUseTypeofForEmittedIL()
    {
        var repoRoot = FindRepoRoot();
        var compilationDir = Path.Combine(repoRoot, "Compilation");
        var violations = new List<string>();

        // Types that must NOT be referenced via typeof() in emitted IL
        var forbiddenPatterns = new[]
        {
            "typeof(RuntimeTypes)",
            "typeof(PropertyDescriptorStore)",
            "typeof(ObjectBuiltIns)",
        };

        foreach (var file in Directory.GetFiles(compilationDir, "*.cs", SearchOption.AllDirectories))
        {
            var fileName = Path.GetFileName(file);

            // Skip the RuntimeTypes files themselves - they define the types, not emit IL referencing them
            if (fileName.StartsWith("RuntimeTypes."))
                continue;

            var lines = File.ReadAllLines(file);
            for (int i = 0; i < lines.Length; i++)
            {
                var line = lines[i];
                var trimmed = line.TrimStart();

                // Skip comments
                if (trimmed.StartsWith("//") || trimmed.StartsWith("*") || trimmed.StartsWith("/*"))
                    continue;

                foreach (var pattern in forbiddenPatterns)
                {
                    if (line.Contains(pattern))
                    {
                        violations.Add($"{fileName}:{i + 1}: {trimmed}");
                    }
                }
            }
        }

        Assert.True(violations.Count == 0,
            $"Found {violations.Count} typeof() references that create SharpTS.dll dependencies in emitted IL.\n" +
            $"Use EmitReflectionCall/EmitReflectionCallVoid instead.\n\n" +
            string.Join("\n", violations.Take(20)));
    }

    private static string FindRepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (dir != null)
        {
            if (Directory.Exists(Path.Combine(dir, "Compilation")) &&
                File.Exists(Path.Combine(dir, "SharpTS.csproj")))
            {
                return dir;
            }
            dir = Path.GetDirectoryName(dir);
        }
        throw new InvalidOperationException("Could not find repository root");
    }
}
