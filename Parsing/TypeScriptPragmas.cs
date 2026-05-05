namespace SharpTS.Parsing;

/// <summary>
/// TypeScript pragma directives recovered from `//`-style comments by the lexer.
/// Mirrors tsc's documented directive set.
/// </summary>
/// <param name="HasTsCheck">A `// @ts-check` comment appeared at the top of the file (before any code token).</param>
/// <param name="HasTsNoCheck">A `// @ts-nocheck` comment appeared at the top of the file.</param>
/// <param name="IgnoreLines">1-based line numbers where `// @ts-ignore` appeared. Type errors on the next non-comment line are suppressed.</param>
/// <param name="ExpectErrorLines">1-based line numbers where `// @ts-expect-error` appeared. The next non-comment line is *required* to produce a type error; absence becomes a diagnostic of its own.</param>
public sealed record TypeScriptPragmas(
    bool HasTsCheck,
    bool HasTsNoCheck,
    IReadOnlySet<int> IgnoreLines,
    IReadOnlySet<int> ExpectErrorLines)
{
    public static TypeScriptPragmas Empty { get; } = new(false, false, new HashSet<int>(), new HashSet<int>());
}
