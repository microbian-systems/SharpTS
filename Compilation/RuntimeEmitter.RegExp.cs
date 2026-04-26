using System.Reflection;
using System.Reflection.Emit;
using System.Text.RegularExpressions;

namespace SharpTS.Compilation;

/// <summary>
/// RegExp-related runtime emission methods.
/// These are $Runtime wrapper methods that delegate to the emitted $RegExp type.
/// </summary>
public partial class RuntimeEmitter
{
    private void EmitRegExpMethods(TypeBuilder typeBuilder, EmittedRuntime runtime)
    {
        EmitCreateRegExp(typeBuilder, runtime);
        EmitCreateRegExpWithFlags(typeBuilder, runtime);
        EmitRegExpTest(typeBuilder, runtime);
        EmitRegExpExec(typeBuilder, runtime);
        EmitRegExpToString(typeBuilder, runtime);
        EmitRegExpGetSource(typeBuilder, runtime);
        EmitRegExpGetFlags(typeBuilder, runtime);
        EmitRegExpGetGlobal(typeBuilder, runtime);
        EmitRegExpGetIgnoreCase(typeBuilder, runtime);
        EmitRegExpGetMultiline(typeBuilder, runtime);
        EmitRegExpGetLastIndex(typeBuilder, runtime);
        EmitRegExpSetLastIndex(typeBuilder, runtime);
        EmitStringMatchRegExp(typeBuilder, runtime);
        EmitStringMatchAllRegExp(typeBuilder, runtime);
        EmitStringReplaceRegExp(typeBuilder, runtime);
        EmitStringReplaceAllRegExp(typeBuilder, runtime);
        EmitStringSearchRegExp(typeBuilder, runtime);
        EmitStringSplitRegExp(typeBuilder, runtime);
    }

    /// <summary>
    /// Runtime helper for String.prototype.replaceAll with optional RegExp pattern.
    /// Compiled signature: (string str, object pattern, string replacement) -> string.
    /// Dispatches to $RegExp.Replace for global-regex patterns, otherwise falls
    /// back to C#'s String.Replace (full-string all-occurrences semantics).
    /// </summary>
    private void EmitStringReplaceAllRegExp(TypeBuilder typeBuilder, EmittedRuntime runtime)
    {
        var method = typeBuilder.DefineMethod(
            "StringReplaceAllRegExp",
            MethodAttributes.Public | MethodAttributes.Static,
            _types.String,
            [_types.String, _types.Object, _types.String]
        );
        runtime.StringReplaceAllRegExp = method;

        var il = method.GetILGenerator();
        var regexpLocal = il.DeclareLocal(runtime.TSRegExpType);
        var searchLocal = il.DeclareLocal(_types.String);
        var stringPathLabel = il.DefineLabel();
        var patternNullLabel = il.DefineLabel();
        var afterSearchLabel = il.DefineLabel();
        var returnOriginalLabel = il.DefineLabel();

        // var regexp = pattern as $RegExp
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Isinst, runtime.TSRegExpType);
        il.Emit(OpCodes.Stloc, regexpLocal);
        il.Emit(OpCodes.Ldloc, regexpLocal);
        il.Emit(OpCodes.Brfalse, stringPathLabel);

