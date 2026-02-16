using System.Reflection;
using System.Reflection.Emit;

namespace SharpTS.Compilation;

/// <summary>
/// Emits $URL and $URLSearchParams classes for compiled mode.
/// These are standalone types that wrap System.Uri for URL parsing.
/// </summary>
public partial class RuntimeEmitter
{
    // $URL fields (set during emission)
    private FieldBuilder _urlUriField = null!;
    private FieldBuilder _urlSearchParamsField = null!;

    // $URLSearchParams fields (set during emission)
    private FieldBuilder _urlSearchParamsDataField = null!;

    /// <summary>
    /// Emits both $URL and $URLSearchParams classes.
    /// Must be called after EmitHeadersClass (same pattern).
    /// </summary>
    private void EmitUrlClasses(ModuleBuilder moduleBuilder, EmittedRuntime runtime)
    {
        // Emit $URLSearchParams first (used by $URL.searchParams)
        EmitUrlSearchParamsClass(moduleBuilder, runtime);
        EmitUrlClass(moduleBuilder, runtime);
    }

    /// <summary>
    /// Emits: public class $URLSearchParams
    /// Fields: _params (List&lt;string&gt; keys, List&lt;string&gt; values — parallel lists for simplicity)
    /// Methods: get, getAll, has, set, append, delete, sort, toString, forEach, entries, keys, values, size
    /// </summary>
    private void EmitUrlSearchParamsClass(ModuleBuilder moduleBuilder, EmittedRuntime runtime)
    {
        var typeBuilder = moduleBuilder.DefineType(
            "$URLSearchParams",
            TypeAttributes.Public | TypeAttributes.Class | TypeAttributes.BeforeFieldInit,
            _types.Object
        );

        var listOfStringType = typeof(List<string>);

        // Two parallel lists: _keys and _values
        var keysField = typeBuilder.DefineField("_keys", listOfStringType, FieldAttributes.Private);
        var valuesField = typeBuilder.DefineField("_values", listOfStringType, FieldAttributes.Private);
        _urlSearchParamsDataField = keysField; // save reference for other methods

        // Constructor: $URLSearchParams(object? init)
        var ctor = typeBuilder.DefineConstructor(
            MethodAttributes.Public,
            CallingConventions.Standard,
            [_types.Object]
        );

        var ctorIL = ctor.GetILGenerator();
        ctorIL.Emit(OpCodes.Ldarg_0);
        ctorIL.Emit(OpCodes.Call, _types.Object.GetConstructor(Type.EmptyTypes)!);

        // _keys = new List<string>()
        ctorIL.Emit(OpCodes.Ldarg_0);
        ctorIL.Emit(OpCodes.Newobj, listOfStringType.GetConstructor(Type.EmptyTypes)!);
        ctorIL.Emit(OpCodes.Stfld, keysField);

        // _values = new List<string>()
        ctorIL.Emit(OpCodes.Ldarg_0);
        ctorIL.Emit(OpCodes.Newobj, listOfStringType.GetConstructor(Type.EmptyTypes)!);
        ctorIL.Emit(OpCodes.Stfld, valuesField);

        // if (init == null) return
        var initNotNullLabel = ctorIL.DefineLabel();
        ctorIL.Emit(OpCodes.Ldarg_1);
        ctorIL.Emit(OpCodes.Brtrue, initNotNullLabel);
        ctorIL.Emit(OpCodes.Ret);

        ctorIL.MarkLabel(initNotNullLabel);

        // If init is string, parse it
        var notStringLabel = ctorIL.DefineLabel();
        ctorIL.Emit(OpCodes.Ldarg_1);
        ctorIL.Emit(OpCodes.Isinst, _types.String);
        ctorIL.Emit(OpCodes.Brfalse, notStringLabel);

        // Parse string: call a static helper method
        ctorIL.Emit(OpCodes.Ldarg_0);
        ctorIL.Emit(OpCodes.Ldarg_1);
        ctorIL.Emit(OpCodes.Castclass, _types.String);
        // We'll emit a ParseInit method and call it
        var parseInitMethod = typeBuilder.DefineMethod(
            "ParseInit",
            MethodAttributes.Private,
            typeof(void),
            [_types.String]
        );
        ctorIL.Emit(OpCodes.Call, parseInitMethod);
        ctorIL.Emit(OpCodes.Ret);

        ctorIL.MarkLabel(notStringLabel);

        // If init is Dictionary<string, object?>, iterate entries
        var notDictLabel = ctorIL.DefineLabel();
        ctorIL.Emit(OpCodes.Ldarg_1);
        ctorIL.Emit(OpCodes.Isinst, _types.DictionaryStringObject);
        ctorIL.Emit(OpCodes.Brfalse, notDictLabel);

        // Parse dictionary init
        ctorIL.Emit(OpCodes.Ldarg_0);
        ctorIL.Emit(OpCodes.Ldarg_1);
        ctorIL.Emit(OpCodes.Castclass, _types.DictionaryStringObject);
        var parseDictMethod = typeBuilder.DefineMethod(
            "ParseDictInit",
            MethodAttributes.Private,
            typeof(void),
            [_types.DictionaryStringObject]
        );
        ctorIL.Emit(OpCodes.Call, parseDictMethod);
        ctorIL.Emit(OpCodes.Ret);

        ctorIL.MarkLabel(notDictLabel);

        // Otherwise try toString() as string init
        ctorIL.Emit(OpCodes.Ldarg_0);
        ctorIL.Emit(OpCodes.Ldarg_1);
        ctorIL.Emit(OpCodes.Callvirt, _types.Object.GetMethod("ToString", Type.EmptyTypes)!);
        ctorIL.Emit(OpCodes.Call, parseInitMethod);
        ctorIL.Emit(OpCodes.Ret);

        // Emit ParseInit(string init) method body
        EmitUrlSearchParamsParseInit(parseInitMethod, keysField, valuesField);

        // Emit ParseDictInit(dict) method body
        EmitUrlSearchParamsParseDictInit(parseDictMethod, keysField, valuesField);

        // Emit instance methods and collect MethodBuilders for GetMember dispatch
        var spMethods = new Dictionary<string, MethodBuilder>();
        spMethods["get"] = EmitUrlSearchParamsGet(typeBuilder, keysField, valuesField);
        spMethods["getAll"] = EmitUrlSearchParamsGetAll(typeBuilder, keysField, valuesField, runtime);
        spMethods["has"] = EmitUrlSearchParamsHas(typeBuilder, keysField);
        spMethods["set"] = EmitUrlSearchParamsSet(typeBuilder, keysField, valuesField);
        spMethods["append"] = EmitUrlSearchParamsAppend(typeBuilder, keysField, valuesField);
        spMethods["delete"] = EmitUrlSearchParamsDelete(typeBuilder, keysField, valuesField);
        spMethods["sort"] = EmitUrlSearchParamsSort(typeBuilder, keysField, valuesField);
        spMethods["toString"] = EmitUrlSearchParamsToString(typeBuilder, keysField, valuesField);
        var sizeGetter = EmitUrlSearchParamsSize(typeBuilder, keysField);
        spMethods["forEach"] = EmitUrlSearchParamsForEach(typeBuilder, keysField, valuesField, runtime);
        spMethods["entries"] = EmitUrlSearchParamsEntries(typeBuilder, keysField, valuesField, runtime);
        spMethods["keys"] = EmitUrlSearchParamsKeys(typeBuilder, keysField, runtime);
        spMethods["values"] = EmitUrlSearchParamsValues(typeBuilder, valuesField, runtime);

        // Emit GetMember(string name) for property dispatch
        EmitUrlSearchParamsGetMember(typeBuilder, runtime, spMethods, sizeGetter);

        runtime.TSUrlSearchParamsType = typeBuilder;
        runtime.TSUrlSearchParamsCtor = ctor;

        typeBuilder.CreateType();
    }

