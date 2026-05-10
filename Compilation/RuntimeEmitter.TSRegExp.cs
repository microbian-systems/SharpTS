using System.Reflection;
using System.Reflection.Emit;
using System.Text;
using System.Text.RegularExpressions;

namespace SharpTS.Compilation;

/// <summary>
/// Emits the $RegExp class for standalone RegExp support.
/// NOTE: Must stay in sync with SharpTS.Runtime.Types.SharpTSRegExp
/// </summary>
public partial class RuntimeEmitter
{
    // $RegExp fields
    private FieldBuilder _tsRegExpRegexField = null!;
    private FieldBuilder _tsRegExpSourceField = null!;
    private FieldBuilder _tsRegExpFlagsField = null!;
    private FieldBuilder _tsRegExpGlobalField = null!;
    private FieldBuilder _tsRegExpIgnoreCaseField = null!;
    private FieldBuilder _tsRegExpMultilineField = null!;
    private FieldBuilder _tsRegExpLastIndexField = null!;

    // $RegExp internal methods
    private MethodBuilder _tsRegExpMatchAllMethod = null!;
    private MethodBuilder _tsRegExpReplaceMethod = null!;
    private MethodBuilder _tsRegExpSearchMethod = null!;
    private MethodBuilder _tsRegExpSplitMethod = null!;
    private MethodBuilder _tsRegExpHasNamedGroupsMethod = null!;
    private FieldBuilder _tsRegExpCompileCacheField = null!;
    private MethodBuilder _tsRegExpGetCachedRegexMethod = null!;

    private void EmitTSRegExpClass(ModuleBuilder moduleBuilder, EmittedRuntime runtime)
    {
        // Define class: public sealed class $RegExp
        var typeBuilder = moduleBuilder.DefineType(
            "$RegExp",
            TypeAttributes.Public | TypeAttributes.Sealed | TypeAttributes.BeforeFieldInit,
            _types.Object
        );
        runtime.TSRegExpType = typeBuilder;

        // Fields
        _tsRegExpRegexField = typeBuilder.DefineField("_regex", typeof(Regex), FieldAttributes.Assembly);
        _tsRegExpSourceField = typeBuilder.DefineField("_source", _types.String, FieldAttributes.Private);
        _tsRegExpFlagsField = typeBuilder.DefineField("_flags", _types.String, FieldAttributes.Private);
        _tsRegExpGlobalField = typeBuilder.DefineField("_global", _types.Boolean, FieldAttributes.Private);
        _tsRegExpIgnoreCaseField = typeBuilder.DefineField("_ignoreCase", _types.Boolean, FieldAttributes.Private);
        _tsRegExpMultilineField = typeBuilder.DefineField("_multiline", _types.Boolean, FieldAttributes.Private);
        _tsRegExpLastIndexField = typeBuilder.DefineField("_lastIndex", _types.Int32, FieldAttributes.Public);

        // Compile cache: shared static ConcurrentDictionary<string, Regex>
        // keyed by `pattern + "\x00" + (int)options`. Each TS regex literal
        // (`/foo/g`) evaluates to `new $RegExp(pattern, flags)` every time
        // the expression runs — without caching, that's per-iter
        // `new Regex(...)` compilation, the dominant cost in regex-in-loop
        // workloads (~250 MB / 100 K iters of validator-style code at
        // baseline). Compiled `Regex` objects are thread-safe and have no
        // per-instance state we depend on (`LastIndex` lives on
        // `$RegExp`, not `Regex`), so cache sharing is sound.
        var cdType = typeof(System.Collections.Concurrent.ConcurrentDictionary<string, Regex>);
        _tsRegExpCompileCacheField = typeBuilder.DefineField(
            "_compileCache", cdType, FieldAttributes.Private | FieldAttributes.Static);
        EmitTSRegExpCompileCacheCctor(typeBuilder, cdType);
        EmitTSRegExpGetCachedRegex(typeBuilder, cdType);

        // Helper method for normalizing flags
        EmitTSRegExpNormalizeFlags(typeBuilder, runtime);

        // Static helper for detecting named groups (must be emitted before constructors which use it)
        EmitTSRegExpHasNamedGroups(typeBuilder);

        // Constructors (pattern+flags first because pattern-only calls it)
        EmitTSRegExpCtorPatternFlags(typeBuilder, runtime);
        EmitTSRegExpCtorPattern(typeBuilder, runtime);

        // Property getters
        EmitTSRegExpSourceGetter(typeBuilder, runtime);
        EmitTSRegExpFlagsGetter(typeBuilder, runtime);
        EmitTSRegExpGlobalGetter(typeBuilder, runtime);
        EmitTSRegExpIgnoreCaseGetter(typeBuilder, runtime);
        EmitTSRegExpMultilineGetter(typeBuilder, runtime);
        EmitTSRegExpLastIndexGetter(typeBuilder, runtime);
        EmitTSRegExpLastIndexSetter(typeBuilder, runtime);

        // Static helper for named groups (must be emitted before Exec which uses it)
        EmitTSRegExpBuildNamedGroups(typeBuilder, runtime);

        // Instance methods
        EmitTSRegExpTest(typeBuilder, runtime);
        EmitTSRegExpExec(typeBuilder, runtime);
        EmitTSRegExpToStringMethod(typeBuilder, runtime);

        // Internal methods for string operations
        EmitTSRegExpMatchAll(typeBuilder, runtime);
        EmitTSRegExpReplace(typeBuilder, runtime);
        EmitTSRegExpSearch(typeBuilder, runtime);
        EmitTSRegExpSplit(typeBuilder, runtime);

        // RegExp.prototype well-known-symbol-keyed helpers (ECMA-262 §22.2.5).
        // Static `(rx, ...)` shape so they can be wrapped by $TSFunction with
        // the regex bound as `_target`.
        EmitTSRegExpSymMatchHelper(typeBuilder, runtime);
        EmitTSRegExpSymMatchAllHelper(typeBuilder, runtime);
        EmitTSRegExpSymReplaceHelper(typeBuilder, runtime);
        EmitTSRegExpSymSearchHelper(typeBuilder, runtime);
        EmitTSRegExpSymSplitHelper(typeBuilder, runtime);

        // RegExp.prototype accessor-descriptor getters (ECMA-262 §22.2.5.{3-12}).
        // Each spec-aligned accessor lives on RegExp.prototype as a real
        // accessor with a getter that throws TypeError on non-RegExp `this`.
        // Installed into the prototype's PropertyDescriptorStore by
        // RegExpPrototypePopulate so `Object.getOwnPropertyDescriptor(
        // RegExp.prototype, 'source').get` returns the helper $TSFunction.
        EmitTSRegExpProtoAccessors(typeBuilder, runtime);

        typeBuilder.CreateType();
    }

    /// <summary>
    /// Static cctor for <c>$RegExp._compileCache</c>: just initialize to
    /// an empty <c>ConcurrentDictionary&lt;string, Regex&gt;</c>. Lazy
    /// construction would be nicer but adds a null-check per lookup; the
    /// upfront allocation is one-time and tiny.
    /// </summary>
    private void EmitTSRegExpCompileCacheCctor(TypeBuilder typeBuilder,
        Type cdType)
    {
        var cctor = typeBuilder.DefineTypeInitializer();
        var il = cctor.GetILGenerator();
        il.Emit(OpCodes.Newobj, cdType.GetConstructor(Type.EmptyTypes)!);
        il.Emit(OpCodes.Stsfld, _tsRegExpCompileCacheField);
        il.Emit(OpCodes.Ret);
    }

    /// <summary>
    /// Emits the cache-or-compile helper used by the <c>$RegExp</c> ctor.
    /// Replaces the hot-path <c>new Regex(pattern, options)</c> with a
    /// shared lookup. Race semantics: two threads racing on the same
    /// (pattern, options) may both compile, but only one's <c>Regex</c>
    /// wins the <c>TryAdd</c> — the other is dropped after returning.
    /// Correctness preserved (compiled <c>Regex</c> instances are
    /// thread-safe and behaviorally identical for the same source +
    /// options); only a transient duplicate compile in the rare race.
    /// </summary>
    private void EmitTSRegExpGetCachedRegex(TypeBuilder typeBuilder, Type cdType)
    {
        // private static Regex _GetCachedRegex(string pattern, RegexOptions options)
        var method = typeBuilder.DefineMethod(
            "_GetCachedRegex",
            MethodAttributes.Private | MethodAttributes.Static,
            typeof(Regex),
            [_types.String, typeof(RegexOptions)]
        );
        _tsRegExpGetCachedRegexMethod = method;

        var il = method.GetILGenerator();
        var keyLocal = il.DeclareLocal(_types.String);
        var cachedLocal = il.DeclareLocal(typeof(Regex));
        var freshLocal = il.DeclareLocal(typeof(Regex));
        var hitLabel = il.DefineLabel();

        // key = pattern + " " + ((int)options).ToString()
        il.Emit(OpCodes.Ldarg_0);                                        // pattern
        il.Emit(OpCodes.Ldstr, " ");                                // separator
        var optionsLocal = il.DeclareLocal(typeof(int));
        il.Emit(OpCodes.Ldarg_1);                                        // options as int (RegexOptions is enum:int)
        il.Emit(OpCodes.Stloc, optionsLocal);
        il.Emit(OpCodes.Ldloca, optionsLocal);
        il.Emit(OpCodes.Call, typeof(int).GetMethod("ToString", Type.EmptyTypes)!);
        il.Emit(OpCodes.Call, _types.String.GetMethod("Concat", [_types.String, _types.String, _types.String])!);
        il.Emit(OpCodes.Stloc, keyLocal);

        // if (_compileCache.TryGetValue(key, out cached)) return cached
        il.Emit(OpCodes.Ldsfld, _tsRegExpCompileCacheField);
        il.Emit(OpCodes.Ldloc, keyLocal);
        il.Emit(OpCodes.Ldloca, cachedLocal);
        il.Emit(OpCodes.Callvirt, cdType.GetMethod("TryGetValue")!);
        il.Emit(OpCodes.Brtrue, hitLabel);

        // var fresh = new Regex(pattern, options); _compileCache.TryAdd(key, fresh); return fresh
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Newobj, typeof(Regex).GetConstructor([_types.String, typeof(RegexOptions)])!);
        il.Emit(OpCodes.Stloc, freshLocal);
        il.Emit(OpCodes.Ldsfld, _tsRegExpCompileCacheField);
        il.Emit(OpCodes.Ldloc, keyLocal);
        il.Emit(OpCodes.Ldloc, freshLocal);
        il.Emit(OpCodes.Callvirt, cdType.GetMethod("TryAdd")!);
        il.Emit(OpCodes.Pop);
        il.Emit(OpCodes.Ldloc, freshLocal);
        il.Emit(OpCodes.Ret);

