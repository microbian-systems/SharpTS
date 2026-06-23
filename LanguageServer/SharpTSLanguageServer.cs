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
    public static async Task RunAsync()
    {
        var server = await OmniSharp.Extensions.LanguageServer.Server.LanguageServer.From(options => options
            .WithInput(Console.OpenStandardInput())
            .WithOutput(Console.OpenStandardOutput())
            .WithServices(services => services
                .AddSingleton<DocumentStore>()
                .AddSingleton<DiagnosticsService>())
            .WithHandler<TextDocumentSyncHandler>());

        await server.WaitForExit;
    }
}
