using System.Text.RegularExpressions;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using SharpTS.LanguageServer.Documentation;
using SharpTS.Runtime.DotNet;

namespace SharpTS.LanguageServer.Services;

/// <summary>
/// Backs hover and completion for SharpTS decorators. Text-based (works off the line at the
/// cursor, no AST position index needed yet), reusing the same CLR resolver as the interop
/// analyzer plus <see cref="XmlDocLoader"/> for .NET XML documentation.
/// </summary>
public sealed class DecoratorService
{
    private readonly Func<string, Type?> _resolve;
    private readonly XmlDocLoader _xmlDoc = new();

    public DecoratorService(Func<string, Type?>? resolve = null)
        => _resolve = resolve ?? DotNetTypeRegistry.Resolve;

    private static readonly Regex DecoratorRx =
        new(@"@(\w+)(?:\s*\(\s*""([^""]*)""\s*\))?", RegexOptions.Compiled);
    private static readonly Regex DecoratorPrefixRx =
        new(@"@(\w*)$", RegexOptions.Compiled);

    private sealed record Builtin(string Name, string Snippet, string Description);

    private static readonly Builtin[] Builtins =
    {
        new("DotNetType", "DotNetType(\"$0\")", "Maps this TypeScript class to an external .NET type. Argument: the fully-qualified CLR type name (e.g. \"System.Text.StringBuilder\")."),
        new("DotNetOverload", "DotNetOverload(\"$0\")", "Pins which .NET overload a method resolves to. Argument: a parameter type (e.g. \"int\")."),
        new("DotNetMethod", "DotNetMethod(\"$0\")", "Binds this member to a specific .NET method name."),
        new("DotNetProperty", "DotNetProperty(\"$0\")", "Binds this member to a specific .NET property name."),
        new("DotNetField", "DotNetField(\"$0\")", "Binds this member to a specific .NET field name."),
        new("DotNetEvent", "DotNetEvent(\"$0\")", "Binds this member to a specific .NET event name."),
    };

    /// <summary>Hover for the decorator under the (0-based) cursor, or null.</summary>
    public Hover? Hover(string text, int line, int character)
    {
        var lineText = GetLine(text, line);
        if (lineText is null) return null;

        foreach (Match m in DecoratorRx.Matches(lineText))
        {
            if (character < m.Index || character > m.Index + m.Length) continue;

            string name = m.Groups[1].Value;
            string? arg = m.Groups[2].Success ? m.Groups[2].Value : null;

            // @DotNetType("X") → show the resolved CLR type + its XML doc.
            if (name == "DotNetType" && arg is not null)
            {
                var type = _resolve(DotNetTypeRegistry.ToClrTypeName(arg));
                if (type is not null)
                    return Markdown(TypeMarkdown(type));
            }

            var builtin = Array.Find(Builtins, b => b.Name == name);
            if (builtin is not null)
                return Markdown($"**@{name}**\n\n{builtin.Description}");

            return null;
        }
        return null;
    }

    /// <summary>Completion list when the cursor follows an <c>@</c>, or null.</summary>
    public CompletionList? Completion(string text, int line, int character)
    {
        var lineText = GetLine(text, line);
        if (lineText is null) return null;
        if (character > lineText.Length) character = lineText.Length;

        var m = DecoratorPrefixRx.Match(lineText[..character]);
        if (!m.Success) return null;

        string prefix = m.Groups[1].Value;
        var items = Builtins
            .Where(b => prefix.Length == 0 || b.Name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            .Select(b => new CompletionItem
            {
                Label = b.Name,
                Kind = CompletionItemKind.Snippet,
                Detail = "SharpTS decorator",
                Documentation = new StringOrMarkupContent(b.Description),
                InsertText = b.Snippet,
                InsertTextFormat = InsertTextFormat.Snippet
            });

        return new CompletionList(items);
    }

    private string TypeMarkdown(Type type)
    {
        string md = $"```csharp\n{type.FullName}\n```";
        var summary = _xmlDoc.GetTypeSummary(type);
        if (!string.IsNullOrWhiteSpace(summary))
            md += $"\n\n{summary}";
        return md;
    }

    private static Hover Markdown(string value) => new()
    {
        Contents = new MarkedStringsOrMarkupContent(
            new MarkupContent { Kind = MarkupKind.Markdown, Value = value })
    };

    private static string? GetLine(string text, int line)
    {
        if (line < 0) return null;
        var lines = text.Split('\n');
        if (line >= lines.Length) return null;
        return lines[line].TrimEnd('\r');
    }
}
