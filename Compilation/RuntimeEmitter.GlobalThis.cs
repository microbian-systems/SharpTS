using System.Reflection;
using System.Reflection.Emit;

namespace SharpTS.Compilation;

public partial class RuntimeEmitter
{
    /// <summary>
    /// Emits globalThis helper methods (GetProperty, SetProperty).
    /// </summary>
    private void EmitGlobalThisMethods(TypeBuilder typeBuilder, EmittedRuntime runtime)
    {
        // Static field to cache the fetch TSFunction for reference equality
        runtime.CachedFetchFunction = typeBuilder.DefineField(
            "_cachedFetchFunction",
            _types.Object,
            FieldAttributes.Private | FieldAttributes.Static);

        // Static fields for cached global function TSFunction objects
        runtime.CachedParseIntFunction = typeBuilder.DefineField(
            "_cachedParseIntFunction",
            _types.Object,
            FieldAttributes.Private | FieldAttributes.Static);

        runtime.CachedParseFloatFunction = typeBuilder.DefineField(
            "_cachedParseFloatFunction",
            _types.Object,
            FieldAttributes.Private | FieldAttributes.Static);

        runtime.CachedIsNaNFunction = typeBuilder.DefineField(
            "_cachedIsNaNFunction",
            _types.Object,
            FieldAttributes.Private | FieldAttributes.Static);

        runtime.CachedIsFiniteFunction = typeBuilder.DefineField(
            "_cachedIsFiniteFunction",
            _types.Object,
            FieldAttributes.Private | FieldAttributes.Static);

        // Static dictionary for user-assigned globalThis properties
        runtime.GlobalThisProperties = typeBuilder.DefineField(
            "_globalThisProperties",
            _types.DictionaryStringObject,
            FieldAttributes.Private | FieldAttributes.Static);

        EmitGlobalThisGetProperty(typeBuilder, runtime);
        EmitGlobalThisSetProperty(typeBuilder, runtime);
    }

    /// <summary>
    /// Emits: public static object GlobalThisGetProperty(string name)
    /// Gets a property from globalThis, checking user-assigned properties first,
    /// then delegating to built-ins.
    /// </summary>
    private void EmitGlobalThisGetProperty(TypeBuilder typeBuilder, EmittedRuntime runtime)
    {
        var method = typeBuilder.DefineMethod(
            "GlobalThisGetProperty",
            MethodAttributes.Public | MethodAttributes.Static,
            _types.Object,
            [_types.String]
        );
        runtime.GlobalThisGetProperty = method;

        var il = method.GetILGenerator();

        var selfRefLabel = il.DefineLabel();
        var undefinedPropLabel = il.DefineLabel();
        var nanLabel = il.DefineLabel();
        var infinityLabel = il.DefineLabel();
        var fetchLabel = il.DefineLabel();
        var parseIntLabel = il.DefineLabel();
        var parseFloatLabel = il.DefineLabel();
        var isNaNLabel = il.DefineLabel();
        var isFiniteLabel = il.DefineLabel();
        var returnLabel = il.DefineLabel();
        var checkBuiltInsLabel = il.DefineLabel();

        // --- Check user-assigned properties dictionary first ---
        var valueLocal = il.DeclareLocal(_types.Object);
        il.Emit(OpCodes.Ldsfld, runtime.GlobalThisProperties);
        il.Emit(OpCodes.Brfalse, checkBuiltInsLabel); // dict not initialized yet
        il.Emit(OpCodes.Ldsfld, runtime.GlobalThisProperties);
        il.Emit(OpCodes.Ldarg_0); // name
        il.Emit(OpCodes.Ldloca, valueLocal);
        var dictTryGetValue = _types.GetMethod(_types.DictionaryStringObject, "TryGetValue", _types.String, _types.Object.MakeByRefType());
        il.Emit(OpCodes.Callvirt, dictTryGetValue);
        il.Emit(OpCodes.Brfalse, checkBuiltInsLabel); // not found in dict
        il.Emit(OpCodes.Ldloc, valueLocal);
        il.Emit(OpCodes.Br, returnLabel);

        il.MarkLabel(checkBuiltInsLabel);

        // Check for "globalThis" (self-reference)
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldstr, "globalThis");
        il.Emit(OpCodes.Call, _types.GetMethod(_types.String, "op_Equality", _types.String, _types.String));
        il.Emit(OpCodes.Brtrue, selfRefLabel);

