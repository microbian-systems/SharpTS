using System.Reflection;
using System.Reflection.Emit;
using System.Text;

namespace SharpTS.Compilation;

/// <summary>
/// Emits parseArgs signatures and helper methods (GetBoolOption, GetArgsArray, GetOptionsDef, ParseArgs).
/// </summary>
public partial class RuntimeEmitter
{
    /// <summary>
    /// Defines signatures for all parseArgs helper methods.
    /// </summary>
    private void DefineUtilParseArgsSignatures(TypeBuilder typeBuilder, EmittedRuntime runtime)
    {
        // GetArgsArray(IDictionary<string, object?> config) -> List<object?>
        runtime.UtilParseArgsGetArgsArray = typeBuilder.DefineMethod(
            "UtilParseArgsGetArgsArray",
            MethodAttributes.Public | MethodAttributes.Static,
            _types.ListOfObjectNullable,
            [_types.DictionaryStringObject]);

        // GetOptionsDef(IDictionary<string, object?> config) -> Dictionary<string, Dictionary<string, object?>>
        runtime.UtilParseArgsGetOptionsDef = typeBuilder.DefineMethod(
            "UtilParseArgsGetOptionsDef",
            MethodAttributes.Public | MethodAttributes.Static,
            typeof(Dictionary<string, Dictionary<string, object?>>),
            [_types.DictionaryStringObject]);

        // GetBoolOption(IDictionary<string, object?> config, string name, bool defaultValue) -> bool
        runtime.UtilParseArgsGetBoolOption = typeBuilder.DefineMethod(
            "UtilParseArgsGetBoolOption",
            MethodAttributes.Public | MethodAttributes.Static,
            _types.Boolean,
            [_types.DictionaryStringObject, _types.String, _types.Boolean]);

        // ParseLongOption(string arg, int index, List<object?> argsArray,
        //                 Dictionary<string, Dictionary<string, object?>> optionsDef,
        //                 Dictionary<string, object?> values, List<object?> tokens,
        //                 bool strict, bool allowNegative, bool returnTokens) -> int
        runtime.UtilParseLongOption = typeBuilder.DefineMethod(
            "UtilParseLongOption",
            MethodAttributes.Public | MethodAttributes.Static,
            _types.Int32,
            [
                _types.String,                                          // arg
                _types.Int32,                                           // index
                _types.ListOfObjectNullable,                            // argsArray
                typeof(Dictionary<string, Dictionary<string, object?>>), // optionsDef
                _types.DictionaryStringObject,                          // values
                _types.ListOfObjectNullable,                            // tokens
                _types.Boolean,                                         // strict
                _types.Boolean,                                         // allowNegative
                _types.Boolean                                          // returnTokens
            ]);

        // ParseShortOptions(string arg, int index, List<object?> argsArray,
        //                   Dictionary<string, Dictionary<string, object?>> optionsDef,
        //                   Dictionary<string, object?> values, List<object?> tokens,
        //                   bool strict, bool returnTokens) -> int
        runtime.UtilParseShortOptions = typeBuilder.DefineMethod(
            "UtilParseShortOptions",
            MethodAttributes.Public | MethodAttributes.Static,
            _types.Int32,
            [
                _types.String,                                          // arg
                _types.Int32,                                           // index
                _types.ListOfObjectNullable,                            // argsArray
                typeof(Dictionary<string, Dictionary<string, object?>>), // optionsDef
                _types.DictionaryStringObject,                          // values
                _types.ListOfObjectNullable,                            // tokens
                _types.Boolean,                                         // strict
                _types.Boolean                                          // returnTokens
            ]);
    }

    /// <summary>
    /// Emits GetBoolOption body - extracts a boolean option from config with default.
    /// GetBoolOption(IDictionary<string, object?> config, string name, bool defaultValue) -> bool
    /// </summary>
    private void EmitUtilParseArgsGetBoolOptionBody(EmittedRuntime runtime)
    {
        var il = runtime.UtilParseArgsGetBoolOption.GetILGenerator();

        var returnDefault = il.DefineLabel();
        var valueLocal = il.DeclareLocal(_types.Object);

        // if (config == null) return defaultValue
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Brfalse, returnDefault);