        il.MarkLabel(hitLabel);
        il.Emit(OpCodes.Ldloc, cachedLocal);
        il.Emit(OpCodes.Ret);
    }

    private void EmitTSRegExpNormalizeFlags(TypeBuilder typeBuilder, EmittedRuntime runtime)
    {
        // private static string NormalizeFlags(string flags)
        var method = typeBuilder.DefineMethod(
            "NormalizeFlags",
            MethodAttributes.Private | MethodAttributes.Static,
            _types.String,
            [_types.String]
        );
        runtime.TSRegExpNormalizeFlags = method;

        var il = method.GetILGenerator();
        var sbLocal = il.DeclareLocal(typeof(StringBuilder));

        // var sb = new StringBuilder()
        il.Emit(OpCodes.Newobj, _types.StringBuilderDefaultCtor);
        il.Emit(OpCodes.Stloc, sbLocal);

        // Build the canonical flag-string in ECMA-262 §22.2.5.3 order:
        // hasIndices (d), global (g), ignoreCase (i), multiline (m), dotAll
        // (s), unicode (u), unicodeSets (v), sticky (y). All eight flags
        // must round-trip through the stored slot so the JS-visible
        // accessors (parsed in $Runtime.GetProperty's $RegExp arm) report
        // correctly. The actual matcher only uses g/i/m/s currently; y
        // (sticky) is honored by Exec via Sticky parsed from this string;
        // u/v don't change the matching engine but must round-trip for
        // tests that inspect flags.
        EmitAppendFlagIfContains(il, sbLocal, 'd');
        EmitAppendFlagIfContains(il, sbLocal, 'g');
        EmitAppendFlagIfContains(il, sbLocal, 'i');
        EmitAppendFlagIfContains(il, sbLocal, 'm');
        EmitAppendFlagIfContains(il, sbLocal, 's');
        EmitAppendFlagIfContains(il, sbLocal, 'u');
        EmitAppendFlagIfContains(il, sbLocal, 'v');
        EmitAppendFlagIfContains(il, sbLocal, 'y');

        // return sb.ToString()
        il.Emit(OpCodes.Ldloc, sbLocal);
        il.Emit(OpCodes.Callvirt, _types.StringBuilderToString);
        il.Emit(OpCodes.Ret);
    }

    private void EmitAppendFlagIfContains(ILGenerator il, LocalBuilder sbLocal, char flag)
    {
        var skipLabel = il.DefineLabel();

        // if (flags.Contains('x'))
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldc_I4, (int)flag);
        il.Emit(OpCodes.Call, _types.String.GetMethod("Contains", [_types.Char])!);
        il.Emit(OpCodes.Brfalse, skipLabel);

        // sb.Append('x')
        il.Emit(OpCodes.Ldloc, sbLocal);
        il.Emit(OpCodes.Ldc_I4, (int)flag);
        il.Emit(OpCodes.Callvirt, _types.StringBuilderAppendChar);
        il.Emit(OpCodes.Pop);

        il.MarkLabel(skipLabel);
    }

    /// <summary>
    /// Emits a static method: bool HasNamedGroups(string pattern)
    /// Detects (?&lt;name&gt;...) patterns, excluding lookbehind (?&lt;= and (?&lt;!.
    /// </summary>
    private void EmitTSRegExpHasNamedGroups(TypeBuilder typeBuilder)
    {
        var method = typeBuilder.DefineMethod(
            "HasNamedGroups",
            MethodAttributes.Public | MethodAttributes.Static,
            _types.Boolean,
            [_types.String]
        );
        _tsRegExpHasNamedGroupsMethod = method;

        var il = method.GetILGenerator();
        var iLocal = il.DeclareLocal(_types.Int32);

        var loopCondLabel = il.DefineLabel();
        var loopBodyLabel = il.DefineLabel();
        var incrementLabel = il.DefineLabel();
        var returnTrueLabel = il.DefineLabel();

        // i = 0
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Stloc, iLocal);
        il.Emit(OpCodes.Br, loopCondLabel);

        // Loop body
        il.MarkLabel(loopBodyLabel);

        // if (pattern[i] == '\\') { i += 2; continue; }
        var notEscapeLabel = il.DefineLabel();
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldloc, iLocal);
        il.Emit(OpCodes.Callvirt, _types.String.GetMethod("get_Chars", [_types.Int32])!);
        il.Emit(OpCodes.Ldc_I4, (int)'\\');
        il.Emit(OpCodes.Bne_Un, notEscapeLabel);
        // i += 2
        il.Emit(OpCodes.Ldloc, iLocal);
        il.Emit(OpCodes.Ldc_I4_2);
        il.Emit(OpCodes.Add);
        il.Emit(OpCodes.Stloc, iLocal);
        il.Emit(OpCodes.Br, loopCondLabel);

        il.MarkLabel(notEscapeLabel);

        // Check: pattern[i] == '('
        var notOpenParenLabel = il.DefineLabel();
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldloc, iLocal);
        il.Emit(OpCodes.Callvirt, _types.String.GetMethod("get_Chars", [_types.Int32])!);
        il.Emit(OpCodes.Ldc_I4, (int)'(');
        il.Emit(OpCodes.Bne_Un, notOpenParenLabel);

        // Check: i + 2 < pattern.Length
        var notMatchLabel = il.DefineLabel();
        il.Emit(OpCodes.Ldloc, iLocal);
        il.Emit(OpCodes.Ldc_I4_2);
        il.Emit(OpCodes.Add);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Callvirt, _types.String.GetProperty("Length")!.GetGetMethod()!);
        il.Emit(OpCodes.Bge, notMatchLabel);

        // Check: pattern[i+1] == '?'
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldloc, iLocal);
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Add);
        il.Emit(OpCodes.Callvirt, _types.String.GetMethod("get_Chars", [_types.Int32])!);
        il.Emit(OpCodes.Ldc_I4, (int)'?');
        il.Emit(OpCodes.Bne_Un, notMatchLabel);

        // Check: pattern[i+2] == '<'
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldloc, iLocal);
        il.Emit(OpCodes.Ldc_I4_2);
        il.Emit(OpCodes.Add);
        il.Emit(OpCodes.Callvirt, _types.String.GetMethod("get_Chars", [_types.Int32])!);
        il.Emit(OpCodes.Ldc_I4, (int)'<');
        il.Emit(OpCodes.Bne_Un, notMatchLabel);

        // We found (?< — check if it's lookbehind: (?<= or (?<!
        // Check: i + 3 < pattern.Length
        var notLookbehindLabel = il.DefineLabel();
        il.Emit(OpCodes.Ldloc, iLocal);
        il.Emit(OpCodes.Ldc_I4_3);
        il.Emit(OpCodes.Add);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Callvirt, _types.String.GetProperty("Length")!.GetGetMethod()!);
        il.Emit(OpCodes.Bge, returnTrueLabel); // i+3 >= Length means no char after <, so it IS a named group

        // Check: pattern[i+3] == '=' || pattern[i+3] == '!'
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldloc, iLocal);
        il.Emit(OpCodes.Ldc_I4_3);
        il.Emit(OpCodes.Add);
        il.Emit(OpCodes.Callvirt, _types.String.GetMethod("get_Chars", [_types.Int32])!);
        var charAfterLessThan = il.DeclareLocal(_types.Char);
        il.Emit(OpCodes.Stloc, charAfterLessThan);

        il.Emit(OpCodes.Ldloc, charAfterLessThan);
        il.Emit(OpCodes.Ldc_I4, (int)'=');
        il.Emit(OpCodes.Beq, notLookbehindLabel);
        il.Emit(OpCodes.Ldloc, charAfterLessThan);
        il.Emit(OpCodes.Ldc_I4, (int)'!');
        il.Emit(OpCodes.Beq, notLookbehindLabel);

        // Not lookbehind — it's a named group
        il.Emit(OpCodes.Br, returnTrueLabel);

        // It IS lookbehind — i += 4; continue
        il.MarkLabel(notLookbehindLabel);
        il.Emit(OpCodes.Ldloc, iLocal);
        il.Emit(OpCodes.Ldc_I4_4);
        il.Emit(OpCodes.Add);
        il.Emit(OpCodes.Stloc, iLocal);
        il.Emit(OpCodes.Br, loopCondLabel);

        il.MarkLabel(notMatchLabel);
        il.MarkLabel(notOpenParenLabel);

        // i++
        il.MarkLabel(incrementLabel);
        il.Emit(OpCodes.Ldloc, iLocal);
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Add);
        il.Emit(OpCodes.Stloc, iLocal);

        // Loop condition: while (i < pattern.Length - 2)
        il.MarkLabel(loopCondLabel);
        il.Emit(OpCodes.Ldloc, iLocal);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Callvirt, _types.String.GetProperty("Length")!.GetGetMethod()!);
        il.Emit(OpCodes.Ldc_I4_2);
        il.Emit(OpCodes.Sub);
        il.Emit(OpCodes.Blt, loopBodyLabel);

        // return false
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Ret);

        // return true
        il.MarkLabel(returnTrueLabel);
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Ret);
    }

    private void EmitTSRegExpCtorPattern(TypeBuilder typeBuilder, EmittedRuntime runtime)
    {
        // public $RegExp(string pattern) : this(pattern, "") { }
        var ctor = typeBuilder.DefineConstructor(
            MethodAttributes.Public,
            CallingConventions.Standard,
            [_types.String]
        );
        runtime.TSRegExpCtorPattern = ctor;

        var il = ctor.GetILGenerator();

        // Call the two-arg constructor with empty string for flags
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldarg_1);  // pattern
        il.Emit(OpCodes.Ldstr, "");  // flags = ""
        il.Emit(OpCodes.Call, runtime.TSRegExpCtorPatternFlags!);
        il.Emit(OpCodes.Ret);
    }

    private void EmitTSRegExpCtorPatternFlags(TypeBuilder typeBuilder, EmittedRuntime runtime)
    {
        // public $RegExp(string pattern, string flags)
        var ctor = typeBuilder.DefineConstructor(
            MethodAttributes.Public,
            CallingConventions.Standard,
            [_types.String, _types.String]
        );
        runtime.TSRegExpCtorPatternFlags = ctor;

        var il = ctor.GetILGenerator();
        var optionsLocal = il.DeclareLocal(typeof(RegexOptions));
        var tryEndLabel = il.DefineLabel();
        var endLabel = il.DefineLabel();

        // Call base constructor
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Call, _types.Object.GetConstructor(Type.EmptyTypes)!);

        // _source = pattern
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Stfld, _tsRegExpSourceField);

        // _flags = NormalizeFlags(flags)
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldarg_2);
        il.Emit(OpCodes.Call, runtime.TSRegExpNormalizeFlags!);
        il.Emit(OpCodes.Stfld, _tsRegExpFlagsField);

        // _global = _flags.Contains('g')
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldfld, _tsRegExpFlagsField);
        il.Emit(OpCodes.Ldc_I4, (int)'g');
        il.Emit(OpCodes.Call, _types.String.GetMethod("Contains", [_types.Char])!);
        il.Emit(OpCodes.Stfld, _tsRegExpGlobalField);

        // _ignoreCase = _flags.Contains('i')
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldfld, _tsRegExpFlagsField);
        il.Emit(OpCodes.Ldc_I4, (int)'i');
        il.Emit(OpCodes.Call, _types.String.GetMethod("Contains", [_types.Char])!);
        il.Emit(OpCodes.Stfld, _tsRegExpIgnoreCaseField);

        // _multiline = _flags.Contains('m')
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldfld, _tsRegExpFlagsField);
        il.Emit(OpCodes.Ldc_I4, (int)'m');
        il.Emit(OpCodes.Call, _types.String.GetMethod("Contains", [_types.Char])!);
        il.Emit(OpCodes.Stfld, _tsRegExpMultilineField);

        // _lastIndex = 0
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Stfld, _tsRegExpLastIndexField);

        // Named groups require non-ECMAScript mode in .NET
        // if (HasNamedGroups(pattern)) options = None; else options = ECMAScript;
        var noNamedGroupsLabel = il.DefineLabel();
        var afterOptionsLabel = il.DefineLabel();

        il.Emit(OpCodes.Ldarg_1); // pattern
        il.Emit(OpCodes.Call, _tsRegExpHasNamedGroupsMethod);
        il.Emit(OpCodes.Brfalse, noNamedGroupsLabel);

        // Named groups: use RegexOptions.None
        il.Emit(OpCodes.Ldc_I4, (int)RegexOptions.None);
        il.Emit(OpCodes.Stloc, optionsLocal);
        il.Emit(OpCodes.Br, afterOptionsLabel);

        il.MarkLabel(noNamedGroupsLabel);
        // No named groups: use ECMAScript
        il.Emit(OpCodes.Ldc_I4, (int)RegexOptions.ECMAScript);
        il.Emit(OpCodes.Stloc, optionsLocal);

        il.MarkLabel(afterOptionsLabel);

        // if (_ignoreCase) options |= RegexOptions.IgnoreCase
        var skipIgnoreCaseLabel = il.DefineLabel();
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldfld, _tsRegExpIgnoreCaseField);
        il.Emit(OpCodes.Brfalse, skipIgnoreCaseLabel);
        il.Emit(OpCodes.Ldloc, optionsLocal);
        il.Emit(OpCodes.Ldc_I4, (int)RegexOptions.IgnoreCase);
        il.Emit(OpCodes.Or);
        il.Emit(OpCodes.Stloc, optionsLocal);
        il.MarkLabel(skipIgnoreCaseLabel);

        // if (_multiline) options |= RegexOptions.Multiline
        var skipMultilineLabel = il.DefineLabel();
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldfld, _tsRegExpMultilineField);
        il.Emit(OpCodes.Brfalse, skipMultilineLabel);
        il.Emit(OpCodes.Ldloc, optionsLocal);
        il.Emit(OpCodes.Ldc_I4, (int)RegexOptions.Multiline);
        il.Emit(OpCodes.Or);
        il.Emit(OpCodes.Stloc, optionsLocal);
        il.MarkLabel(skipMultilineLabel);

        // if (_flags.Contains('s')) options = (options & ~ECMAScript) | Singleline
        // ECMAScript mode is mutually exclusive with Singleline in .NET, so clear it when 's' is requested.
        var skipSinglelineLabel = il.DefineLabel();
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldfld, _tsRegExpFlagsField);
        il.Emit(OpCodes.Ldc_I4, (int)'s');
        il.Emit(OpCodes.Call, _types.String.GetMethod("Contains", [_types.Char])!);
        il.Emit(OpCodes.Brfalse, skipSinglelineLabel);
        il.Emit(OpCodes.Ldloc, optionsLocal);
        il.Emit(OpCodes.Ldc_I4, ~(int)RegexOptions.ECMAScript);
        il.Emit(OpCodes.And);
        il.Emit(OpCodes.Ldc_I4, (int)RegexOptions.Singleline);
        il.Emit(OpCodes.Or);
        il.Emit(OpCodes.Stloc, optionsLocal);
        il.MarkLabel(skipSinglelineLabel);

        // try { _regex = _GetCachedRegex(pattern, options); }
        // Cache lookup keyed by (pattern, options) — see
        // EmitTSRegExpGetCachedRegex for rationale. Replaces a per-call
        // Regex compilation that dominates regex-in-loop workloads.
        il.BeginExceptionBlock();
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldarg_1);  // pattern
        il.Emit(OpCodes.Ldloc, optionsLocal);
        il.Emit(OpCodes.Call, _tsRegExpGetCachedRegexMethod);
        il.Emit(OpCodes.Stfld, _tsRegExpRegexField);
        il.Emit(OpCodes.Leave, endLabel);

        // catch (ArgumentException ex) { throw new Exception("Invalid regular expression: " + ex.Message); }
        il.BeginCatchBlock(typeof(ArgumentException));
        var exLocal = il.DeclareLocal(typeof(ArgumentException));
        il.Emit(OpCodes.Stloc, exLocal);
        il.Emit(OpCodes.Ldstr, "Invalid regular expression: ");
        il.Emit(OpCodes.Ldloc, exLocal);
        il.Emit(OpCodes.Callvirt, typeof(Exception).GetProperty("Message")!.GetGetMethod()!);
        il.Emit(OpCodes.Call, _types.String.GetMethod("Concat", [_types.String, _types.String])!);
        il.Emit(OpCodes.Newobj, _types.Exception.GetConstructor([_types.String])!);
        il.Emit(OpCodes.Throw);

        il.EndExceptionBlock();

        il.MarkLabel(endLabel);
        il.Emit(OpCodes.Ret);
    }

    private void EmitTSRegExpSourceGetter(TypeBuilder typeBuilder, EmittedRuntime runtime)
    {
        var method = typeBuilder.DefineMethod(
            "get_Source",
            MethodAttributes.Public,
            _types.String,
            Type.EmptyTypes
        );
        runtime.TSRegExpSourceGetter = method;

        var il = method.GetILGenerator();
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldfld, _tsRegExpSourceField);
        il.Emit(OpCodes.Ret);
    }

    private void EmitTSRegExpFlagsGetter(TypeBuilder typeBuilder, EmittedRuntime runtime)
    {
        var method = typeBuilder.DefineMethod(
            "get_Flags",
            MethodAttributes.Public,
            _types.String,
            Type.EmptyTypes
        );
        runtime.TSRegExpFlagsGetter = method;

        var il = method.GetILGenerator();
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldfld, _tsRegExpFlagsField);
        il.Emit(OpCodes.Ret);
    }

    private void EmitTSRegExpGlobalGetter(TypeBuilder typeBuilder, EmittedRuntime runtime)
    {
        var method = typeBuilder.DefineMethod(
            "get_Global",
            MethodAttributes.Public,
            _types.Boolean,
            Type.EmptyTypes
        );
        runtime.TSRegExpGlobalGetter = method;

        var il = method.GetILGenerator();
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldfld, _tsRegExpGlobalField);
        il.Emit(OpCodes.Ret);
    }

    private void EmitTSRegExpIgnoreCaseGetter(TypeBuilder typeBuilder, EmittedRuntime runtime)
    {
        var method = typeBuilder.DefineMethod(
            "get_IgnoreCase",
            MethodAttributes.Public,
            _types.Boolean,
            Type.EmptyTypes
        );
        runtime.TSRegExpIgnoreCaseGetter = method;

        var il = method.GetILGenerator();
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldfld, _tsRegExpIgnoreCaseField);
        il.Emit(OpCodes.Ret);
    }

    private void EmitTSRegExpMultilineGetter(TypeBuilder typeBuilder, EmittedRuntime runtime)
    {
        var method = typeBuilder.DefineMethod(
            "get_Multiline",
            MethodAttributes.Public,
            _types.Boolean,
            Type.EmptyTypes
        );
        runtime.TSRegExpMultilineGetter = method;

        var il = method.GetILGenerator();
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldfld, _tsRegExpMultilineField);
        il.Emit(OpCodes.Ret);
    }

    private void EmitTSRegExpLastIndexGetter(TypeBuilder typeBuilder, EmittedRuntime runtime)
    {
        var method = typeBuilder.DefineMethod(
            "get_LastIndex",
            MethodAttributes.Public,
            _types.Int32,
            Type.EmptyTypes
        );
        runtime.TSRegExpLastIndexGetter = method;

        var il = method.GetILGenerator();
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldfld, _tsRegExpLastIndexField);
        il.Emit(OpCodes.Ret);
    }

    private void EmitTSRegExpLastIndexSetter(TypeBuilder typeBuilder, EmittedRuntime runtime)
    {
        var method = typeBuilder.DefineMethod(
            "set_LastIndex",
            MethodAttributes.Public,
            _types.Void,
            [_types.Int32]
        );
        runtime.TSRegExpLastIndexSetter = method;

        var il = method.GetILGenerator();
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Stfld, _tsRegExpLastIndexField);
        il.Emit(OpCodes.Ret);
    }

    private void EmitTSRegExpTest(TypeBuilder typeBuilder, EmittedRuntime runtime)
    {
        // public bool Test(string input)
        var method = typeBuilder.DefineMethod(
            "Test",
            MethodAttributes.Public,
            _types.Boolean,
            [_types.String]
        );
        runtime.TSRegExpTestMethod = method;

        var il = method.GetILGenerator();
        var globalLabel = il.DefineLabel();
        var matchFoundLabel = il.DefineLabel();
        var notGlobalLabel = il.DefineLabel();
        var returnFalseLabel = il.DefineLabel();
        var matchLocal = il.DeclareLocal(typeof(Match));
        var startIndexLocal = il.DeclareLocal(_types.Int32);
        var inputLocal = il.DeclareLocal(_types.String);

        // input = input ?? "undefined" (JS: regex.test(undefined) tests against "undefined")
        il.Emit(OpCodes.Ldarg_1);
        var inputNotNullLabel = il.DefineLabel();
        il.Emit(OpCodes.Brtrue, inputNotNullLabel);
        il.Emit(OpCodes.Ldstr, "undefined");
        il.Emit(OpCodes.Stloc, inputLocal);
        var inputAssignedLabel = il.DefineLabel();
        il.Emit(OpCodes.Br, inputAssignedLabel);
        il.MarkLabel(inputNotNullLabel);
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Stloc, inputLocal);
        il.MarkLabel(inputAssignedLabel);

        // if (_global) goto globalLabel
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldfld, _tsRegExpGlobalField);
        il.Emit(OpCodes.Brtrue, globalLabel);

        // Non-global: return _regex.IsMatch(input)
        il.MarkLabel(notGlobalLabel);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldfld, _tsRegExpRegexField);
        il.Emit(OpCodes.Ldloc, inputLocal);
        il.Emit(OpCodes.Callvirt, typeof(Regex).GetMethod("IsMatch", [_types.String])!);
        il.Emit(OpCodes.Ret);

        // Global path
        il.MarkLabel(globalLabel);

        // if (LastIndex > input.Length) { LastIndex = 0; return false; }
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldfld, _tsRegExpLastIndexField);
        il.Emit(OpCodes.Ldloc, inputLocal);
        il.Emit(OpCodes.Callvirt, _types.String.GetProperty("Length")!.GetGetMethod()!);
        il.Emit(OpCodes.Ble, returnFalseLabel);

        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Stfld, _tsRegExpLastIndexField);
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Ret);

        il.MarkLabel(returnFalseLabel);

        // var startIndex = Math.Min(LastIndex, input.Length)
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldfld, _tsRegExpLastIndexField);
        il.Emit(OpCodes.Ldloc, inputLocal);
        il.Emit(OpCodes.Callvirt, _types.String.GetProperty("Length")!.GetGetMethod()!);
        il.Emit(OpCodes.Call, typeof(Math).GetMethod("Min", [_types.Int32, _types.Int32])!);
        il.Emit(OpCodes.Stloc, startIndexLocal);

        // var match = _regex.Match(input, startIndex)
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldfld, _tsRegExpRegexField);
        il.Emit(OpCodes.Ldloc, inputLocal);
        il.Emit(OpCodes.Ldloc, startIndexLocal);
        il.Emit(OpCodes.Callvirt, _types.RegexMatchStringInt);
        il.Emit(OpCodes.Stloc, matchLocal);

        // if (match.Success)
        il.Emit(OpCodes.Ldloc, matchLocal);
        il.Emit(OpCodes.Callvirt, typeof(Match).GetProperty("Success")!.GetGetMethod()!);
        il.Emit(OpCodes.Brtrue, matchFoundLabel);

        // No match: LastIndex = 0; return false
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Stfld, _tsRegExpLastIndexField);
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Ret);

        // Match found: LastIndex = match.Index + match.Length; return true
        il.MarkLabel(matchFoundLabel);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldloc, matchLocal);
        il.Emit(OpCodes.Callvirt, typeof(Capture).GetProperty("Index")!.GetGetMethod()!);
        il.Emit(OpCodes.Ldloc, matchLocal);
        il.Emit(OpCodes.Callvirt, typeof(Capture).GetProperty("Length")!.GetGetMethod()!);
        il.Emit(OpCodes.Add);
        il.Emit(OpCodes.Stfld, _tsRegExpLastIndexField);
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Ret);
    }

    private void EmitTSRegExpExec(TypeBuilder typeBuilder, EmittedRuntime runtime)
    {
        // public object? Exec(string input) - returns $Object or null
        var method = typeBuilder.DefineMethod(
            "Exec",
            MethodAttributes.Public,
            _types.Object,
            [_types.String]
        );
        runtime.TSRegExpExecMethod = method;

        var il = method.GetILGenerator();
        var globalMatchLabel = il.DefineLabel();
        var nonGlobalMatchLabel = il.DefineLabel();
        var checkSuccessLabel = il.DefineLabel();
        var matchSuccessLabel = il.DefineLabel();
        var noMatchLabel = il.DefineLabel();
        var loopStartLabel = il.DefineLabel();
        var loopEndLabel = il.DefineLabel();

        var matchLocal = il.DeclareLocal(typeof(Match));
        var startIndexLocal = il.DeclareLocal(_types.Int32);
        var fieldsLocal = il.DeclareLocal(_types.DictionaryStringObject);
        var iLocal = il.DeclareLocal(_types.Int32);
        var groupLocal = il.DeclareLocal(typeof(Group));

        // if (_global)
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldfld, _tsRegExpGlobalField);
        il.Emit(OpCodes.Brtrue, globalMatchLabel);

        // Non-global: match = _regex.Match(input)
        il.MarkLabel(nonGlobalMatchLabel);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldfld, _tsRegExpRegexField);
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Callvirt, _types.RegexMatchString);
        il.Emit(OpCodes.Stloc, matchLocal);
        il.Emit(OpCodes.Br, checkSuccessLabel);

        // Global path
        il.MarkLabel(globalMatchLabel);

        // if (LastIndex > input.Length) { LastIndex = 0; return null; }
        var continueGlobalLabel = il.DefineLabel();
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldfld, _tsRegExpLastIndexField);
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Callvirt, _types.String.GetProperty("Length")!.GetGetMethod()!);
        il.Emit(OpCodes.Ble, continueGlobalLabel);

        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Stfld, _tsRegExpLastIndexField);
        il.Emit(OpCodes.Ldnull);
        il.Emit(OpCodes.Ret);

        il.MarkLabel(continueGlobalLabel);

        // startIndex = Math.Min(LastIndex, input.Length)
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldfld, _tsRegExpLastIndexField);
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Callvirt, _types.String.GetProperty("Length")!.GetGetMethod()!);
        il.Emit(OpCodes.Call, typeof(Math).GetMethod("Min", [_types.Int32, _types.Int32])!);
        il.Emit(OpCodes.Stloc, startIndexLocal);

        // match = _regex.Match(input, startIndex)
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldfld, _tsRegExpRegexField);
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Ldloc, startIndexLocal);
        il.Emit(OpCodes.Callvirt, _types.RegexMatchStringInt);
        il.Emit(OpCodes.Stloc, matchLocal);

        // Check if match was successful
        il.MarkLabel(checkSuccessLabel);
        il.Emit(OpCodes.Ldloc, matchLocal);
        il.Emit(OpCodes.Callvirt, typeof(Match).GetProperty("Success")!.GetGetMethod()!);
        il.Emit(OpCodes.Brtrue, matchSuccessLabel);

        // No match
        il.MarkLabel(noMatchLabel);

        // if (_global) LastIndex = 0
        var skipResetLabel = il.DefineLabel();
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldfld, _tsRegExpGlobalField);
        il.Emit(OpCodes.Brfalse, skipResetLabel);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Stfld, _tsRegExpLastIndexField);
        il.MarkLabel(skipResetLabel);
        il.Emit(OpCodes.Ldnull);
        il.Emit(OpCodes.Ret);

        // Match success
        il.MarkLabel(matchSuccessLabel);

        // if (_global) LastIndex = match.Index + match.Length
        var skipUpdateLabel = il.DefineLabel();
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldfld, _tsRegExpGlobalField);
        il.Emit(OpCodes.Brfalse, skipUpdateLabel);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldloc, matchLocal);
        il.Emit(OpCodes.Callvirt, typeof(Capture).GetProperty("Index")!.GetGetMethod()!);
        il.Emit(OpCodes.Ldloc, matchLocal);
        il.Emit(OpCodes.Callvirt, typeof(Capture).GetProperty("Length")!.GetGetMethod()!);
        il.Emit(OpCodes.Add);
        il.Emit(OpCodes.Stfld, _tsRegExpLastIndexField);
        il.MarkLabel(skipUpdateLabel);

        // ECMA-262 22.2.5.6.6: the exec result is an Array exotic object —
        // tests assert `__executed instanceof Array === true` and rely on
        // `length` derived from the array's element count. Build a real
        // List<object?> (the compile-mode array shape; passes
        // `instanceof Array`), populate it with the match + capture-group
        // values, then attach `index` / `input` / `groups` via the
        // PropertyDescriptorStore so `result.index` etc. still resolve.
        var resultLocal = il.DeclareLocal(_types.ListOfObject);
        var listAdd = _types.ListOfObject.GetMethod("Add", [_types.Object])!;

        // var result = new List<object?>(match.Groups.Count);
        il.Emit(OpCodes.Ldloc, matchLocal);
        il.Emit(OpCodes.Callvirt, typeof(Match).GetProperty("Groups")!.GetGetMethod()!);
        il.Emit(OpCodes.Callvirt, typeof(GroupCollection).GetProperty("Count")!.GetGetMethod()!);
        il.Emit(OpCodes.Newobj, _types.GetConstructor(_types.ListOfObject, _types.Int32));
        il.Emit(OpCodes.Stloc, resultLocal);

        // result.Add(match.Value)  // index 0 = whole match
        il.Emit(OpCodes.Ldloc, resultLocal);
        il.Emit(OpCodes.Ldloc, matchLocal);
        il.Emit(OpCodes.Callvirt, typeof(Capture).GetProperty("Value")!.GetGetMethod()!);
        il.Emit(OpCodes.Callvirt, listAdd);

        // for (int i = 1; i < match.Groups.Count; i++) { ... result.Add(...) }
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Stloc, iLocal);

        il.MarkLabel(loopStartLabel);
        il.Emit(OpCodes.Ldloc, iLocal);
        il.Emit(OpCodes.Ldloc, matchLocal);
        il.Emit(OpCodes.Callvirt, typeof(Match).GetProperty("Groups")!.GetGetMethod()!);
        il.Emit(OpCodes.Callvirt, typeof(GroupCollection).GetProperty("Count")!.GetGetMethod()!);
        il.Emit(OpCodes.Bge, loopEndLabel);

        // var group = match.Groups[i]
        il.Emit(OpCodes.Ldloc, matchLocal);
        il.Emit(OpCodes.Callvirt, typeof(Match).GetProperty("Groups")!.GetGetMethod()!);
        il.Emit(OpCodes.Ldloc, iLocal);
        il.Emit(OpCodes.Callvirt, typeof(GroupCollection).GetMethod("get_Item", [_types.Int32])!);
        il.Emit(OpCodes.Stloc, groupLocal);

        // result.Add(group.Success ? group.Value : undefined)
        var groupSuccessLabel = il.DefineLabel();
        var addGroupEndLabel = il.DefineLabel();

        il.Emit(OpCodes.Ldloc, resultLocal);
        il.Emit(OpCodes.Ldloc, groupLocal);
        il.Emit(OpCodes.Callvirt, typeof(Group).GetProperty("Success")!.GetGetMethod()!);
        il.Emit(OpCodes.Brtrue, groupSuccessLabel);
        // ECMA-262 22.2.5.2.6: unmatched optional capture groups produce
        // `undefined`, not null.
        il.Emit(OpCodes.Ldsfld, runtime.UndefinedInstance);
        il.Emit(OpCodes.Br, addGroupEndLabel);
        il.MarkLabel(groupSuccessLabel);
        il.Emit(OpCodes.Ldloc, groupLocal);
        il.Emit(OpCodes.Callvirt, typeof(Capture).GetProperty("Value")!.GetGetMethod()!);
        il.MarkLabel(addGroupEndLabel);
        il.Emit(OpCodes.Callvirt, listAdd);

        // i++
        il.Emit(OpCodes.Ldloc, iLocal);
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Add);
        il.Emit(OpCodes.Stloc, iLocal);
        il.Emit(OpCodes.Br, loopStartLabel);

        il.MarkLabel(loopEndLabel);

        // Attach index/input/groups via PropertyDescriptorStore. Helper closure:
        // descriptor = new $CompiledPropertyDescriptor { Value = <stack top> };
        // PropertyDescriptorStore.DefineProperty(result, name, descriptor);
        var pdsDescLocal = il.DeclareLocal(runtime.CompiledPropertyDescriptorType);
        void DefineMetadataProperty(string name, Action emitValue)
        {
            il.Emit(OpCodes.Newobj, runtime.CompiledPropertyDescriptorCtor);
            il.Emit(OpCodes.Stloc, pdsDescLocal);
            // descriptor.Value = <value>
            il.Emit(OpCodes.Ldloc, pdsDescLocal);
            emitValue();
            il.Emit(OpCodes.Callvirt, runtime.CompiledPropertyDescriptorValue.GetSetMethod()!);
            // PropertyDescriptorStore.DefineProperty(result, name, descriptor)
            il.Emit(OpCodes.Ldloc, resultLocal);
            il.Emit(OpCodes.Ldstr, name);
            il.Emit(OpCodes.Ldloc, pdsDescLocal);
            il.Emit(OpCodes.Call, runtime.PDSDefineProperty);
            il.Emit(OpCodes.Pop); // discard bool return
        }

        DefineMetadataProperty("index", () =>
        {
            il.Emit(OpCodes.Ldloc, matchLocal);
            il.Emit(OpCodes.Callvirt, typeof(Capture).GetProperty("Index")!.GetGetMethod()!);
            il.Emit(OpCodes.Conv_R8);
            il.Emit(OpCodes.Box, _types.Double);
        });
        DefineMetadataProperty("input", () => il.Emit(OpCodes.Ldarg_1));
        DefineMetadataProperty("groups", () =>
        {
            il.Emit(OpCodes.Ldloc, matchLocal);
            il.Emit(OpCodes.Call, runtime.BuildNamedGroups!);
        });

        // return result (List<object?>)
        il.Emit(OpCodes.Ldloc, resultLocal);
        il.Emit(OpCodes.Ret);
    }

    private void EmitTSRegExpToStringMethod(TypeBuilder typeBuilder, EmittedRuntime runtime)
    {
        // public override string ToString() => $"/{_source}/{_flags}"
        var method = typeBuilder.DefineMethod(
            "ToString",
            MethodAttributes.Public | MethodAttributes.Virtual | MethodAttributes.HideBySig,
            _types.String,
            Type.EmptyTypes
        );
        runtime.TSRegExpToStringMethod = method;

        var il = method.GetILGenerator();

        // "/" + _source + "/" + _flags
        il.Emit(OpCodes.Ldstr, "/");
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldfld, _tsRegExpSourceField);
        il.Emit(OpCodes.Ldstr, "/");
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldfld, _tsRegExpFlagsField);
        il.Emit(OpCodes.Call, _types.String.GetMethod("Concat", [_types.String, _types.String, _types.String, _types.String])!);
        il.Emit(OpCodes.Ret);
    }

    private void EmitTSRegExpMatchAll(TypeBuilder typeBuilder, EmittedRuntime runtime)
    {
        // internal List<string> MatchAll(string input)
        var method = typeBuilder.DefineMethod(
            "MatchAll",
            MethodAttributes.Assembly,
            typeof(List<string>),
            [_types.String]
        );
        _tsRegExpMatchAllMethod = method;

        var il = method.GetILGenerator();
        var resultLocal = il.DeclareLocal(typeof(List<string>));
        var matchesLocal = il.DeclareLocal(typeof(MatchCollection));
        var enumeratorLocal = il.DeclareLocal(typeof(System.Collections.IEnumerator));
        var loopStartLabel = il.DefineLabel();
        var loopEndLabel = il.DefineLabel();
        var finallyLabel = il.DefineLabel();

        // var result = new List<string>()
        il.Emit(OpCodes.Newobj, typeof(List<string>).GetConstructor(Type.EmptyTypes)!);
        il.Emit(OpCodes.Stloc, resultLocal);

        // var matches = _regex.Matches(input)
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldfld, _tsRegExpRegexField);
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Callvirt, typeof(Regex).GetMethod("Matches", [_types.String])!);
        il.Emit(OpCodes.Stloc, matchesLocal);

        // foreach (Match m in matches) { result.Add(m.Value); }
        il.Emit(OpCodes.Ldloc, matchesLocal);
        il.Emit(OpCodes.Callvirt, typeof(MatchCollection).GetMethod("GetEnumerator")!);
        il.Emit(OpCodes.Stloc, enumeratorLocal);

        il.BeginExceptionBlock();

        il.MarkLabel(loopStartLabel);
        il.Emit(OpCodes.Ldloc, enumeratorLocal);
        il.Emit(OpCodes.Callvirt, typeof(System.Collections.IEnumerator).GetMethod("MoveNext")!);
        il.Emit(OpCodes.Brfalse, loopEndLabel);

        // result.Add(((Match)enumerator.Current).Value)
        il.Emit(OpCodes.Ldloc, resultLocal);
        il.Emit(OpCodes.Ldloc, enumeratorLocal);
        il.Emit(OpCodes.Callvirt, typeof(System.Collections.IEnumerator).GetProperty("Current")!.GetGetMethod()!);
        il.Emit(OpCodes.Castclass, typeof(Match));
        il.Emit(OpCodes.Callvirt, typeof(Capture).GetProperty("Value")!.GetGetMethod()!);
        il.Emit(OpCodes.Callvirt, typeof(List<string>).GetMethod("Add", [_types.String])!);
        il.Emit(OpCodes.Br, loopStartLabel);

        il.MarkLabel(loopEndLabel);
        il.Emit(OpCodes.Leave, finallyLabel);

        // Finally block to dispose if IDisposable
        il.BeginFinallyBlock();
        var disposeEndLabel = il.DefineLabel();
        il.Emit(OpCodes.Ldloc, enumeratorLocal);
        il.Emit(OpCodes.Isinst, typeof(IDisposable));
        il.Emit(OpCodes.Brfalse, disposeEndLabel);
        il.Emit(OpCodes.Ldloc, enumeratorLocal);
        il.Emit(OpCodes.Castclass, typeof(IDisposable));
        il.Emit(OpCodes.Callvirt, _types.DisposableDispose);
        il.MarkLabel(disposeEndLabel);
        il.EndExceptionBlock();

        il.MarkLabel(finallyLabel);

        // return result
        il.Emit(OpCodes.Ldloc, resultLocal);
        il.Emit(OpCodes.Ret);
    }

    private void EmitTSRegExpReplace(TypeBuilder typeBuilder, EmittedRuntime runtime)
    {
        // internal string Replace(string input, string replacement)
        var method = typeBuilder.DefineMethod(
            "Replace",
            MethodAttributes.Assembly,
            _types.String,
            [_types.String, _types.String]
        );
        _tsRegExpReplaceMethod = method;

        var il = method.GetILGenerator();
        var globalLabel = il.DefineLabel();
        var endLabel = il.DefineLabel();

        // if (_global)
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldfld, _tsRegExpGlobalField);
        il.Emit(OpCodes.Brtrue, globalLabel);

        // Non-global: return _regex.Replace(input, replacement, 1)
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldfld, _tsRegExpRegexField);
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Ldarg_2);
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Callvirt, typeof(Regex).GetMethod("Replace", [_types.String, _types.String, _types.Int32])!);
        il.Emit(OpCodes.Ret);

        // Global: return _regex.Replace(input, replacement)
        il.MarkLabel(globalLabel);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldfld, _tsRegExpRegexField);
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Ldarg_2);
        il.Emit(OpCodes.Callvirt, typeof(Regex).GetMethod("Replace", [_types.String, _types.String])!);
        il.Emit(OpCodes.Ret);
    }

    private void EmitTSRegExpSearch(TypeBuilder typeBuilder, EmittedRuntime runtime)
    {
        // internal int Search(string input)
        var method = typeBuilder.DefineMethod(
            "Search",
            MethodAttributes.Assembly,
            _types.Int32,
            [_types.String]
        );
        _tsRegExpSearchMethod = method;

        var il = method.GetILGenerator();
        var matchLocal = il.DeclareLocal(typeof(Match));
        var successLabel = il.DefineLabel();

        // var match = _regex.Match(input)
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldfld, _tsRegExpRegexField);
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Callvirt, _types.RegexMatchString);
        il.Emit(OpCodes.Stloc, matchLocal);

        // return match.Success ? match.Index : -1
        il.Emit(OpCodes.Ldloc, matchLocal);
        il.Emit(OpCodes.Callvirt, typeof(Match).GetProperty("Success")!.GetGetMethod()!);
        il.Emit(OpCodes.Brtrue, successLabel);

        il.Emit(OpCodes.Ldc_I4_M1);
        il.Emit(OpCodes.Ret);

        il.MarkLabel(successLabel);
        il.Emit(OpCodes.Ldloc, matchLocal);
        il.Emit(OpCodes.Callvirt, typeof(Capture).GetProperty("Index")!.GetGetMethod()!);
        il.Emit(OpCodes.Ret);
    }

    private void EmitTSRegExpSplit(TypeBuilder typeBuilder, EmittedRuntime runtime)
    {
        // internal string[] Split(string input)
        var method = typeBuilder.DefineMethod(
            "Split",
            MethodAttributes.Assembly,
            typeof(string[]),
            [_types.String]
        );
        _tsRegExpSplitMethod = method;

        var il = method.GetILGenerator();
        var partsLocal = il.DeclareLocal(typeof(string[]));
        var startLocal = il.DeclareLocal(_types.Int32);
        var endLocal = il.DeclareLocal(_types.Int32);
        var resultLocal = il.DeclareLocal(typeof(string[]));

        // var parts = _regex.Split(input)
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldfld, _tsRegExpRegexField);
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Callvirt, typeof(Regex).GetMethod("Split", [_types.String])!);
        il.Emit(OpCodes.Stloc, partsLocal);

        // ECMA-262 22.1.3.21: empty-match regex (e.g. /(?:)/) should NOT
        // produce empty entries at the split boundaries. .NET's Regex.Split
        // inserts a leading "" before the first match and trailing "" after
        // the last match when both occur at the string boundary, giving
        // "hello".split(/(?:)/) → ["", "h", "e", "l", "l", "o", ""] (7) when
        // the spec wants ["h", "e", "l", "l", "o"] (5). Detection: regex
        // matches the empty string at position 0 of the empty input string
        // (i.e. it can match with zero-width). Trim leading + trailing "".
        var skipTrimLabel = il.DefineLabel();
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldfld, _tsRegExpRegexField);
        il.Emit(OpCodes.Ldstr, "");
        il.Emit(OpCodes.Callvirt, typeof(Regex).GetMethod("IsMatch", [_types.String])!);
        il.Emit(OpCodes.Brfalse, skipTrimLabel);

        // start = 0
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Stloc, startLocal);
        // end = parts.Length
        il.Emit(OpCodes.Ldloc, partsLocal);
        il.Emit(OpCodes.Ldlen);
        il.Emit(OpCodes.Conv_I4);
        il.Emit(OpCodes.Stloc, endLocal);

        // while (start < end && parts[start] == "") start++;
        var startLoopLabel = il.DefineLabel();
        var startLoopEndLabel = il.DefineLabel();
        il.MarkLabel(startLoopLabel);
        il.Emit(OpCodes.Ldloc, startLocal);
        il.Emit(OpCodes.Ldloc, endLocal);
        il.Emit(OpCodes.Bge, startLoopEndLabel);
        il.Emit(OpCodes.Ldloc, partsLocal);
        il.Emit(OpCodes.Ldloc, startLocal);
        il.Emit(OpCodes.Ldelem_Ref);
        il.Emit(OpCodes.Ldstr, "");
        il.Emit(OpCodes.Call, _types.String.GetMethod("op_Equality", [_types.String, _types.String])!);
        il.Emit(OpCodes.Brfalse, startLoopEndLabel);
        il.Emit(OpCodes.Ldloc, startLocal);
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Add);
        il.Emit(OpCodes.Stloc, startLocal);
        il.Emit(OpCodes.Br, startLoopLabel);
        il.MarkLabel(startLoopEndLabel);

        // while (end > start && parts[end-1] == "") end--;
        var endLoopLabel = il.DefineLabel();
        var endLoopEndLabel = il.DefineLabel();
        il.MarkLabel(endLoopLabel);
        il.Emit(OpCodes.Ldloc, endLocal);
        il.Emit(OpCodes.Ldloc, startLocal);
        il.Emit(OpCodes.Ble, endLoopEndLabel);
        il.Emit(OpCodes.Ldloc, partsLocal);
        il.Emit(OpCodes.Ldloc, endLocal);
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Sub);
        il.Emit(OpCodes.Ldelem_Ref);
        il.Emit(OpCodes.Ldstr, "");
        il.Emit(OpCodes.Call, _types.String.GetMethod("op_Equality", [_types.String, _types.String])!);
        il.Emit(OpCodes.Brfalse, endLoopEndLabel);
        il.Emit(OpCodes.Ldloc, endLocal);
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Sub);
        il.Emit(OpCodes.Stloc, endLocal);
        il.Emit(OpCodes.Br, endLoopLabel);
        il.MarkLabel(endLoopEndLabel);

        // var result = new string[end - start]
        il.Emit(OpCodes.Ldloc, endLocal);
        il.Emit(OpCodes.Ldloc, startLocal);
        il.Emit(OpCodes.Sub);
        il.Emit(OpCodes.Newarr, _types.String);
        il.Emit(OpCodes.Stloc, resultLocal);

        // Array.Copy(parts, start, result, 0, end - start)
        il.Emit(OpCodes.Ldloc, partsLocal);
        il.Emit(OpCodes.Ldloc, startLocal);
        il.Emit(OpCodes.Ldloc, resultLocal);
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Ldloc, endLocal);
        il.Emit(OpCodes.Ldloc, startLocal);
        il.Emit(OpCodes.Sub);
        il.Emit(OpCodes.Call, typeof(Array).GetMethod("Copy", [typeof(Array), _types.Int32, typeof(Array), _types.Int32, _types.Int32])!);

        il.Emit(OpCodes.Ldloc, resultLocal);
        il.Emit(OpCodes.Ret);

        il.MarkLabel(skipTrimLabel);
        il.Emit(OpCodes.Ldloc, partsLocal);
        il.Emit(OpCodes.Ret);
    }

    /// <summary>
    /// Emits: public static object? BuildNamedGroups(Match match)
    /// on the $RegExp class. Returns a $Object with named group values, or null.
    /// </summary>
    private void EmitTSRegExpBuildNamedGroups(TypeBuilder typeBuilder, EmittedRuntime runtime)
    {
        var method = typeBuilder.DefineMethod(
            "BuildNamedGroups",
            MethodAttributes.Public | MethodAttributes.Static,
            _types.Object,
            [typeof(Match)]
        );
        runtime.BuildNamedGroups = method;

        var il = method.GetILGenerator();
        var dictLocal = il.DeclareLocal(_types.DictionaryStringObject);
        var groupCollLocal = il.DeclareLocal(typeof(GroupCollection));
        var enumeratorLocal = il.DeclareLocal(typeof(System.Collections.IEnumerator));
        var groupLocal = il.DeclareLocal(typeof(Group));
        var nameLocal = il.DeclareLocal(_types.String);
        var intParseResultLocal = il.DeclareLocal(_types.Int32);

        var loopStartLabel = il.DefineLabel();
        var loopEndLabel = il.DefineLabel();
        var groupNotSuccessLabel = il.DefineLabel();
        var afterValueLabel = il.DefineLabel();
        var dictReadyLabel = il.DefineLabel();
        var returnNullLabel = il.DefineLabel();
        var finallyLabel = il.DefineLabel();

        // dict = null (initially)
        il.Emit(OpCodes.Ldnull);
        il.Emit(OpCodes.Stloc, dictLocal);

        // var groupColl = match.Groups
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Callvirt, typeof(Match).GetProperty("Groups")!.GetGetMethod()!);
        il.Emit(OpCodes.Stloc, groupCollLocal);

        // Get enumerator
        il.Emit(OpCodes.Ldloc, groupCollLocal);
        il.Emit(OpCodes.Callvirt, typeof(GroupCollection).GetMethod("GetEnumerator")!);
        il.Emit(OpCodes.Stloc, enumeratorLocal);

        il.BeginExceptionBlock();

        il.MarkLabel(loopStartLabel);
        il.Emit(OpCodes.Ldloc, enumeratorLocal);
        il.Emit(OpCodes.Callvirt, typeof(System.Collections.IEnumerator).GetMethod("MoveNext")!);
        il.Emit(OpCodes.Brfalse, loopEndLabel);

        // group = (Group)enumerator.Current
        il.Emit(OpCodes.Ldloc, enumeratorLocal);
        il.Emit(OpCodes.Callvirt, typeof(System.Collections.IEnumerator).GetProperty("Current")!.GetGetMethod()!);
        il.Emit(OpCodes.Castclass, typeof(Group));
        il.Emit(OpCodes.Stloc, groupLocal);

        // name = group.Name
        il.Emit(OpCodes.Ldloc, groupLocal);
        il.Emit(OpCodes.Callvirt, typeof(Group).GetProperty("Name")!.GetGetMethod()!);
        il.Emit(OpCodes.Stloc, nameLocal);

        // if (int.TryParse(name, out _)) continue (skip numeric group names)
        il.Emit(OpCodes.Ldloc, nameLocal);
        il.Emit(OpCodes.Ldloca, intParseResultLocal);
        il.Emit(OpCodes.Call, _types.Int32.GetMethod("TryParse", [_types.String, _types.Int32.MakeByRefType()])!);
        il.Emit(OpCodes.Brtrue, loopStartLabel);

        // Ensure dict is initialized
        il.Emit(OpCodes.Ldloc, dictLocal);
        il.Emit(OpCodes.Brtrue, dictReadyLabel);
        il.Emit(OpCodes.Newobj, _types.GetConstructor(_types.DictionaryStringObject));
        il.Emit(OpCodes.Stloc, dictLocal);
        il.MarkLabel(dictReadyLabel);

        // dict[name] = group.Success ? group.Value : null
        il.Emit(OpCodes.Ldloc, dictLocal);
        il.Emit(OpCodes.Ldloc, nameLocal);

        il.Emit(OpCodes.Ldloc, groupLocal);
        il.Emit(OpCodes.Callvirt, typeof(Group).GetProperty("Success")!.GetGetMethod()!);
        il.Emit(OpCodes.Brfalse, groupNotSuccessLabel);

        il.Emit(OpCodes.Ldloc, groupLocal);
        il.Emit(OpCodes.Callvirt, typeof(Capture).GetProperty("Value")!.GetGetMethod()!);
        il.Emit(OpCodes.Br, afterValueLabel);

        il.MarkLabel(groupNotSuccessLabel);
        il.Emit(OpCodes.Ldnull);

        il.MarkLabel(afterValueLabel);
        il.Emit(OpCodes.Callvirt, _types.DictionaryStringObject.GetMethod("set_Item", [_types.String, _types.Object])!);
        il.Emit(OpCodes.Br, loopStartLabel);

        il.MarkLabel(loopEndLabel);
        il.Emit(OpCodes.Leave, finallyLabel);

        // Finally block
        il.BeginFinallyBlock();
        var disposeEndLabel = il.DefineLabel();
        il.Emit(OpCodes.Ldloc, enumeratorLocal);
        il.Emit(OpCodes.Isinst, typeof(IDisposable));
        il.Emit(OpCodes.Brfalse, disposeEndLabel);
        il.Emit(OpCodes.Ldloc, enumeratorLocal);
        il.Emit(OpCodes.Castclass, typeof(IDisposable));
        il.Emit(OpCodes.Callvirt, _types.DisposableDispose);
        il.MarkLabel(disposeEndLabel);
        il.EndExceptionBlock();

        il.MarkLabel(finallyLabel);

        // return dict == null ? null : new $Object(dict)
        il.Emit(OpCodes.Ldloc, dictLocal);
        il.Emit(OpCodes.Brfalse, returnNullLabel);

        il.Emit(OpCodes.Ldloc, dictLocal);
        il.Emit(OpCodes.Newobj, runtime.TSObjectCtor!);
        il.Emit(OpCodes.Ret);

        il.MarkLabel(returnNullLabel);
        il.Emit(OpCodes.Ldnull);
        il.Emit(OpCodes.Ret);
    }

    /// <summary>
    /// Emits the static helper SymMatch(object rx, object str) -> object on $RegExp.
    /// ECMA-262 §22.2.5.7 RegExp.prototype[@@match]:
    ///  - non-global: return rx.Exec(s)  (Array exotic or null)
    ///  - global:    return list of all match[0] values, or null if no matches
    /// </summary>
    private void EmitTSRegExpSymMatchHelper(TypeBuilder typeBuilder, EmittedRuntime runtime)
    {
        var method = typeBuilder.DefineMethod(
            "SymMatch",
            MethodAttributes.Public | MethodAttributes.Static,
            _types.Object,
            [_types.Object, _types.Object]
        );
        runtime.TSRegExpSymMatchHelper = method;

        var il = method.GetILGenerator();
        var rxLocal = il.DeclareLocal(typeBuilder);
        var sLocal = il.DeclareLocal(_types.String);
        var listLocal = il.DeclareLocal(typeof(List<string>));
        var resultLocal = il.DeclareLocal(_types.ListOfObject);
        var enumLocal = il.DeclareLocal(typeof(List<string>.Enumerator));

        var globalLabel = il.DefineLabel();
        var emptyMatchLabel = il.DefineLabel();
        var loopStartLabel = il.DefineLabel();
        var loopBodyLabel = il.DefineLabel();
        var slowPathLabel = il.DefineLabel();

        // ECMA-262 §22.2.5.6 step 2: throw TypeError if `this` is not an Object.
        EmitRequireObjectArg(il, runtime, method, argIndex: 0, "RegExp.prototype[Symbol.match]");

        // Slow path: any Object that is NOT a $RegExp goes through the
        // spec algorithm via Get/exec — see EmitSymMatchSlowPath.
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Isinst, typeBuilder);
        il.Emit(OpCodes.Brfalse, slowPathLabel);

        // var rx = ($RegExp)arg0;
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Castclass, typeBuilder);
        il.Emit(OpCodes.Stloc, rxLocal);

        // var s = arg1?.ToString() ?? "undefined";
        EmitArgToJsString(il, runtime, argIndex: 1, sLocal);

        // if (rx._global) goto globalLabel;
        il.Emit(OpCodes.Ldloc, rxLocal);
        il.Emit(OpCodes.Ldfld, _tsRegExpGlobalField);
        il.Emit(OpCodes.Brtrue, globalLabel);

        // Non-global: return rx.Exec(s);
        il.Emit(OpCodes.Ldloc, rxLocal);
        il.Emit(OpCodes.Ldloc, sLocal);
        il.Emit(OpCodes.Callvirt, runtime.TSRegExpExecMethod);
        il.Emit(OpCodes.Ret);

        // Global: build a List<object?> of all match values.
        il.MarkLabel(globalLabel);
        // var list = rx.MatchAll(s);
        il.Emit(OpCodes.Ldloc, rxLocal);
        il.Emit(OpCodes.Ldloc, sLocal);
        il.Emit(OpCodes.Call, _tsRegExpMatchAllMethod);
        il.Emit(OpCodes.Stloc, listLocal);

        // if (list.Count == 0) return null;
        il.Emit(OpCodes.Ldloc, listLocal);
        il.Emit(OpCodes.Callvirt, typeof(List<string>).GetProperty("Count")!.GetGetMethod()!);
        il.Emit(OpCodes.Brfalse, emptyMatchLabel);

        // var result = new List<object?>(list.Count);
        il.Emit(OpCodes.Ldloc, listLocal);
        il.Emit(OpCodes.Callvirt, typeof(List<string>).GetProperty("Count")!.GetGetMethod()!);
        il.Emit(OpCodes.Newobj, _types.GetConstructor(_types.ListOfObject, _types.Int32));
        il.Emit(OpCodes.Stloc, resultLocal);

        // foreach (var m in list) result.Add(m);
        il.Emit(OpCodes.Ldloc, listLocal);
        il.Emit(OpCodes.Callvirt, typeof(List<string>).GetMethod("GetEnumerator")!);
        il.Emit(OpCodes.Stloc, enumLocal);

        il.Emit(OpCodes.Br, loopStartLabel);
        il.MarkLabel(loopBodyLabel);
        il.Emit(OpCodes.Ldloc, resultLocal);
        il.Emit(OpCodes.Ldloca, enumLocal);
        il.Emit(OpCodes.Call, typeof(List<string>.Enumerator).GetProperty("Current")!.GetGetMethod()!);
        il.Emit(OpCodes.Callvirt, _types.GetMethod(_types.ListOfObject, "Add", _types.Object));

        il.MarkLabel(loopStartLabel);
        il.Emit(OpCodes.Ldloca, enumLocal);
        il.Emit(OpCodes.Call, typeof(List<string>.Enumerator).GetMethod("MoveNext")!);
        il.Emit(OpCodes.Brtrue, loopBodyLabel);

        // return new $Array(result);
        il.Emit(OpCodes.Ldloc, resultLocal);
        il.Emit(OpCodes.Newobj, runtime.TSArrayCtor);
        il.Emit(OpCodes.Ret);

        // return null
        il.MarkLabel(emptyMatchLabel);
        il.Emit(OpCodes.Ldnull);
        il.Emit(OpCodes.Ret);

        // === Slow path: spec-aligned algorithm for any Object `this` ===
        // ECMA-262 §22.2.5.6 RegExp.prototype [@@match]:
        //   3. S = ToString(string)
        //   4. flags = ToString(? Get(rx, "flags"))
        //   5. If flags doesn't contain "g": return ? RegExpExec(rx, S)
        //   6. Else (global): set lastIndex=0, loop calling RegExpExec,
        //      collecting result["0"] until null. Return null if no
        //      match, else the array. Empty matches advance lastIndex.
        il.MarkLabel(slowPathLabel);
        EmitArgToJsString(il, runtime, argIndex: 1, sLocal);
        EmitSymMatchSlowPath(il, runtime, sLocal);
    }

    /// <summary>
    /// Emits the spec algorithm for <c>RegExp.prototype[Symbol.match]</c>
    /// on a non-$RegExp <c>this</c>. <paramref name="sLocal"/> already holds
    /// the ToString'd <c>string</c> argument.
    /// </summary>
    private void EmitSymMatchSlowPath(ILGenerator il, EmittedRuntime runtime, LocalBuilder sLocal)
    {
        var rxObjLocal = il.DeclareLocal(_types.Object);
        var flagsLocal = il.DeclareLocal(_types.String);
        var execResultLocal = il.DeclareLocal(_types.Object);
        var matchStrLocal = il.DeclareLocal(_types.String);
        var arrLocal = il.DeclareLocal(_types.ListOfObject);
        var nLocal = il.DeclareLocal(_types.Int32);

        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Stloc, rxObjLocal);

        // flags = ToString(Get(rx, "flags"))
        il.Emit(OpCodes.Ldloc, rxObjLocal);
        il.Emit(OpCodes.Ldstr, "flags");
        il.Emit(OpCodes.Call, runtime.GetProperty);
        il.Emit(OpCodes.Call, runtime.Stringify);
        il.Emit(OpCodes.Stloc, flagsLocal);

        // if (!flags.Contains('g')) return RegExpExec(rx, S);
        var globalPathLabel = il.DefineLabel();
        il.Emit(OpCodes.Ldloc, flagsLocal);
        il.Emit(OpCodes.Ldstr, "g");
        il.Emit(OpCodes.Callvirt, _types.GetMethod(_types.String, "Contains", _types.String));
        il.Emit(OpCodes.Brtrue, globalPathLabel);

        EmitRegExpExecSlow(il, runtime, rxObjLocal, sLocal, execResultLocal);
        il.Emit(OpCodes.Ldloc, execResultLocal);
        il.Emit(OpCodes.Ret);

        il.MarkLabel(globalPathLabel);
        // Set(rx, "lastIndex", 0)
        il.Emit(OpCodes.Ldloc, rxObjLocal);
        il.Emit(OpCodes.Ldstr, "lastIndex");
        il.Emit(OpCodes.Ldc_R8, 0.0);
        il.Emit(OpCodes.Box, _types.Double);
        il.Emit(OpCodes.Call, runtime.SetProperty);

        // arr = new List<object?>()
        il.Emit(OpCodes.Newobj, _types.GetDefaultConstructor(_types.ListOfObject));
        il.Emit(OpCodes.Stloc, arrLocal);
        // n = 0
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Stloc, nLocal);

        // Loop:
        //   result = RegExpExec(rx, S)
        //   if (result == null) { if (n == 0) return null; else return arr; }
        //   matchStr = ToString(Get(result, "0"))
        //   arr.Add(matchStr)
        //   n++
        //   if (matchStr.Length == 0) advance lastIndex
        var loopTopLabel = il.DefineLabel();
        var nullResultLabel = il.DefineLabel();
        var emptyMatchAdvLabel = il.DefineLabel();
        var afterEmptyAdvLabel = il.DefineLabel();
        il.MarkLabel(loopTopLabel);

        EmitRegExpExecSlow(il, runtime, rxObjLocal, sLocal, execResultLocal);
        il.Emit(OpCodes.Ldloc, execResultLocal);
        il.Emit(OpCodes.Brfalse, nullResultLabel);

        // matchStr = ToString(Get(result, "0"))
        il.Emit(OpCodes.Ldloc, execResultLocal);
        il.Emit(OpCodes.Ldstr, "0");
        il.Emit(OpCodes.Call, runtime.GetProperty);
        il.Emit(OpCodes.Call, runtime.Stringify);
        il.Emit(OpCodes.Stloc, matchStrLocal);

        // arr.Add(matchStr)
        il.Emit(OpCodes.Ldloc, arrLocal);
        il.Emit(OpCodes.Ldloc, matchStrLocal);
        il.Emit(OpCodes.Callvirt, _types.GetMethod(_types.ListOfObject, "Add", _types.Object));

        // n++
        il.Emit(OpCodes.Ldloc, nLocal);
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Add);
        il.Emit(OpCodes.Stloc, nLocal);

        // If matchStr.Length == 0, advance lastIndex past it (avoid infinite
        // loop on zero-width matches like /(?:)/g).
        il.Emit(OpCodes.Ldloc, matchStrLocal);
        il.Emit(OpCodes.Callvirt, _types.GetProperty(_types.String, "Length").GetGetMethod()!);
        il.Emit(OpCodes.Brtrue, afterEmptyAdvLabel);

        // lastIndex = ToInt(Get(rx, "lastIndex")) + 1; SetProperty(rx, "lastIndex", lastIndex)
        il.MarkLabel(emptyMatchAdvLabel);
        il.Emit(OpCodes.Ldloc, rxObjLocal);
        il.Emit(OpCodes.Ldstr, "lastIndex");
        il.Emit(OpCodes.Ldloc, rxObjLocal);
        il.Emit(OpCodes.Ldstr, "lastIndex");
        il.Emit(OpCodes.Call, runtime.GetProperty);
        EmitToInt32Coerce(il, runtime);
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Add);
        il.Emit(OpCodes.Conv_R8);
        il.Emit(OpCodes.Box, _types.Double);
        il.Emit(OpCodes.Call, runtime.SetProperty);

        il.MarkLabel(afterEmptyAdvLabel);
        il.Emit(OpCodes.Br, loopTopLabel);

        // Null result: return null if n==0 else return new $Array(arr)
        il.MarkLabel(nullResultLabel);
        var returnArrLabel = il.DefineLabel();
        il.Emit(OpCodes.Ldloc, nLocal);
        il.Emit(OpCodes.Brtrue, returnArrLabel);
        il.Emit(OpCodes.Ldnull);
        il.Emit(OpCodes.Ret);

        il.MarkLabel(returnArrLabel);
        il.Emit(OpCodes.Ldloc, arrLocal);
        il.Emit(OpCodes.Newobj, runtime.TSArrayCtor);
        il.Emit(OpCodes.Ret);
    }

    /// <summary>
    /// Coerces a boxed numeric value at the top of the stack to int32, with
    /// JS semantics: null/false → 0, true → 1, double via truncate, other
    /// types → 0. Used by the Symbol.* slow paths to read lastIndex.
    /// </summary>
    private void EmitToInt32Coerce(ILGenerator il, EmittedRuntime runtime)
    {
        // stack: object value
        var doubleLabel = il.DefineLabel();
        var boolLabel = il.DefineLabel();
        var doneLabel = il.DefineLabel();
        var localCoerce = il.DeclareLocal(_types.Object);
        il.Emit(OpCodes.Stloc, localCoerce);

        il.Emit(OpCodes.Ldloc, localCoerce);
        il.Emit(OpCodes.Isinst, _types.Double);
        il.Emit(OpCodes.Brtrue, doubleLabel);
        il.Emit(OpCodes.Ldloc, localCoerce);
        il.Emit(OpCodes.Isinst, _types.Boolean);
        il.Emit(OpCodes.Brtrue, boolLabel);
        // default: 0
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Br, doneLabel);

        il.MarkLabel(doubleLabel);
        il.Emit(OpCodes.Ldloc, localCoerce);
        il.Emit(OpCodes.Unbox_Any, _types.Double);
        il.Emit(OpCodes.Conv_I4);
        il.Emit(OpCodes.Br, doneLabel);

        il.MarkLabel(boolLabel);
        il.Emit(OpCodes.Ldloc, localCoerce);
        il.Emit(OpCodes.Unbox_Any, _types.Boolean);
        // bool → int via Conv_I4 (true=1, false=0)

        il.MarkLabel(doneLabel);
    }

    /// <summary>
    /// Emits the static helper SymMatchAll(object rx, object str) -> object on $RegExp.
    /// ECMA-262 §22.2.5.8 — returns an iterable of full-match strings.
    /// Simplified to a $Array of strings (a JS Array is itself iterable, satisfying
    /// most for-of consumers; full Iterator-prototype semantics are a separate task).
    /// </summary>
    private void EmitTSRegExpSymMatchAllHelper(TypeBuilder typeBuilder, EmittedRuntime runtime)
    {
        var method = typeBuilder.DefineMethod(
            "SymMatchAll",
            MethodAttributes.Public | MethodAttributes.Static,
            _types.Object,
            [_types.Object, _types.Object]
        );
        runtime.TSRegExpSymMatchAllHelper = method;

        var il = method.GetILGenerator();
        var rxLocal = il.DeclareLocal(typeBuilder);
        var sLocal = il.DeclareLocal(_types.String);
        var listLocal = il.DeclareLocal(typeof(List<string>));
        var resultLocal = il.DeclareLocal(_types.ListOfObject);
        var enumLocal = il.DeclareLocal(typeof(List<string>.Enumerator));

        var loopStartLabel = il.DefineLabel();
        var loopBodyLabel = il.DefineLabel();

        // ECMA-262 §22.2.5.8 step 2: throw TypeError if `this` is not an Object.
        EmitRequireObjectArg(il, runtime, method, argIndex: 0, "RegExp.prototype[Symbol.matchAll]");

        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Castclass, typeBuilder);
        il.Emit(OpCodes.Stloc, rxLocal);

        EmitArgToJsString(il, runtime, argIndex: 1, sLocal);

        // var list = rx.MatchAll(s);
        il.Emit(OpCodes.Ldloc, rxLocal);
        il.Emit(OpCodes.Ldloc, sLocal);
        il.Emit(OpCodes.Call, _tsRegExpMatchAllMethod);
        il.Emit(OpCodes.Stloc, listLocal);

        // var result = new List<object?>(list.Count);
        il.Emit(OpCodes.Ldloc, listLocal);
        il.Emit(OpCodes.Callvirt, typeof(List<string>).GetProperty("Count")!.GetGetMethod()!);
        il.Emit(OpCodes.Newobj, _types.GetConstructor(_types.ListOfObject, _types.Int32));
        il.Emit(OpCodes.Stloc, resultLocal);

        il.Emit(OpCodes.Ldloc, listLocal);
        il.Emit(OpCodes.Callvirt, typeof(List<string>).GetMethod("GetEnumerator")!);
        il.Emit(OpCodes.Stloc, enumLocal);

        il.Emit(OpCodes.Br, loopStartLabel);
        il.MarkLabel(loopBodyLabel);
        il.Emit(OpCodes.Ldloc, resultLocal);
        il.Emit(OpCodes.Ldloca, enumLocal);
        il.Emit(OpCodes.Call, typeof(List<string>.Enumerator).GetProperty("Current")!.GetGetMethod()!);
        il.Emit(OpCodes.Callvirt, _types.GetMethod(_types.ListOfObject, "Add", _types.Object));

        il.MarkLabel(loopStartLabel);
        il.Emit(OpCodes.Ldloca, enumLocal);
        il.Emit(OpCodes.Call, typeof(List<string>.Enumerator).GetMethod("MoveNext")!);
        il.Emit(OpCodes.Brtrue, loopBodyLabel);

        il.Emit(OpCodes.Ldloc, resultLocal);
        il.Emit(OpCodes.Newobj, runtime.TSArrayCtor);
        il.Emit(OpCodes.Ret);
    }

    /// <summary>
    /// Emits SymReplace(object rx, object str, object replacement) -> string.
    /// </summary>
    private void EmitTSRegExpSymReplaceHelper(TypeBuilder typeBuilder, EmittedRuntime runtime)
    {
        var method = typeBuilder.DefineMethod(
            "SymReplace",
            MethodAttributes.Public | MethodAttributes.Static,
            _types.Object,
            [_types.Object, _types.Object, _types.Object]
        );
        runtime.TSRegExpSymReplaceHelper = method;

        var il = method.GetILGenerator();
        var rxLocal = il.DeclareLocal(typeBuilder);
        var sLocal = il.DeclareLocal(_types.String);
        var rLocal = il.DeclareLocal(_types.String);

        // ECMA-262 §22.2.5.10 step 2: throw TypeError if `this` is not an Object.
        EmitRequireObjectArg(il, runtime, method, argIndex: 0, "RegExp.prototype[Symbol.replace]");

        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Castclass, typeBuilder);
        il.Emit(OpCodes.Stloc, rxLocal);

        EmitArgToJsString(il, runtime, argIndex: 1, sLocal);
        EmitArgToJsString(il, runtime, argIndex: 2, rLocal);

        // return rx.Replace(s, r);
        il.Emit(OpCodes.Ldloc, rxLocal);
        il.Emit(OpCodes.Ldloc, sLocal);
        il.Emit(OpCodes.Ldloc, rLocal);
        il.Emit(OpCodes.Call, _tsRegExpReplaceMethod);
        il.Emit(OpCodes.Ret);
    }

    /// <summary>
    /// Emits SymSearch(object rx, object str) -> double (boxed).
    ///
    /// Fast path: rx is a real $RegExp — call the internal Search method.
    /// Slow path: rx is any other Object (test262 patterns:
    /// `RegExp.prototype[Symbol.search].call({lastIndex:0, exec:fn})`) —
    /// run the spec algorithm via emitted Get/Set + InvokeWithThis on the
    /// user-installed `exec` callable.
    /// </summary>
    private void EmitTSRegExpSymSearchHelper(TypeBuilder typeBuilder, EmittedRuntime runtime)
    {
        var method = typeBuilder.DefineMethod(
            "SymSearch",
            MethodAttributes.Public | MethodAttributes.Static,
            _types.Object,
            [_types.Object, _types.Object]
        );
        runtime.TSRegExpSymSearchHelper = method;

        var il = method.GetILGenerator();
        var sLocal = il.DeclareLocal(_types.String);

        // ECMA-262 §22.2.5.11 step 2: throw TypeError if `this` is not an Object.
        EmitRequireObjectArg(il, runtime, method, argIndex: 0, "RegExp.prototype[Symbol.search]");

        // Spec-aligned algorithm for any Object `this`. The previous fast
        // path that direct-called \$RegExp.Search bypassed the spec steps
        // around `lastIndex` save/restore + RegExpExec dispatch, which
        // test262's builtin-coerce-lastindex.js / get-lastindex-err.js /
        // set-lastindex-{init,restore}-* tests verify. \$Runtime.GetProperty
        // / SetProperty now route \$RegExp's lastIndex through the typed
        // setter with ToLength coercion, so the slow path produces the same
        // result as the previous direct-method fast path on real regexes
        // while picking up the spec-mandated side effects.
        //
        // ECMA-262 §22.2.5.11 RegExp.prototype [@@search]:
        //   3. S = ToString(string)
        //   4. previousLastIndex = Get(rx, "lastIndex")
        //   5. If !SameValue(previousLastIndex, 0): Set(rx, "lastIndex", 0)
        //   6. result = RegExpExec(rx, S)
        //   7. currentLastIndex = Get(rx, "lastIndex")
        //   8. If !SameValue(currentLastIndex, previousLastIndex):
        //        Set(rx, "lastIndex", previousLastIndex)
        //   9. If result is null, return -1
        //  10. Return Get(result, "index")
        EmitArgToJsString(il, runtime, argIndex: 1, sLocal);
        EmitSymSearchSlowPath(il, runtime, sLocal);
    }

    /// <summary>
    /// Emits the spec-algorithm body for <c>RegExp.prototype[Symbol.search]</c>
    /// on a non-$RegExp <c>this</c>. <paramref name="sLocal"/> already holds
    /// the ToString'd <c>string</c> argument.
    /// </summary>
    private void EmitSymSearchSlowPath(ILGenerator il, EmittedRuntime runtime, LocalBuilder sLocal)
    {
        var rxObjLocal = il.DeclareLocal(_types.Object);
        var prevLILocal = il.DeclareLocal(_types.Object);
        var resultLocal = il.DeclareLocal(_types.Object);
        var currLILocal = il.DeclareLocal(_types.Object);

        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Stloc, rxObjLocal);

        // previousLastIndex = Get(rx, "lastIndex")
        il.Emit(OpCodes.Ldloc, rxObjLocal);
        il.Emit(OpCodes.Ldstr, "lastIndex");
        il.Emit(OpCodes.Call, runtime.GetProperty);
        il.Emit(OpCodes.Stloc, prevLILocal);

        // If previousLastIndex is not numeric 0 → Set(rx, "lastIndex", 0)
        var skipResetLabel = il.DefineLabel();
        EmitIsNumericZero(il, runtime, prevLILocal);
        il.Emit(OpCodes.Brtrue, skipResetLabel);
        il.Emit(OpCodes.Ldloc, rxObjLocal);
        il.Emit(OpCodes.Ldstr, "lastIndex");
        il.Emit(OpCodes.Ldc_R8, 0.0);
        il.Emit(OpCodes.Box, _types.Double);
        il.Emit(OpCodes.Call, runtime.SetProperty);
        il.MarkLabel(skipResetLabel);

        // result = RegExpExec(rx, S)
        EmitRegExpExecSlow(il, runtime, rxObjLocal, sLocal, resultLocal);

        // currentLastIndex = Get(rx, "lastIndex")
        il.Emit(OpCodes.Ldloc, rxObjLocal);
        il.Emit(OpCodes.Ldstr, "lastIndex");
        il.Emit(OpCodes.Call, runtime.GetProperty);
        il.Emit(OpCodes.Stloc, currLILocal);

        // If currentLastIndex !== previousLastIndex → restore
        var skipRestoreLabel = il.DefineLabel();
        il.Emit(OpCodes.Ldloc, currLILocal);
        il.Emit(OpCodes.Ldloc, prevLILocal);
        il.Emit(OpCodes.Call, _types.GetMethod(_types.Object, "Equals", _types.Object, _types.Object));
        il.Emit(OpCodes.Brtrue, skipRestoreLabel);
        il.Emit(OpCodes.Ldloc, rxObjLocal);
        il.Emit(OpCodes.Ldstr, "lastIndex");
        il.Emit(OpCodes.Ldloc, prevLILocal);
        il.Emit(OpCodes.Call, runtime.SetProperty);
        il.MarkLabel(skipRestoreLabel);

        // If result is null → return -1
        var hasMatchLabel = il.DefineLabel();
        il.Emit(OpCodes.Ldloc, resultLocal);
        il.Emit(OpCodes.Brtrue, hasMatchLabel);
        il.Emit(OpCodes.Ldc_R8, -1.0);
        il.Emit(OpCodes.Box, _types.Double);
        il.Emit(OpCodes.Ret);

        // Return Get(result, "index")
        il.MarkLabel(hasMatchLabel);
        il.Emit(OpCodes.Ldloc, resultLocal);
        il.Emit(OpCodes.Ldstr, "index");
        il.Emit(OpCodes.Call, runtime.GetProperty);
        il.Emit(OpCodes.Ret);
    }

    /// <summary>
    /// Emits ECMA-262 §22.2.5.2.1 RegExpExec(rx, S) for a non-$RegExp
    /// <paramref name="rxObjLocal"/>. Reads <c>exec</c> via Get; if it is
    /// callable ($TSFunction) invokes it with rx as receiver and stores
    /// the result in <paramref name="resultLocal"/>. Throws TypeError if
    /// exec is not callable (the spec fallback only covers built-in
    /// regexes which we already handled in the fast path).
    /// </summary>
    private void EmitRegExpExecSlow(ILGenerator il, EmittedRuntime runtime, LocalBuilder rxObjLocal, LocalBuilder sLocal, LocalBuilder resultLocal)
    {
        var execLocal = il.DeclareLocal(_types.Object);
        var argsLocal = il.DeclareLocal(_types.ObjectArray);

        // exec = Get(rx, "exec")
        il.Emit(OpCodes.Ldloc, rxObjLocal);
        il.Emit(OpCodes.Ldstr, "exec");
        il.Emit(OpCodes.Call, runtime.GetProperty);
        il.Emit(OpCodes.Stloc, execLocal);

        // If exec isn't a $TSFunction → TypeError.
        var execOkLabel = il.DefineLabel();
        il.Emit(OpCodes.Ldloc, execLocal);
        il.Emit(OpCodes.Isinst, runtime.TSFunctionType);
        il.Emit(OpCodes.Brtrue, execOkLabel);
        il.Emit(OpCodes.Ldstr, "RegExp.prototype method called on non-RegExp without a callable exec");
        il.Emit(OpCodes.Newobj, runtime.TSTypeErrorCtor);
        il.Emit(OpCodes.Call, runtime.CreateException);
        il.Emit(OpCodes.Throw);
        il.MarkLabel(execOkLabel);

        // result = exec.InvokeWithThis(rx, [S])
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Newarr, _types.Object);
        il.Emit(OpCodes.Stloc, argsLocal);
        il.Emit(OpCodes.Ldloc, argsLocal);
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Ldloc, sLocal);
        il.Emit(OpCodes.Stelem_Ref);

        il.Emit(OpCodes.Ldloc, execLocal);
        il.Emit(OpCodes.Castclass, runtime.TSFunctionType);
        il.Emit(OpCodes.Ldloc, rxObjLocal);
        il.Emit(OpCodes.Ldloc, argsLocal);
        il.Emit(OpCodes.Callvirt, runtime.TSFunctionInvokeWithThis);
        il.Emit(OpCodes.Stloc, resultLocal);

        // ECMA-262 §22.2.5.2.1 step 5c: if the user-provided exec returned a
        // value that is neither Object nor Null, throw TypeError. Undefined,
        // numbers, strings, booleans, symbols all qualify as non-Object.
        var resultOkLabel = il.DefineLabel();
        var nonObjectLabel = il.DefineLabel();

        // null is permitted — short-circuit to ok and let caller distinguish
        // "no match" from a real result via downstream is-null tests.
        il.Emit(OpCodes.Ldloc, resultLocal);
        il.Emit(OpCodes.Brfalse, resultOkLabel);
        // $Undefined → throw
        il.Emit(OpCodes.Ldloc, resultLocal);
        il.Emit(OpCodes.Isinst, runtime.UndefinedType);
        il.Emit(OpCodes.Brtrue, nonObjectLabel);
        // string → throw
        il.Emit(OpCodes.Ldloc, resultLocal);
        il.Emit(OpCodes.Isinst, _types.String);
        il.Emit(OpCodes.Brtrue, nonObjectLabel);
        // boolean → throw
        il.Emit(OpCodes.Ldloc, resultLocal);
        il.Emit(OpCodes.Isinst, _types.Boolean);
        il.Emit(OpCodes.Brtrue, nonObjectLabel);
        // double → throw
        il.Emit(OpCodes.Ldloc, resultLocal);
        il.Emit(OpCodes.Isinst, _types.Double);
        il.Emit(OpCodes.Brtrue, nonObjectLabel);
        // int → throw (rare in compiled output but defensive)
        il.Emit(OpCodes.Ldloc, resultLocal);
        il.Emit(OpCodes.Isinst, _types.Int32);
        il.Emit(OpCodes.Brtrue, nonObjectLabel);
        // symbol → throw
        il.Emit(OpCodes.Ldloc, resultLocal);
        il.Emit(OpCodes.Isinst, runtime.TSSymbolType);
        il.Emit(OpCodes.Brtrue, nonObjectLabel);
        il.Emit(OpCodes.Br, resultOkLabel);

        il.MarkLabel(nonObjectLabel);
        il.Emit(OpCodes.Ldstr, "RegExp exec returned a non-Object, non-Null value");
        il.Emit(OpCodes.Newobj, runtime.TSTypeErrorCtor);
        il.Emit(OpCodes.Call, runtime.CreateException);
        il.Emit(OpCodes.Throw);

        il.MarkLabel(resultOkLabel);
    }

    /// <summary>
    /// Emits a boolean push: <c>local is double d &amp;&amp; d == 0</c>, also
    /// treating null/non-numeric as "not zero". Used for SameValue(li, 0)
    /// short-circuit checks in the Symbol.* slow paths.
    /// </summary>
    private void EmitIsNumericZero(ILGenerator il, EmittedRuntime runtime, LocalBuilder local)
    {
        var notDoubleLabel = il.DefineLabel();
        var doneLabel = il.DefineLabel();

        il.Emit(OpCodes.Ldloc, local);
        il.Emit(OpCodes.Isinst, _types.Double);
        il.Emit(OpCodes.Brfalse, notDoubleLabel);
        il.Emit(OpCodes.Ldloc, local);
        il.Emit(OpCodes.Unbox_Any, _types.Double);
        il.Emit(OpCodes.Ldc_R8, 0.0);
        il.Emit(OpCodes.Ceq);
        il.Emit(OpCodes.Br, doneLabel);
        il.MarkLabel(notDoubleLabel);
        il.Emit(OpCodes.Ldc_I4_0);
        il.MarkLabel(doneLabel);
    }

    /// <summary>
    /// Emits SymSplit(object rx, object str, object limit) -> $Array.
    /// </summary>
    private void EmitTSRegExpSymSplitHelper(TypeBuilder typeBuilder, EmittedRuntime runtime)
    {
        var method = typeBuilder.DefineMethod(
            "SymSplit",
            MethodAttributes.Public | MethodAttributes.Static,
            _types.Object,
            [_types.Object, _types.Object, _types.Object]
        );
        runtime.TSRegExpSymSplitHelper = method;

        var il = method.GetILGenerator();
        var rxLocal = il.DeclareLocal(typeBuilder);
        var sLocal = il.DeclareLocal(_types.String);
        var partsLocal = il.DeclareLocal(typeof(string[]));
        var resultLocal = il.DeclareLocal(_types.ListOfObject);
        var nLocal = il.DeclareLocal(_types.Int32);
        var iLocal = il.DeclareLocal(_types.Int32);

        var noLimitLabel = il.DefineLabel();
        var loopStartLabel = il.DefineLabel();
        var loopEndLabel = il.DefineLabel();

        // ECMA-262 §22.2.5.13 step 2: throw TypeError if `this` is not an Object.
        EmitRequireObjectArg(il, runtime, method, argIndex: 0, "RegExp.prototype[Symbol.split]");

        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Castclass, typeBuilder);
        il.Emit(OpCodes.Stloc, rxLocal);

        EmitArgToJsString(il, runtime, argIndex: 1, sLocal);

        // var parts = rx.Split(s);
        il.Emit(OpCodes.Ldloc, rxLocal);
        il.Emit(OpCodes.Ldloc, sLocal);
        il.Emit(OpCodes.Call, _tsRegExpSplitMethod);
        il.Emit(OpCodes.Stloc, partsLocal);

        // n = parts.Length;
        il.Emit(OpCodes.Ldloc, partsLocal);
        il.Emit(OpCodes.Ldlen);
        il.Emit(OpCodes.Conv_I4);
        il.Emit(OpCodes.Stloc, nLocal);

        // if (limit is double d && d >= 0 && (int)d < n) n = (int)d;
        il.Emit(OpCodes.Ldarg_2);
        il.Emit(OpCodes.Isinst, _types.Double);
        il.Emit(OpCodes.Brfalse, noLimitLabel);

        var limTmp = il.DeclareLocal(_types.Int32);
        il.Emit(OpCodes.Ldarg_2);
        il.Emit(OpCodes.Unbox_Any, _types.Double);
        il.Emit(OpCodes.Conv_I4);
        il.Emit(OpCodes.Stloc, limTmp);
        // if (lim < 0) skip
        il.Emit(OpCodes.Ldloc, limTmp);
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Blt, noLimitLabel);
        // if (lim < n) n = lim;
        il.Emit(OpCodes.Ldloc, limTmp);
        il.Emit(OpCodes.Ldloc, nLocal);
        il.Emit(OpCodes.Bge, noLimitLabel);
        il.Emit(OpCodes.Ldloc, limTmp);
        il.Emit(OpCodes.Stloc, nLocal);

        il.MarkLabel(noLimitLabel);

        // var result = new List<object?>(n);
        il.Emit(OpCodes.Ldloc, nLocal);
        il.Emit(OpCodes.Newobj, _types.GetConstructor(_types.ListOfObject, _types.Int32));
        il.Emit(OpCodes.Stloc, resultLocal);

        // for (int i = 0; i < n; i++) result.Add(parts[i]);
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Stloc, iLocal);
        il.Emit(OpCodes.Br, loopEndLabel);

        il.MarkLabel(loopStartLabel);
        il.Emit(OpCodes.Ldloc, resultLocal);
        il.Emit(OpCodes.Ldloc, partsLocal);
        il.Emit(OpCodes.Ldloc, iLocal);
        il.Emit(OpCodes.Ldelem_Ref);
        il.Emit(OpCodes.Callvirt, _types.GetMethod(_types.ListOfObject, "Add", _types.Object));

        il.Emit(OpCodes.Ldloc, iLocal);
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Add);
        il.Emit(OpCodes.Stloc, iLocal);

        il.MarkLabel(loopEndLabel);
        il.Emit(OpCodes.Ldloc, iLocal);
        il.Emit(OpCodes.Ldloc, nLocal);
        il.Emit(OpCodes.Blt, loopStartLabel);

        // return new $Array(result);
        il.Emit(OpCodes.Ldloc, resultLocal);
        il.Emit(OpCodes.Newobj, runtime.TSArrayCtor);
        il.Emit(OpCodes.Ret);
    }

    /// <summary>
    /// Emits IL: <c>local = arg{argIndex} == null ? "undefined" : arg{argIndex}.ToString()</c>.
    /// </summary>
    /// <summary>
    /// Emits the eleven RegExp.prototype accessor-descriptor getters as
    /// static methods on $RegExp. Each helper:
    ///   - Takes <c>object __this</c>. Name-naming "__this" makes
    ///     $TSFunction._expectsThis=true so the call-site receiver is routed
    ///     into arg0 — required for the test262 pattern
    ///     <c>var get = Object.getOwnPropertyDescriptor(RegExp.prototype,
    ///     'source').get; get.call(undefined)</c>.
    ///   - If __this is not Object → throw TypeError. (null, undefined,
    ///     primitives, $TSSymbol.)
    ///   - If __this is RegExp.prototype itself → return the spec-specified
    ///     "fallback for prototype" value (source: "(?:)", flags: "", all
    ///     booleans: false).
    ///   - If __this is a $RegExp instance → return the slot value via the
    ///     typed getter or parsed-from-flags.
    ///   - Otherwise (some other Object) → throw TypeError.
    /// Then <see cref="EmitRegExpPrototypePopulate"/> installs them as
    /// PDS accessor descriptors on RegExp.prototype.
    /// </summary>
    private void EmitTSRegExpProtoAccessors(TypeBuilder typeBuilder, EmittedRuntime runtime)
    {
        runtime.TSRegExpProtoGetSource    = EmitProtoAccessor(typeBuilder, runtime, "ProtoGetSource",    () => true,  EmitSourceBody);
        runtime.TSRegExpProtoGetFlags     = EmitProtoAccessor(typeBuilder, runtime, "ProtoGetFlags",     () => true,  EmitFlagsBody);
        runtime.TSRegExpProtoGetGlobal    = EmitProtoBoolAccessor(typeBuilder, runtime, "ProtoGetGlobal",    'g');
        runtime.TSRegExpProtoGetIgnoreCase = EmitProtoBoolAccessor(typeBuilder, runtime, "ProtoGetIgnoreCase", 'i');
        runtime.TSRegExpProtoGetMultiline = EmitProtoBoolAccessor(typeBuilder, runtime, "ProtoGetMultiline", 'm');
        runtime.TSRegExpProtoGetSticky    = EmitProtoBoolAccessor(typeBuilder, runtime, "ProtoGetSticky",    'y');
        runtime.TSRegExpProtoGetUnicode   = EmitProtoBoolAccessor(typeBuilder, runtime, "ProtoGetUnicode",   'u');
        runtime.TSRegExpProtoGetDotAll    = EmitProtoBoolAccessor(typeBuilder, runtime, "ProtoGetDotAll",    's');
        runtime.TSRegExpProtoGetHasIndices = EmitProtoBoolAccessor(typeBuilder, runtime, "ProtoGetHasIndices", 'd');
        runtime.TSRegExpProtoGetUnicodeSets = EmitProtoBoolAccessor(typeBuilder, runtime, "ProtoGetUnicodeSets", 'v');

        // source body: return rx._source
        void EmitSourceBody(ILGenerator il, LocalBuilder rx)
        {
            il.Emit(OpCodes.Ldloc, rx);
            il.Emit(OpCodes.Callvirt, runtime.TSRegExpSourceGetter);
        }
        // flags body: return rx._flags
        void EmitFlagsBody(ILGenerator il, LocalBuilder rx)
        {
            il.Emit(OpCodes.Ldloc, rx);
            il.Emit(OpCodes.Callvirt, runtime.TSRegExpFlagsGetter);
        }
    }

    /// <summary>
    /// Emits a string-returning proto accessor (source / flags). Returns the
    /// MethodBuilder for the helper. The body shape:
    /// <code>
    /// if (__this is RegExp.prototype) return "<defaultForProto>";
    /// if (!(__this is $RegExp rx)) throw new TypeError("…");
    /// return &lt;bodyEmitter&gt;(rx);
    /// </code>
    /// </summary>
    private MethodBuilder EmitProtoAccessor(TypeBuilder typeBuilder, EmittedRuntime runtime, string name, System.Func<bool> _stringReturn, System.Action<ILGenerator, LocalBuilder> bodyEmitter)
    {
        var helper = typeBuilder.DefineMethod(
            name,
            MethodAttributes.Public | MethodAttributes.Static,
            _types.Object,
            [_types.Object]);
        try { helper.DefineParameter(1, ParameterAttributes.None, "__this"); }
        catch { /* already named — ignore */ }

        var il = helper.GetILGenerator();
        EmitProtoAccessorPrologue(il, runtime, name, out var rxLocal, out var protoFallbackLabel);

        // $RegExp path
        bodyEmitter(il, rxLocal);
        il.Emit(OpCodes.Ret);

        // RegExp.prototype path — spec defaults are "(?:)" for source, "" for flags.
        il.MarkLabel(protoFallbackLabel);
        il.Emit(OpCodes.Ldstr, name == "ProtoGetSource" ? "(?:)" : "");
        il.Emit(OpCodes.Ret);
        return helper;
    }

    /// <summary>
    /// Emits a bool-returning proto accessor (global, ignoreCase, multiline,
    /// sticky, unicode, dotAll, hasIndices, unicodeSets). For RegExp instances
    /// it parses the flags string for <paramref name="flagChar"/>; the
    /// RegExp.prototype fallback returns <c>false</c> per spec.
    /// </summary>
    private MethodBuilder EmitProtoBoolAccessor(TypeBuilder typeBuilder, EmittedRuntime runtime, string name, char flagChar)
    {
        var helper = typeBuilder.DefineMethod(
            name,
            MethodAttributes.Public | MethodAttributes.Static,
            _types.Object,
            [_types.Object]);
        try { helper.DefineParameter(1, ParameterAttributes.None, "__this"); }
        catch { /* already named — ignore */ }

        var il = helper.GetILGenerator();
        EmitProtoAccessorPrologue(il, runtime, name, out var rxLocal, out var protoFallbackLabel);

        // $RegExp path: return rx._flags.Contains(flagChar) boxed.
        il.Emit(OpCodes.Ldloc, rxLocal);
        il.Emit(OpCodes.Callvirt, runtime.TSRegExpFlagsGetter);
        il.Emit(OpCodes.Ldc_I4, (int)flagChar);
        il.Emit(OpCodes.Callvirt, _types.GetMethod(_types.String, "Contains", _types.Char));
        il.Emit(OpCodes.Box, _types.Boolean);
        il.Emit(OpCodes.Ret);

        // Prototype fallback: false.
        il.MarkLabel(protoFallbackLabel);
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Box, _types.Boolean);
        il.Emit(OpCodes.Ret);
        return helper;
    }

    /// <summary>
    /// Shared prologue for every proto accessor:
    /// 1. Throw TypeError if __this is null/undefined/primitive/symbol.
    /// 2. If __this is RegExp.prototype itself → jump to protoFallbackLabel.
    /// 3. If __this is a $RegExp → store as rxLocal and fall through.
    /// 4. Anything else (some other Object) → throw TypeError.
    /// </summary>
    private void EmitProtoAccessorPrologue(ILGenerator il, EmittedRuntime runtime, string name, out LocalBuilder rxLocal, out Label protoFallbackLabel)
    {
        rxLocal = il.DeclareLocal(runtime.TSRegExpType);
        protoFallbackLabel = il.DefineLabel();
        var throwLabel = il.DefineLabel();
        var checkRegExpLabel = il.DefineLabel();

        // Primitives → throw.
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Brfalse, throwLabel);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Isinst, runtime.UndefinedType);
        il.Emit(OpCodes.Brtrue, throwLabel);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Isinst, _types.String);
        il.Emit(OpCodes.Brtrue, throwLabel);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Isinst, _types.Boolean);
        il.Emit(OpCodes.Brtrue, throwLabel);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Isinst, _types.Double);
        il.Emit(OpCodes.Brtrue, throwLabel);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Isinst, _types.Int32);
        il.Emit(OpCodes.Brtrue, throwLabel);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Isinst, runtime.TSSymbolType);
        il.Emit(OpCodes.Brtrue, throwLabel);

        // If arg is RegExp.prototype itself → spec fallback.
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldsfld, runtime.RegExpPrototypeField);
        il.Emit(OpCodes.Ceq);
        il.Emit(OpCodes.Brtrue, protoFallbackLabel);

        // If $RegExp → store and fall through to body.
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Isinst, runtime.TSRegExpType);
        il.Emit(OpCodes.Stloc, rxLocal);
        il.Emit(OpCodes.Ldloc, rxLocal);
        il.Emit(OpCodes.Brtrue, checkRegExpLabel);
        // Any other Object → TypeError.
        il.Emit(OpCodes.Br, throwLabel);

        il.MarkLabel(throwLabel);
        il.Emit(OpCodes.Ldstr, "RegExp.prototype accessor called on non-RegExp");
        il.Emit(OpCodes.Newobj, runtime.TSTypeErrorCtor);
        il.Emit(OpCodes.Call, runtime.CreateException);
        il.Emit(OpCodes.Throw);

        il.MarkLabel(checkRegExpLabel);
    }

    private void EmitArgToJsString(ILGenerator il, EmittedRuntime runtime, int argIndex, LocalBuilder local)
    {
        var nullLabel = il.DefineLabel();
        var doneLabel = il.DefineLabel();

        il.Emit(OpCodes.Ldarg, argIndex);
        il.Emit(OpCodes.Brfalse, nullLabel);

        // Route through $Runtime.Stringify so user-installed toString
        // (and dict literals' Number/Array/etc. cases) coerce per spec
        // instead of returning the C# Object.ToString fallback. Forward-
        // declared in DefineRuntimeClassPhase1 so we can call it here.
        il.Emit(OpCodes.Ldarg, argIndex);
        il.Emit(OpCodes.Call, runtime.Stringify);
        il.Emit(OpCodes.Stloc, local);
        il.Emit(OpCodes.Br, doneLabel);

        il.MarkLabel(nullLabel);
        il.Emit(OpCodes.Ldstr, "undefined");
        il.Emit(OpCodes.Stloc, local);

        il.MarkLabel(doneLabel);
    }

    /// <summary>
    /// ECMA-262 §22.2.5 step 2: each well-known-symbol-keyed RegExp protocol
    /// method requires the receiver (passed as <paramref name="argIndex"/>)
    /// to be an Object. Null, undefined, booleans, numbers, strings, and
    /// symbols throw TypeError before any further work. The previous
    /// implementations let a `Castclass` to <c>$RegExp</c> raise an
    /// InvalidCastException, which surfaced as a generic Error and failed
    /// test262's <c>assert.throws(TypeError, ...)</c> bucket.
    /// </summary>
    /// <remarks>
    /// $RegExp emits before $Runtime, so <c>runtime.CreateException</c> /
    /// <c>runtime.Stringify</c> aren't yet bound when this helper runs —
    /// we inline the wrap (System.Exception + <c>Data["__tsValue"]</c>)
    /// here. Matches what <c>$Runtime.CreateException</c> does on the
    /// runtime side.
    /// </remarks>
    private void EmitRequireObjectArg(ILGenerator il, EmittedRuntime runtime, MethodBuilder method, int argIndex, string siteName)
    {
        if (argIndex == 0)
        {
            try { method.DefineParameter(1, ParameterAttributes.None, "__this"); }
            catch { /* already named — ignore */ }
        }

        var okLabel = il.DefineLabel();
        var throwLabel = il.DefineLabel();

        // null → throw
        il.Emit(OpCodes.Ldarg, argIndex);
        il.Emit(OpCodes.Brfalse, throwLabel);

        // $Undefined → throw
        il.Emit(OpCodes.Ldarg, argIndex);
        il.Emit(OpCodes.Isinst, runtime.UndefinedType);
        il.Emit(OpCodes.Brtrue, throwLabel);

        // string → throw
        il.Emit(OpCodes.Ldarg, argIndex);
        il.Emit(OpCodes.Isinst, _types.String);
        il.Emit(OpCodes.Brtrue, throwLabel);

        // boolean (boxed bool) → throw
        il.Emit(OpCodes.Ldarg, argIndex);
        il.Emit(OpCodes.Isinst, _types.Boolean);
        il.Emit(OpCodes.Brtrue, throwLabel);

        // number (boxed double) → throw
        il.Emit(OpCodes.Ldarg, argIndex);
        il.Emit(OpCodes.Isinst, _types.Double);
        il.Emit(OpCodes.Brtrue, throwLabel);

        // boxed int (rare in compiled output but harmless to check)
        il.Emit(OpCodes.Ldarg, argIndex);
        il.Emit(OpCodes.Isinst, _types.Int32);
        il.Emit(OpCodes.Brtrue, throwLabel);

        // $TSSymbol is a primitive per ECMA-262 (Type "Symbol", not Object).
        // Test262's this-val-non-obj.js lists Symbol.match itself as a
        // "non-object" value and expects TypeError when passed as `this`.
        il.Emit(OpCodes.Ldarg, argIndex);
        il.Emit(OpCodes.Isinst, runtime.TSSymbolType);
        il.Emit(OpCodes.Brtrue, throwLabel);

        il.Emit(OpCodes.Br, okLabel);

        il.MarkLabel(throwLabel);
        // Build a $TypeError, wrap it via $Runtime.CreateException — both
        // are forward-declared by DefineRuntimeClassPhase1 so we can call
        // them from $RegExp's helpers despite emitting before $Runtime's
        // body is filled in.
        il.Emit(OpCodes.Ldstr, siteName + " called on non-object");
        il.Emit(OpCodes.Newobj, runtime.TSTypeErrorCtor);
        il.Emit(OpCodes.Call, runtime.CreateException);
        il.Emit(OpCodes.Throw);

        il.MarkLabel(okLabel);
    }
}
