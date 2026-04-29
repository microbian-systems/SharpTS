using System.Reflection;
using System.Reflection.Emit;

namespace SharpTS.Compilation;

public partial class RuntimeEmitter
{
    /// <summary>
    /// Emits a static method that populates the <c>String.prototype</c>
    /// singleton dictionary (<see cref="EmittedRuntime.StringPrototypeField"/>)
    /// with <c>$TSFunction</c> wrappers around the <c>$Runtime.String*</c>
    /// helpers. Mirrors <see cref="EmitArrayPrototypePopulate"/>.
    /// </summary>
    /// <remarks>
    /// Must be emitted AFTER all <c>EmitString*</c> helpers so the wrapped
    /// MethodBuilders are non-null. Most Test262 tests don't directly invoke
    /// the wrappers — they probe via <c>typeof String.prototype.X</c> /
    /// <c>isConstructor(String.prototype.X)</c>. Direct method calls on
    /// strings (<c>"abc".substring(1)</c>) flow through the type-checked
    /// dispatch path and bypass these wrappers.
    /// </remarks>
    /// <summary>
    /// Pre-defines the populate MethodBuilder shell so callers (e.g.
    /// $Runtime.GetProperty's Type-prototype branch) can reference it
    /// before the body is emitted.
    /// </summary>
    private void DefineStringPrototypePopulateShell(TypeBuilder typeBuilder, EmittedRuntime runtime)
    {
        runtime.StringPrototypePopulateMethod = typeBuilder.DefineMethod(
            "_StringPrototypePopulate",
            MethodAttributes.Public | MethodAttributes.Static,
            _types.Void,
            Type.EmptyTypes);
    }