        // if (!config.TryGetValue(name, out var val)) return defaultValue
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Ldloca, valueLocal);
        il.Emit(OpCodes.Callvirt, _types.DictionaryStringObject.GetMethod("TryGetValue")!);
        il.Emit(OpCodes.Brfalse, returnDefault);

        // if (val is bool b) return b
        il.Emit(OpCodes.Ldloc, valueLocal);
        il.Emit(OpCodes.Isinst, typeof(bool));
        il.Emit(OpCodes.Brfalse, returnDefault);
        il.Emit(OpCodes.Ldloc, valueLocal);
        il.Emit(OpCodes.Unbox_Any, typeof(bool));
        il.Emit(OpCodes.Ret);

        // return defaultValue
        il.MarkLabel(returnDefault);
        il.Emit(OpCodes.Ldarg_2);
        il.Emit(OpCodes.Ret);
    }

    /// <summary>
    /// Emits GetArgsArray body - extracts args array from config.
    /// GetArgsArray(IDictionary<string, object?> config) -> List<object?>
    /// </summary>
    private void EmitUtilParseArgsGetArgsArrayBody(EmittedRuntime runtime)
    {
        var il = runtime.UtilParseArgsGetArgsArray.GetILGenerator();

        var returnEmpty = il.DefineLabel();
        var valueLocal = il.DeclareLocal(_types.Object);

        // if (config == null) return new List<object?>()
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Brfalse, returnEmpty);

        // if (!config.TryGetValue("args", out var argsVal)) return empty
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldstr, "args");
        il.Emit(OpCodes.Ldloca, valueLocal);
        il.Emit(OpCodes.Callvirt, _types.DictionaryStringObject.GetMethod("TryGetValue")!);
        il.Emit(OpCodes.Brfalse, returnEmpty);

        // if (argsVal is IList<object?> arr) return new List<object?>(arr)
        il.Emit(OpCodes.Ldloc, valueLocal);
        il.Emit(OpCodes.Isinst, _types.ListOfObjectNullable);
        il.Emit(OpCodes.Brfalse, returnEmpty);
        il.Emit(OpCodes.Ldloc, valueLocal);
        il.Emit(OpCodes.Castclass, _types.ListOfObjectNullable);
        il.Emit(OpCodes.Newobj, _types.ListObjectFromEnumerableCtor);
        il.Emit(OpCodes.Ret);

        // return new List<object?>()
        il.MarkLabel(returnEmpty);
        il.Emit(OpCodes.Newobj, _types.ListObjectNullableDefaultCtor);
        il.Emit(OpCodes.Ret);
    }

    /// <summary>
    /// Emits GetOptionsDef body - extracts options definitions from config.
    /// GetOptionsDef(IDictionary<string, object?> config) -> Dictionary<string, Dictionary<string, object?>>
    /// </summary>
    private void EmitUtilParseArgsGetOptionsDefBody(EmittedRuntime runtime)
    {
        var il = runtime.UtilParseArgsGetOptionsDef.GetILGenerator();
        var resultType = typeof(Dictionary<string, Dictionary<string, object?>>);

        var returnEmpty = il.DefineLabel();
        var loopStart = il.DefineLabel();
        var loopCondition = il.DefineLabel();

        var resultLocal = il.DeclareLocal(resultType);
        var valueLocal = il.DeclareLocal(_types.Object);
        var optionsLocal = il.DeclareLocal(_types.DictionaryStringObject);
        var keysLocal = il.DeclareLocal(typeof(List<string>));
        var countLocal = il.DeclareLocal(_types.Int32);
        var iLocal = il.DeclareLocal(_types.Int32);
        var keyLocal = il.DeclareLocal(_types.String);
        var optDefLocal = il.DeclareLocal(_types.Object);

        // result = new Dictionary<string, Dictionary<string, object?>>()
        il.Emit(OpCodes.Newobj, resultType.GetConstructor(Type.EmptyTypes)!);
        il.Emit(OpCodes.Stloc, resultLocal);

        // if (config == null) return result
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Brfalse, returnEmpty);

        // if (!config.TryGetValue("options", out var optionsVal)) return result
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldstr, "options");
        il.Emit(OpCodes.Ldloca, valueLocal);
        il.Emit(OpCodes.Callvirt, _types.DictionaryStringObject.GetMethod("TryGetValue")!);
        il.Emit(OpCodes.Brfalse, returnEmpty);

        // if (optionsVal is not IDictionary<string, object?> options) return result
        il.Emit(OpCodes.Ldloc, valueLocal);
        il.Emit(OpCodes.Isinst, _types.DictionaryStringObject);
        il.Emit(OpCodes.Brfalse, returnEmpty);
        il.Emit(OpCodes.Ldloc, valueLocal);
        il.Emit(OpCodes.Castclass, _types.DictionaryStringObject);
        il.Emit(OpCodes.Stloc, optionsLocal);

        // keys = new List<string>(options.Keys)
        il.Emit(OpCodes.Ldloc, optionsLocal);
        il.Emit(OpCodes.Callvirt, _types.DictionaryStringObject.GetProperty("Keys")!.GetGetMethod()!);
        il.Emit(OpCodes.Newobj, _types.ListStringFromEnumerableCtor);
        il.Emit(OpCodes.Stloc, keysLocal);

        // count = keys.Count
        il.Emit(OpCodes.Ldloc, keysLocal);
        il.Emit(OpCodes.Callvirt, typeof(List<string>).GetProperty("Count")!.GetGetMethod()!);
        il.Emit(OpCodes.Stloc, countLocal);

        // i = 0
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Stloc, iLocal);
        il.Emit(OpCodes.Br, loopCondition);

        // Loop body
        il.MarkLabel(loopStart);

        // key = keys[i]
        il.Emit(OpCodes.Ldloc, keysLocal);
        il.Emit(OpCodes.Ldloc, iLocal);
        il.Emit(OpCodes.Callvirt, _types.ListStringGetItem);
        il.Emit(OpCodes.Stloc, keyLocal);

        // optDef = options[key]
        il.Emit(OpCodes.Ldloc, optionsLocal);
        il.Emit(OpCodes.Ldloc, keyLocal);
        il.Emit(OpCodes.Callvirt, _types.DictionaryStringObject.GetMethod("get_Item", [typeof(string)])!);
        il.Emit(OpCodes.Stloc, optDefLocal);

        // if (optDef is IDictionary<string, object?> optDefDict) result[key] = new Dictionary<string, object?>(optDefDict)
        var skipAdd = il.DefineLabel();
        il.Emit(OpCodes.Ldloc, optDefLocal);
        il.Emit(OpCodes.Isinst, _types.DictionaryStringObject);
        il.Emit(OpCodes.Brfalse, skipAdd);

        il.Emit(OpCodes.Ldloc, resultLocal);
        il.Emit(OpCodes.Ldloc, keyLocal);
        il.Emit(OpCodes.Ldloc, optDefLocal);
        il.Emit(OpCodes.Castclass, _types.DictionaryStringObject);
        il.Emit(OpCodes.Newobj, typeof(Dictionary<string, object?>).GetConstructor([typeof(IDictionary<string, object?>)])!);
        il.Emit(OpCodes.Callvirt, resultType.GetMethod("set_Item", [typeof(string), typeof(Dictionary<string, object?>)])!);

        il.MarkLabel(skipAdd);

        // i++
        il.Emit(OpCodes.Ldloc, iLocal);
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Add);
        il.Emit(OpCodes.Stloc, iLocal);

        // Loop condition: i < count
        il.MarkLabel(loopCondition);
        il.Emit(OpCodes.Ldloc, iLocal);
        il.Emit(OpCodes.Ldloc, countLocal);
        il.Emit(OpCodes.Blt, loopStart);

        // return result
        il.MarkLabel(returnEmpty);
        il.Emit(OpCodes.Ldloc, resultLocal);
        il.Emit(OpCodes.Ret);
    }

    /// <summary>
    /// Emits ParseArgs body - the main argument parsing entry point.
    /// </summary>
    private void EmitUtilParseArgsBody(EmittedRuntime runtime)
    {
        var il = runtime.UtilParseArgs.GetILGenerator();
        var optionsDefType = typeof(Dictionary<string, Dictionary<string, object?>>);

        // Locals
        var configDictLocal = il.DeclareLocal(_types.DictionaryStringObject);     // loc.0
        var argsArrayLocal = il.DeclareLocal(_types.ListOfObjectNullable);        // loc.1
        var optionsDefLocal = il.DeclareLocal(optionsDefType);                    // loc.2
        var strictLocal = il.DeclareLocal(_types.Boolean);                        // loc.3
        var allowPositionalsLocal = il.DeclareLocal(_types.Boolean);              // loc.4
        var allowNegativeLocal = il.DeclareLocal(_types.Boolean);                 // loc.5
        var returnTokensLocal = il.DeclareLocal(_types.Boolean);                  // loc.6
        var valuesLocal = il.DeclareLocal(_types.DictionaryStringObject);         // loc.7
        var positionalsLocal = il.DeclareLocal(_types.ListOfObjectNullable);      // loc.8
        var tokensLocal = il.DeclareLocal(_types.ListOfObjectNullable);           // loc.9
        var iLocal = il.DeclareLocal(_types.Int32);                               // loc.10
        var argLocal = il.DeclareLocal(_types.String);                            // loc.11
        var resultLocal = il.DeclareLocal(_types.DictionaryStringObject);         // loc.12
        var keysLocal = il.DeclareLocal(typeof(List<string>));                    // loc.13
        var kLocal = il.DeclareLocal(_types.Int32);                               // loc.14
        var keyLocal = il.DeclareLocal(_types.String);                            // loc.15
        var optDefLocal = il.DeclareLocal(typeof(Dictionary<string, object?>));   // loc.16
        var defaultValLocal = il.DeclareLocal(_types.Object);                     // loc.17

        // Labels
        var mainLoopStart = il.DefineLabel();
        var mainLoopCondition = il.DefineLabel();
        var checkTerminator = il.DefineLabel();
        var checkLongOption = il.DefineLabel();
        var checkShortOption = il.DefineLabel();
        var handlePositional = il.DefineLabel();
        var afterTerminator = il.DefineLabel();
        var terminatorLoop = il.DefineLabel();
        var terminatorLoopCond = il.DefineLabel();
        var buildResult = il.DefineLabel();
        var defaultsLoopStart = il.DefineLabel();
        var defaultsLoopCond = il.DefineLabel();

        // Cast config to dictionary
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Isinst, _types.DictionaryStringObject);
        il.Emit(OpCodes.Stloc, configDictLocal);

        // argsArray = GetArgsArray(configDict)
        il.Emit(OpCodes.Ldloc, configDictLocal);
        il.Emit(OpCodes.Call, runtime.UtilParseArgsGetArgsArray);
        il.Emit(OpCodes.Stloc, argsArrayLocal);

        // optionsDef = GetOptionsDef(configDict)
        il.Emit(OpCodes.Ldloc, configDictLocal);
        il.Emit(OpCodes.Call, runtime.UtilParseArgsGetOptionsDef);
        il.Emit(OpCodes.Stloc, optionsDefLocal);

        // strict = GetBoolOption(configDict, "strict", true)
        il.Emit(OpCodes.Ldloc, configDictLocal);
        il.Emit(OpCodes.Ldstr, "strict");
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Call, runtime.UtilParseArgsGetBoolOption);
        il.Emit(OpCodes.Stloc, strictLocal);

        // allowPositionals = GetBoolOption(configDict, "allowPositionals", !strict)
        il.Emit(OpCodes.Ldloc, configDictLocal);
        il.Emit(OpCodes.Ldstr, "allowPositionals");
        il.Emit(OpCodes.Ldloc, strictLocal);
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Ceq); // !strict
        il.Emit(OpCodes.Call, runtime.UtilParseArgsGetBoolOption);
        il.Emit(OpCodes.Stloc, allowPositionalsLocal);

        // allowNegative = GetBoolOption(configDict, "allowNegative", false)
        il.Emit(OpCodes.Ldloc, configDictLocal);
        il.Emit(OpCodes.Ldstr, "allowNegative");
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Call, runtime.UtilParseArgsGetBoolOption);
        il.Emit(OpCodes.Stloc, allowNegativeLocal);

        // returnTokens = GetBoolOption(configDict, "tokens", false)
        il.Emit(OpCodes.Ldloc, configDictLocal);
        il.Emit(OpCodes.Ldstr, "tokens");
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Call, runtime.UtilParseArgsGetBoolOption);
        il.Emit(OpCodes.Stloc, returnTokensLocal);

        // values = new Dictionary<string, object?>()
        il.Emit(OpCodes.Newobj, _types.DictionaryStringObjectNullableCtor);
        il.Emit(OpCodes.Stloc, valuesLocal);

        // positionals = new List<object?>()
        il.Emit(OpCodes.Newobj, _types.ListObjectNullableDefaultCtor);
        il.Emit(OpCodes.Stloc, positionalsLocal);

        // tokens = new List<object?>()
        il.Emit(OpCodes.Newobj, _types.ListObjectNullableDefaultCtor);
        il.Emit(OpCodes.Stloc, tokensLocal);

        // Apply defaults from optionsDef
        // keys = new List<string>(optionsDef.Keys)
        il.Emit(OpCodes.Ldloc, optionsDefLocal);
        il.Emit(OpCodes.Callvirt, optionsDefType.GetProperty("Keys")!.GetGetMethod()!);
        il.Emit(OpCodes.Newobj, _types.ListStringFromEnumerableCtor);
        il.Emit(OpCodes.Stloc, keysLocal);

        // k = 0
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Stloc, kLocal);
        il.Emit(OpCodes.Br, defaultsLoopCond);

        // Defaults loop
        il.MarkLabel(defaultsLoopStart);
        // key = keys[k]
        il.Emit(OpCodes.Ldloc, keysLocal);
        il.Emit(OpCodes.Ldloc, kLocal);
        il.Emit(OpCodes.Callvirt, _types.ListStringGetItem);
        il.Emit(OpCodes.Stloc, keyLocal);

        // optDef = optionsDef[key]
        il.Emit(OpCodes.Ldloc, optionsDefLocal);
        il.Emit(OpCodes.Ldloc, keyLocal);
        il.Emit(OpCodes.Callvirt, optionsDefType.GetMethod("get_Item", [typeof(string)])!);
        il.Emit(OpCodes.Stloc, optDefLocal);

        // if (optDef.TryGetValue("default", out defaultVal) && defaultVal != null)
        var skipDefault = il.DefineLabel();
        il.Emit(OpCodes.Ldloc, optDefLocal);
        il.Emit(OpCodes.Ldstr, "default");
        il.Emit(OpCodes.Ldloca, defaultValLocal);
        il.Emit(OpCodes.Callvirt, _types.DictionaryStringObjectNullableTryGetValue);
        il.Emit(OpCodes.Brfalse, skipDefault);

        il.Emit(OpCodes.Ldloc, defaultValLocal);
        il.Emit(OpCodes.Brfalse, skipDefault);

        // values[key] = defaultVal
        il.Emit(OpCodes.Ldloc, valuesLocal);
        il.Emit(OpCodes.Ldloc, keyLocal);
        il.Emit(OpCodes.Ldloc, defaultValLocal);
        il.Emit(OpCodes.Callvirt, _types.DictionaryStringObject.GetMethod("set_Item", [typeof(string), typeof(object)])!);

        il.MarkLabel(skipDefault);
        // k++
        il.Emit(OpCodes.Ldloc, kLocal);
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Add);
        il.Emit(OpCodes.Stloc, kLocal);

        il.MarkLabel(defaultsLoopCond);
        il.Emit(OpCodes.Ldloc, kLocal);
        il.Emit(OpCodes.Ldloc, keysLocal);
        il.Emit(OpCodes.Callvirt, typeof(List<string>).GetProperty("Count")!.GetGetMethod()!);
        il.Emit(OpCodes.Blt, defaultsLoopStart);

        // Main parsing loop: i = 0
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Stloc, iLocal);
        il.Emit(OpCodes.Br, mainLoopCondition);

        // Main loop body
        il.MarkLabel(mainLoopStart);

        // arg = argsArray[i]?.ToString() ?? ""
        il.Emit(OpCodes.Ldloc, argsArrayLocal);
        il.Emit(OpCodes.Ldloc, iLocal);
        il.Emit(OpCodes.Callvirt, _types.ListOfObjectNullable.GetMethod("get_Item", [typeof(int)])!);
        il.Emit(OpCodes.Dup);
        var argNotNull = il.DefineLabel();
        il.Emit(OpCodes.Brtrue, argNotNull);
        il.Emit(OpCodes.Pop);
        il.Emit(OpCodes.Ldstr, "");
        var argReady = il.DefineLabel();
        il.Emit(OpCodes.Br, argReady);
        il.MarkLabel(argNotNull);
        il.Emit(OpCodes.Callvirt, _types.Object.GetMethod("ToString", Type.EmptyTypes)!);
        il.Emit(OpCodes.Dup);
        var strNotNull = il.DefineLabel();
        il.Emit(OpCodes.Brtrue, strNotNull);
        il.Emit(OpCodes.Pop);
        il.Emit(OpCodes.Ldstr, "");
        il.MarkLabel(strNotNull);
        il.MarkLabel(argReady);
        il.Emit(OpCodes.Stloc, argLocal);

        // if (arg == "--")
        il.Emit(OpCodes.Ldloc, argLocal);
        il.Emit(OpCodes.Ldstr, "--");
        il.Emit(OpCodes.Call, _types.String.GetMethod("op_Equality", [typeof(string), typeof(string)])!);
        il.Emit(OpCodes.Brtrue, checkTerminator);

        // else if (arg.StartsWith("--"))
        il.Emit(OpCodes.Ldloc, argLocal);
        il.Emit(OpCodes.Ldstr, "--");
        il.Emit(OpCodes.Callvirt, _types.String.GetMethod("StartsWith", [typeof(string)])!);
        il.Emit(OpCodes.Brtrue, checkLongOption);

        // else if (arg.StartsWith("-") && arg.Length > 1)
        il.Emit(OpCodes.Ldloc, argLocal);
        il.Emit(OpCodes.Ldstr, "-");
        il.Emit(OpCodes.Callvirt, _types.String.GetMethod("StartsWith", [typeof(string)])!);
        il.Emit(OpCodes.Brfalse, handlePositional);

        il.Emit(OpCodes.Ldloc, argLocal);
        il.Emit(OpCodes.Callvirt, _types.String.GetProperty("Length")!.GetGetMethod()!);
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Ble, handlePositional);
        il.Emit(OpCodes.Br, checkShortOption);

        // Handle option terminator "--"
        il.MarkLabel(checkTerminator);
        // Add terminator token if needed
        il.Emit(OpCodes.Ldloc, returnTokensLocal);
        var skipTermToken = il.DefineLabel();
        il.Emit(OpCodes.Brfalse, skipTermToken);

        var termTokenDict = il.DeclareLocal(_types.DictionaryStringObject);
        il.Emit(OpCodes.Newobj, _types.DictionaryStringObjectNullableCtor);
        il.Emit(OpCodes.Stloc, termTokenDict);
        il.Emit(OpCodes.Ldloc, termTokenDict);
        il.Emit(OpCodes.Ldstr, "kind");
        il.Emit(OpCodes.Ldstr, "option-terminator");
        il.Emit(OpCodes.Callvirt, _types.DictionaryStringObject.GetMethod("set_Item", [typeof(string), typeof(object)])!);
        il.Emit(OpCodes.Ldloc, termTokenDict);
        il.Emit(OpCodes.Ldstr, "index");
        il.Emit(OpCodes.Ldloc, iLocal);
        il.Emit(OpCodes.Conv_R8);
        il.Emit(OpCodes.Box, typeof(double));
        il.Emit(OpCodes.Callvirt, _types.DictionaryStringObject.GetMethod("set_Item", [typeof(string), typeof(object)])!);
        il.Emit(OpCodes.Ldloc, tokensLocal);
        il.Emit(OpCodes.Ldloc, termTokenDict);
        il.Emit(OpCodes.Callvirt, _types.ListOfObjectNullable.GetMethod("Add", [typeof(object)])!);

        il.MarkLabel(skipTermToken);
        // i++
        il.Emit(OpCodes.Ldloc, iLocal);
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Add);
        il.Emit(OpCodes.Stloc, iLocal);

        // Rest are positionals
        il.Emit(OpCodes.Br, terminatorLoopCond);

        il.MarkLabel(terminatorLoop);
        // arg = argsArray[i]?.ToString() ?? ""
        il.Emit(OpCodes.Ldloc, argsArrayLocal);
        il.Emit(OpCodes.Ldloc, iLocal);
        il.Emit(OpCodes.Callvirt, _types.ListOfObjectNullable.GetMethod("get_Item", [typeof(int)])!);
        il.Emit(OpCodes.Dup);
        var termArgNotNull = il.DefineLabel();
        il.Emit(OpCodes.Brtrue, termArgNotNull);
        il.Emit(OpCodes.Pop);
        il.Emit(OpCodes.Ldstr, "");
        var termArgReady = il.DefineLabel();
        il.Emit(OpCodes.Br, termArgReady);
        il.MarkLabel(termArgNotNull);
        il.Emit(OpCodes.Callvirt, _types.Object.GetMethod("ToString", Type.EmptyTypes)!);
        il.Emit(OpCodes.Dup);
        var termStrNotNull = il.DefineLabel();
        il.Emit(OpCodes.Brtrue, termStrNotNull);
        il.Emit(OpCodes.Pop);
        il.Emit(OpCodes.Ldstr, "");
        il.MarkLabel(termStrNotNull);
        il.MarkLabel(termArgReady);
        il.Emit(OpCodes.Stloc, argLocal);

        // if (!allowPositionals && strict) throw
        var allowTermPositional = il.DefineLabel();
        il.Emit(OpCodes.Ldloc, allowPositionalsLocal);
        il.Emit(OpCodes.Brtrue, allowTermPositional);
        il.Emit(OpCodes.Ldloc, strictLocal);
        il.Emit(OpCodes.Brfalse, allowTermPositional);

        il.Emit(OpCodes.Ldstr, "Unexpected argument: ");
        il.Emit(OpCodes.Ldloc, argLocal);
        il.Emit(OpCodes.Call, _types.String.GetMethod("Concat", [typeof(string), typeof(string)])!);
        il.Emit(OpCodes.Newobj, _types.ExceptionCtorString);
        il.Emit(OpCodes.Throw);

        il.MarkLabel(allowTermPositional);
        // positionals.Add(arg)
        il.Emit(OpCodes.Ldloc, positionalsLocal);
        il.Emit(OpCodes.Ldloc, argLocal);
        il.Emit(OpCodes.Callvirt, _types.ListOfObjectNullable.GetMethod("Add", [typeof(object)])!);

        // Add positional token if needed
        il.Emit(OpCodes.Ldloc, returnTokensLocal);
        var skipPosToken2 = il.DefineLabel();
        il.Emit(OpCodes.Brfalse, skipPosToken2);

        var posTokenDict2 = il.DeclareLocal(_types.DictionaryStringObject);
        il.Emit(OpCodes.Newobj, _types.DictionaryStringObjectNullableCtor);
        il.Emit(OpCodes.Stloc, posTokenDict2);
        il.Emit(OpCodes.Ldloc, posTokenDict2);
        il.Emit(OpCodes.Ldstr, "kind");
        il.Emit(OpCodes.Ldstr, "positional");
        il.Emit(OpCodes.Callvirt, _types.DictionaryStringObject.GetMethod("set_Item", [typeof(string), typeof(object)])!);
        il.Emit(OpCodes.Ldloc, posTokenDict2);
        il.Emit(OpCodes.Ldstr, "index");
        il.Emit(OpCodes.Ldloc, iLocal);
        il.Emit(OpCodes.Conv_R8);
        il.Emit(OpCodes.Box, typeof(double));
        il.Emit(OpCodes.Callvirt, _types.DictionaryStringObject.GetMethod("set_Item", [typeof(string), typeof(object)])!);
        il.Emit(OpCodes.Ldloc, posTokenDict2);
        il.Emit(OpCodes.Ldstr, "value");
        il.Emit(OpCodes.Ldloc, argLocal);
        il.Emit(OpCodes.Callvirt, _types.DictionaryStringObject.GetMethod("set_Item", [typeof(string), typeof(object)])!);
        il.Emit(OpCodes.Ldloc, tokensLocal);
        il.Emit(OpCodes.Ldloc, posTokenDict2);
        il.Emit(OpCodes.Callvirt, _types.ListOfObjectNullable.GetMethod("Add", [typeof(object)])!);

        il.MarkLabel(skipPosToken2);
        // i++
        il.Emit(OpCodes.Ldloc, iLocal);
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Add);
        il.Emit(OpCodes.Stloc, iLocal);

        il.MarkLabel(terminatorLoopCond);
        il.Emit(OpCodes.Ldloc, iLocal);
        il.Emit(OpCodes.Ldloc, argsArrayLocal);
        il.Emit(OpCodes.Callvirt, _types.ListOfObjectNullable.GetProperty("Count")!.GetGetMethod()!);
        il.Emit(OpCodes.Blt, terminatorLoop);

        // After terminator handling, go to build result
        il.Emit(OpCodes.Br, buildResult);

        // Handle long option (--xxx)
        il.MarkLabel(checkLongOption);
        // i = ParseLongOption(arg, i, argsArray, optionsDef, values, tokens, strict, allowNegative, returnTokens)
        il.Emit(OpCodes.Ldloc, argLocal);
        il.Emit(OpCodes.Ldloc, iLocal);
        il.Emit(OpCodes.Ldloc, argsArrayLocal);
        il.Emit(OpCodes.Ldloc, optionsDefLocal);
        il.Emit(OpCodes.Ldloc, valuesLocal);
        il.Emit(OpCodes.Ldloc, tokensLocal);
        il.Emit(OpCodes.Ldloc, strictLocal);
        il.Emit(OpCodes.Ldloc, allowNegativeLocal);
        il.Emit(OpCodes.Ldloc, returnTokensLocal);
        il.Emit(OpCodes.Call, runtime.UtilParseLongOption);
        il.Emit(OpCodes.Stloc, iLocal);
        il.Emit(OpCodes.Br, mainLoopCondition);

        // Handle short option (-x)
        il.MarkLabel(checkShortOption);
        // i = ParseShortOptions(arg, i, argsArray, optionsDef, values, tokens, strict, returnTokens)
        il.Emit(OpCodes.Ldloc, argLocal);
        il.Emit(OpCodes.Ldloc, iLocal);
        il.Emit(OpCodes.Ldloc, argsArrayLocal);
        il.Emit(OpCodes.Ldloc, optionsDefLocal);
        il.Emit(OpCodes.Ldloc, valuesLocal);
        il.Emit(OpCodes.Ldloc, tokensLocal);
        il.Emit(OpCodes.Ldloc, strictLocal);
        il.Emit(OpCodes.Ldloc, returnTokensLocal);
        il.Emit(OpCodes.Call, runtime.UtilParseShortOptions);
        il.Emit(OpCodes.Stloc, iLocal);
        il.Emit(OpCodes.Br, mainLoopCondition);

        // Handle positional argument
        il.MarkLabel(handlePositional);
        // if (!allowPositionals && strict) throw
        var allowPos = il.DefineLabel();
        il.Emit(OpCodes.Ldloc, allowPositionalsLocal);
        il.Emit(OpCodes.Brtrue, allowPos);
        il.Emit(OpCodes.Ldloc, strictLocal);
        il.Emit(OpCodes.Brfalse, allowPos);

        il.Emit(OpCodes.Ldstr, "Unexpected argument: ");
        il.Emit(OpCodes.Ldloc, argLocal);
        il.Emit(OpCodes.Call, _types.String.GetMethod("Concat", [typeof(string), typeof(string)])!);
        il.Emit(OpCodes.Newobj, _types.ExceptionCtorString);
        il.Emit(OpCodes.Throw);

        il.MarkLabel(allowPos);
        // positionals.Add(arg)
        il.Emit(OpCodes.Ldloc, positionalsLocal);
        il.Emit(OpCodes.Ldloc, argLocal);
        il.Emit(OpCodes.Callvirt, _types.ListOfObjectNullable.GetMethod("Add", [typeof(object)])!);

        // Add positional token if needed
        il.Emit(OpCodes.Ldloc, returnTokensLocal);
        var skipPosToken = il.DefineLabel();
        il.Emit(OpCodes.Brfalse, skipPosToken);

        var posTokenDict = il.DeclareLocal(_types.DictionaryStringObject);
        il.Emit(OpCodes.Newobj, _types.DictionaryStringObjectNullableCtor);
        il.Emit(OpCodes.Stloc, posTokenDict);
        il.Emit(OpCodes.Ldloc, posTokenDict);
        il.Emit(OpCodes.Ldstr, "kind");
        il.Emit(OpCodes.Ldstr, "positional");
        il.Emit(OpCodes.Callvirt, _types.DictionaryStringObject.GetMethod("set_Item", [typeof(string), typeof(object)])!);
        il.Emit(OpCodes.Ldloc, posTokenDict);
        il.Emit(OpCodes.Ldstr, "index");
        il.Emit(OpCodes.Ldloc, iLocal);
        il.Emit(OpCodes.Conv_R8);
        il.Emit(OpCodes.Box, typeof(double));
        il.Emit(OpCodes.Callvirt, _types.DictionaryStringObject.GetMethod("set_Item", [typeof(string), typeof(object)])!);
        il.Emit(OpCodes.Ldloc, posTokenDict);
        il.Emit(OpCodes.Ldstr, "value");
        il.Emit(OpCodes.Ldloc, argLocal);
        il.Emit(OpCodes.Callvirt, _types.DictionaryStringObject.GetMethod("set_Item", [typeof(string), typeof(object)])!);
        il.Emit(OpCodes.Ldloc, tokensLocal);
        il.Emit(OpCodes.Ldloc, posTokenDict);
        il.Emit(OpCodes.Callvirt, _types.ListOfObjectNullable.GetMethod("Add", [typeof(object)])!);

        il.MarkLabel(skipPosToken);
        // i++
        il.Emit(OpCodes.Ldloc, iLocal);
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Add);
        il.Emit(OpCodes.Stloc, iLocal);

        // Main loop condition: i < argsArray.Count
        il.MarkLabel(mainLoopCondition);
        il.Emit(OpCodes.Ldloc, iLocal);
        il.Emit(OpCodes.Ldloc, argsArrayLocal);
        il.Emit(OpCodes.Callvirt, _types.ListOfObjectNullable.GetProperty("Count")!.GetGetMethod()!);
        il.Emit(OpCodes.Blt, mainLoopStart);

        // Build result object
        il.MarkLabel(buildResult);
        // result = new Dictionary<string, object?>()
        il.Emit(OpCodes.Newobj, _types.DictionaryStringObjectNullableCtor);
        il.Emit(OpCodes.Stloc, resultLocal);

        // result["values"] = values
        il.Emit(OpCodes.Ldloc, resultLocal);
        il.Emit(OpCodes.Ldstr, "values");
        il.Emit(OpCodes.Ldloc, valuesLocal);
        il.Emit(OpCodes.Callvirt, _types.DictionaryStringObject.GetMethod("set_Item", [typeof(string), typeof(object)])!);

        // result["positionals"] = positionals
        il.Emit(OpCodes.Ldloc, resultLocal);
        il.Emit(OpCodes.Ldstr, "positionals");
        il.Emit(OpCodes.Ldloc, positionalsLocal);
        il.Emit(OpCodes.Callvirt, _types.DictionaryStringObject.GetMethod("set_Item", [typeof(string), typeof(object)])!);

        // if (returnTokens) result["tokens"] = tokens
        var skipTokensResult = il.DefineLabel();
        il.Emit(OpCodes.Ldloc, returnTokensLocal);
        il.Emit(OpCodes.Brfalse, skipTokensResult);
        il.Emit(OpCodes.Ldloc, resultLocal);
        il.Emit(OpCodes.Ldstr, "tokens");
        il.Emit(OpCodes.Ldloc, tokensLocal);
        il.Emit(OpCodes.Callvirt, _types.DictionaryStringObject.GetMethod("set_Item", [typeof(string), typeof(object)])!);

        il.MarkLabel(skipTokensResult);
        // return result
        il.Emit(OpCodes.Ldloc, resultLocal);
        il.Emit(OpCodes.Ret);
    }
}
