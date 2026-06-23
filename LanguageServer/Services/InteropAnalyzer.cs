using System.Reflection;
using SharpTS.Diagnostics;
using SharpTS.Parsing;
using SharpTS.Parsing.Visitors;
using SharpTS.Runtime.DotNet;

namespace SharpTS.LanguageServer.Services;

/// <summary>
/// Static analyzer for <c>@DotNetType</c> interop bindings. Reproduces, at type-check
/// time, the .NET-resolution errors that today only surface at interpret time
/// (<c>Execution/Interpreter.DotNet.cs</c>) and compile time
/// (<c>Compilation/ILEmitter.Calls.ExternalInterop.cs</c>) — the diagnostics tsserver
/// structurally cannot produce, which are the unique value of the SharpTS language server.
///
/// Diagnostics carry no <c>TsCode</c> (SharpTS-only), so they are PUBLISHED under the
/// "complement tsserver" model rather than suppressed.
///
/// Coverage (reflection-only — no type-checker required):
///   Tier 1  — @DotNetType target type not found.
///   Tier 2  — declared member not found on the CLR type.
///   Tier 3a — @DotNetOverload hint is malformed / matches no overload.
///   Tier 3b — member exists but with the opposite static-ness (precise message).
///   Tier 3c — binding declares a constructor but the CLR type has no public one.
///   Tier 3d — addEventListener/removeEventListener arity + unknown event name.
/// (Argument-type / overload resolution at call sites is Tier 3e — needs the type
///  checker's inferred argument types and is intentionally out of scope here.)
///
/// RESOLUTION SEAM: <see cref="DotNetTypeRegistry.Resolve"/> by default (in-process,
/// mirrors the interpreter); the production server injects
/// <c>AssemblyReferenceLoader.TryResolve</c> to validate against the project's referenced
/// assemblies. Member/overload/event lookups reuse the runtime's own resolvers
/// (<see cref="DotNetTypeRegistry"/>, <see cref="DotNetMethodResolver"/>) so verdicts
/// match runtime behavior exactly — no reimplemented semantics, no divergence.
/// </summary>
public sealed class InteropAnalyzer
{
    private readonly Func<string, Type?> _resolve;

    public InteropAnalyzer(Func<string, Type?>? resolve = null)
        => _resolve = resolve ?? DotNetTypeRegistry.Resolve;

    // DOM-style addEventListener/removeEventListener are event-binder intrinsics
    // (Runtime/DotNet/DotNetEventBinder.cs), not real CLR methods — never flag them as
    // missing members; their *calls* are validated separately (Tier 3d).
    private static readonly HashSet<string> EventIntrinsics =
        new(StringComparer.Ordinal) { "addEventListener", "removeEventListener" };

    public List<Diagnostic> Analyze(IEnumerable<Stmt> statements)
    {
        var diags = new List<Diagnostic>();
        var bindings = new Dictionary<string, Type>(StringComparer.Ordinal);

        var stmtList = statements as IReadOnlyList<Stmt> ?? statements.ToList();

        // Pass 1 — validate each @DotNetType class and record name -> CLR type.
        foreach (var stmt in stmtList)
            if (stmt is Stmt.Class cls)
                AnalyzeClass(cls, diags, bindings);

        // Pass 2 — Tier 3d: validate event-subscription call sites against the bindings.
        var visitor = new EventCallVisitor(bindings, diags);
        foreach (var stmt in stmtList)
            visitor.Visit(stmt);

        return diags;
    }