    private void EmitStringPrototypePopulate(TypeBuilder typeBuilder, EmittedRuntime runtime)
    {
        var method = runtime.StringPrototypePopulateMethod;
        var il = method.GetILGenerator();
        var setItem = _types.GetMethod(_types.DictionaryStringObject, "set_Item",
            _types.String, _types.Object);

        // Idempotent: if dict already has entries, return early.
        var doFillLabel = il.DefineLabel();
        il.Emit(OpCodes.Ldsfld, runtime.StringPrototypeField);
        il.Emit(OpCodes.Callvirt, _types.GetProperty(_types.DictionaryStringObject, "Count").GetGetMethod()!);
        il.Emit(OpCodes.Brfalse, doFillLabel);
        il.Emit(OpCodes.Ret);
        il.MarkLabel(doFillLabel);

        // ECMA-262 22.1.3 String.prototype.constructor === String. Compiled
        // bare `String` resolves to typeof(string).
        il.Emit(OpCodes.Ldsfld, runtime.StringPrototypeField);
        il.Emit(OpCodes.Ldstr, "constructor");
        il.Emit(OpCodes.Ldtoken, _types.String);
        il.Emit(OpCodes.Call, _types.GetMethod(_types.Type, "GetTypeFromHandle", _types.RuntimeTypeHandle));
        il.Emit(OpCodes.Callvirt, setItem);

        // Wire with explicit JS-spec name + length via TSFunctionCtorWithCache.
        // Length is the user-callable arg count per ECMA-262 (e.g. substring
        // = 2, even though the underlying StringSubstring takes 3 .NET params
        // because the receiver is the first arg). Without this cache, Test262
        // `String.prototype.substring.length === 2` returns 3.
        void Wire(string jsName, MethodBuilder? helper, int jsLength)
        {
            if (helper is null) return;
            // Name the first parameter "__this" so $TSFunction.InvokeWithThis
            // prepends the call-site receiver when this helper is invoked via
            // a borrowed prototype method (`obj.charAt = String.prototype.charAt;
            // obj.charAt(0)`). DefineParameter is idempotent when called multiple
            // times for the same position; safe even if some helpers already
            // named their first param.
            try { helper.DefineParameter(1, System.Reflection.ParameterAttributes.None, "__this"); }
            catch { /* parameter already defined elsewhere — ignore */ }
            il.Emit(OpCodes.Ldsfld, runtime.StringPrototypeField);
            il.Emit(OpCodes.Ldstr, jsName);
            il.Emit(OpCodes.Ldnull);
            il.Emit(OpCodes.Ldtoken, helper);
            il.Emit(OpCodes.Ldtoken, helper.DeclaringType!);
            il.Emit(OpCodes.Call, _types.GetMethod(_types.MethodBase, "GetMethodFromHandle",
                _types.RuntimeMethodHandle, _types.RuntimeTypeHandle));
            il.Emit(OpCodes.Castclass, _types.MethodInfo);
            il.Emit(OpCodes.Ldstr, jsName);
            il.Emit(OpCodes.Ldc_I4, jsLength);
            il.Emit(OpCodes.Newobj, runtime.TSFunctionCtorWithCache);
            il.Emit(OpCodes.Callvirt, setItem);
        }

        Wire("charAt",         runtime.StringCharAt,         1);
        Wire("charCodeAt",     runtime.StringCharCodeAt,     1);
        Wire("codePointAt",    runtime.StringCodePointAt,    1);
        Wire("substring",      runtime.StringSubstring,      2);
        Wire("substr",         runtime.StringSubstr,         2);
        Wire("indexOf",        runtime.StringIndexOf,        1);
        Wire("lastIndexOf",    runtime.StringLastIndexOf,    1);
        Wire("toUpperCase",    runtime.StringToUpperCase,    0);
        Wire("toLowerCase",    runtime.StringToLowerCase,    0);
        Wire("trim",           runtime.StringTrim,           0);
        Wire("trimStart",      runtime.StringTrimStart,      0);
        Wire("trimEnd",        runtime.StringTrimEnd,        0);
        Wire("replace",        runtime.StringReplace,        2);
        Wire("replaceAll",     runtime.StringReplaceAll,     2);
        Wire("split",          runtime.StringSplit,          2);
        Wire("includes",       runtime.StringIncludes,       1);
        Wire("startsWith",     runtime.StringStartsWith,     1);
        Wire("endsWith",       runtime.StringEndsWith,       1);
        Wire("slice",          runtime.StringSlice,          2);
        Wire("repeat",         runtime.StringRepeat,         1);
        Wire("padStart",       runtime.StringPadStart,       1);
        Wire("padEnd",         runtime.StringPadEnd,         1);
        Wire("concat",         runtime.StringConcat,         1);
        Wire("at",             runtime.StringAt,             1);
        Wire("normalize",      runtime.StringNormalize,      0);
        Wire("localeCompare",  runtime.StringLocaleCompare,  1);

        // Methods without dedicated $Runtime helpers — wired to a generic
        // stub so typeof + isConstructor probes pass. The pattern matcher
        // and inline dispatch handle direct invocations of these on actual
        // strings; the wrappers here are only observed by Test262
        // `not-a-constructor.js` harness probes.
        // match/matchAll/search throw TypeError on null/undefined receiver per
        // ECMA-262 22.1.3.* step 1. Use the strict stub so borrowed-method
        // patterns (`String.prototype.match.call(null, /./)`) propagate.
        Wire("match",                runtime.StringPrototypeStrictStub,    1);
        Wire("matchAll",             runtime.StringPrototypeStrictStub,    1);
        Wire("search",               runtime.StringPrototypeStrictStub,    1);
        Wire("toString",             runtime.StringPrototypeGenericStub,   0);
        Wire("valueOf",              runtime.StringPrototypeGenericStub,   0);
        // ECMA-262 22.1.3.21/22 RequireObjectCoercible(this) is the first step.
        // Wire to the strict StringToLowerCase / StringToUpperCase variants so
        // borrowed-method calls on null/undefined throw TypeError instead of
        // returning empty string. We don't actually localize (no Intl in the
        // standalone DLL), so toLocaleX === toX is acceptable per spec note.
        Wire("toLocaleLowerCase",    runtime.StringToLowerCase,            0);
        Wire("toLocaleUpperCase",    runtime.StringToUpperCase,            0);
        Wire("isWellFormed",         runtime.StringPrototypeGenericStub,   0);
        Wire("toWellFormed",         runtime.StringPrototypeGenericStub,   0);

        // Per ECMA-262 §22.1.3 String.prototype's [[Prototype]] is %Object.prototype%.
        il.Emit(OpCodes.Ldsfld, runtime.StringPrototypeField);
        il.Emit(OpCodes.Ldsfld, runtime.ObjectPrototypeField);
        il.Emit(OpCodes.Call, runtime.PDSSetPrototype);

        il.Emit(OpCodes.Ret);
    }
}
