using System.Reflection;
using System.Reflection.Emit;

namespace SharpTS.Compilation;

public partial class RuntimeEmitter
{
    /// <summary>
    /// Value-position namespace singletons for AbortSignal and Intl (#224).
    ///
    /// Direct forms (`AbortSignal.abort(x)`, `new Intl.NumberFormat(...)`) are
    /// intercepted at compile time by AbortSignalStaticEmitter /
    /// TryEmitIntlConstructor and never touch these. The singletons only
    /// surface when the namespace is used as a VALUE — bare reference,
    /// aliasing (`const I = Intl`), `typeof`, passing as an argument — the
    /// same role MathSingletonField/JsonSingletonField play for Math/JSON.
    /// Members are $TSFunction wrappers over the existing $Runtime helpers, so
    /// aliased calls (`A.abort('r')`) and aliased construction
    /// (`new I.NumberFormat(...)` via NewOnFunction + the IsConstructor
    /// CreateIntl* exemption) reuse one implementation.
    ///
    /// Lazily populated: the populate shell is called before each bare-
    /// reference load; the field doubles as the populated flag.
    /// </summary>
    private void EmitNamespaceSingletons(TypeBuilder typeBuilder, EmittedRuntime runtime)
    {
        if (_features.UsesAbortController)
        {
            (runtime.AbortSignalNamespaceField, runtime.AbortSignalNamespacePopulate) =
                EmitNamespaceSingleton(typeBuilder, runtime, "AbortSignal",
                [
                    ("abort", runtime.AbortSignalAbort, 1),
                    ("timeout", runtime.AbortSignalTimeout, 1),
                    ("any", runtime.AbortSignalAny, 1),
                ]);
        }

        if (_features.UsesIntl)
        {
            (runtime.IntlNamespaceField, runtime.IntlNamespacePopulate) =
                EmitNamespaceSingleton(typeBuilder, runtime, "Intl",
                [
                    ("NumberFormat", runtime.CreateIntlNumberFormat, 2),
                    ("DateTimeFormat", runtime.CreateIntlDateTimeFormat, 2),
                    ("Collator", runtime.CreateIntlCollator, 2),
                    ("PluralRules", runtime.CreateIntlPluralRules, 2),
                    ("RelativeTimeFormat", runtime.CreateIntlRelativeTimeFormat, 2),
                    ("ListFormat", runtime.CreateIntlListFormat, 2),
                    ("Segmenter", runtime.CreateIntlSegmenter, 2),
                    ("DisplayNames", runtime.CreateIntlDisplayNames, 2),
                ]);
        }
    }

    private (FieldBuilder Field, MethodBuilder Populate) EmitNamespaceSingleton(
        TypeBuilder typeBuilder,
        EmittedRuntime runtime,
        string namespaceName,
        (string JsName, MethodBuilder Helper, int JsLength)[] members)
    {
        var field = typeBuilder.DefineField(
            $"_{namespaceName}Namespace",
            _types.DictionaryStringObject,
            FieldAttributes.Public | FieldAttributes.Static);

        var method = typeBuilder.DefineMethod(
            $"_{namespaceName}NamespacePopulate",
            MethodAttributes.Public | MethodAttributes.Static,
            _types.Void,
            Type.EmptyTypes);

        var il = method.GetILGenerator();
        var setItem = _types.GetMethod(_types.DictionaryStringObject, "set_Item",
            _types.String, _types.Object);

        // Already populated? (field doubles as the flag)
        var doFillLabel = il.DefineLabel();
        il.Emit(OpCodes.Ldsfld, field);
        il.Emit(OpCodes.Brfalse, doFillLabel);
        il.Emit(OpCodes.Ret);
        il.MarkLabel(doFillLabel);

        var dictLocal = il.DeclareLocal(_types.DictionaryStringObject);
        il.Emit(OpCodes.Newobj, _types.GetDefaultConstructor(_types.DictionaryStringObject));
        il.Emit(OpCodes.Stloc, dictLocal);

        foreach (var (jsName, helper, jsLength) in members)
        {
            // dict[jsName] = $TSFunction.GetOrCreate(helper, jsName, jsLength)
            // — GetOrCreate keys on MethodInfo so the wrapper identity is
            // shared with any other path exposing the same helper.
            il.Emit(OpCodes.Ldloc, dictLocal);
            il.Emit(OpCodes.Ldstr, jsName);
            il.Emit(OpCodes.Ldtoken, helper);
            il.Emit(OpCodes.Call, _types.GetMethod(_types.MethodBase, "GetMethodFromHandle", _types.RuntimeMethodHandle));
            il.Emit(OpCodes.Castclass, _types.MethodInfo);
            il.Emit(OpCodes.Ldstr, jsName);
            il.Emit(OpCodes.Ldc_I4, jsLength);
            il.Emit(OpCodes.Call, runtime.TSFunctionGetOrCreate);
            il.Emit(OpCodes.Callvirt, setItem);
        }

        // Publish last so a half-filled dict is never observable.
        il.Emit(OpCodes.Ldloc, dictLocal);
        il.Emit(OpCodes.Stsfld, field);
        il.Emit(OpCodes.Ret);

        return (field, method);
    }
}