    /// <summary>
    /// Emits ParseInit(string init) — parses "a=1&amp;b=2" query string into parallel lists.
    /// </summary>
    private void EmitUrlSearchParamsParseInit(MethodBuilder method, FieldBuilder keysField, FieldBuilder valuesField)
    {
        var il = method.GetILGenerator();
        var listOfStringType = typeof(List<string>);

        // Trim leading '?' if present
        var initLocal = il.DeclareLocal(_types.String);
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Newarr, _types.Char);
        il.Emit(OpCodes.Dup);
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Ldc_I4_S, (byte)'?');
        il.Emit(OpCodes.Stelem_I2);
        il.Emit(OpCodes.Callvirt, _types.String.GetMethod("TrimStart", [typeof(char[])])!);
        il.Emit(OpCodes.Stloc, initLocal);

        // if (string.IsNullOrEmpty(init)) return
        var notEmptyLabel = il.DefineLabel();
        il.Emit(OpCodes.Ldloc, initLocal);
        il.Emit(OpCodes.Call, _types.String.GetMethod("IsNullOrEmpty", [_types.String])!);
        il.Emit(OpCodes.Brfalse, notEmptyLabel);
        il.Emit(OpCodes.Ret);

        il.MarkLabel(notEmptyLabel);

        // string[] pairs = init.Split('&', StringSplitOptions.RemoveEmptyEntries)
        var pairsLocal = il.DeclareLocal(typeof(string[]));
        il.Emit(OpCodes.Ldloc, initLocal);
        il.Emit(OpCodes.Ldc_I4_S, (byte)'&');
        il.Emit(OpCodes.Ldc_I4_1); // StringSplitOptions.RemoveEmptyEntries
        il.Emit(OpCodes.Call, _types.String.GetMethod("Split", [_types.Char, typeof(StringSplitOptions)])!);
        il.Emit(OpCodes.Stloc, pairsLocal);

        // for (int i = 0; i < pairs.Length; i++)
        var indexLocal = il.DeclareLocal(_types.Int32);
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Stloc, indexLocal);

        var loopStart = il.DefineLabel();
        var loopEnd = il.DefineLabel();

        il.MarkLabel(loopStart);
        il.Emit(OpCodes.Ldloc, indexLocal);
        il.Emit(OpCodes.Ldloc, pairsLocal);
        il.Emit(OpCodes.Ldlen);
        il.Emit(OpCodes.Conv_I4);
        il.Emit(OpCodes.Bge, loopEnd);

        // string pair = pairs[i]
        var pairLocal = il.DeclareLocal(_types.String);
        il.Emit(OpCodes.Ldloc, pairsLocal);
        il.Emit(OpCodes.Ldloc, indexLocal);
        il.Emit(OpCodes.Ldelem_Ref);
        il.Emit(OpCodes.Stloc, pairLocal);

        // int eqIndex = pair.IndexOf('=')
        var eqIndexLocal = il.DeclareLocal(_types.Int32);
        il.Emit(OpCodes.Ldloc, pairLocal);
        il.Emit(OpCodes.Ldc_I4_S, (byte)'=');
        il.Emit(OpCodes.Callvirt, _types.String.GetMethod("IndexOf", [_types.Char])!);
        il.Emit(OpCodes.Stloc, eqIndexLocal);

        // if (eqIndex >= 0)
        var noEqLabel = il.DefineLabel();
        var pairDoneLabel = il.DefineLabel();
        il.Emit(OpCodes.Ldloc, eqIndexLocal);
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Blt, noEqLabel);

        // key = pair.Substring(0, eqIndex)
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldfld, keysField);
        il.Emit(OpCodes.Ldloc, pairLocal);
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Ldloc, eqIndexLocal);
        il.Emit(OpCodes.Callvirt, _types.String.GetMethod("Substring", [_types.Int32, _types.Int32])!);
        // Replace '+' with ' '
        il.Emit(OpCodes.Ldc_I4_S, (byte)'+');
        il.Emit(OpCodes.Ldc_I4_S, (byte)' ');
        il.Emit(OpCodes.Callvirt, _types.String.GetMethod("Replace", [_types.Char, _types.Char])!);
        // Uri.UnescapeDataString
        il.Emit(OpCodes.Call, _types.Uri.GetMethod("UnescapeDataString", [_types.String])!);
        il.Emit(OpCodes.Callvirt, listOfStringType.GetMethod("Add", [_types.String])!);

        // value = pair.Substring(eqIndex + 1)
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldfld, valuesField);
        il.Emit(OpCodes.Ldloc, pairLocal);
        il.Emit(OpCodes.Ldloc, eqIndexLocal);
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Add);
        il.Emit(OpCodes.Callvirt, _types.String.GetMethod("Substring", [_types.Int32])!);
        il.Emit(OpCodes.Ldc_I4_S, (byte)'+');
        il.Emit(OpCodes.Ldc_I4_S, (byte)' ');
        il.Emit(OpCodes.Callvirt, _types.String.GetMethod("Replace", [_types.Char, _types.Char])!);
        il.Emit(OpCodes.Call, _types.Uri.GetMethod("UnescapeDataString", [_types.String])!);
        il.Emit(OpCodes.Callvirt, listOfStringType.GetMethod("Add", [_types.String])!);

        il.Emit(OpCodes.Br, pairDoneLabel);

        // No '=' found: key = pair, value = ""
        il.MarkLabel(noEqLabel);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldfld, keysField);
        il.Emit(OpCodes.Ldloc, pairLocal);
        il.Emit(OpCodes.Ldc_I4_S, (byte)'+');
        il.Emit(OpCodes.Ldc_I4_S, (byte)' ');
        il.Emit(OpCodes.Callvirt, _types.String.GetMethod("Replace", [_types.Char, _types.Char])!);
        il.Emit(OpCodes.Call, _types.Uri.GetMethod("UnescapeDataString", [_types.String])!);
        il.Emit(OpCodes.Callvirt, listOfStringType.GetMethod("Add", [_types.String])!);

        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldfld, valuesField);
        il.Emit(OpCodes.Ldstr, "");
        il.Emit(OpCodes.Callvirt, listOfStringType.GetMethod("Add", [_types.String])!);

        il.MarkLabel(pairDoneLabel);

        // i++
        il.Emit(OpCodes.Ldloc, indexLocal);
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Add);
        il.Emit(OpCodes.Stloc, indexLocal);
        il.Emit(OpCodes.Br, loopStart);

        il.MarkLabel(loopEnd);
        il.Emit(OpCodes.Ret);
    }

    /// <summary>
    /// Emits ParseDictInit(Dictionary&lt;string, object?&gt; dict) — populates from dict entries.
    /// </summary>
    private void EmitUrlSearchParamsParseDictInit(MethodBuilder method, FieldBuilder keysField, FieldBuilder valuesField)
    {
        var il = method.GetILGenerator();
        var listOfStringType = typeof(List<string>);

        // Iterate over dictionary
        var getEnumeratorMethod = _types.DictionaryStringObject.GetMethod("GetEnumerator", Type.EmptyTypes)!;
        var enumeratorType = getEnumeratorMethod.ReturnType;
        var enumeratorLocal = il.DeclareLocal(enumeratorType);

        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Callvirt, getEnumeratorMethod);
        il.Emit(OpCodes.Stloc, enumeratorLocal);

        var loopStart = il.DefineLabel();
        var loopEnd = il.DefineLabel();

        il.MarkLabel(loopStart);
        il.Emit(OpCodes.Ldloca, enumeratorLocal);
        il.Emit(OpCodes.Call, enumeratorType.GetMethod("MoveNext")!);
        il.Emit(OpCodes.Brfalse, loopEnd);

        var currentProp = enumeratorType.GetProperty("Current")!;
        var kvpType = currentProp.PropertyType;
        var kvpLocal = il.DeclareLocal(kvpType);
        il.Emit(OpCodes.Ldloca, enumeratorLocal);
        il.Emit(OpCodes.Call, currentProp.GetGetMethod()!);
        il.Emit(OpCodes.Stloc, kvpLocal);

        // _keys.Add(kvp.Key)
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldfld, keysField);
        il.Emit(OpCodes.Ldloca, kvpLocal);
        il.Emit(OpCodes.Call, kvpType.GetProperty("Key")!.GetGetMethod()!);
        il.Emit(OpCodes.Callvirt, listOfStringType.GetMethod("Add", [_types.String])!);

        // _values.Add(kvp.Value?.ToString() ?? "")
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldfld, valuesField);
        il.Emit(OpCodes.Ldloca, kvpLocal);
        il.Emit(OpCodes.Call, kvpType.GetProperty("Value")!.GetGetMethod()!);
        var hasValueLabel = il.DefineLabel();
        var valueDoneLabel = il.DefineLabel();
        il.Emit(OpCodes.Dup);
        il.Emit(OpCodes.Brtrue, hasValueLabel);
        il.Emit(OpCodes.Pop);
        il.Emit(OpCodes.Ldstr, "");
        il.Emit(OpCodes.Br, valueDoneLabel);
        il.MarkLabel(hasValueLabel);
        il.Emit(OpCodes.Callvirt, _types.Object.GetMethod("ToString", Type.EmptyTypes)!);
        il.Emit(OpCodes.Dup);
        var notNullLabel = il.DefineLabel();
        il.Emit(OpCodes.Brtrue, notNullLabel);
        il.Emit(OpCodes.Pop);
        il.Emit(OpCodes.Ldstr, "");
        il.MarkLabel(notNullLabel);
        il.MarkLabel(valueDoneLabel);
        il.Emit(OpCodes.Callvirt, listOfStringType.GetMethod("Add", [_types.String])!);

        il.Emit(OpCodes.Br, loopStart);
        il.MarkLabel(loopEnd);

        // Dispose enumerator
        var disposeMethod = enumeratorType.GetMethod("Dispose", Type.EmptyTypes);
        if (disposeMethod != null)
        {
            il.Emit(OpCodes.Ldloca, enumeratorLocal);
            il.Emit(OpCodes.Call, disposeMethod);
        }

        il.Emit(OpCodes.Ret);
    }

    /// <summary>
    /// Emits GetMember(string name) on $URLSearchParams for property dispatch.
    /// "size" is a property (calls get_size directly), all others are methods wrapped in TSFunction.
    /// </summary>
    private void EmitUrlSearchParamsGetMember(
        TypeBuilder typeBuilder,
        EmittedRuntime runtime,
        Dictionary<string, MethodBuilder> methods,
        MethodBuilder sizeGetter)
    {
        var method = typeBuilder.DefineMethod("GetMember", MethodAttributes.Public, _types.Object, [_types.String]);
        var il = method.GetILGenerator();

        // "size" property — call get_size() directly
        var notSizeLabel = il.DefineLabel();
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Ldstr, "size");
        il.Emit(OpCodes.Call, _types.String.GetMethod("op_Equality", [_types.String, _types.String])!);
        il.Emit(OpCodes.Brfalse, notSizeLabel);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Call, sizeGetter);
        il.Emit(OpCodes.Ret);
        il.MarkLabel(notSizeLabel);

        // For each method, check name and wrap in TSFunction
        foreach (var (name, mb) in methods)
        {
            var nextLabel = il.DefineLabel();
            il.Emit(OpCodes.Ldarg_1);
            il.Emit(OpCodes.Ldstr, name);
            il.Emit(OpCodes.Call, _types.String.GetMethod("op_Equality", [_types.String, _types.String])!);
            il.Emit(OpCodes.Brfalse, nextLabel);
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Ldtoken, mb);
            il.Emit(OpCodes.Ldtoken, typeBuilder);
            il.Emit(OpCodes.Call, _types.GetMethod(_types.MethodBase, "GetMethodFromHandle", _types.RuntimeMethodHandle, _types.RuntimeTypeHandle));
            il.Emit(OpCodes.Castclass, _types.MethodInfo);
            il.Emit(OpCodes.Newobj, runtime.TSFunctionCtor);
            il.Emit(OpCodes.Ret);
            il.MarkLabel(nextLabel);
        }

        // Default: return null
        il.Emit(OpCodes.Ldnull);
        il.Emit(OpCodes.Ret);
    }

    /// <summary>
    /// Emits: public object get(object name) → string | null
    /// </summary>
    private MethodBuilder EmitUrlSearchParamsGet(TypeBuilder typeBuilder, FieldBuilder keysField, FieldBuilder valuesField)
    {
        var method = typeBuilder.DefineMethod("get", MethodAttributes.Public, _types.Object, [_types.Object]);
        var il = method.GetILGenerator();
        var listOfStringType = typeof(List<string>);

        var nameLocal = il.DeclareLocal(_types.String);
        EmitArgToString(il, 1, nameLocal);

        // for (int i = 0; i < _keys.Count; i++)
        var indexLocal = il.DeclareLocal(_types.Int32);
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Stloc, indexLocal);

        var loopStart = il.DefineLabel();
        var loopEnd = il.DefineLabel();

        il.MarkLabel(loopStart);
        il.Emit(OpCodes.Ldloc, indexLocal);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldfld, keysField);
        il.Emit(OpCodes.Callvirt, listOfStringType.GetProperty("Count")!.GetGetMethod()!);
        il.Emit(OpCodes.Bge, loopEnd);

        // if (_keys[i] == name) return _values[i]
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldfld, keysField);
        il.Emit(OpCodes.Ldloc, indexLocal);
        il.Emit(OpCodes.Callvirt, listOfStringType.GetMethod("get_Item", [_types.Int32])!);
        il.Emit(OpCodes.Ldloc, nameLocal);
        il.Emit(OpCodes.Call, _types.String.GetMethod("op_Equality", [_types.String, _types.String])!);
        var notMatchLabel = il.DefineLabel();
        il.Emit(OpCodes.Brfalse, notMatchLabel);

        // Found: return _values[i]
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldfld, valuesField);
        il.Emit(OpCodes.Ldloc, indexLocal);
        il.Emit(OpCodes.Callvirt, listOfStringType.GetMethod("get_Item", [_types.Int32])!);
        il.Emit(OpCodes.Ret);

        il.MarkLabel(notMatchLabel);
        il.Emit(OpCodes.Ldloc, indexLocal);
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Add);
        il.Emit(OpCodes.Stloc, indexLocal);
        il.Emit(OpCodes.Br, loopStart);

        il.MarkLabel(loopEnd);
        // Not found: return null
        il.Emit(OpCodes.Ldnull);
        il.Emit(OpCodes.Ret);
        return method;
    }

    /// <summary>
    /// Emits: public object getAll(object name) → object[] (as array via runtime.CreateArray)
    /// </summary>
    private MethodBuilder EmitUrlSearchParamsGetAll(TypeBuilder typeBuilder, FieldBuilder keysField, FieldBuilder valuesField, EmittedRuntime runtime)
    {
        var method = typeBuilder.DefineMethod("getAll", MethodAttributes.Public, _types.Object, [_types.Object]);
        var il = method.GetILGenerator();
        var listOfStringType = typeof(List<string>);
        var listOfObjectType = typeof(List<object?>);

        var nameLocal = il.DeclareLocal(_types.String);
        EmitArgToString(il, 1, nameLocal);

        // var result = new List<object?>()
        var resultLocal = il.DeclareLocal(listOfObjectType);
        il.Emit(OpCodes.Newobj, listOfObjectType.GetConstructor(Type.EmptyTypes)!);
        il.Emit(OpCodes.Stloc, resultLocal);

        // for loop
        var indexLocal = il.DeclareLocal(_types.Int32);
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Stloc, indexLocal);

        var loopStart = il.DefineLabel();
        var loopEnd = il.DefineLabel();

        il.MarkLabel(loopStart);
        il.Emit(OpCodes.Ldloc, indexLocal);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldfld, keysField);
        il.Emit(OpCodes.Callvirt, listOfStringType.GetProperty("Count")!.GetGetMethod()!);
        il.Emit(OpCodes.Bge, loopEnd);

        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldfld, keysField);
        il.Emit(OpCodes.Ldloc, indexLocal);
        il.Emit(OpCodes.Callvirt, listOfStringType.GetMethod("get_Item", [_types.Int32])!);
        il.Emit(OpCodes.Ldloc, nameLocal);
        il.Emit(OpCodes.Call, _types.String.GetMethod("op_Equality", [_types.String, _types.String])!);
        var notMatchLabel = il.DefineLabel();
        il.Emit(OpCodes.Brfalse, notMatchLabel);

        // result.Add(_values[i])
        il.Emit(OpCodes.Ldloc, resultLocal);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldfld, valuesField);
        il.Emit(OpCodes.Ldloc, indexLocal);
        il.Emit(OpCodes.Callvirt, listOfStringType.GetMethod("get_Item", [_types.Int32])!);
        il.Emit(OpCodes.Callvirt, listOfObjectType.GetMethod("Add", [typeof(object)])!);

        il.MarkLabel(notMatchLabel);
        il.Emit(OpCodes.Ldloc, indexLocal);
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Add);
        il.Emit(OpCodes.Stloc, indexLocal);
        il.Emit(OpCodes.Br, loopStart);

        il.MarkLabel(loopEnd);

        // return runtime.CreateArray(result.ToArray())
        il.Emit(OpCodes.Ldloc, resultLocal);
        il.Emit(OpCodes.Callvirt, listOfObjectType.GetMethod("ToArray")!);
        il.Emit(OpCodes.Call, runtime.CreateArray);
        il.Emit(OpCodes.Ret);
        return method;
    }

    /// <summary>
    /// Emits: public object has(object name) → bool (boxed)
    /// </summary>
    private MethodBuilder EmitUrlSearchParamsHas(TypeBuilder typeBuilder, FieldBuilder keysField)
    {
        var method = typeBuilder.DefineMethod("has", MethodAttributes.Public, _types.Object, [_types.Object]);
        var il = method.GetILGenerator();
        var listOfStringType = typeof(List<string>);

        var nameLocal = il.DeclareLocal(_types.String);
        EmitArgToString(il, 1, nameLocal);

        var indexLocal = il.DeclareLocal(_types.Int32);
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Stloc, indexLocal);

        var loopStart = il.DefineLabel();
        var loopEnd = il.DefineLabel();

        il.MarkLabel(loopStart);
        il.Emit(OpCodes.Ldloc, indexLocal);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldfld, keysField);
        il.Emit(OpCodes.Callvirt, listOfStringType.GetProperty("Count")!.GetGetMethod()!);
        il.Emit(OpCodes.Bge, loopEnd);

        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldfld, keysField);
        il.Emit(OpCodes.Ldloc, indexLocal);
        il.Emit(OpCodes.Callvirt, listOfStringType.GetMethod("get_Item", [_types.Int32])!);
        il.Emit(OpCodes.Ldloc, nameLocal);
        il.Emit(OpCodes.Call, _types.String.GetMethod("op_Equality", [_types.String, _types.String])!);
        var notMatchLabel = il.DefineLabel();
        il.Emit(OpCodes.Brfalse, notMatchLabel);

        // Found: return true
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Box, _types.Boolean);
        il.Emit(OpCodes.Ret);

        il.MarkLabel(notMatchLabel);
        il.Emit(OpCodes.Ldloc, indexLocal);
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Add);
        il.Emit(OpCodes.Stloc, indexLocal);
        il.Emit(OpCodes.Br, loopStart);

        il.MarkLabel(loopEnd);
        // Not found: return false
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Box, _types.Boolean);
        il.Emit(OpCodes.Ret);
        return method;
    }

    /// <summary>
    /// Emits: public object set(object name, object value) → undefined
    /// </summary>
    private MethodBuilder EmitUrlSearchParamsSet(TypeBuilder typeBuilder, FieldBuilder keysField, FieldBuilder valuesField)
    {
        var method = typeBuilder.DefineMethod("set", MethodAttributes.Public, _types.Object, [_types.Object, _types.Object]);
        var il = method.GetILGenerator();
        var listOfStringType = typeof(List<string>);

        var nameLocal = il.DeclareLocal(_types.String);
        EmitArgToString(il, 1, nameLocal);
        var valueLocal = il.DeclareLocal(_types.String);
        EmitArgToString(il, 2, valueLocal);

        // Remove existing entries with this key (iterate backwards)
        var indexLocal = il.DeclareLocal(_types.Int32);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldfld, keysField);
        il.Emit(OpCodes.Callvirt, listOfStringType.GetProperty("Count")!.GetGetMethod()!);
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Sub);
        il.Emit(OpCodes.Stloc, indexLocal);

        var removeLoopStart = il.DefineLabel();
        var removeLoopEnd = il.DefineLabel();

        il.MarkLabel(removeLoopStart);
        il.Emit(OpCodes.Ldloc, indexLocal);
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Blt, removeLoopEnd);

        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldfld, keysField);
        il.Emit(OpCodes.Ldloc, indexLocal);
        il.Emit(OpCodes.Callvirt, listOfStringType.GetMethod("get_Item", [_types.Int32])!);
        il.Emit(OpCodes.Ldloc, nameLocal);
        il.Emit(OpCodes.Call, _types.String.GetMethod("op_Equality", [_types.String, _types.String])!);
        var noRemoveLabel = il.DefineLabel();
        il.Emit(OpCodes.Brfalse, noRemoveLabel);

        // RemoveAt(i)
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldfld, keysField);
        il.Emit(OpCodes.Ldloc, indexLocal);
        il.Emit(OpCodes.Callvirt, listOfStringType.GetMethod("RemoveAt", [_types.Int32])!);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldfld, valuesField);
        il.Emit(OpCodes.Ldloc, indexLocal);
        il.Emit(OpCodes.Callvirt, listOfStringType.GetMethod("RemoveAt", [_types.Int32])!);

        il.MarkLabel(noRemoveLabel);
        il.Emit(OpCodes.Ldloc, indexLocal);
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Sub);
        il.Emit(OpCodes.Stloc, indexLocal);
        il.Emit(OpCodes.Br, removeLoopStart);

        il.MarkLabel(removeLoopEnd);

        // Add new entry
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldfld, keysField);
        il.Emit(OpCodes.Ldloc, nameLocal);
        il.Emit(OpCodes.Callvirt, listOfStringType.GetMethod("Add", [_types.String])!);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldfld, valuesField);
        il.Emit(OpCodes.Ldloc, valueLocal);
        il.Emit(OpCodes.Callvirt, listOfStringType.GetMethod("Add", [_types.String])!);

        il.Emit(OpCodes.Ldnull);
        il.Emit(OpCodes.Ret);
        return method;
    }

    /// <summary>
    /// Emits: public object append(object name, object value) → undefined
    /// </summary>
    private MethodBuilder EmitUrlSearchParamsAppend(TypeBuilder typeBuilder, FieldBuilder keysField, FieldBuilder valuesField)
    {
        var method = typeBuilder.DefineMethod("append", MethodAttributes.Public, _types.Object, [_types.Object, _types.Object]);
        var il = method.GetILGenerator();
        var listOfStringType = typeof(List<string>);

        var nameLocal = il.DeclareLocal(_types.String);
        EmitArgToString(il, 1, nameLocal);
        var valueLocal = il.DeclareLocal(_types.String);
        EmitArgToString(il, 2, valueLocal);

        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldfld, keysField);
        il.Emit(OpCodes.Ldloc, nameLocal);
        il.Emit(OpCodes.Callvirt, listOfStringType.GetMethod("Add", [_types.String])!);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldfld, valuesField);
        il.Emit(OpCodes.Ldloc, valueLocal);
        il.Emit(OpCodes.Callvirt, listOfStringType.GetMethod("Add", [_types.String])!);

        il.Emit(OpCodes.Ldnull);
        il.Emit(OpCodes.Ret);
        return method;
    }

    /// <summary>
    /// Emits: public object delete(object name) → undefined
    /// </summary>
    private MethodBuilder EmitUrlSearchParamsDelete(TypeBuilder typeBuilder, FieldBuilder keysField, FieldBuilder valuesField)
    {
        var method = typeBuilder.DefineMethod("delete", MethodAttributes.Public, _types.Object, [_types.Object]);
        var il = method.GetILGenerator();
        var listOfStringType = typeof(List<string>);

        var nameLocal = il.DeclareLocal(_types.String);
        EmitArgToString(il, 1, nameLocal);

        // Remove entries (iterate backwards)
        var indexLocal = il.DeclareLocal(_types.Int32);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldfld, keysField);
        il.Emit(OpCodes.Callvirt, listOfStringType.GetProperty("Count")!.GetGetMethod()!);
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Sub);
        il.Emit(OpCodes.Stloc, indexLocal);

        var loopStart = il.DefineLabel();
        var loopEnd = il.DefineLabel();

        il.MarkLabel(loopStart);
        il.Emit(OpCodes.Ldloc, indexLocal);
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Blt, loopEnd);

        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldfld, keysField);
        il.Emit(OpCodes.Ldloc, indexLocal);
        il.Emit(OpCodes.Callvirt, listOfStringType.GetMethod("get_Item", [_types.Int32])!);
        il.Emit(OpCodes.Ldloc, nameLocal);
        il.Emit(OpCodes.Call, _types.String.GetMethod("op_Equality", [_types.String, _types.String])!);
        var noRemoveLabel = il.DefineLabel();
        il.Emit(OpCodes.Brfalse, noRemoveLabel);

        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldfld, keysField);
        il.Emit(OpCodes.Ldloc, indexLocal);
        il.Emit(OpCodes.Callvirt, listOfStringType.GetMethod("RemoveAt", [_types.Int32])!);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldfld, valuesField);
        il.Emit(OpCodes.Ldloc, indexLocal);
        il.Emit(OpCodes.Callvirt, listOfStringType.GetMethod("RemoveAt", [_types.Int32])!);

        il.MarkLabel(noRemoveLabel);
        il.Emit(OpCodes.Ldloc, indexLocal);
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Sub);
        il.Emit(OpCodes.Stloc, indexLocal);
        il.Emit(OpCodes.Br, loopStart);

        il.MarkLabel(loopEnd);
        il.Emit(OpCodes.Ldnull);
        il.Emit(OpCodes.Ret);
        return method;
    }

    /// <summary>
    /// Emits: public object sort() → undefined
    /// Sorts keys and values in parallel by key name using bubble sort.
    /// </summary>
    private MethodBuilder EmitUrlSearchParamsSort(TypeBuilder typeBuilder, FieldBuilder keysField, FieldBuilder valuesField)
    {
        var method = typeBuilder.DefineMethod("sort", MethodAttributes.Public, _types.Object, Type.EmptyTypes);
        var il = method.GetILGenerator();
        var listOfStringType = typeof(List<string>);

        // Simple bubble sort on parallel lists
        var countLocal = il.DeclareLocal(_types.Int32);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldfld, keysField);
        il.Emit(OpCodes.Callvirt, listOfStringType.GetProperty("Count")!.GetGetMethod()!);
        il.Emit(OpCodes.Stloc, countLocal);

        // for (int i = 0; i < count - 1; i++)
        var iLocal = il.DeclareLocal(_types.Int32);
        var jLocal = il.DeclareLocal(_types.Int32);
        var tempLocal = il.DeclareLocal(_types.String);

        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Stloc, iLocal);
        var outerLoopStart = il.DefineLabel();
        var outerLoopEnd = il.DefineLabel();

        il.MarkLabel(outerLoopStart);
        il.Emit(OpCodes.Ldloc, iLocal);
        il.Emit(OpCodes.Ldloc, countLocal);
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Sub);
        il.Emit(OpCodes.Bge, outerLoopEnd);

        // for (int j = 0; j < count - 1 - i; j++)
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Stloc, jLocal);
        var innerLoopStart = il.DefineLabel();
        var innerLoopEnd = il.DefineLabel();

        il.MarkLabel(innerLoopStart);
        il.Emit(OpCodes.Ldloc, jLocal);
        il.Emit(OpCodes.Ldloc, countLocal);
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Sub);
        il.Emit(OpCodes.Ldloc, iLocal);
        il.Emit(OpCodes.Sub);
        il.Emit(OpCodes.Bge, innerLoopEnd);

        // if (string.Compare(_keys[j], _keys[j+1], StringComparison.Ordinal) > 0) swap
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldfld, keysField);
        il.Emit(OpCodes.Ldloc, jLocal);
        il.Emit(OpCodes.Callvirt, listOfStringType.GetMethod("get_Item", [_types.Int32])!);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldfld, keysField);
        il.Emit(OpCodes.Ldloc, jLocal);
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Add);
        il.Emit(OpCodes.Callvirt, listOfStringType.GetMethod("get_Item", [_types.Int32])!);
        il.Emit(OpCodes.Ldc_I4_4); // StringComparison.Ordinal
        il.Emit(OpCodes.Call, _types.String.GetMethod("Compare", [_types.String, _types.String, typeof(StringComparison)])!);
        var noSwapLabel = il.DefineLabel();
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Ble, noSwapLabel);

        // Swap keys[j] and keys[j+1]
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldfld, keysField);
        il.Emit(OpCodes.Ldloc, jLocal);
        il.Emit(OpCodes.Callvirt, listOfStringType.GetMethod("get_Item", [_types.Int32])!);
        il.Emit(OpCodes.Stloc, tempLocal);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldfld, keysField);
        il.Emit(OpCodes.Ldloc, jLocal);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldfld, keysField);
        il.Emit(OpCodes.Ldloc, jLocal);
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Add);
        il.Emit(OpCodes.Callvirt, listOfStringType.GetMethod("get_Item", [_types.Int32])!);
        il.Emit(OpCodes.Callvirt, listOfStringType.GetMethod("set_Item", [_types.Int32, _types.String])!);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldfld, keysField);
        il.Emit(OpCodes.Ldloc, jLocal);
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Add);
        il.Emit(OpCodes.Ldloc, tempLocal);
        il.Emit(OpCodes.Callvirt, listOfStringType.GetMethod("set_Item", [_types.Int32, _types.String])!);

        // Swap values[j] and values[j+1]
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldfld, valuesField);
        il.Emit(OpCodes.Ldloc, jLocal);
        il.Emit(OpCodes.Callvirt, listOfStringType.GetMethod("get_Item", [_types.Int32])!);
        il.Emit(OpCodes.Stloc, tempLocal);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldfld, valuesField);
        il.Emit(OpCodes.Ldloc, jLocal);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldfld, valuesField);
        il.Emit(OpCodes.Ldloc, jLocal);
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Add);
        il.Emit(OpCodes.Callvirt, listOfStringType.GetMethod("get_Item", [_types.Int32])!);
        il.Emit(OpCodes.Callvirt, listOfStringType.GetMethod("set_Item", [_types.Int32, _types.String])!);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldfld, valuesField);
        il.Emit(OpCodes.Ldloc, jLocal);
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Add);
        il.Emit(OpCodes.Ldloc, tempLocal);
        il.Emit(OpCodes.Callvirt, listOfStringType.GetMethod("set_Item", [_types.Int32, _types.String])!);

        il.MarkLabel(noSwapLabel);
        il.Emit(OpCodes.Ldloc, jLocal);
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Add);
        il.Emit(OpCodes.Stloc, jLocal);
        il.Emit(OpCodes.Br, innerLoopStart);

        il.MarkLabel(innerLoopEnd);
        il.Emit(OpCodes.Ldloc, iLocal);
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Add);
        il.Emit(OpCodes.Stloc, iLocal);
        il.Emit(OpCodes.Br, outerLoopStart);

        il.MarkLabel(outerLoopEnd);
        il.Emit(OpCodes.Ldnull);
        il.Emit(OpCodes.Ret);
        return method;
    }

    /// <summary>
    /// Emits: public object toString() → string (URL-encoded query string)
    /// Uses Uri.EscapeDataString for each key/value and joins with "&amp;".
    /// </summary>
    private MethodBuilder EmitUrlSearchParamsToString(TypeBuilder typeBuilder, FieldBuilder keysField, FieldBuilder valuesField)
    {
        var method = typeBuilder.DefineMethod("toString", MethodAttributes.Public, _types.Object, Type.EmptyTypes);
        var il = method.GetILGenerator();
        var listOfStringType = typeof(List<string>);

        // Build result using List<string> for pairs, then string.Join
        var pairsLocal = il.DeclareLocal(listOfStringType);
        il.Emit(OpCodes.Newobj, listOfStringType.GetConstructor(Type.EmptyTypes)!);
        il.Emit(OpCodes.Stloc, pairsLocal);

        var indexLocal = il.DeclareLocal(_types.Int32);
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Stloc, indexLocal);

        var loopStart = il.DefineLabel();
        var loopEnd = il.DefineLabel();

        il.MarkLabel(loopStart);
        il.Emit(OpCodes.Ldloc, indexLocal);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldfld, keysField);
        il.Emit(OpCodes.Callvirt, listOfStringType.GetProperty("Count")!.GetGetMethod()!);
        il.Emit(OpCodes.Bge, loopEnd);

        // pairs.Add(Uri.EscapeDataString(key) + "=" + Uri.EscapeDataString(value))
        il.Emit(OpCodes.Ldloc, pairsLocal);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldfld, keysField);
        il.Emit(OpCodes.Ldloc, indexLocal);
        il.Emit(OpCodes.Callvirt, listOfStringType.GetMethod("get_Item", [_types.Int32])!);
        il.Emit(OpCodes.Call, _types.Uri.GetMethod("EscapeDataString", [_types.String])!);
        il.Emit(OpCodes.Ldstr, "=");
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldfld, valuesField);
        il.Emit(OpCodes.Ldloc, indexLocal);
        il.Emit(OpCodes.Callvirt, listOfStringType.GetMethod("get_Item", [_types.Int32])!);
        il.Emit(OpCodes.Call, _types.Uri.GetMethod("EscapeDataString", [_types.String])!);
        il.Emit(OpCodes.Call, _types.String.GetMethod("Concat", [_types.String, _types.String, _types.String])!);
        il.Emit(OpCodes.Callvirt, listOfStringType.GetMethod("Add", [_types.String])!);

        il.Emit(OpCodes.Ldloc, indexLocal);
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Add);
        il.Emit(OpCodes.Stloc, indexLocal);
        il.Emit(OpCodes.Br, loopStart);

        il.MarkLabel(loopEnd);

        // return string.Join("&", pairs)
        il.Emit(OpCodes.Ldstr, "&");
        il.Emit(OpCodes.Ldloc, pairsLocal);
        il.Emit(OpCodes.Call, _types.String.GetMethod("Join", [_types.String, typeof(IEnumerable<string>)])!);
        il.Emit(OpCodes.Ret);
        return method;
    }

    /// <summary>
    /// Emits: public object get_size() → double (boxed count)
    /// Also named "size" as a property getter.
    /// </summary>
    private MethodBuilder EmitUrlSearchParamsSize(TypeBuilder typeBuilder, FieldBuilder keysField)
    {
        var method = typeBuilder.DefineMethod("get_size", MethodAttributes.Public, _types.Object, Type.EmptyTypes);
        var il = method.GetILGenerator();
        var listOfStringType = typeof(List<string>);

        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldfld, keysField);
        il.Emit(OpCodes.Callvirt, listOfStringType.GetProperty("Count")!.GetGetMethod()!);
        il.Emit(OpCodes.Conv_R8);
        il.Emit(OpCodes.Box, typeof(double));
        il.Emit(OpCodes.Ret);
        return method;
    }

    /// <summary>
    /// Emits: public object forEach(object callback) → undefined
    /// Calls callback(value, key) for each entry.
    /// </summary>
    private MethodBuilder EmitUrlSearchParamsForEach(TypeBuilder typeBuilder, FieldBuilder keysField, FieldBuilder valuesField, EmittedRuntime runtime)
    {
        var method = typeBuilder.DefineMethod("forEach", MethodAttributes.Public, _types.Object, [_types.Object]);
        var il = method.GetILGenerator();
        var listOfStringType = typeof(List<string>);

        // Cast callback to TSFunction and call Invoke
        var callbackLocal = il.DeclareLocal(_types.Object);
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Stloc, callbackLocal);

        var indexLocal = il.DeclareLocal(_types.Int32);
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Stloc, indexLocal);

        var loopStart = il.DefineLabel();
        var loopEnd = il.DefineLabel();

        il.MarkLabel(loopStart);
        il.Emit(OpCodes.Ldloc, indexLocal);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldfld, keysField);
        il.Emit(OpCodes.Callvirt, listOfStringType.GetProperty("Count")!.GetGetMethod()!);
        il.Emit(OpCodes.Bge, loopEnd);

        // Call callback via TSFunction.Invoke(new object?[] { value, key })
        il.Emit(OpCodes.Ldloc, callbackLocal);
        il.Emit(OpCodes.Ldc_I4_2);
        il.Emit(OpCodes.Newarr, _types.Object);
        il.Emit(OpCodes.Dup);
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldfld, valuesField);
        il.Emit(OpCodes.Ldloc, indexLocal);
        il.Emit(OpCodes.Callvirt, listOfStringType.GetMethod("get_Item", [_types.Int32])!);
        il.Emit(OpCodes.Stelem_Ref);
        il.Emit(OpCodes.Dup);
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldfld, keysField);
        il.Emit(OpCodes.Ldloc, indexLocal);
        il.Emit(OpCodes.Callvirt, listOfStringType.GetMethod("get_Item", [_types.Int32])!);
        il.Emit(OpCodes.Stelem_Ref);
        il.Emit(OpCodes.Callvirt, runtime.TSFunctionInvoke);
        il.Emit(OpCodes.Pop);

        il.Emit(OpCodes.Ldloc, indexLocal);
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Add);
        il.Emit(OpCodes.Stloc, indexLocal);
        il.Emit(OpCodes.Br, loopStart);

        il.MarkLabel(loopEnd);
        il.Emit(OpCodes.Ldnull);
        il.Emit(OpCodes.Ret);
        return method;
    }

    /// <summary>
    /// Emits: public object entries() → iterator of [key, value] arrays
    /// </summary>
    private MethodBuilder EmitUrlSearchParamsEntries(TypeBuilder typeBuilder, FieldBuilder keysField, FieldBuilder valuesField, EmittedRuntime runtime)
    {
        var method = typeBuilder.DefineMethod("entries", MethodAttributes.Public, _types.Object, Type.EmptyTypes);
        var il = method.GetILGenerator();
        var listOfStringType = typeof(List<string>);
        var listOfObjectType = typeof(List<object?>);

        var resultLocal = il.DeclareLocal(listOfObjectType);
        il.Emit(OpCodes.Newobj, listOfObjectType.GetConstructor(Type.EmptyTypes)!);
        il.Emit(OpCodes.Stloc, resultLocal);

        var indexLocal = il.DeclareLocal(_types.Int32);
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Stloc, indexLocal);

        var loopStart = il.DefineLabel();
        var loopEnd = il.DefineLabel();

        il.MarkLabel(loopStart);
        il.Emit(OpCodes.Ldloc, indexLocal);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldfld, keysField);
        il.Emit(OpCodes.Callvirt, listOfStringType.GetProperty("Count")!.GetGetMethod()!);
        il.Emit(OpCodes.Bge, loopEnd);

        // result.Add(CreateArray([key, value]))
        il.Emit(OpCodes.Ldloc, resultLocal);
        il.Emit(OpCodes.Ldc_I4_2);
        il.Emit(OpCodes.Newarr, _types.Object);
        il.Emit(OpCodes.Dup);
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldfld, keysField);
        il.Emit(OpCodes.Ldloc, indexLocal);
        il.Emit(OpCodes.Callvirt, listOfStringType.GetMethod("get_Item", [_types.Int32])!);
        il.Emit(OpCodes.Stelem_Ref);
        il.Emit(OpCodes.Dup);
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldfld, valuesField);
        il.Emit(OpCodes.Ldloc, indexLocal);
        il.Emit(OpCodes.Callvirt, listOfStringType.GetMethod("get_Item", [_types.Int32])!);
        il.Emit(OpCodes.Stelem_Ref);
        il.Emit(OpCodes.Call, runtime.CreateArray);
        il.Emit(OpCodes.Callvirt, listOfObjectType.GetMethod("Add", [typeof(object)])!);

        il.Emit(OpCodes.Ldloc, indexLocal);
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Add);
        il.Emit(OpCodes.Stloc, indexLocal);
        il.Emit(OpCodes.Br, loopStart);

        il.MarkLabel(loopEnd);

        // return CreateIterator(result.ToArray())
        il.Emit(OpCodes.Ldloc, resultLocal);
        il.Emit(OpCodes.Callvirt, listOfObjectType.GetMethod("ToArray")!);
        il.Emit(OpCodes.Call, runtime.CreateArray);
        il.Emit(OpCodes.Ret);
        return method;
    }

    /// <summary>
    /// Emits: public object keys() → iterator of key strings
    /// </summary>
    private MethodBuilder EmitUrlSearchParamsKeys(TypeBuilder typeBuilder, FieldBuilder keysField, EmittedRuntime runtime)
    {
        var method = typeBuilder.DefineMethod("keys", MethodAttributes.Public, _types.Object, Type.EmptyTypes);
        var il = method.GetILGenerator();
        var listOfStringType = typeof(List<string>);
        var listOfObjectType = typeof(List<object?>);

        var resultLocal = il.DeclareLocal(listOfObjectType);
        il.Emit(OpCodes.Newobj, listOfObjectType.GetConstructor(Type.EmptyTypes)!);
        il.Emit(OpCodes.Stloc, resultLocal);

        var indexLocal = il.DeclareLocal(_types.Int32);
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Stloc, indexLocal);

        var loopStart = il.DefineLabel();
        var loopEnd = il.DefineLabel();

        il.MarkLabel(loopStart);
        il.Emit(OpCodes.Ldloc, indexLocal);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldfld, keysField);
        il.Emit(OpCodes.Callvirt, listOfStringType.GetProperty("Count")!.GetGetMethod()!);
        il.Emit(OpCodes.Bge, loopEnd);

        il.Emit(OpCodes.Ldloc, resultLocal);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldfld, keysField);
        il.Emit(OpCodes.Ldloc, indexLocal);
        il.Emit(OpCodes.Callvirt, listOfStringType.GetMethod("get_Item", [_types.Int32])!);
        il.Emit(OpCodes.Callvirt, listOfObjectType.GetMethod("Add", [typeof(object)])!);

        il.Emit(OpCodes.Ldloc, indexLocal);
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Add);
        il.Emit(OpCodes.Stloc, indexLocal);
        il.Emit(OpCodes.Br, loopStart);

        il.MarkLabel(loopEnd);

        il.Emit(OpCodes.Ldloc, resultLocal);
        il.Emit(OpCodes.Callvirt, listOfObjectType.GetMethod("ToArray")!);
        il.Emit(OpCodes.Call, runtime.CreateArray);
        il.Emit(OpCodes.Ret);
        return method;
    }

    /// <summary>
    /// Emits: public object values() → iterator of value strings
    /// </summary>
    private MethodBuilder EmitUrlSearchParamsValues(TypeBuilder typeBuilder, FieldBuilder valuesField, EmittedRuntime runtime)
    {
        var method = typeBuilder.DefineMethod("values", MethodAttributes.Public, _types.Object, Type.EmptyTypes);
        var il = method.GetILGenerator();
        var listOfStringType = typeof(List<string>);
        var listOfObjectType = typeof(List<object?>);

        var resultLocal = il.DeclareLocal(listOfObjectType);
        il.Emit(OpCodes.Newobj, listOfObjectType.GetConstructor(Type.EmptyTypes)!);
        il.Emit(OpCodes.Stloc, resultLocal);

        var indexLocal = il.DeclareLocal(_types.Int32);
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Stloc, indexLocal);

        var loopStart = il.DefineLabel();
        var loopEnd = il.DefineLabel();

        il.MarkLabel(loopStart);
        il.Emit(OpCodes.Ldloc, indexLocal);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldfld, valuesField);
        il.Emit(OpCodes.Callvirt, listOfStringType.GetProperty("Count")!.GetGetMethod()!);
        il.Emit(OpCodes.Bge, loopEnd);

        il.Emit(OpCodes.Ldloc, resultLocal);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldfld, valuesField);
        il.Emit(OpCodes.Ldloc, indexLocal);
        il.Emit(OpCodes.Callvirt, listOfStringType.GetMethod("get_Item", [_types.Int32])!);
        il.Emit(OpCodes.Callvirt, listOfObjectType.GetMethod("Add", [typeof(object)])!);

        il.Emit(OpCodes.Ldloc, indexLocal);
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Add);
        il.Emit(OpCodes.Stloc, indexLocal);
        il.Emit(OpCodes.Br, loopStart);

        il.MarkLabel(loopEnd);

        il.Emit(OpCodes.Ldloc, resultLocal);
        il.Emit(OpCodes.Callvirt, listOfObjectType.GetMethod("ToArray")!);
        il.Emit(OpCodes.Call, runtime.CreateArray);
        il.Emit(OpCodes.Ret);
        return method;
    }

    /// <summary>
    /// Emits the $URL class.
    /// Fields: _uri (System.Uri), _searchParams ($URLSearchParams or null)
    /// Constructor: $URL(object? url, object? base)
    /// </summary>
    private void EmitUrlClass(ModuleBuilder moduleBuilder, EmittedRuntime runtime)
    {
        var typeBuilder = moduleBuilder.DefineType(
            "$URL",
            TypeAttributes.Public | TypeAttributes.Class | TypeAttributes.BeforeFieldInit,
            _types.Object
        );

        _urlUriField = typeBuilder.DefineField("_uri", _types.Uri, FieldAttributes.Private);
        _urlSearchParamsField = typeBuilder.DefineField("_searchParams", runtime.TSUrlSearchParamsType, FieldAttributes.Private);

        // Constructor: $URL(object? url, object? base)
        var ctor = typeBuilder.DefineConstructor(
            MethodAttributes.Public,
            CallingConventions.Standard,
            [_types.Object, _types.Object]
        );

        var ctorIL = ctor.GetILGenerator();
        ctorIL.Emit(OpCodes.Ldarg_0);
        ctorIL.Emit(OpCodes.Call, _types.Object.GetConstructor(Type.EmptyTypes)!);

        // string urlStr = url?.ToString() ?? ""
        var urlStrLocal = ctorIL.DeclareLocal(_types.String);
        EmitArgToStringCtor(ctorIL, 1, urlStrLocal);

        // if (base != null)
        var noBaseLabel = ctorIL.DefineLabel();
        ctorIL.Emit(OpCodes.Ldarg_2);
        ctorIL.Emit(OpCodes.Brfalse, noBaseLabel);

        // string baseStr = base.ToString()
        var baseStrLocal = ctorIL.DeclareLocal(_types.String);
        EmitArgToStringCtor(ctorIL, 2, baseStrLocal);

        // var baseUri = new Uri(baseStr, UriKind.Absolute)
        var baseUriLocal = ctorIL.DeclareLocal(_types.Uri);
        ctorIL.Emit(OpCodes.Ldloc, baseStrLocal);
        ctorIL.Emit(OpCodes.Ldc_I4_1); // UriKind.Absolute
        ctorIL.Emit(OpCodes.Newobj, _types.Uri.GetConstructor([_types.String, _types.UriKind])!);
        ctorIL.Emit(OpCodes.Stloc, baseUriLocal);

        // _uri = new Uri(baseUri, urlStr)
        ctorIL.Emit(OpCodes.Ldarg_0);
        ctorIL.Emit(OpCodes.Ldloc, baseUriLocal);
        ctorIL.Emit(OpCodes.Ldloc, urlStrLocal);
        ctorIL.Emit(OpCodes.Newobj, _types.Uri.GetConstructor([_types.Uri, _types.String])!);
        ctorIL.Emit(OpCodes.Stfld, _urlUriField);
        var ctorDoneLabel = ctorIL.DefineLabel();
        ctorIL.Emit(OpCodes.Br, ctorDoneLabel);

        ctorIL.MarkLabel(noBaseLabel);
        // _uri = new Uri(urlStr, UriKind.Absolute)
        ctorIL.Emit(OpCodes.Ldarg_0);
        ctorIL.Emit(OpCodes.Ldloc, urlStrLocal);
        ctorIL.Emit(OpCodes.Ldc_I4_1); // UriKind.Absolute
        ctorIL.Emit(OpCodes.Newobj, _types.Uri.GetConstructor([_types.String, _types.UriKind])!);
        ctorIL.Emit(OpCodes.Stfld, _urlUriField);

        ctorIL.MarkLabel(ctorDoneLabel);
        ctorIL.Emit(OpCodes.Ret);

        // Emit property getter methods and collect MethodBuilders for GetMember dispatch
        var getters = new Dictionary<string, MethodBuilder>();

        getters["href"] = EmitUrlPropertyGetter(typeBuilder, "get_href", il =>
        {
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Ldfld, _urlUriField);
            il.Emit(OpCodes.Callvirt, _types.Uri.GetProperty("AbsoluteUri")!.GetGetMethod()!);
        });

        getters["protocol"] = EmitUrlPropertyGetter(typeBuilder, "get_protocol", il =>
        {
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Ldfld, _urlUriField);
            il.Emit(OpCodes.Callvirt, _types.Uri.GetProperty("Scheme")!.GetGetMethod()!);
            il.Emit(OpCodes.Ldstr, ":");
            il.Emit(OpCodes.Call, _types.String.GetMethod("Concat", [_types.String, _types.String])!);
        });

        getters["hostname"] = EmitUrlPropertyGetter(typeBuilder, "get_hostname", il =>
        {
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Ldfld, _urlUriField);
            il.Emit(OpCodes.Callvirt, _types.Uri.GetProperty("Host")!.GetGetMethod()!);
        });

        getters["host"] = EmitUrlHostGetter(typeBuilder);
        getters["port"] = EmitUrlPortGetter(typeBuilder);

        getters["pathname"] = EmitUrlPropertyGetter(typeBuilder, "get_pathname", il =>
        {
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Ldfld, _urlUriField);
            il.Emit(OpCodes.Callvirt, _types.Uri.GetProperty("AbsolutePath")!.GetGetMethod()!);
        });

        getters["search"] = EmitUrlSearchGetter(typeBuilder);
        getters["hash"] = EmitUrlHashGetter(typeBuilder);
        getters["origin"] = EmitUrlOriginGetter(typeBuilder);
        getters["username"] = EmitUrlUsernameGetter(typeBuilder);
        getters["password"] = EmitUrlPasswordGetter(typeBuilder);
        getters["searchParams"] = EmitUrlSearchParamsGetter(typeBuilder, runtime);
        var toStringMethod = EmitUrlToStringMethod(typeBuilder);
        var toJsonMethod = EmitUrlToJsonMethod(typeBuilder);

        // Emit GetMember(string name) for property dispatch
        EmitUrlGetMember(typeBuilder, runtime, getters, toStringMethod, toJsonMethod);

        runtime.TSUrlType = typeBuilder;
        runtime.TSUrlCtor = ctor;

        typeBuilder.CreateType();
    }

    /// <summary>
    /// Helper to emit a simple property getter method on $URL.
    /// </summary>
    private void EmitUrlGetMember(
        TypeBuilder typeBuilder,
        EmittedRuntime runtime,
        Dictionary<string, MethodBuilder> getters,
        MethodBuilder toStringMethod,
        MethodBuilder toJsonMethod)
    {
        var method = typeBuilder.DefineMethod("GetMember", MethodAttributes.Public, _types.Object, [_types.String]);
        var il = method.GetILGenerator();
        var defaultLabel = il.DefineLabel();

        // For each property, check if name matches and call the getter
        foreach (var (name, getter) in getters)
        {
            var nextLabel = il.DefineLabel();
            il.Emit(OpCodes.Ldarg_1);
            il.Emit(OpCodes.Ldstr, name);
            il.Emit(OpCodes.Call, _types.String.GetMethod("op_Equality", [_types.String, _types.String])!);
            il.Emit(OpCodes.Brfalse, nextLabel);
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Call, getter);
            il.Emit(OpCodes.Ret);
            il.MarkLabel(nextLabel);
        }

        // For toString method, wrap in TSFunction
        var notToStringLabel = il.DefineLabel();
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Ldstr, "toString");
        il.Emit(OpCodes.Call, _types.String.GetMethod("op_Equality", [_types.String, _types.String])!);
        il.Emit(OpCodes.Brfalse, notToStringLabel);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldtoken, toStringMethod);
        il.Emit(OpCodes.Ldtoken, typeBuilder);
        il.Emit(OpCodes.Call, _types.GetMethod(_types.MethodBase, "GetMethodFromHandle", _types.RuntimeMethodHandle, _types.RuntimeTypeHandle));
        il.Emit(OpCodes.Castclass, _types.MethodInfo);
        il.Emit(OpCodes.Newobj, runtime.TSFunctionCtor);
        il.Emit(OpCodes.Ret);
        il.MarkLabel(notToStringLabel);

        // For toJSON method, wrap in TSFunction
        var notToJsonLabel = il.DefineLabel();
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Ldstr, "toJSON");
        il.Emit(OpCodes.Call, _types.String.GetMethod("op_Equality", [_types.String, _types.String])!);
        il.Emit(OpCodes.Brfalse, notToJsonLabel);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldtoken, toJsonMethod);
        il.Emit(OpCodes.Ldtoken, typeBuilder);
        il.Emit(OpCodes.Call, _types.GetMethod(_types.MethodBase, "GetMethodFromHandle", _types.RuntimeMethodHandle, _types.RuntimeTypeHandle));
        il.Emit(OpCodes.Castclass, _types.MethodInfo);
        il.Emit(OpCodes.Newobj, runtime.TSFunctionCtor);
        il.Emit(OpCodes.Ret);
        il.MarkLabel(notToJsonLabel);

        // Default: return null
        il.Emit(OpCodes.Ldnull);
        il.Emit(OpCodes.Ret);
    }

    private MethodBuilder EmitUrlPropertyGetter(TypeBuilder typeBuilder, string methodName, Action<ILGenerator> emitBody)
    {
        var method = typeBuilder.DefineMethod(methodName, MethodAttributes.Public, _types.Object, Type.EmptyTypes);
        var il = method.GetILGenerator();
        emitBody(il);
        il.Emit(OpCodes.Ret);
        return method;
    }

    /// <summary>
    /// Emits: public object get_host() — hostname:port or just hostname
    /// </summary>
    private MethodBuilder EmitUrlHostGetter(TypeBuilder typeBuilder)
    {
        var method = typeBuilder.DefineMethod("get_host", MethodAttributes.Public, _types.Object, Type.EmptyTypes);
        var il = method.GetILGenerator();

        // if (_uri.IsDefaultPort) return _uri.Host else return _uri.Host + ":" + _uri.Port
        var defaultPortLabel = il.DefineLabel();
        var doneLabel = il.DefineLabel();

        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldfld, _urlUriField);
        il.Emit(OpCodes.Callvirt, _types.Uri.GetProperty("IsDefaultPort")!.GetGetMethod()!);
        il.Emit(OpCodes.Brtrue, defaultPortLabel);

        // Non-default: host + ":" + port.ToString()
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldfld, _urlUriField);
        il.Emit(OpCodes.Callvirt, _types.Uri.GetProperty("Host")!.GetGetMethod()!);
        il.Emit(OpCodes.Ldstr, ":");
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldfld, _urlUriField);
        il.Emit(OpCodes.Callvirt, _types.Uri.GetProperty("Port")!.GetGetMethod()!);
        il.Emit(OpCodes.Box, _types.Int32);
        il.Emit(OpCodes.Callvirt, _types.Object.GetMethod("ToString", Type.EmptyTypes)!);
        il.Emit(OpCodes.Call, _types.String.GetMethod("Concat", [_types.String, _types.String, _types.String])!);
        il.Emit(OpCodes.Br, doneLabel);

        il.MarkLabel(defaultPortLabel);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldfld, _urlUriField);
        il.Emit(OpCodes.Callvirt, _types.Uri.GetProperty("Host")!.GetGetMethod()!);

        il.MarkLabel(doneLabel);
        il.Emit(OpCodes.Ret);
        return method;
    }

    /// <summary>
    /// Emits: public object get_port() — port string or ""
    /// </summary>
    private MethodBuilder EmitUrlPortGetter(TypeBuilder typeBuilder)
    {
        var method = typeBuilder.DefineMethod("get_port", MethodAttributes.Public, _types.Object, Type.EmptyTypes);
        var il = method.GetILGenerator();

        var defaultPortLabel = il.DefineLabel();
        var doneLabel = il.DefineLabel();

        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldfld, _urlUriField);
        il.Emit(OpCodes.Callvirt, _types.Uri.GetProperty("IsDefaultPort")!.GetGetMethod()!);
        il.Emit(OpCodes.Brtrue, defaultPortLabel);

        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldfld, _urlUriField);
        il.Emit(OpCodes.Callvirt, _types.Uri.GetProperty("Port")!.GetGetMethod()!);
        il.Emit(OpCodes.Box, _types.Int32);
        il.Emit(OpCodes.Callvirt, _types.Object.GetMethod("ToString", Type.EmptyTypes)!);
        il.Emit(OpCodes.Br, doneLabel);

        il.MarkLabel(defaultPortLabel);
        il.Emit(OpCodes.Ldstr, "");

        il.MarkLabel(doneLabel);
        il.Emit(OpCodes.Ret);
        return method;
    }

    /// <summary>
    /// Emits: public object get_search() — "?query" or ""
    /// </summary>
    private MethodBuilder EmitUrlSearchGetter(TypeBuilder typeBuilder)
    {
        var method = typeBuilder.DefineMethod("get_search", MethodAttributes.Public, _types.Object, Type.EmptyTypes);
        var il = method.GetILGenerator();

        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldfld, _urlUriField);
        il.Emit(OpCodes.Callvirt, _types.Uri.GetProperty("Query")!.GetGetMethod()!);
        il.Emit(OpCodes.Dup);
        il.Emit(OpCodes.Call, _types.String.GetMethod("IsNullOrEmpty", [_types.String])!);
        var notEmptyLabel = il.DefineLabel();
        il.Emit(OpCodes.Brfalse, notEmptyLabel);
        il.Emit(OpCodes.Pop);
        il.Emit(OpCodes.Ldstr, "");
        il.MarkLabel(notEmptyLabel);
        il.Emit(OpCodes.Ret);
        return method;
    }

    /// <summary>
    /// Emits: public object get_hash() — "#fragment" or ""
    /// </summary>
    private MethodBuilder EmitUrlHashGetter(TypeBuilder typeBuilder)
    {
        var method = typeBuilder.DefineMethod("get_hash", MethodAttributes.Public, _types.Object, Type.EmptyTypes);
        var il = method.GetILGenerator();

        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldfld, _urlUriField);
        il.Emit(OpCodes.Callvirt, _types.Uri.GetProperty("Fragment")!.GetGetMethod()!);
        il.Emit(OpCodes.Dup);
        il.Emit(OpCodes.Call, _types.String.GetMethod("IsNullOrEmpty", [_types.String])!);
        var notEmptyLabel = il.DefineLabel();
        il.Emit(OpCodes.Brfalse, notEmptyLabel);
        il.Emit(OpCodes.Pop);
        il.Emit(OpCodes.Ldstr, "");
        il.MarkLabel(notEmptyLabel);
        il.Emit(OpCodes.Ret);
        return method;
    }

    /// <summary>
    /// Emits: public object get_origin() — "protocol//host"
    /// </summary>
    private MethodBuilder EmitUrlOriginGetter(TypeBuilder typeBuilder)
    {
        var method = typeBuilder.DefineMethod("get_origin", MethodAttributes.Public, _types.Object, Type.EmptyTypes);
        var il = method.GetILGenerator();

        // protocol = scheme + ":"
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldfld, _urlUriField);
        il.Emit(OpCodes.Callvirt, _types.Uri.GetProperty("Scheme")!.GetGetMethod()!);
        il.Emit(OpCodes.Ldstr, "://");

        // host part
        var defaultPortLabel = il.DefineLabel();
        var hostDoneLabel = il.DefineLabel();
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldfld, _urlUriField);
        il.Emit(OpCodes.Callvirt, _types.Uri.GetProperty("IsDefaultPort")!.GetGetMethod()!);
        il.Emit(OpCodes.Brtrue, defaultPortLabel);

        // Non-default port: host + ":" + port
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldfld, _urlUriField);
        il.Emit(OpCodes.Callvirt, _types.Uri.GetProperty("Host")!.GetGetMethod()!);
        il.Emit(OpCodes.Ldstr, ":");
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldfld, _urlUriField);
        il.Emit(OpCodes.Callvirt, _types.Uri.GetProperty("Port")!.GetGetMethod()!);
        il.Emit(OpCodes.Box, _types.Int32);
        il.Emit(OpCodes.Callvirt, _types.Object.GetMethod("ToString", Type.EmptyTypes)!);
        il.Emit(OpCodes.Call, _types.String.GetMethod("Concat", [_types.String, _types.String, _types.String])!);
        il.Emit(OpCodes.Br, hostDoneLabel);

        il.MarkLabel(defaultPortLabel);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldfld, _urlUriField);
        il.Emit(OpCodes.Callvirt, _types.Uri.GetProperty("Host")!.GetGetMethod()!);

        il.MarkLabel(hostDoneLabel);

        // Concat all: scheme + "://" + host
        il.Emit(OpCodes.Call, _types.String.GetMethod("Concat", [_types.String, _types.String, _types.String])!);
        il.Emit(OpCodes.Ret);
        return method;
    }

    /// <summary>
    /// Emits: public object get_username()
    /// </summary>
    private MethodBuilder EmitUrlUsernameGetter(TypeBuilder typeBuilder)
    {
        var method = typeBuilder.DefineMethod("get_username", MethodAttributes.Public, _types.Object, Type.EmptyTypes);
        var il = method.GetILGenerator();

        // Uri.UnescapeDataString(_uri.UserInfo.Split(':')[0])
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldfld, _urlUriField);
        il.Emit(OpCodes.Callvirt, _types.Uri.GetProperty("UserInfo")!.GetGetMethod()!);
        il.Emit(OpCodes.Ldc_I4_S, (byte)':');
        il.Emit(OpCodes.Ldc_I4_0); // StringSplitOptions.None
        il.Emit(OpCodes.Call, _types.String.GetMethod("Split", [_types.Char, typeof(StringSplitOptions)])!);
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Ldelem_Ref);
        il.Emit(OpCodes.Call, _types.Uri.GetMethod("UnescapeDataString", [_types.String])!);
        il.Emit(OpCodes.Ret);
        return method;
    }

    /// <summary>
    /// Emits: public object get_password()
    /// </summary>
    private MethodBuilder EmitUrlPasswordGetter(TypeBuilder typeBuilder)
    {
        var method = typeBuilder.DefineMethod("get_password", MethodAttributes.Public, _types.Object, Type.EmptyTypes);
        var il = method.GetILGenerator();

        // var parts = _uri.UserInfo.Split(':')
        var partsLocal = il.DeclareLocal(typeof(string[]));
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldfld, _urlUriField);
        il.Emit(OpCodes.Callvirt, _types.Uri.GetProperty("UserInfo")!.GetGetMethod()!);
        il.Emit(OpCodes.Ldc_I4_S, (byte)':');
        il.Emit(OpCodes.Ldc_I4_0); // StringSplitOptions.None
        il.Emit(OpCodes.Call, _types.String.GetMethod("Split", [_types.Char, typeof(StringSplitOptions)])!);
        il.Emit(OpCodes.Stloc, partsLocal);

        // if (parts.Length > 1) return UnescapeDataString(parts[1]) else return ""
        var noPasswordLabel = il.DefineLabel();
        il.Emit(OpCodes.Ldloc, partsLocal);
        il.Emit(OpCodes.Ldlen);
        il.Emit(OpCodes.Conv_I4);
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Ble, noPasswordLabel);

        il.Emit(OpCodes.Ldloc, partsLocal);
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Ldelem_Ref);
        il.Emit(OpCodes.Call, _types.Uri.GetMethod("UnescapeDataString", [_types.String])!);
        il.Emit(OpCodes.Ret);

        il.MarkLabel(noPasswordLabel);
        il.Emit(OpCodes.Ldstr, "");
        il.Emit(OpCodes.Ret);
        return method;
    }

    /// <summary>
    /// Emits: public object get_searchParams() — lazy-creates $URLSearchParams from query string
    /// </summary>
    private MethodBuilder EmitUrlSearchParamsGetter(TypeBuilder typeBuilder, EmittedRuntime runtime)
    {
        var method = typeBuilder.DefineMethod("get_searchParams", MethodAttributes.Public, _types.Object, Type.EmptyTypes);
        var il = method.GetILGenerator();

        // if (_searchParams != null) return _searchParams
        var createLabel = il.DefineLabel();
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldfld, _urlSearchParamsField);
        il.Emit(OpCodes.Brfalse, createLabel);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldfld, _urlSearchParamsField);
        il.Emit(OpCodes.Ret);

        il.MarkLabel(createLabel);

        // Use a local to store the new instance
        var spLocal = il.DeclareLocal(runtime.TSUrlSearchParamsType);

        // Get query string, trim '?', create $URLSearchParams
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldfld, _urlUriField);
        il.Emit(OpCodes.Callvirt, _types.Uri.GetProperty("Query")!.GetGetMethod()!);
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Newarr, _types.Char);
        il.Emit(OpCodes.Dup);
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Ldc_I4_S, (byte)'?');
        il.Emit(OpCodes.Stelem_I2);
        il.Emit(OpCodes.Callvirt, _types.String.GetMethod("TrimStart", [typeof(char[])])!);
        il.Emit(OpCodes.Newobj, runtime.TSUrlSearchParamsCtor);
        il.Emit(OpCodes.Stloc, spLocal);

        // _searchParams = sp
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldloc, spLocal);
        il.Emit(OpCodes.Stfld, _urlSearchParamsField);

        // return sp
        il.Emit(OpCodes.Ldloc, spLocal);
        il.Emit(OpCodes.Ret);
        return method;
    }

    /// <summary>
    /// Emits: public object toString() → href string
    /// </summary>
    private MethodBuilder EmitUrlToStringMethod(TypeBuilder typeBuilder)
    {
        var method = typeBuilder.DefineMethod("toString", MethodAttributes.Public, _types.Object, Type.EmptyTypes);
        var il = method.GetILGenerator();
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldfld, _urlUriField);
        il.Emit(OpCodes.Callvirt, _types.Uri.GetProperty("AbsoluteUri")!.GetGetMethod()!);
        il.Emit(OpCodes.Ret);
        return method;
    }

    /// <summary>
    /// Emits: public object toJSON() → href string
    /// </summary>
    private MethodBuilder EmitUrlToJsonMethod(TypeBuilder typeBuilder)
    {
        var method = typeBuilder.DefineMethod("toJSON", MethodAttributes.Public, _types.Object, Type.EmptyTypes);
        var il = method.GetILGenerator();
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldfld, _urlUriField);
        il.Emit(OpCodes.Callvirt, _types.Uri.GetProperty("AbsoluteUri")!.GetGetMethod()!);
        il.Emit(OpCodes.Ret);
        return method;
    }

    /// <summary>
    /// Helper to emit arg-to-string conversion in a constructor context.
    /// Same logic as EmitArgToString but works for constructor IL.
    /// </summary>
    private void EmitArgToStringCtor(ILGenerator il, int argIndex, LocalBuilder local)
    {
        il.Emit(OpCodes.Ldarg, argIndex);
        var hasArgLabel = il.DefineLabel();
        var doneLabel = il.DefineLabel();
        il.Emit(OpCodes.Dup);
        il.Emit(OpCodes.Brtrue, hasArgLabel);
        il.Emit(OpCodes.Pop);
        il.Emit(OpCodes.Ldstr, "");
        il.Emit(OpCodes.Br, doneLabel);
        il.MarkLabel(hasArgLabel);
        il.Emit(OpCodes.Callvirt, _types.Object.GetMethod("ToString", Type.EmptyTypes)!);
        il.Emit(OpCodes.Dup);
        var notNullLabel = il.DefineLabel();
        il.Emit(OpCodes.Brtrue, notNullLabel);
        il.Emit(OpCodes.Pop);
        il.Emit(OpCodes.Ldstr, "");
        il.MarkLabel(notNullLabel);
        il.MarkLabel(doneLabel);
        il.Emit(OpCodes.Stloc, local);
    }
}
