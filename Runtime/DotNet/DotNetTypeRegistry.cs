using System.Collections.Concurrent;
using System.Reflection;

namespace SharpTS.Runtime.DotNet;

/// <summary>
/// Resolves and caches .NET <see cref="Type"/> instances for <c>@DotNetType</c>-annotated
/// TypeScript classes in interpreter mode. Shared across the interpreter process.
/// </summary>
public static class DotNetTypeRegistry
{
    private static readonly ConcurrentDictionary<string, Type> _cache = new(StringComparer.Ordinal);

    /// <summary>
    /// Resolves a fully-qualified .NET type name, searching all currently loaded assemblies.
    /// </summary>
    public static Type? Resolve(string clrTypeName)
    {
        if (_cache.TryGetValue(clrTypeName, out var cached)) return cached;

        var type = Type.GetType(clrTypeName, throwOnError: false);
        if (type == null)
        {
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                type = assembly.GetType(clrTypeName, throwOnError: false);
                if (type != null) break;
            }
        }

        if (type != null)
        {
            _cache[clrTypeName] = type;
        }
        return type;
    }

    /// <summary>
    /// Clears the type cache. Used by tests to ensure isolation.
    /// </summary>
    public static void ClearCache() => _cache.Clear();

    /// <summary>
    /// Converts a friendly generic type name to CLR syntax.
    /// Example: <c>List&lt;&gt;</c> -> <c>System.Collections.Generic.List`1</c>.
    /// Mirrors <c>ILCompiler.ToClrTypeName</c> so both modes accept the same syntax.
    /// </summary>
    public static string ToClrTypeName(string friendlyName)
    {
        int genericStart = friendlyName.IndexOf('<');
        if (genericStart < 0) return friendlyName;

        string baseName = friendlyName[..genericStart];
        string genericPart = friendlyName[genericStart..];
        int paramCount = genericPart.Count(c => c == ',') + 1;
        return $"{baseName}`{paramCount}";
    }

    /// <summary>
    /// Returns all public methods with the given name (case-sensitive or PascalCase equivalent).
    /// </summary>
    public static MethodInfo[] GetMethods(Type type, string jsName, bool isStatic)
    {
        string pascal = ToPascalCase(jsName);
        var flags = BindingFlags.Public | (isStatic ? BindingFlags.Static : BindingFlags.Instance);
        return type.GetMethods(flags)
            .Where(m => m.Name == jsName || m.Name == pascal)
            .ToArray();
    }

    /// <summary>
    /// Returns the first matching property or field for the given JS-facing name.
    /// </summary>
    public static MemberInfo? GetPropertyOrField(Type type, string jsName, bool isStatic)
    {
        string pascal = ToPascalCase(jsName);
        var flags = BindingFlags.Public | (isStatic ? BindingFlags.Static : BindingFlags.Instance);

        var property = type.GetProperty(pascal, flags) ?? type.GetProperty(jsName, flags);
        if (property != null) return property;

        return type.GetField(pascal, flags) ?? type.GetField(jsName, flags);
    }

    /// <summary>
    /// Converts a camelCase name to PascalCase (first character uppercased).
    /// Matches <c>NamingConventions.ToPascalCase</c> semantics but lives here to avoid
    /// a Compilation namespace dependency.
    /// </summary>
    public static string ToPascalCase(string name)
    {
        if (string.IsNullOrEmpty(name) || char.IsUpper(name[0])) return name;
        return char.ToUpperInvariant(name[0]) + name[1..];
    }
}