        // return regexp.Replace(str, replacement) — Replace already walks all
        // matches for global regexes (spec requires the RegExp be global, but
        // enforcement is in the interpreter; the IL helper accepts any regex).
        il.Emit(OpCodes.Ldloc, regexpLocal);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldarg_2);
        il.Emit(OpCodes.Call, _tsRegExpReplaceMethod);
        il.Emit(OpCodes.Ret);

        // String pattern path: search = pattern?.ToString() ?? ""
        il.MarkLabel(stringPathLabel);
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Brfalse, patternNullLabel);
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Callvirt, _types.Object.GetMethod("ToString")!);
        il.Emit(OpCodes.Br, afterSearchLabel);
        il.MarkLabel(patternNullLabel);
        il.Emit(OpCodes.Ldstr, "");
        il.MarkLabel(afterSearchLabel);
        il.Emit(OpCodes.Stloc, searchLocal);

        // if (search.Length == 0) return str
        il.Emit(OpCodes.Ldloc, searchLocal);
        il.Emit(OpCodes.Callvirt, _types.String.GetProperty("Length")!.GetGetMethod()!);
        il.Emit(OpCodes.Brfalse, returnOriginalLabel);

        // return str.Replace(search, replacement)
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldloc, searchLocal);
        il.Emit(OpCodes.Ldarg_2);
        il.Emit(OpCodes.Callvirt, _types.String.GetMethod("Replace", [_types.String, _types.String])!);
        il.Emit(OpCodes.Ret);

        il.MarkLabel(returnOriginalLabel);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ret);
    }

    private void EmitCreateRegExp(TypeBuilder typeBuilder, EmittedRuntime runtime)
    {
        var method = typeBuilder.DefineMethod(
            "CreateRegExp",
            MethodAttributes.Public | MethodAttributes.Static,
            _types.Object,
            [_types.String]
        );
        runtime.CreateRegExp = method;

        var il = method.GetILGenerator();
        // return new $RegExp(pattern)
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Newobj, runtime.TSRegExpCtorPattern);
        il.Emit(OpCodes.Ret);
    }

    private void EmitCreateRegExpWithFlags(TypeBuilder typeBuilder, EmittedRuntime runtime)
    {
        var method = typeBuilder.DefineMethod(
            "CreateRegExpWithFlags",
            MethodAttributes.Public | MethodAttributes.Static,
            _types.Object,
            [_types.String, _types.String]
        );
        runtime.CreateRegExpWithFlags = method;

        var il = method.GetILGenerator();
        // return new $RegExp(pattern, flags)
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Newobj, runtime.TSRegExpCtorPatternFlags);
        il.Emit(OpCodes.Ret);
    }

    private void EmitRegExpTest(TypeBuilder typeBuilder, EmittedRuntime runtime)
    {
        var method = typeBuilder.DefineMethod(
            "RegExpTest",
            MethodAttributes.Public | MethodAttributes.Static,
            _types.Boolean,
            [_types.Object, _types.String]
        );
        runtime.RegExpTest = method;

        var il = method.GetILGenerator();
        var notRegExpLabel = il.DefineLabel();
        var regexpLocal = il.DeclareLocal(runtime.TSRegExpType);

        // var regexp = regex as $RegExp
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Isinst, runtime.TSRegExpType);
        il.Emit(OpCodes.Stloc, regexpLocal);

        // if (regexp == null) return false
        il.Emit(OpCodes.Ldloc, regexpLocal);
        il.Emit(OpCodes.Brfalse, notRegExpLabel);

        // return regexp.Test(input)
        il.Emit(OpCodes.Ldloc, regexpLocal);
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Callvirt, runtime.TSRegExpTestMethod);
        il.Emit(OpCodes.Ret);

        il.MarkLabel(notRegExpLabel);
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Ret);
    }

    private void EmitRegExpExec(TypeBuilder typeBuilder, EmittedRuntime runtime)
    {
        var method = typeBuilder.DefineMethod(
            "RegExpExec",
            MethodAttributes.Public | MethodAttributes.Static,
            _types.Object,
            [_types.Object, _types.String]
        );
        runtime.RegExpExec = method;

        var il = method.GetILGenerator();
        var notRegExpLabel = il.DefineLabel();
        var regexpLocal = il.DeclareLocal(runtime.TSRegExpType);

        // var regexp = regex as $RegExp
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Isinst, runtime.TSRegExpType);
        il.Emit(OpCodes.Stloc, regexpLocal);

        // if (regexp == null) return null
        il.Emit(OpCodes.Ldloc, regexpLocal);
        il.Emit(OpCodes.Brfalse, notRegExpLabel);

        // return regexp.Exec(input)
        il.Emit(OpCodes.Ldloc, regexpLocal);
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Callvirt, runtime.TSRegExpExecMethod);
        il.Emit(OpCodes.Ret);

        il.MarkLabel(notRegExpLabel);
        il.Emit(OpCodes.Ldnull);
        il.Emit(OpCodes.Ret);
    }

    private void EmitRegExpToString(TypeBuilder typeBuilder, EmittedRuntime runtime)
    {
        var method = typeBuilder.DefineMethod(
            "RegExpToString",
            MethodAttributes.Public | MethodAttributes.Static,
            _types.String,
            [_types.Object]
        );
        runtime.RegExpToString = method;

        var il = method.GetILGenerator();
        var notRegExpLabel = il.DefineLabel();
        var regexpLocal = il.DeclareLocal(runtime.TSRegExpType);

        // var regexp = regex as $RegExp
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Isinst, runtime.TSRegExpType);
        il.Emit(OpCodes.Stloc, regexpLocal);

        // if (regexp == null) return "/(?:)/"
        il.Emit(OpCodes.Ldloc, regexpLocal);
        il.Emit(OpCodes.Brfalse, notRegExpLabel);

        // return regexp.ToString()
        il.Emit(OpCodes.Ldloc, regexpLocal);
        il.Emit(OpCodes.Callvirt, runtime.TSRegExpToStringMethod);
        il.Emit(OpCodes.Ret);

        il.MarkLabel(notRegExpLabel);
        il.Emit(OpCodes.Ldstr, "/(?:)/");
        il.Emit(OpCodes.Ret);
    }

    private void EmitRegExpGetSource(TypeBuilder typeBuilder, EmittedRuntime runtime)
    {
        var method = typeBuilder.DefineMethod(
            "RegExpGetSource",
            MethodAttributes.Public | MethodAttributes.Static,
            _types.String,
            [_types.Object]
        );
        runtime.RegExpGetSource = method;

        var il = method.GetILGenerator();
        var notRegExpLabel = il.DefineLabel();
        var regexpLocal = il.DeclareLocal(runtime.TSRegExpType);

        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Isinst, runtime.TSRegExpType);
        il.Emit(OpCodes.Stloc, regexpLocal);

        il.Emit(OpCodes.Ldloc, regexpLocal);
        il.Emit(OpCodes.Brfalse, notRegExpLabel);

        il.Emit(OpCodes.Ldloc, regexpLocal);
        il.Emit(OpCodes.Callvirt, runtime.TSRegExpSourceGetter);
        il.Emit(OpCodes.Ret);

        il.MarkLabel(notRegExpLabel);
        il.Emit(OpCodes.Ldstr, "");
        il.Emit(OpCodes.Ret);
    }

    private void EmitRegExpGetFlags(TypeBuilder typeBuilder, EmittedRuntime runtime)
    {
        var method = typeBuilder.DefineMethod(
            "RegExpGetFlags",
            MethodAttributes.Public | MethodAttributes.Static,
            _types.String,
            [_types.Object]
        );
        runtime.RegExpGetFlags = method;

        var il = method.GetILGenerator();
        var notRegExpLabel = il.DefineLabel();
        var regexpLocal = il.DeclareLocal(runtime.TSRegExpType);

        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Isinst, runtime.TSRegExpType);
        il.Emit(OpCodes.Stloc, regexpLocal);

        il.Emit(OpCodes.Ldloc, regexpLocal);
        il.Emit(OpCodes.Brfalse, notRegExpLabel);

        il.Emit(OpCodes.Ldloc, regexpLocal);
        il.Emit(OpCodes.Callvirt, runtime.TSRegExpFlagsGetter);
        il.Emit(OpCodes.Ret);

        il.MarkLabel(notRegExpLabel);
        il.Emit(OpCodes.Ldstr, "");
        il.Emit(OpCodes.Ret);
    }

    private void EmitRegExpGetGlobal(TypeBuilder typeBuilder, EmittedRuntime runtime)
    {
        var method = typeBuilder.DefineMethod(
            "RegExpGetGlobal",
            MethodAttributes.Public | MethodAttributes.Static,
            _types.Boolean,
            [_types.Object]
        );
        runtime.RegExpGetGlobal = method;

        var il = method.GetILGenerator();
        var notRegExpLabel = il.DefineLabel();
        var regexpLocal = il.DeclareLocal(runtime.TSRegExpType);

        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Isinst, runtime.TSRegExpType);
        il.Emit(OpCodes.Stloc, regexpLocal);

        il.Emit(OpCodes.Ldloc, regexpLocal);
        il.Emit(OpCodes.Brfalse, notRegExpLabel);

        il.Emit(OpCodes.Ldloc, regexpLocal);
        il.Emit(OpCodes.Callvirt, runtime.TSRegExpGlobalGetter);
        il.Emit(OpCodes.Ret);

        il.MarkLabel(notRegExpLabel);
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Ret);
    }

    private void EmitRegExpGetIgnoreCase(TypeBuilder typeBuilder, EmittedRuntime runtime)
    {
        var method = typeBuilder.DefineMethod(
            "RegExpGetIgnoreCase",
            MethodAttributes.Public | MethodAttributes.Static,
            _types.Boolean,
            [_types.Object]
        );
        runtime.RegExpGetIgnoreCase = method;

        var il = method.GetILGenerator();
        var notRegExpLabel = il.DefineLabel();
        var regexpLocal = il.DeclareLocal(runtime.TSRegExpType);

        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Isinst, runtime.TSRegExpType);
        il.Emit(OpCodes.Stloc, regexpLocal);

        il.Emit(OpCodes.Ldloc, regexpLocal);
        il.Emit(OpCodes.Brfalse, notRegExpLabel);

        il.Emit(OpCodes.Ldloc, regexpLocal);
        il.Emit(OpCodes.Callvirt, runtime.TSRegExpIgnoreCaseGetter);
        il.Emit(OpCodes.Ret);

        il.MarkLabel(notRegExpLabel);
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Ret);
    }

    private void EmitRegExpGetMultiline(TypeBuilder typeBuilder, EmittedRuntime runtime)
    {
        var method = typeBuilder.DefineMethod(
            "RegExpGetMultiline",
            MethodAttributes.Public | MethodAttributes.Static,
            _types.Boolean,
            [_types.Object]
        );
        runtime.RegExpGetMultiline = method;

        var il = method.GetILGenerator();
        var notRegExpLabel = il.DefineLabel();
        var regexpLocal = il.DeclareLocal(runtime.TSRegExpType);

        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Isinst, runtime.TSRegExpType);
        il.Emit(OpCodes.Stloc, regexpLocal);

        il.Emit(OpCodes.Ldloc, regexpLocal);
        il.Emit(OpCodes.Brfalse, notRegExpLabel);

        il.Emit(OpCodes.Ldloc, regexpLocal);
        il.Emit(OpCodes.Callvirt, runtime.TSRegExpMultilineGetter);
        il.Emit(OpCodes.Ret);

        il.MarkLabel(notRegExpLabel);
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Ret);
    }

    private void EmitRegExpGetLastIndex(TypeBuilder typeBuilder, EmittedRuntime runtime)
    {
        var method = typeBuilder.DefineMethod(
            "RegExpGetLastIndex",
            MethodAttributes.Public | MethodAttributes.Static,
            _types.Double,
            [_types.Object]
        );
        runtime.RegExpGetLastIndex = method;

        var il = method.GetILGenerator();
        var notRegExpLabel = il.DefineLabel();
        var regexpLocal = il.DeclareLocal(runtime.TSRegExpType);

        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Isinst, runtime.TSRegExpType);
        il.Emit(OpCodes.Stloc, regexpLocal);

        il.Emit(OpCodes.Ldloc, regexpLocal);
        il.Emit(OpCodes.Brfalse, notRegExpLabel);

        il.Emit(OpCodes.Ldloc, regexpLocal);
        il.Emit(OpCodes.Callvirt, runtime.TSRegExpLastIndexGetter);
        il.Emit(OpCodes.Conv_R8);
        il.Emit(OpCodes.Ret);

        il.MarkLabel(notRegExpLabel);
        il.Emit(OpCodes.Ldc_R8, 0.0);
        il.Emit(OpCodes.Ret);
    }

    private void EmitRegExpSetLastIndex(TypeBuilder typeBuilder, EmittedRuntime runtime)
    {
        var method = typeBuilder.DefineMethod(
            "RegExpSetLastIndex",
            MethodAttributes.Public | MethodAttributes.Static,
            _types.Void,
            [_types.Object, _types.Double]
        );
        runtime.RegExpSetLastIndex = method;

        var il = method.GetILGenerator();
        var notRegExpLabel = il.DefineLabel();
        var regexpLocal = il.DeclareLocal(runtime.TSRegExpType);

        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Isinst, runtime.TSRegExpType);
        il.Emit(OpCodes.Stloc, regexpLocal);

        il.Emit(OpCodes.Ldloc, regexpLocal);
        il.Emit(OpCodes.Brfalse, notRegExpLabel);

        il.Emit(OpCodes.Ldloc, regexpLocal);
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Conv_I4);
        il.Emit(OpCodes.Callvirt, runtime.TSRegExpLastIndexSetter);

        il.MarkLabel(notRegExpLabel);
        il.Emit(OpCodes.Ret);
    }

    private void EmitStringMatchRegExp(TypeBuilder typeBuilder, EmittedRuntime runtime)
    {
        // StringMatch(string str, object? pattern) -> object?
        // If pattern is $RegExp with global flag, return all matches as array
        // If pattern is $RegExp without global flag, return exec result
        // If pattern is string, return simple string match
        var method = typeBuilder.DefineMethod(
            "StringMatchRegExp",
            MethodAttributes.Public | MethodAttributes.Static,
            _types.Object,
            [_types.String, _types.Object]
        );
        runtime.StringMatchRegExp = method;

        var il = method.GetILGenerator();
        var regexpLocal = il.DeclareLocal(runtime.TSRegExpType);
        var isStringPatternLabel = il.DefineLabel();
        var globalMatchLabel = il.DefineLabel();
        var searchLocal = il.DeclareLocal(_types.String);
        var idxLocal = il.DeclareLocal(_types.Int32);
        var notFoundLabel = il.DefineLabel();
        var matchesLocal = il.DeclareLocal(typeof(List<string>));
        var elementsLocal = il.DeclareLocal(_types.ListOfObject);
        var iLocal = il.DeclareLocal(_types.Int32);
        var loopStartLabel = il.DefineLabel();
        var loopEndLabel = il.DefineLabel();

        // var regexp = pattern as $RegExp
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Isinst, runtime.TSRegExpType);
        il.Emit(OpCodes.Stloc, regexpLocal);

        // if (regexp != null)
        il.Emit(OpCodes.Ldloc, regexpLocal);
        il.Emit(OpCodes.Brfalse, isStringPatternLabel);

        // if (regexp.Global)
        il.Emit(OpCodes.Ldloc, regexpLocal);
        il.Emit(OpCodes.Callvirt, runtime.TSRegExpGlobalGetter);
        il.Emit(OpCodes.Brtrue, globalMatchLabel);

        // Non-global: return regexp.Exec(str)
        il.Emit(OpCodes.Ldloc, regexpLocal);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Callvirt, runtime.TSRegExpExecMethod);
        il.Emit(OpCodes.Ret);

        // Global match: get all matches and return as array
        il.MarkLabel(globalMatchLabel);

        // var matches = regexp.MatchAll(str)
        il.Emit(OpCodes.Ldloc, regexpLocal);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Call, _tsRegExpMatchAllMethod);
        il.Emit(OpCodes.Stloc, matchesLocal);

        // if (matches.Count == 0) return null
        var hasMatchesLabel = il.DefineLabel();
        il.Emit(OpCodes.Ldloc, matchesLocal);
        il.Emit(OpCodes.Callvirt, typeof(List<string>).GetProperty("Count")!.GetGetMethod()!);
        il.Emit(OpCodes.Brtrue, hasMatchesLabel);
        il.Emit(OpCodes.Ldnull);
        il.Emit(OpCodes.Ret);

        il.MarkLabel(hasMatchesLabel);

        // Convert List<string> to $Array
        // var elements = new List<object?>()
        il.Emit(OpCodes.Newobj, _types.ListOfObject.GetConstructor(Type.EmptyTypes)!);
        il.Emit(OpCodes.Stloc, elementsLocal);

        // for (int i = 0; i < matches.Count; i++) elements.Add(matches[i])
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Stloc, iLocal);

        il.MarkLabel(loopStartLabel);
        il.Emit(OpCodes.Ldloc, iLocal);
        il.Emit(OpCodes.Ldloc, matchesLocal);
        il.Emit(OpCodes.Callvirt, typeof(List<string>).GetProperty("Count")!.GetGetMethod()!);
        il.Emit(OpCodes.Bge, loopEndLabel);

        il.Emit(OpCodes.Ldloc, elementsLocal);
        il.Emit(OpCodes.Ldloc, matchesLocal);
        il.Emit(OpCodes.Ldloc, iLocal);
        il.Emit(OpCodes.Callvirt, typeof(List<string>).GetMethod("get_Item", [_types.Int32])!);
        il.Emit(OpCodes.Callvirt, _types.ListOfObject.GetMethod("Add", [_types.Object])!);

        il.Emit(OpCodes.Ldloc, iLocal);
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Add);
        il.Emit(OpCodes.Stloc, iLocal);
        il.Emit(OpCodes.Br, loopStartLabel);

        il.MarkLabel(loopEndLabel);

        // return new $Array(elements)
        il.Emit(OpCodes.Ldloc, elementsLocal);
        il.Emit(OpCodes.Newobj, runtime.TSArrayCtor);
        il.Emit(OpCodes.Ret);

        // String pattern fallback
        il.MarkLabel(isStringPatternLabel);

        // ECMA-262 ToString protocol — handles objects with custom toString.
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Call, runtime.ToJsString);
        il.Emit(OpCodes.Stloc, searchLocal);

        // var idx = str.IndexOf(search)
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldloc, searchLocal);
        il.Emit(OpCodes.Callvirt, _types.String.GetMethod("IndexOf", [_types.String])!);
        il.Emit(OpCodes.Stloc, idxLocal);

        // if (idx < 0) return null
        il.Emit(OpCodes.Ldloc, idxLocal);
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Blt, notFoundLabel);

        // return new $Array([search])
        il.Emit(OpCodes.Newobj, _types.ListOfObject.GetConstructor(Type.EmptyTypes)!);
        il.Emit(OpCodes.Dup);
        il.Emit(OpCodes.Ldloc, searchLocal);
        il.Emit(OpCodes.Callvirt, _types.ListOfObject.GetMethod("Add", [_types.Object])!);
        il.Emit(OpCodes.Newobj, runtime.TSArrayCtor);
        il.Emit(OpCodes.Ret);

        il.MarkLabel(notFoundLabel);
        il.Emit(OpCodes.Ldnull);
        il.Emit(OpCodes.Ret);
    }

    private void EmitStringMatchAllRegExp(TypeBuilder typeBuilder, EmittedRuntime runtime)
    {
        // StringMatchAll(string str, object? pattern) -> object?
        // Builds $Object match results directly, accessing $RegExp._regex field.
        // Uses index-based iteration (MatchCollection[i]) to avoid try/finally complexity.
        var method = typeBuilder.DefineMethod(
            "StringMatchAllRegExp",
            MethodAttributes.Public | MethodAttributes.Static,
            _types.Object,
            [_types.String, _types.Object]
        );
        runtime.StringMatchAllRegExp = method;

        var il = method.GetILGenerator();
        var regexpLocal = il.DeclareLocal(runtime.TSRegExpType);
        var isStringPatternLabel = il.DefineLabel();
        var regexLocal = il.DeclareLocal(typeof(Regex));

        // var regexp = pattern as $RegExp
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Isinst, runtime.TSRegExpType);
        il.Emit(OpCodes.Stloc, regexpLocal);

        // if (regexp == null) goto stringPattern
        il.Emit(OpCodes.Ldloc, regexpLocal);
        il.Emit(OpCodes.Brfalse, isStringPatternLabel);

        // if (!regexp.Global) throw TypeError
        var isGlobalLabel = il.DefineLabel();
        il.Emit(OpCodes.Ldloc, regexpLocal);
        il.Emit(OpCodes.Callvirt, runtime.TSRegExpGlobalGetter);
        il.Emit(OpCodes.Brtrue, isGlobalLabel);
        il.Emit(OpCodes.Ldstr, "String.prototype.matchAll called with a non-global RegExp argument");
        il.Emit(OpCodes.Newobj, runtime.TSTypeErrorCtor);
        il.Emit(OpCodes.Call, runtime.CreateException);
        il.Emit(OpCodes.Throw);

        il.MarkLabel(isGlobalLabel);

        // regexLocal = regexp._regex
        il.Emit(OpCodes.Ldloc, regexpLocal);
        il.Emit(OpCodes.Ldfld, _tsRegExpRegexField);
        il.Emit(OpCodes.Stloc, regexLocal);

        var buildResultLabel = il.DefineLabel();
        il.Emit(OpCodes.Br, buildResultLabel);

        // String pattern fallback
        il.MarkLabel(isStringPatternLabel);

        var escapedLocal = il.DeclareLocal(_types.String);
        // ECMA-262 ToString protocol — handles objects with custom toString.
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Call, runtime.ToJsString);
        il.Emit(OpCodes.Call, typeof(Regex).GetMethod("Escape", [_types.String])!);
        il.Emit(OpCodes.Stloc, escapedLocal);

        il.Emit(OpCodes.Ldloc, escapedLocal);
        il.Emit(OpCodes.Newobj, typeof(Regex).GetConstructor([_types.String])!);
        il.Emit(OpCodes.Stloc, regexLocal);

        // Common path: use index-based iteration over MatchCollection
        il.MarkLabel(buildResultLabel);

        var resultLocal = il.DeclareLocal(_types.ListOfObject);
        var matchCollLocal = il.DeclareLocal(typeof(MatchCollection));
        var matchLocal = il.DeclareLocal(typeof(Match));
        var fieldsLocal = il.DeclareLocal(_types.DictionaryStringObject);
        var iLocal = il.DeclareLocal(_types.Int32);
        var countLocal = il.DeclareLocal(_types.Int32);
        var groupIndexLocal = il.DeclareLocal(_types.Int32);
        var groupLocal = il.DeclareLocal(typeof(Group));

        var loopStartLabel = il.DefineLabel();
        var loopEndLabel = il.DefineLabel();
        var groupLoopStartLabel = il.DefineLabel();
        var groupLoopEndLabel = il.DefineLabel();

        // var result = new List<object?>()
        il.Emit(OpCodes.Newobj, _types.ListOfObject.GetConstructor(Type.EmptyTypes)!);
        il.Emit(OpCodes.Stloc, resultLocal);

        // var matchColl = regex.Matches(str)
        il.Emit(OpCodes.Ldloc, regexLocal);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Callvirt, typeof(Regex).GetMethod("Matches", [_types.String])!);
        il.Emit(OpCodes.Stloc, matchCollLocal);

        // var count = matchColl.Count
        il.Emit(OpCodes.Ldloc, matchCollLocal);
        il.Emit(OpCodes.Callvirt, typeof(MatchCollection).GetProperty("Count")!.GetGetMethod()!);
        il.Emit(OpCodes.Stloc, countLocal);

        // for (int i = 0; i < count; i++)
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Stloc, iLocal);

        il.MarkLabel(loopStartLabel);
        il.Emit(OpCodes.Ldloc, iLocal);
        il.Emit(OpCodes.Ldloc, countLocal);
        il.Emit(OpCodes.Bge, loopEndLabel);

        // var match = matchColl[i]
        il.Emit(OpCodes.Ldloc, matchCollLocal);
        il.Emit(OpCodes.Ldloc, iLocal);
        il.Emit(OpCodes.Callvirt, typeof(MatchCollection).GetMethod("get_Item", [_types.Int32])!);
        il.Emit(OpCodes.Stloc, matchLocal);

        // var fields = new Dictionary<string, object?>()
        var dictSetItem = _types.DictionaryStringObject.GetMethod("set_Item", [_types.String, _types.Object])!;
        il.Emit(OpCodes.Newobj, _types.GetConstructor(_types.DictionaryStringObject));
        il.Emit(OpCodes.Stloc, fieldsLocal);

        // fields["0"] = match.Value
        il.Emit(OpCodes.Ldloc, fieldsLocal);
        il.Emit(OpCodes.Ldstr, "0");
        il.Emit(OpCodes.Ldloc, matchLocal);
        il.Emit(OpCodes.Callvirt, typeof(Capture).GetProperty("Value")!.GetGetMethod()!);
        il.Emit(OpCodes.Callvirt, dictSetItem);

        // fields["index"] = (double)match.Index
        il.Emit(OpCodes.Ldloc, fieldsLocal);
        il.Emit(OpCodes.Ldstr, "index");
        il.Emit(OpCodes.Ldloc, matchLocal);
        il.Emit(OpCodes.Callvirt, typeof(Capture).GetProperty("Index")!.GetGetMethod()!);
        il.Emit(OpCodes.Conv_R8);
        il.Emit(OpCodes.Box, _types.Double);
        il.Emit(OpCodes.Callvirt, dictSetItem);

        // fields["input"] = str (arg_0)
        il.Emit(OpCodes.Ldloc, fieldsLocal);
        il.Emit(OpCodes.Ldstr, "input");
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Callvirt, dictSetItem);

        // fields["groups"] = BuildNamedGroups(match)
        il.Emit(OpCodes.Ldloc, fieldsLocal);
        il.Emit(OpCodes.Ldstr, "groups");
        il.Emit(OpCodes.Ldloc, matchLocal);
        il.Emit(OpCodes.Call, runtime.BuildNamedGroups);
        il.Emit(OpCodes.Callvirt, dictSetItem);

        // for (int gi = 1; gi < match.Groups.Count; gi++)
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Stloc, groupIndexLocal);

        il.MarkLabel(groupLoopStartLabel);
        il.Emit(OpCodes.Ldloc, groupIndexLocal);
        il.Emit(OpCodes.Ldloc, matchLocal);
        il.Emit(OpCodes.Callvirt, typeof(Match).GetProperty("Groups")!.GetGetMethod()!);
        il.Emit(OpCodes.Callvirt, typeof(GroupCollection).GetProperty("Count")!.GetGetMethod()!);
        il.Emit(OpCodes.Bge, groupLoopEndLabel);

        // var group = match.Groups[gi]
        il.Emit(OpCodes.Ldloc, matchLocal);
        il.Emit(OpCodes.Callvirt, typeof(Match).GetProperty("Groups")!.GetGetMethod()!);
        il.Emit(OpCodes.Ldloc, groupIndexLocal);
        il.Emit(OpCodes.Callvirt, typeof(GroupCollection).GetMethod("get_Item", [_types.Int32])!);
        il.Emit(OpCodes.Stloc, groupLocal);

        // fields[gi.ToString()] = group.Success ? group.Value : null
        var groupSuccessLabel = il.DefineLabel();
        var groupDoneLabel = il.DefineLabel();

        il.Emit(OpCodes.Ldloc, fieldsLocal);
        il.Emit(OpCodes.Ldloca, groupIndexLocal);
        il.Emit(OpCodes.Call, _types.Int32.GetMethod("ToString", Type.EmptyTypes)!);

        il.Emit(OpCodes.Ldloc, groupLocal);
        il.Emit(OpCodes.Callvirt, typeof(Group).GetProperty("Success")!.GetGetMethod()!);
        il.Emit(OpCodes.Brfalse, groupSuccessLabel);

        il.Emit(OpCodes.Ldloc, groupLocal);
        il.Emit(OpCodes.Callvirt, typeof(Capture).GetProperty("Value")!.GetGetMethod()!);
        il.Emit(OpCodes.Br, groupDoneLabel);

        il.MarkLabel(groupSuccessLabel);
        il.Emit(OpCodes.Ldnull);

        il.MarkLabel(groupDoneLabel);
        il.Emit(OpCodes.Callvirt, dictSetItem);

        il.Emit(OpCodes.Ldloc, groupIndexLocal);
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Add);
        il.Emit(OpCodes.Stloc, groupIndexLocal);
        il.Emit(OpCodes.Br, groupLoopStartLabel);

        il.MarkLabel(groupLoopEndLabel);

        // result.Add(new $Object(fields))
        il.Emit(OpCodes.Ldloc, resultLocal);
        il.Emit(OpCodes.Ldloc, fieldsLocal);
        il.Emit(OpCodes.Newobj, runtime.TSObjectCtor);
        il.Emit(OpCodes.Callvirt, _types.ListOfObject.GetMethod("Add", [_types.Object])!);

        // i++
        il.Emit(OpCodes.Ldloc, iLocal);
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Add);
        il.Emit(OpCodes.Stloc, iLocal);
        il.Emit(OpCodes.Br, loopStartLabel);

        il.MarkLabel(loopEndLabel);

        // return new $Array(result)
        il.Emit(OpCodes.Ldloc, resultLocal);
        il.Emit(OpCodes.Newobj, runtime.TSArrayCtor);
        il.Emit(OpCodes.Ret);
    }

    private void EmitStringReplaceRegExp(TypeBuilder typeBuilder, EmittedRuntime runtime)
    {
        // StringReplace(string str, object? pattern, string replacement) -> string
        var method = typeBuilder.DefineMethod(
            "StringReplaceRegExp",
            MethodAttributes.Public | MethodAttributes.Static,
            _types.String,
            [_types.String, _types.Object, _types.String]
        );
        runtime.StringReplaceRegExp = method;

        var il = method.GetILGenerator();
        var regexpLocal = il.DeclareLocal(runtime.TSRegExpType);
        var isStringPatternLabel = il.DefineLabel();
        var searchLocal = il.DeclareLocal(_types.String);
        var idxLocal = il.DeclareLocal(_types.Int32);
        var notFoundLabel = il.DefineLabel();

        // var regexp = pattern as $RegExp
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Isinst, runtime.TSRegExpType);
        il.Emit(OpCodes.Stloc, regexpLocal);

        // if (regexp != null)
        il.Emit(OpCodes.Ldloc, regexpLocal);
        il.Emit(OpCodes.Brfalse, isStringPatternLabel);

        // return regexp.Replace(str, replacement)
        il.Emit(OpCodes.Ldloc, regexpLocal);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldarg_2);
        il.Emit(OpCodes.Call, _tsRegExpReplaceMethod);
        il.Emit(OpCodes.Ret);

        // String pattern fallback
        il.MarkLabel(isStringPatternLabel);

        // ECMA-262 ToString protocol — handles objects with custom toString.
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Call, runtime.ToJsString);
        il.Emit(OpCodes.Stloc, searchLocal);

        // var idx = str.IndexOf(search)
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldloc, searchLocal);
        il.Emit(OpCodes.Callvirt, _types.String.GetMethod("IndexOf", [_types.String])!);
        il.Emit(OpCodes.Stloc, idxLocal);

        // if (idx < 0) return str
        il.Emit(OpCodes.Ldloc, idxLocal);
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Blt, notFoundLabel);

        // return str.Substring(0, idx) + replacement + str.Substring(idx + search.Length)
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Ldloc, idxLocal);
        il.Emit(OpCodes.Callvirt, _types.String.GetMethod("Substring", [_types.Int32, _types.Int32])!);

        il.Emit(OpCodes.Ldarg_2); // replacement

        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldloc, idxLocal);
        il.Emit(OpCodes.Ldloc, searchLocal);
        il.Emit(OpCodes.Callvirt, _types.String.GetProperty("Length")!.GetGetMethod()!);
        il.Emit(OpCodes.Add);
        il.Emit(OpCodes.Callvirt, _types.String.GetMethod("Substring", [_types.Int32])!);

        il.Emit(OpCodes.Call, _types.String.GetMethod("Concat", [_types.String, _types.String, _types.String])!);
        il.Emit(OpCodes.Ret);

        il.MarkLabel(notFoundLabel);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ret);
    }

    private void EmitStringSearchRegExp(TypeBuilder typeBuilder, EmittedRuntime runtime)
    {
        // StringSearch(string str, object? pattern) -> double (index or -1)
        var method = typeBuilder.DefineMethod(
            "StringSearchRegExp",
            MethodAttributes.Public | MethodAttributes.Static,
            _types.Double,
            [_types.String, _types.Object]
        );
        runtime.StringSearchRegExp = method;

        var il = method.GetILGenerator();
        var regexpLocal = il.DeclareLocal(runtime.TSRegExpType);
        var isStringPatternLabel = il.DefineLabel();
        var searchLocal = il.DeclareLocal(_types.String);

        // var regexp = pattern as $RegExp
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Isinst, runtime.TSRegExpType);
        il.Emit(OpCodes.Stloc, regexpLocal);

        // if (regexp != null)
        il.Emit(OpCodes.Ldloc, regexpLocal);
        il.Emit(OpCodes.Brfalse, isStringPatternLabel);

        // return (double)regexp.Search(str)
        il.Emit(OpCodes.Ldloc, regexpLocal);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Call, _tsRegExpSearchMethod);
        il.Emit(OpCodes.Conv_R8);
        il.Emit(OpCodes.Ret);

        // String pattern fallback — coerce via ECMA-262 ToString protocol so
        // objects with custom toString return the user-defined string.
        il.MarkLabel(isStringPatternLabel);

        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Call, runtime.ToJsString);
        il.Emit(OpCodes.Stloc, searchLocal);

        // return (double)str.IndexOf(search)
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldloc, searchLocal);
        il.Emit(OpCodes.Callvirt, _types.String.GetMethod("IndexOf", [_types.String])!);
        il.Emit(OpCodes.Conv_R8);
        il.Emit(OpCodes.Ret);
    }

    private void EmitStringSplitRegExp(TypeBuilder typeBuilder, EmittedRuntime runtime)
    {
        // StringSplit(string str, object? separator) -> List<object?>
        var method = typeBuilder.DefineMethod(
            "StringSplitRegExp",
            MethodAttributes.Public | MethodAttributes.Static,
            _types.ListOfObject,
            [_types.String, _types.Object]
        );
        runtime.StringSplitRegExp = method;

        var il = method.GetILGenerator();
        var regexpLocal = il.DeclareLocal(runtime.TSRegExpType);
        var isStringPatternLabel = il.DefineLabel();
        var sepLocal = il.DeclareLocal(_types.String);
        var partsLocal = il.DeclareLocal(typeof(string[]));
        var resultLocal = il.DeclareLocal(_types.ListOfObject);
        var iLocal = il.DeclareLocal(_types.Int32);
        var loopStartLabel = il.DefineLabel();
        var loopEndLabel = il.DefineLabel();

        // var regexp = separator as $RegExp
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Isinst, runtime.TSRegExpType);
        il.Emit(OpCodes.Stloc, regexpLocal);

        // if (regexp != null)
        il.Emit(OpCodes.Ldloc, regexpLocal);
        il.Emit(OpCodes.Brfalse, isStringPatternLabel);

        // var parts = regexp.Split(str)
        il.Emit(OpCodes.Ldloc, regexpLocal);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Call, _tsRegExpSplitMethod);
        il.Emit(OpCodes.Stloc, partsLocal);

        // Convert to List<object?>
        il.Emit(OpCodes.Newobj, _types.ListOfObject.GetConstructor(Type.EmptyTypes)!);
        il.Emit(OpCodes.Stloc, resultLocal);

        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Stloc, iLocal);

        il.MarkLabel(loopStartLabel);
        il.Emit(OpCodes.Ldloc, iLocal);
        il.Emit(OpCodes.Ldloc, partsLocal);
        il.Emit(OpCodes.Ldlen);
        il.Emit(OpCodes.Conv_I4);
        il.Emit(OpCodes.Bge, loopEndLabel);

        il.Emit(OpCodes.Ldloc, resultLocal);
        il.Emit(OpCodes.Ldloc, partsLocal);
        il.Emit(OpCodes.Ldloc, iLocal);
        il.Emit(OpCodes.Ldelem_Ref);
        il.Emit(OpCodes.Callvirt, _types.ListOfObject.GetMethod("Add", [_types.Object])!);

        il.Emit(OpCodes.Ldloc, iLocal);
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Add);
        il.Emit(OpCodes.Stloc, iLocal);
        il.Emit(OpCodes.Br, loopStartLabel);

        il.MarkLabel(loopEndLabel);
        il.Emit(OpCodes.Ldloc, resultLocal);
        il.Emit(OpCodes.Ret);

        // String pattern fallback — coerce via ECMA-262 ToString protocol.
        il.MarkLabel(isStringPatternLabel);

        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Call, runtime.ToJsString);
        il.Emit(OpCodes.Stloc, sepLocal);

        // Handle empty separator: split into characters
        var nonEmptySepLabel = il.DefineLabel();
        var splitDoneLabel = il.DefineLabel();
        il.Emit(OpCodes.Ldloc, sepLocal);
        il.Emit(OpCodes.Ldstr, "");
        il.Emit(OpCodes.Call, _types.String.GetMethod("op_Equality", [_types.String, _types.String])!);
        il.Emit(OpCodes.Brfalse, nonEmptySepLabel);

        // Empty separator: split into characters
        // result = new List<object?>()
        il.Emit(OpCodes.Newobj, _types.ListOfObject.GetConstructor(Type.EmptyTypes)!);
        il.Emit(OpCodes.Stloc, resultLocal);

        var charLoopStartLabel = il.DefineLabel();
        var charLoopEndLabel = il.DefineLabel();
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Stloc, iLocal);

        il.MarkLabel(charLoopStartLabel);
        il.Emit(OpCodes.Ldloc, iLocal);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Callvirt, _types.String.GetProperty("Length")!.GetGetMethod()!);
        il.Emit(OpCodes.Bge, charLoopEndLabel);

        // result.Add(Char.ToString(str[i]))
        il.Emit(OpCodes.Ldloc, resultLocal);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldloc, iLocal);
        il.Emit(OpCodes.Callvirt, _types.String.GetMethod("get_Chars", [_types.Int32])!);
        il.Emit(OpCodes.Call, _types.GetMethod(_types.Char, "ToString", _types.Char));
        il.Emit(OpCodes.Callvirt, _types.ListOfObject.GetMethod("Add", [_types.Object])!);

        il.Emit(OpCodes.Ldloc, iLocal);
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Add);
        il.Emit(OpCodes.Stloc, iLocal);
        il.Emit(OpCodes.Br, charLoopStartLabel);

        il.MarkLabel(charLoopEndLabel);
        il.Emit(OpCodes.Ldloc, resultLocal);
        il.Emit(OpCodes.Ret);

        // Non-empty separator: use string.Split
        il.MarkLabel(nonEmptySepLabel);

        // parts = str.Split(sep, StringSplitOptions.None)
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldloc, sepLocal);
        il.Emit(OpCodes.Ldc_I4, (int)StringSplitOptions.None);
        il.Emit(OpCodes.Call, _types.GetMethod(_types.String, "Split", _types.String, _types.StringSplitOptions));
        il.Emit(OpCodes.Stloc, partsLocal);

        // Convert to List<object?>
        il.Emit(OpCodes.Newobj, _types.ListOfObject.GetConstructor(Type.EmptyTypes)!);
        il.Emit(OpCodes.Stloc, resultLocal);

        var strLoopStartLabel = il.DefineLabel();
        var strLoopEndLabel = il.DefineLabel();
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Stloc, iLocal);

        il.MarkLabel(strLoopStartLabel);
        il.Emit(OpCodes.Ldloc, iLocal);
        il.Emit(OpCodes.Ldloc, partsLocal);
        il.Emit(OpCodes.Ldlen);
        il.Emit(OpCodes.Conv_I4);
        il.Emit(OpCodes.Bge, strLoopEndLabel);

        il.Emit(OpCodes.Ldloc, resultLocal);
        il.Emit(OpCodes.Ldloc, partsLocal);
        il.Emit(OpCodes.Ldloc, iLocal);
        il.Emit(OpCodes.Ldelem_Ref);
        il.Emit(OpCodes.Callvirt, _types.ListOfObject.GetMethod("Add", [_types.Object])!);

        il.Emit(OpCodes.Ldloc, iLocal);
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Add);
        il.Emit(OpCodes.Stloc, iLocal);
        il.Emit(OpCodes.Br, strLoopStartLabel);

        il.MarkLabel(strLoopEndLabel);
        il.Emit(OpCodes.Ldloc, resultLocal);
        il.Emit(OpCodes.Ret);
    }
}
