using OmniSharp.Extensions.LanguageServer.Protocol.Client.Capabilities;
using OmniSharp.Extensions.LanguageServer.Protocol.Document;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using OmniSharp.Extensions.LanguageServer.Protocol.Server.Capabilities;
using SharpTS.LanguageServer.Services;

namespace SharpTS.LanguageServer.Handlers;

/// <summary>Hover for SharpTS decorators (resolved .NET type + XML doc).</summary>
public sealed class HoverHandler : HoverHandlerBase
{
    private readonly DocumentStore _store;
    private readonly DecoratorService _decorators;

    public HoverHandler(DocumentStore store, DecoratorService decorators)
    {
        _store = store;
        _decorators = decorators;
    }

    public override Task<Hover?> Handle(HoverParams request, CancellationToken ct)
    {
        if (!_store.TryGet(request.TextDocument.Uri.ToString(), out var text))
            return Task.FromResult<Hover?>(null);

        return Task.FromResult(
            _decorators.Hover(text, request.Position.Line, request.Position.Character));
    }

    protected override HoverRegistrationOptions CreateRegistrationOptions(
        HoverCapability capability, ClientCapabilities clientCapabilities)
        => new() { DocumentSelector = TextDocumentSelector.ForLanguage("typescript", "typescriptreact") };
}
