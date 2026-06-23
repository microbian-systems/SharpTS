using SharpTS.LanguageServer.Conversions;
using SharpTS.Parsing;
using LspDiagnostic = OmniSharp.Extensions.LanguageServer.Protocol.Models.Diagnostic;

namespace SharpTS.LanguageServer.Services;

/// <summary>
/// Produces the LSP diagnostics for a document. Phase 1 = the @DotNetType interop analyzer
/// (the tsserver-impossible value). Parse errors are left to the built-in TypeScript server.
/// </summary>
public sealed class DiagnosticsService
{
    private readonly InteropAnalyzer _interop = new();

    public List<LspDiagnostic> Analyze(string text, DiagnosticPublishMode mode = DiagnosticPublishMode.SharpTsOnly)
    {
        // Stage3 decorators are the run-mode default and are required for @DotNetType to parse.
        var tokens = new Lexer(text).ScanTokens();
        var parsed = new Parser(tokens, DecoratorMode.Stage3).Parse();
        if (!parsed.IsSuccess)
            return new List<LspDiagnostic>(); // syntax errors belong to tsserver

        var diags = _interop.Analyze(parsed.Statements);
        return LspConversions.ToLsp(diags, text, mode);
    }
}