        // Check for "undefined"
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldstr, "undefined");
        il.Emit(OpCodes.Call, _types.GetMethod(_types.String, "op_Equality", _types.String, _types.String));
        il.Emit(OpCodes.Brtrue, undefinedPropLabel);

        // Check for "NaN"
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldstr, "NaN");
        il.Emit(OpCodes.Call, _types.GetMethod(_types.String, "op_Equality", _types.String, _types.String));
        il.Emit(OpCodes.Brtrue, nanLabel);

        // Check for "Infinity"
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldstr, "Infinity");
        il.Emit(OpCodes.Call, _types.GetMethod(_types.String, "op_Equality", _types.String, _types.String));
        il.Emit(OpCodes.Brtrue, infinityLabel);

        // Check for "fetch" — only when the program references fetch (or any fetch-family
        // identifier). The `runtime.Fetch` MethodBuilder is null otherwise.
        if (_features.UsesFetch)
        {
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Ldstr, "fetch");
            il.Emit(OpCodes.Call, _types.GetMethod(_types.String, "op_Equality", _types.String, _types.String));
            il.Emit(OpCodes.Brtrue, fetchLabel);
        }

        // Check for "parseInt"
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldstr, "parseInt");
        il.Emit(OpCodes.Call, _types.GetMethod(_types.String, "op_Equality", _types.String, _types.String));
        il.Emit(OpCodes.Brtrue, parseIntLabel);

        // Check for "parseFloat"
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldstr, "parseFloat");
        il.Emit(OpCodes.Call, _types.GetMethod(_types.String, "op_Equality", _types.String, _types.String));
        il.Emit(OpCodes.Brtrue, parseFloatLabel);

        // Check for "isNaN"
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldstr, "isNaN");
        il.Emit(OpCodes.Call, _types.GetMethod(_types.String, "op_Equality", _types.String, _types.String));
        il.Emit(OpCodes.Brtrue, isNaNLabel);

        // Check for "isFinite"
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldstr, "isFinite");
        il.Emit(OpCodes.Call, _types.GetMethod(_types.String, "op_Equality", _types.String, _types.String));
        il.Emit(OpCodes.Brtrue, isFiniteLabel);

        // Built-in class constructors — return the actual .NET Type (Ldtoken +
        // GetTypeFromHandle) so `typeof globalThis.Array === "function"` and
        // `globalThis.Array === Array` hold. Previously these all returned null
        // as a "namespace marker," which broke lodash-style feature detection:
        // `typeof root.Object === "object" && root.Object === Object` was false
        // because root.Object was null.
        var strEquals = _types.GetMethod(_types.String, "op_Equality", _types.String, _types.String);
        var getTypeFromHandle = _types.GetMethod(_types.Type, "GetTypeFromHandle", _types.RuntimeTypeHandle);

        void EmitTypeBranch(string name, Type t)
        {
            var notThisName = il.DefineLabel();
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Ldstr, name);
            il.Emit(OpCodes.Call, strEquals);
            il.Emit(OpCodes.Brfalse, notThisName);
            il.Emit(OpCodes.Ldtoken, t);
            il.Emit(OpCodes.Call, getTypeFromHandle);
            il.Emit(OpCodes.Br, returnLabel);
            il.MarkLabel(notThisName);
        }

        EmitTypeBranch("Array", _types.IListOfObject);
        if (_features.UsesDate)
            EmitTypeBranch("Date", runtime.TSDateType);
        if (_features.UsesRegExp)
            EmitTypeBranch("RegExp", runtime.TSRegExpType);
        EmitTypeBranch("Map", _types.DictionaryObjectObject);
        EmitTypeBranch("Set", _types.HashSetOfObject);
        EmitTypeBranch("WeakMap", _types.ConditionalWeakTableObjectObject);
        EmitTypeBranch("WeakSet", _types.ConditionalWeakTableObjectObject);
        EmitTypeBranch("Promise", _types.TaskOfObject);
        EmitTypeBranch("Buffer", runtime.TSBufferType);
        EmitTypeBranch("Function", runtime.TSFunctionType);
        if (_features.UsesTextEncoding)
        {
            EmitTypeBranch("TextEncoder", runtime.TSTextEncoderType);
            EmitTypeBranch("TextDecoder", runtime.TSTextDecoderType);
        }
        // `Object` — return System.Object's Type token so `globalThis.Object === Object`
        // holds (bare `Object` lowers to this same helper via ILEmitter.Expressions.cs,
        // so both sides produce the canonical Type instance). The compile-time static
        // dispatch for `Object.keys(obj)` etc. runs through ObjectStaticEmitter before
        // the receiver is evaluated as a value, so this change doesn't affect it.
        EmitTypeBranch("Object", _types.Object);
        // Number / String / Boolean (issue #62) — expose the underlying
        // primitive .NET types so `typeof Number === "function"` and
        // `globalThis.Number === Number` hold. Compile-time static dispatch
        // for `Number.isInteger(x)` etc. routes through the dedicated
        // NumberStaticEmitter/StringStaticEmitter before the receiver is
        // evaluated, so these branches only matter for value-form access.
        EmitTypeBranch("Number", _types.Double);
        EmitTypeBranch("String", _types.String);
        EmitTypeBranch("Boolean", _types.Boolean);

        // Remaining named namespaces (Math, JSON, console, Error, Reflect,
        // process, Symbol) are represented as singletons in the runtime rather
        // than .NET Type instances. Keep the null-marker behavior for those —
        // compile-time static dispatch already routes through their dedicated
        // namespace emitters.
        string[] singletonNamespaces =
        [
            "Math", "JSON", "console", "Error", "Reflect", "process", "Symbol"
        ];
        foreach (var ns in singletonNamespaces)
        {
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Ldstr, ns);
            il.Emit(OpCodes.Call, strEquals);
            il.Emit(OpCodes.Brtrue, selfRefLabel); // null marker
        }

        // Default: return undefined
        il.Emit(OpCodes.Ldsfld, runtime.UndefinedInstance);
        il.Emit(OpCodes.Br, returnLabel);

        // Self-reference: return null (marker for globalThis in property access chains)
        il.MarkLabel(selfRefLabel);
        il.Emit(OpCodes.Ldnull);
        il.Emit(OpCodes.Br, returnLabel);

        // undefined property
        il.MarkLabel(undefinedPropLabel);
        il.Emit(OpCodes.Ldsfld, runtime.UndefinedInstance);
        il.Emit(OpCodes.Br, returnLabel);

        // NaN property
        il.MarkLabel(nanLabel);
        il.Emit(OpCodes.Ldc_R8, double.NaN);
        il.Emit(OpCodes.Box, _types.Double);
        il.Emit(OpCodes.Br, returnLabel);

        // Infinity property
        il.MarkLabel(infinityLabel);
        il.Emit(OpCodes.Ldc_R8, double.PositiveInfinity);
        il.Emit(OpCodes.Box, _types.Double);
        il.Emit(OpCodes.Br, returnLabel);

        // fetch property - return cached fetch TSFunction (only emitted when UsesFetch)
        if (_features.UsesFetch)
        {
            il.MarkLabel(fetchLabel);
            EmitCachedTSFunction(il, runtime.CachedFetchFunction, runtime.Fetch, runtime);
            il.Emit(OpCodes.Br, returnLabel);
        }

        // parseInt - return cached TSFunction wrapping NumberParseInt
        il.MarkLabel(parseIntLabel);
        EmitCachedTSFunction(il, runtime.CachedParseIntFunction, runtime.NumberParseInt, runtime);
        il.Emit(OpCodes.Br, returnLabel);

        // parseFloat - return cached TSFunction wrapping NumberParseFloat
        il.MarkLabel(parseFloatLabel);
        EmitCachedTSFunction(il, runtime.CachedParseFloatFunction, runtime.NumberParseFloat, runtime);
        il.Emit(OpCodes.Br, returnLabel);

        // isNaN - return cached TSFunction wrapping NumberIsNaN
        il.MarkLabel(isNaNLabel);
        EmitCachedTSFunction(il, runtime.CachedIsNaNFunction, runtime.NumberIsNaN, runtime);
        il.Emit(OpCodes.Br, returnLabel);

        // isFinite - return cached TSFunction wrapping NumberIsFinite
        il.MarkLabel(isFiniteLabel);
        EmitCachedTSFunction(il, runtime.CachedIsFiniteFunction, runtime.NumberIsFinite, runtime);
        il.Emit(OpCodes.Br, returnLabel);

        il.MarkLabel(returnLabel);
        il.Emit(OpCodes.Ret);
    }

    /// <summary>
    /// Emits IL to load a cached TSFunction, creating it lazily if null.
    /// Pattern: if (cachedField == null) { cachedField = new TSFunction(null, methodInfo); } push cachedField;
    /// </summary>
    private void EmitCachedTSFunction(ILGenerator il, FieldBuilder cachedField, MethodBuilder wrappedMethod, EmittedRuntime runtime)
    {
        var alreadyCachedLabel = il.DefineLabel();
        il.Emit(OpCodes.Ldsfld, cachedField);
        il.Emit(OpCodes.Brtrue, alreadyCachedLabel);
        // Create and cache the TSFunction
        il.Emit(OpCodes.Ldnull); // target (static method)
        il.Emit(OpCodes.Ldtoken, wrappedMethod);
        il.Emit(OpCodes.Call, _types.GetMethod(_types.MethodBase, "GetMethodFromHandle", _types.RuntimeMethodHandle));
        il.Emit(OpCodes.Castclass, _types.MethodInfo);
        il.Emit(OpCodes.Newobj, runtime.TSFunctionCtor);
        il.Emit(OpCodes.Stsfld, cachedField);
        il.MarkLabel(alreadyCachedLabel);
        il.Emit(OpCodes.Ldsfld, cachedField);
    }

    /// <summary>
    /// Emits: public static void GlobalThisSetProperty(string name, object value)
    /// Sets a property on globalThis, storing in a static dictionary.
    /// </summary>
    private void EmitGlobalThisSetProperty(TypeBuilder typeBuilder, EmittedRuntime runtime)
    {
        var method = typeBuilder.DefineMethod(
            "GlobalThisSetProperty",
            MethodAttributes.Public | MethodAttributes.Static,
            _types.Void,
            [_types.String, _types.Object]
        );
        runtime.GlobalThisSetProperty = method;

        var il = method.GetILGenerator();

        // Lazily initialize the dictionary: if (_globalThisProperties == null) _globalThisProperties = new();
        var dictReadyLabel = il.DefineLabel();
        il.Emit(OpCodes.Ldsfld, runtime.GlobalThisProperties);
        il.Emit(OpCodes.Brtrue, dictReadyLabel);
        il.Emit(OpCodes.Newobj, _types.GetDefaultConstructor(_types.DictionaryStringObject));
        il.Emit(OpCodes.Stsfld, runtime.GlobalThisProperties);
        il.MarkLabel(dictReadyLabel);

        // _globalThisProperties[name] = value
        il.Emit(OpCodes.Ldsfld, runtime.GlobalThisProperties);
        il.Emit(OpCodes.Ldarg_0); // name
        il.Emit(OpCodes.Ldarg_1); // value
        var dictSetItem = _types.GetMethod(_types.DictionaryStringObject, "set_Item", _types.String, _types.Object);
        il.Emit(OpCodes.Callvirt, dictSetItem);

        il.Emit(OpCodes.Ret);
    }
}