    private void AnalyzeClass(Stmt.Class cls, List<Diagnostic> diags, Dictionary<string, Type> bindings)
    {
        var (mapping, decoratorLine) = FindDotNetType(cls);
        if (mapping is null) return;

        string clrName = DotNetTypeRegistry.ToClrTypeName(mapping);
        Type? type = _resolve(clrName);
        if (type == null)
        {
            // Tier 1 — mirrors Interpreter.DotNet.cs:31, surfaced statically at edit time.
            diags.Add(Diagnostic.TypeError(
                $"@DotNetType: .NET type '{clrName}' not found in any loaded assembly.",
                SourceLocation.FromLine(decoratorLine)));
            return;
        }

        bindings[cls.Name.Lexeme] = type;

        foreach (var m in cls.Methods)
        {
            string name = m.Name.Lexeme;

            if (name == "constructor") { CheckConstructor(type, m.Name.Line, diags); continue; }
            if (EventIntrinsics.Contains(name)) continue;

            if (!MemberExists(type, name, m.IsStatic))
            {
                AddMissingMember(type, name, m.IsStatic, "method", m.Name.Line, diags);
                continue;
            }

            CheckOverloadHint(m, type, diags); // Tier 3a (only when the method resolves)
        }

        foreach (var f in cls.Fields)
        {
            string name = f.Name.Lexeme;
            if (!MemberExists(type, name, f.IsStatic))
                AddMissingMember(type, name, f.IsStatic, "property/field", f.Name.Line, diags);
        }
    }

    private static bool MemberExists(Type type, string name, bool isStatic)
        => DotNetTypeRegistry.GetMethods(type, name, isStatic).Length > 0
        || DotNetTypeRegistry.GetPropertyOrField(type, name, isStatic) != null;

    /// <summary>Tier 2 + Tier 3b: "not found", upgraded to a static-ness mismatch message
    /// when the member exists with the opposite static-ness.</summary>
    private static void AddMissingMember(Type type, string name, bool isStatic, string kind, int line, List<Diagnostic> diags)
    {
        string pascal = DotNetTypeRegistry.ToPascalCase(name);
        if (MemberExists(type, name, !isStatic))
        {
            string declared = isStatic ? "static" : "instance";
            string actual = isStatic ? "instance" : "static";
            diags.Add(Diagnostic.TypeError(
                $"@DotNetType '{type.FullName}': member '{name}' exists but is {actual}, not {declared} as declared.",
                SourceLocation.FromLine(line)));
        }
        else
        {
            diags.Add(Diagnostic.TypeError(
                $"@DotNetType '{type.FullName}': no {kind} '{name}' (nor PascalCase '{pascal}').",
                SourceLocation.FromLine(line)));
        }
    }

    // Primitive aliases accepted in an @DotNetOverload hint. Mirrors the alias arm of
    // DotNetMethodResolver.ResolveHintType; ideally shared via an internal API rather than
    // duplicated (production follow-up). Anything not here is resolved via the injected
    // resolver, so fully-qualified names ("System.Guid") work in whatever context applies.
    private static readonly HashSet<string> HintAliases = new(StringComparer.OrdinalIgnoreCase)
    {
        "int", "int32", "long", "int64", "short", "int16", "byte", "sbyte",
        "uint", "uint32", "ulong", "uint64", "ushort", "uint16",
        "float", "single", "double", "decimal", "bool", "boolean", "char", "string", "object"
    };

    /// <summary>Tier 3a: validate that every type named in an @DotNetOverload hint actually
    /// resolves. We validate the hint's TYPE NAMES only — not whether they match an overload.
    ///
    /// Why name-only: the runtime's overload-match step (<see cref="DotNetMethodResolver"/>)
    /// compares hint types to candidate parameter types by reference equality via in-process
    /// <c>typeof</c>, which is unsound when candidates come from a MetadataLoadContext (the
    /// production resolver) — cross-context types never compare equal, producing false
    /// positives. Validating names via the injected resolver is context-correct; overload
    /// *matching* belongs to Tier 3e (by type name, not identity).</summary>
    private void CheckOverloadHint(Stmt.Function m, Type type, List<Diagnostic> diags)
    {
        string? hint = ExtractDecoratorArg(m.Decorators, "DotNetOverload");
        if (hint is null) return;
        if (DotNetTypeRegistry.GetMethods(type, m.Name.Lexeme, m.IsStatic).Length == 0) return;

        foreach (var part in hint.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            if (HintAliases.Contains(part) || _resolve(part) != null) continue;
            diags.Add(Diagnostic.TypeError(
                $"@DotNetOverload(\"{hint}\") on '{m.Name.Lexeme}': unknown type '{part}' in hint.",
                SourceLocation.FromLine(m.Name.Line)));
        }
    }

