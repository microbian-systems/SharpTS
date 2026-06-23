using Microsoft.Extensions.DependencyInjection;
using OmniSharp.Extensions.LanguageServer.Server;
using SharpTS.LanguageServer.Handlers;
using SharpTS.LanguageServer.Services;

namespace SharpTS.LanguageServer;

/// <summary>
/// Entry point for the SharpTS language server (LSP over stdio). Launched by
/// <c>sharpts lsp</c>. Speaks standard LSP, so any editor (VS Code, Neovim, Helix, …)
/// can drive it.
/// </summary>
public static class SharpTSLanguageServer
{
    /// <param name="resolve">CLR type resolver for @DotNetType validation. Null falls back
    /// to the in-process registry (BCL only).</param>
    /// <param name="typeNames">Enumerates public type names from referenced assemblies for
    /// CLR-type-name completion inside @DotNetType("…"). Null = no such completion.</param>
    public static async Task RunAsync(Func<string, Type?>? resolve = null, Func<IEnumerable<string>>? typeNames = null)
    {
        var server = await OmniSharp.Extensions.LanguageServer.Server.LanguageServer.From(options => options
            .WithInput(Console.OpenStandardInput())
            .WithOutput(Console.OpenStandardOutput())
            .WithServices(services => services
                .AddSingleton<DocumentStore>()
                .AddSingleton(new DiagnosticsService(resolve))
                .AddSingleton(new DecoratorService(resolve, typeNames))
                .AddSingleton(new MemberHoverService(resolve)))
            .WithHandler<TextDocumentSyncHandler>()
            .WithHandler<HoverHandler>()
            .WithHandler<CompletionHandler>()
            .WithHandler<SignatureHelpHandler>());

        await server.WaitForExit;
    }
}
