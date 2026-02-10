using System.Text;
using SharpTS.Diagnostics.Exceptions;

namespace SharpTS.Compilation.Bundling.Canonical;

/// <summary>
/// Canonical single-file bundler that mirrors the .NET HostModel bundle pipeline.
///
/// Pipeline:
///   1. Resolve apphost template
///   2. Read and patch apphost (DLL name placeholder)
///   3. Locate bundle header placeholder
///   4. Classify and collect files to embed
///   5. Embed files with alignment rules
///   6. Write manifest
///   7. Patch header offset
///   8. Write output with atomic I/O policy
/// </summary>
public class CanonicalBundler : IBundler
{
    /// <inheritdoc/>
    public BundleTechnique Technique => BundleTechnique.CanonicalBundler;

    /// <inheritdoc/>
    public BundleResult CreateSingleFileExecutable(string dllPath, string exePath, string assemblyName)
    {
        var options = new CanonicalBundleOptions
        {
            AssemblyName = assemblyName,
            DllPath = dllPath,
            OutputPath = exePath
        };

        return CreateBundle(options);
    }

    /// <summary>
    /// Creates a single-file bundle with full options control.
    /// </summary>
    public BundleResult CreateBundle(CanonicalBundleOptions options)
    {
        var diag = options.DiagnosticOutput ? Console.Out : null;

        // 1. Resolve apphost template
        var (apphostPath, sdkVersion) = ResolveAppHostTemplate(options, diag);
        diag?.WriteLine($"[canonical] Resolved apphost template: {apphostPath} (SDK {sdkVersion})");

        // 2. Read apphost template
        var apphostBytes = ReadWithRetry(apphostPath, options);
        diag?.WriteLine($"[canonical] Apphost template size: {apphostBytes.Length} bytes");

        // 3. Patch DLL path placeholder
        CanonicalHostWriter.PatchDllPath(apphostBytes, options.AssemblyName);

        // 4. Find bundle header placeholder (before we start appending data)
        var headerPlaceholderOffset = CanonicalHostWriter.FindBundleHeaderPlaceholder(apphostBytes);
        diag?.WriteLine($"[canonical] Bundle header placeholder at offset: {headerPlaceholderOffset}");

        // 5. Collect files to embed
        var filesToEmbed = CollectFiles(options, sdkVersion, diag);

        // 6. Build the bundle
        var bundleBytes = AssembleBundle(apphostBytes, headerPlaceholderOffset, filesToEmbed, diag);

        // 7. Write output
        WriteOutput(options, bundleBytes);
        diag?.WriteLine($"[canonical] Wrote bundle: {options.OutputPath} ({bundleBytes.Length} bytes)");

        return new BundleResult(options.OutputPath, BundleTechnique.CanonicalBundler);
    }

    /// <summary>
    /// Collects and classifies all files to embed in the bundle.
    /// </summary>
    private static List<BundleFileItem> CollectFiles(CanonicalBundleOptions options, Version sdkVersion, TextWriter? diag)
    {
        var files = new List<BundleFileItem>();

        // Primary: main DLL (always included)
        var dllName = $"{options.AssemblyName}.dll";
        var dllBytes = ReadWithRetry(options.DllPath, options);
        var dllType = CanonicalFileClassifier.Classify(dllName);
        files.Add(new BundleFileItem(dllName, dllBytes, dllType));
        diag?.WriteLine($"[canonical] File: {dllName} ({dllBytes.Length} bytes, {dllType})");

        // Primary: runtimeconfig.json (always generated)
        var configName = $"{options.AssemblyName}.runtimeconfig.json";
        var runtimeConfig = GenerateRuntimeConfig(options.AssemblyName, sdkVersion);
        var configBytes = Encoding.UTF8.GetBytes(runtimeConfig);
        var configType = CanonicalFileClassifier.Classify(configName);
        files.Add(new BundleFileItem(configName, configBytes, configType));
        diag?.WriteLine($"[canonical] File: {configName} ({configBytes.Length} bytes, {configType})");

        // Additional files (from options)
        foreach (var (sourcePath, relativePath) in options.AdditionalFiles)
        {
            // Apply exclude patterns
            if (IsExcluded(relativePath, options.ExcludePatterns))
            {
                diag?.WriteLine($"[canonical] Excluded: {relativePath}");
                continue;
            }

            var additionalBytes = ReadWithRetry(sourcePath, options);
            var additionalType = CanonicalFileClassifier.Classify(relativePath);
            files.Add(new BundleFileItem(relativePath, additionalBytes, additionalType));
            diag?.WriteLine($"[canonical] File: {relativePath} ({additionalBytes.Length} bytes, {additionalType})");
        }

        return files;
    }

