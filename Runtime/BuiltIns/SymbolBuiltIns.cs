using SharpTS.Runtime.Types;

namespace SharpTS.Runtime.BuiltIns;

/// <summary>
/// Provides implementations for JavaScript Symbol static members.
/// </summary>
/// <remarks>
/// Contains well-known symbols (iterator, asyncIterator, toStringTag, etc.)
/// that back the <c>Symbol.x</c> syntax in TypeScript.
/// Called by <see cref="Execution.Interpreter"/> when resolving property access on Symbol.
/// All members are returned as BuiltInMethod for consistency with the registry pattern.
/// </remarks>
/// <seealso cref="SharpTSSymbol"/>
public static class SymbolBuiltIns
{
    /// <summary>
    /// Gets a static member from the Symbol namespace.
    /// All members are returned as BuiltInMethod for consistency with the registry.
    /// </summary>
    /// <param name="name">The member name (e.g., "iterator", "asyncIterator")</param>
    /// <returns>A BuiltInMethod wrapping the well-known symbol or method, or null if not found</returns>
    public static object? GetStaticMember(string name)
    {
        return name switch
        {
            // Well-known symbols are PROPERTY-style constants (access returns the symbol
            // value), not methods. Use CreateConstant so the IsConstant flag is set and
            // the interpreter's Get fast-path materializes them on access — otherwise
            // `Symbol.iterator` returns the wrapper method instead of the symbol itself
            // and `typeof Symbol.iterator === 'symbol'` fails.
            "iterator" => BuiltInMethod.CreateConstant("iterator", SharpTSSymbol.Iterator),
            "asyncIterator" => BuiltInMethod.CreateConstant("asyncIterator", SharpTSSymbol.AsyncIterator),
            "toStringTag" => BuiltInMethod.CreateConstant("toStringTag", SharpTSSymbol.ToStringTag),
            "hasInstance" => BuiltInMethod.CreateConstant("hasInstance", SharpTSSymbol.HasInstance),
            "isConcatSpreadable" => BuiltInMethod.CreateConstant("isConcatSpreadable", SharpTSSymbol.IsConcatSpreadable),
            "toPrimitive" => BuiltInMethod.CreateConstant("toPrimitive", SharpTSSymbol.ToPrimitive),
            "species" => BuiltInMethod.CreateConstant("species", SharpTSSymbol.Species),
            "unscopables" => BuiltInMethod.CreateConstant("unscopables", SharpTSSymbol.Unscopables),
            "dispose" => BuiltInMethod.CreateConstant("dispose", SharpTSSymbol.Dispose),
            "asyncDispose" => BuiltInMethod.CreateConstant("asyncDispose", SharpTSSymbol.AsyncDispose),

            // Symbol.for() - returns a shared symbol from the global symbol registry
            "for" => new BuiltInMethod("for", 1, (_, _, args) =>
            {
                var key = args.Count > 0 ? args[0]?.ToString() ?? "undefined" : "undefined";
                return SharpTSSymbol.For(key);
            }),

            // Symbol.keyFor() - returns the key for a symbol in the global registry, or undefined
            "keyFor" => new BuiltInMethod("keyFor", 1, (_, _, args) =>
            {
                if (args.Count > 0 && args[0] is SharpTSSymbol sym)
                {
                    var key = SharpTSSymbol.KeyFor(sym);
                    // Return undefined if symbol is not in global registry (matches JS behavior)
                    return key ?? (object?)SharpTSUndefined.Instance;
                }
                return SharpTSUndefined.Instance;
            }),

            _ => null
        };
    }
}