    /// <summary>Tier 3c: a declared constructor needs a public instance constructor on the
    /// CLR type. Skips value types (structs are always constructible; GetConstructors omits
    /// the implicit default ctor).</summary>
    private static void CheckConstructor(Type type, int line, List<Diagnostic> diags)
    {
        if (type.IsValueType) return;
        if (type.GetConstructors(BindingFlags.Public | BindingFlags.Instance).Length == 0)
            diags.Add(Diagnostic.TypeError(
                $"@DotNetType '{type.FullName}': no public constructor (the type cannot be instantiated with 'new').",
                SourceLocation.FromLine(line)));
    }

    private static (string? mapping, int line) FindDotNetType(Stmt.Class cls)
    {
        if (cls.Decorators == null) return (null, 0);
        foreach (var d in cls.Decorators)
            if (d.Expression is Expr.Call { Callee: Expr.Variable { Name.Lexeme: "DotNetType" }, Arguments: [Expr.Literal { Value: string typeName }] })
                return (typeName, d.AtToken.Line);
        return (null, 0);
    }

    private static string? ExtractDecoratorArg(List<Decorator>? decorators, string name)
    {
        if (decorators == null) return null;
        foreach (var d in decorators)
            if (d.Expression is Expr.Call { Callee: Expr.Variable v, Arguments: [Expr.Literal { Value: string arg }] }
                && v.Name.Lexeme == name)
                return arg;
        return null;
    }

    /// <summary>Tier 3d: finds addEventListener/removeEventListener calls whose receiver
    /// resolves (purely structurally) to a known @DotNetType, and checks arity + event name.
    /// Bails to no-diagnostic whenever the receiver type can't be resolved — false negatives,
    /// never false positives.</summary>
    private sealed class EventCallVisitor(IReadOnlyDictionary<string, Type> bindings, List<Diagnostic> diags) : AstVisitorBase
    {
        protected override void VisitCall(Expr.Call call)
        {
            if (call.Callee is Expr.Get { Name.Lexeme: "addEventListener" or "removeEventListener" } g
                && ResolveReceiver(g.Object) is var (type, isStatic) && type is not null)
            {
                string op = g.Name.Lexeme;
                if (call.Arguments.Count < 2)
                {
                    diags.Add(Diagnostic.TypeError(
                        $"'{op}' on '@DotNetType {type.FullName}' requires (eventName, handler) — got {call.Arguments.Count} argument(s).",
                        SourceLocation.FromLine(g.Name.Line)));
                }
                else if (call.Arguments[0] is Expr.Literal { Value: string evName }
                         && DotNetTypeRegistry.GetEvent(type, evName, isStatic) == null)
                {
                    diags.Add(Diagnostic.TypeError(
                        $"Event '{evName}' not found on '@DotNetType {type.FullName}'.",
                        SourceLocation.FromLine(g.Name.Line)));
                }
            }

            base.VisitCall(call); // keep traversing nested expressions
        }

        // Structurally resolve a receiver to (CLR type, isStaticContext). Returns (null,false)
        // when it can't be determined — the caller treats that as "skip".
        private (Type? type, bool isStatic) ResolveReceiver(Expr e)
        {
            switch (e)
            {
                case Expr.Variable v when bindings.TryGetValue(v.Name.Lexeme, out var t):
                    return (t, true); // class name used as a static accessor
                case Expr.New { Callee: Expr.Variable cv } when bindings.TryGetValue(cv.Name.Lexeme, out var nt):
                    return (nt, false); // a freshly constructed instance
                case Expr.Get g:
                    var (rt, rStatic) = ResolveReceiver(g.Object);
                    if (rt is null) return (null, false);
                    return DotNetTypeRegistry.GetPropertyOrField(rt, g.Name.Lexeme, rStatic) switch
                    {
                        PropertyInfo p => (p.PropertyType, false),
                        FieldInfo f => (f.FieldType, false),
                        _ => (null, false)
                    };
                case Expr.Grouping grp: return ResolveReceiver(grp.Expression);
                case Expr.NonNullAssertion nn: return ResolveReceiver(nn.Expression);
                default: return (null, false);
            }
        }
    }
}
