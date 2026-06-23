using SharpTS.Compilation;
using SharpTS.LanguageServer;
using SharpTS.LanguageServer.Project;

// Entry point for the `sharpts-lsp` tool: this executable *is* the language server
// (LSP over stdio). Parses the same assembly-reference options the old `sharpts lsp`
// command did, then hands off to the server host.
string? projectFile = null, sdkPath = null;
var references = new List<string>();

for (int i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--project" when i + 1 < args.Length: projectFile = args[++i]; break;
        case "--sdk-path" when i + 1 < args.Length: sdkPath = args[++i]; break;
        case "-r" or "--reference" when i + 1 < args.Length: references.Add(args[++i]); break;
    }
}

try
{
    // Resolve @DotNetType targets against the project's referenced assemblies (via
    // MetadataLoadContext). With no project/refs the loader still resolves the BCL.
    var paths = new List<string>(references);
    if (projectFile != null && File.Exists(projectFile))
        paths.AddRange(CsprojParser.Parse(projectFile));

    using var loader = new AssemblyReferenceLoader(paths, sdkPath);
    Func<IEnumerable<string>> typeNames = () => loader.GetAllPublicTypes()
        .Select(t => t.FullName)
        .Where(n => !string.IsNullOrEmpty(n))
        .Cast<string>();

    await SharpTSLanguageServer.RunAsync(loader.TryResolve, typeNames);
}
catch (Exception ex)
{
    await Console.Error.WriteLineAsync($"[LSP Fatal] {ex.Message}");
    Environment.Exit(1);
}