    /// <summary>
    /// Checks whether a relative path matches any exclusion pattern.
    /// Supports simple suffix matching (e.g., "*.pdb" matches "app.pdb").
    /// </summary>
    private static bool IsExcluded(string relativePath, IReadOnlyList<string> excludePatterns)
    {
        var fileName = Path.GetFileName(relativePath);
        foreach (var pattern in excludePatterns)
        {
            if (pattern.StartsWith('*'))
            {
                var suffix = pattern[1..];
                if (fileName.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            else if (fileName.Equals(pattern, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Assembles the complete bundle in memory.
    /// Layout: [apphost] [padding] [files...] [manifest]
    /// </summary>
    private static byte[] AssembleBundle(
        byte[] apphostBytes,
        int headerPlaceholderOffset,
        List<BundleFileItem> files,
        TextWriter? diag)
    {
        var alignment = CanonicalBundleFormat.GetCurrentAssemblyAlignment();

        // Generate deterministic bundle ID from all file contents
        var allContents = files.Select(f => f.Data).ToArray();
        var bundleId = CanonicalManifest.GenerateBundleId(allContents);
        diag?.WriteLine($"[canonical] Bundle ID: {bundleId}");

        // Build entries (offsets set during stream assembly)
        var entries = files.Select(f =>
            new CanonicalFileEntry(f.RelativePath, f.Data.Length, f.Type)).ToList();

        using var stream = new MemoryStream();

        // Write apphost
        stream.Write(apphostBytes);

        // Write each file with appropriate alignment
        for (int i = 0; i < files.Count; i++)
        {
            var file = files[i];
            var entry = entries[i];

            // Align assembly-type files
            if (CanonicalFileClassifier.RequiresAlignment(file.Type))
            {
                WritePadding(stream, alignment);
            }

            entry.Offset = stream.Position;
            stream.Write(file.Data);
            diag?.WriteLine($"[canonical] Embedded: {file.RelativePath} at offset {entry.Offset}");
        }

        // Write manifest
        var manifestOffset = stream.Position;
        var manifest = new CanonicalManifest(bundleId);
        foreach (var entry in entries)
        {
            manifest.AddEntry(entry);
        }

        using (var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true))
        {
            manifest.Write(writer);
        }

        diag?.WriteLine($"[canonical] Manifest at offset {manifestOffset}, {entries.Count} entries");

        // Finalize bytes and patch header offset
        var bundleBytes = stream.ToArray();
        CanonicalHostWriter.PatchBundleHeaderOffset(bundleBytes, headerPlaceholderOffset, manifestOffset);

        return bundleBytes;
    }

    /// <summary>
    /// Writes zero-padding to align the stream position to the given alignment.
    /// </summary>
    private static void WritePadding(MemoryStream stream, int alignment)
    {
        var misalignment = stream.Position % alignment;
        if (misalignment != 0)
        {
            var padding = alignment - misalignment;
            Span<byte> zeros = stackalloc byte[Math.Min((int)padding, 4096)];
            zeros.Clear();
            var remaining = padding;
            while (remaining > 0)
            {
                var chunk = (int)Math.Min(remaining, zeros.Length);
                stream.Write(zeros[..chunk]);
                remaining -= chunk;
            }
        }
    }

    /// <summary>
    /// Resolves the apphost template path and SDK version.
    /// </summary>
    private static (string Path, Version Version) ResolveAppHostTemplate(CanonicalBundleOptions options, TextWriter? diag)
    {
        if (options.AppHostTemplatePath != null)
        {
            if (!File.Exists(options.AppHostTemplatePath))
            {
                throw new CompileException(
                    $"Specified apphost template not found: {options.AppHostTemplatePath}");
            }

            var version = options.TargetFrameworkVersion ?? Environment.Version;
            return (options.AppHostTemplatePath, version);
        }

        var (path, sdkVersion) = AppHostTemplateResolver.FindAppHostTemplateWithVersion(diag);
        if (path == null || sdkVersion == null)
        {
            throw new CompileException(
                "Could not find or download apphost template. " +
                "Install the .NET SDK or ensure network access, " +
                "or provide an explicit template path via options.");
        }

        return (path, sdkVersion);
    }

    /// <summary>
    /// Reads a file with retry policy for transient I/O errors.
    /// </summary>
    private static byte[] ReadWithRetry(string path, CanonicalBundleOptions options)
    {
        byte[] result = [];
        BundlingIoPolicy.ExecuteWithRetry(
            () => result = File.ReadAllBytes(path),
            options.MaxRetries,
            options.RetryBaseDelayMs,
            $"reading '{Path.GetFileName(path)}'");
        return result;
    }

    /// <summary>
    /// Writes the bundle output using atomic or direct write based on options.
    /// </summary>
    private static void WriteOutput(CanonicalBundleOptions options, byte[] bundleBytes)
    {
        var dir = Path.GetDirectoryName(options.OutputPath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
        {
            Directory.CreateDirectory(dir);
        }

        if (options.UseAtomicWrite)
        {
            BundlingIoPolicy.AtomicWriteAllBytes(
                options.OutputPath, bundleBytes,
                options.MaxRetries, options.RetryBaseDelayMs);
        }
        else
        {
            BundlingIoPolicy.ExecuteWithRetry(
                () => File.WriteAllBytes(options.OutputPath, bundleBytes),
                options.MaxRetries,
                options.RetryBaseDelayMs,
                $"writing '{Path.GetFileName(options.OutputPath)}'");
        }

        CanonicalHostWriter.SetExecutePermission(options.OutputPath);
    }

    /// <summary>
    /// Generates runtimeconfig.json content for the given SDK version.
    /// </summary>
    private static string GenerateRuntimeConfig(string assemblyName, Version sdkVersion)
    {
        return $$"""
            {
              "runtimeOptions": {
                "tfm": "net{{sdkVersion.Major}}.{{sdkVersion.Minor}}",
                "framework": {
                  "name": "Microsoft.NETCore.App",
                  "version": "{{sdkVersion.Major}}.{{sdkVersion.Minor}}.{{sdkVersion.Build}}"
                }
              }
            }
            """;
    }

    /// <summary>
    /// Internal record for a file to embed in the bundle.
    /// </summary>
    private sealed record BundleFileItem(string RelativePath, byte[] Data, CanonicalFileType Type);
}
