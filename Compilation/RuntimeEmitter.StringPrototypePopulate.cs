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
    private void EmitStringPrototypePopulate(TypeBuilder typeBuilder, EmittedRuntime runtime)
    {
        var method = typeBuilder.DefineMethod(
            "_StringPrototypePopulate",
            MethodAttributes.Public | MethodAttributes.Static,
            _types.Void,
            Type.EmptyTypes);

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

        void Wire(string jsName, MethodBuilder? helper)
        {
            if (helper is null) return;
            il.Emit(OpCodes.Ldsfld, runtime.StringPrototypeField);
            il.Emit(OpCodes.Ldstr, jsName);
            il.Emit(OpCodes.Ldnull);
            il.Emit(OpCodes.Ldtoken, helper);
            il.Emit(OpCodes.Ldtoken, helper.DeclaringType!);
            il.Emit(OpCodes.Call, _types.GetMethod(_types.MethodBase, "GetMethodFromHandle",
                _types.RuntimeMethodHandle, _types.RuntimeTypeHandle));
            il.Emit(OpCodes.Castclass, _types.MethodInfo);
            il.Emit(OpCodes.Newobj, runtime.TSFunctionCtor);
            il.Emit(OpCodes.Callvirt, setItem);
        }

        Wire("charAt",         runtime.StringCharAt);
        Wire("charCodeAt",     runtime.StringCharCodeAt);
        Wire("codePointAt",    runtime.StringCodePointAt);
        Wire("substring",      runtime.StringSubstring);
        Wire("substr",         runtime.StringSubstr);
        Wire("indexOf",        runtime.StringIndexOf);
        Wire("lastIndexOf",    runtime.StringLastIndexOf);
        Wire("toUpperCase",    runtime.StringToUpperCase);
        Wire("toLowerCase",    runtime.StringToLowerCase);
        Wire("trim",           runtime.StringTrim);
        Wire("trimStart",      runtime.StringTrimStart);
        Wire("trimEnd",        runtime.StringTrimEnd);
        Wire("replace",        runtime.StringReplace);
        Wire("replaceAll",     runtime.StringReplaceAll);
        Wire("split",          runtime.StringSplit);
        Wire("includes",       runtime.StringIncludes);
        Wire("startsWith",     runtime.StringStartsWith);
        Wire("endsWith",       runtime.StringEndsWith);
        Wire("slice",          runtime.StringSlice);
        Wire("repeat",         runtime.StringRepeat);
        Wire("padStart",       runtime.StringPadStart);
        Wire("padEnd",         runtime.StringPadEnd);
        Wire("concat",         runtime.StringConcat);
        Wire("at",             runtime.StringAt);
        Wire("normalize",      runtime.StringNormalize);
        Wire("localeCompare",  runtime.StringLocaleCompare);

        il.Emit(OpCodes.Ret);

        runtime.StringPrototypePopulateMethod = method;
    }
}
