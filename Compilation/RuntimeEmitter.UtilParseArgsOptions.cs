using System.Reflection;
using System.Reflection.Emit;
using System.Text;

namespace SharpTS.Compilation;

/// <summary>
/// Emits parseArgs option parsing methods (ParseLongOption, ParseShortOptions).
/// </summary>
public partial class RuntimeEmitter
{
    /// <summary>
    /// Emits ParseLongOption body - parses --option and --option=value arguments.
    /// </summary>
    private void EmitUtilParseLongOptionBody(EmittedRuntime runtime)
    {
        var il = runtime.UtilParseLongOption.GetILGenerator();
        var optionsDefType = typeof(Dictionary<string, Dictionary<string, object?>>);

        // Locals
        var nameLocal = il.DeclareLocal(_types.String);           // loc.0
        var inlineValueLocal = il.DeclareLocal(_types.String);    // loc.1
        var hasInlineValueLocal = il.DeclareLocal(_types.Boolean); // loc.2
        var eqIndexLocal = il.DeclareLocal(_types.Int32);         // loc.3
        var isNegatedLocal = il.DeclareLocal(_types.Boolean);     // loc.4
        var originalNameLocal = il.DeclareLocal(_types.String);   // loc.5
        var optDefLocal = il.DeclareLocal(typeof(Dictionary<string, object?>)); // loc.6
        var optTypeLocal = il.DeclareLocal(_types.String);        // loc.7
        var multipleLocal = il.DeclareLocal(_types.Boolean);      // loc.8
        var valueLocal = il.DeclareLocal(_types.Object);          // loc.9
        var indexLocal = il.DeclareLocal(_types.Int32);           // loc.10 - working copy of index
        var typeValLocal = il.DeclareLocal(_types.Object);        // loc.11
        var mValLocal = il.DeclareLocal(_types.Object);           // loc.12
        var existingLocal = il.DeclareLocal(_types.Object);       // loc.13
        var listLocal = il.DeclareLocal(_types.ListOfObjectNullable); // loc.14

        // Labels
        var noInlineValue = il.DefineLabel();
        var checkNegation = il.DefineLabel();
        var afterNegation = il.DefineLabel();
        var unknownOption = il.DefineLabel();
        var isBooleanType = il.DefineLabel();
        var isStringType = il.DefineLabel();
        var afterValueExtraction = il.DefineLabel();
        var storeValue = il.DefineLabel();
        var checkMultiple = il.DefineLabel();
        var notMultiple = il.DefineLabel();
        var addTokens = il.DefineLabel();
        var returnIndex = il.DefineLabel();

        // index = arg1 (working copy)
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Stloc, indexLocal);

        // hasInlineValue = false
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Stloc, hasInlineValueLocal);

        // inlineValue = null
        il.Emit(OpCodes.Ldnull);
        il.Emit(OpCodes.Stloc, inlineValueLocal);

        // eqIndex = arg.IndexOf('=')
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldc_I4, '=');
        il.Emit(OpCodes.Callvirt, _types.String.GetMethod("IndexOf", [typeof(char)])!);
        il.Emit(OpCodes.Stloc, eqIndexLocal);

        // if (eqIndex > 0) { name = arg[2..eqIndex]; inlineValue = arg[(eqIndex+1)..]; hasInlineValue = true }
        il.Emit(OpCodes.Ldloc, eqIndexLocal);
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Ble, noInlineValue);

        // Has inline value
        // name = arg.Substring(2, eqIndex - 2)
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldc_I4_2);
        il.Emit(OpCodes.Ldloc, eqIndexLocal);
        il.Emit(OpCodes.Ldc_I4_2);
        il.Emit(OpCodes.Sub);
        il.Emit(OpCodes.Callvirt, _types.String.GetMethod("Substring", [typeof(int), typeof(int)])!);
        il.Emit(OpCodes.Stloc, nameLocal);

        // inlineValue = arg.Substring(eqIndex + 1)
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldloc, eqIndexLocal);
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Add);
        il.Emit(OpCodes.Callvirt, _types.String.GetMethod("Substring", [typeof(int)])!);
        il.Emit(OpCodes.Stloc, inlineValueLocal);

        // hasInlineValue = true
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Stloc, hasInlineValueLocal);
        il.Emit(OpCodes.Br, checkNegation);

        // No inline value
        il.MarkLabel(noInlineValue);
        // name = arg.Substring(2)
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldc_I4_2);
        il.Emit(OpCodes.Callvirt, _types.String.GetMethod("Substring", [typeof(int)])!);
        il.Emit(OpCodes.Stloc, nameLocal);

        // Check for negation (--no-xxx)
        il.MarkLabel(checkNegation);
        // isNegated = false
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Stloc, isNegatedLocal);

        // originalName = name
        il.Emit(OpCodes.Ldloc, nameLocal);
        il.Emit(OpCodes.Stloc, originalNameLocal);

        // if (allowNegative && name.StartsWith("no-"))
        il.Emit(OpCodes.Ldarg, 7); // allowNegative
        il.Emit(OpCodes.Brfalse, afterNegation);

        il.Emit(OpCodes.Ldloc, nameLocal);
        il.Emit(OpCodes.Ldstr, "no-");
        il.Emit(OpCodes.Callvirt, _types.String.GetMethod("StartsWith", [typeof(string)])!);
        il.Emit(OpCodes.Brfalse, afterNegation);

        // positiveName = name.Substring(3)
        var positiveNameLocal = il.DeclareLocal(_types.String);
        il.Emit(OpCodes.Ldloc, nameLocal);
        il.Emit(OpCodes.Ldc_I4_3);
        il.Emit(OpCodes.Callvirt, _types.String.GetMethod("Substring", [typeof(int)])!);
        il.Emit(OpCodes.Stloc, positiveNameLocal);

        // if (optionsDef.TryGetValue(positiveName, out var posDef) && posDef["type"] == "boolean")
        var posDefLocal = il.DeclareLocal(typeof(Dictionary<string, object?>));
        var skipNegation = il.DefineLabel();

        il.Emit(OpCodes.Ldarg_3); // optionsDef
        il.Emit(OpCodes.Ldloc, positiveNameLocal);
        il.Emit(OpCodes.Ldloca, posDefLocal);
        il.Emit(OpCodes.Callvirt, optionsDefType.GetMethod("TryGetValue")!);
        il.Emit(OpCodes.Brfalse, skipNegation);

        // Check if type is "boolean"
        il.Emit(OpCodes.Ldloc, posDefLocal);
        il.Emit(OpCodes.Ldstr, "type");
        il.Emit(OpCodes.Ldloca, typeValLocal);
        il.Emit(OpCodes.Callvirt, _types.DictionaryStringObjectNullableTryGetValue);
        il.Emit(OpCodes.Brfalse, skipNegation);

        il.Emit(OpCodes.Ldloc, typeValLocal);
        il.Emit(OpCodes.Callvirt, _types.Object.GetMethod("ToString", Type.EmptyTypes)!);
        il.Emit(OpCodes.Ldstr, "boolean");
        il.Emit(OpCodes.Call, _types.String.GetMethod("op_Equality", [typeof(string), typeof(string)])!);
        il.Emit(OpCodes.Brfalse, skipNegation);

        // name = positiveName; isNegated = true
        il.Emit(OpCodes.Ldloc, positiveNameLocal);
        il.Emit(OpCodes.Stloc, nameLocal);
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Stloc, isNegatedLocal);

        il.MarkLabel(skipNegation);
        il.MarkLabel(afterNegation);

        // if (!optionsDef.TryGetValue(name, out optDef)) goto unknownOption
        var foundOption = il.DefineLabel();
        il.Emit(OpCodes.Ldarg_3); // optionsDef
        il.Emit(OpCodes.Ldloc, nameLocal);
        il.Emit(OpCodes.Ldloca, optDefLocal);
        il.Emit(OpCodes.Callvirt, optionsDefType.GetMethod("TryGetValue")!);
        il.Emit(OpCodes.Brtrue, foundOption); // Jump to foundOption if TryGetValue returns true
        il.Emit(OpCodes.Br, unknownOption);   // Otherwise go to unknownOption

        il.MarkLabel(foundOption);

        // Option found - extract type and multiple flag
        // optType = optDef.TryGetValue("type", out t) ? t?.ToString() : "boolean"
        var defaultType = il.DefineLabel();
        var afterType = il.DefineLabel();

        il.Emit(OpCodes.Ldloc, optDefLocal);
        il.Emit(OpCodes.Ldstr, "type");
        il.Emit(OpCodes.Ldloca, typeValLocal);
        il.Emit(OpCodes.Callvirt, _types.DictionaryStringObjectNullableTryGetValue);
        il.Emit(OpCodes.Brfalse, defaultType);

        il.Emit(OpCodes.Ldloc, typeValLocal);
        il.Emit(OpCodes.Brfalse, defaultType);

        il.Emit(OpCodes.Ldloc, typeValLocal);
        il.Emit(OpCodes.Callvirt, _types.Object.GetMethod("ToString", Type.EmptyTypes)!);
        il.Emit(OpCodes.Stloc, optTypeLocal);
        il.Emit(OpCodes.Br, afterType);

        il.MarkLabel(defaultType);
        il.Emit(OpCodes.Ldstr, "boolean");
        il.Emit(OpCodes.Stloc, optTypeLocal);

        il.MarkLabel(afterType);

        // multiple = optDef.TryGetValue("multiple", out m) && m is true
        var notMultipleLabel = il.DefineLabel();
        var afterMultiple = il.DefineLabel();

        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Stloc, multipleLocal);

        il.Emit(OpCodes.Ldloc, optDefLocal);
        il.Emit(OpCodes.Ldstr, "multiple");
        il.Emit(OpCodes.Ldloca, mValLocal);
        il.Emit(OpCodes.Callvirt, _types.DictionaryStringObjectNullableTryGetValue);
        il.Emit(OpCodes.Brfalse, afterMultiple);

        il.Emit(OpCodes.Ldloc, mValLocal);
        il.Emit(OpCodes.Isinst, typeof(bool));
        il.Emit(OpCodes.Brfalse, afterMultiple);

        il.Emit(OpCodes.Ldloc, mValLocal);
        il.Emit(OpCodes.Unbox_Any, typeof(bool));
        il.Emit(OpCodes.Stloc, multipleLocal);

        il.MarkLabel(afterMultiple);

        // Now handle value extraction based on type
        // if (optType == "boolean")
        il.Emit(OpCodes.Ldloc, optTypeLocal);
        il.Emit(OpCodes.Ldstr, "boolean");
        il.Emit(OpCodes.Call, _types.String.GetMethod("op_Equality", [typeof(string), typeof(string)])!);
        il.Emit(OpCodes.Brtrue, isBooleanType);
        il.Emit(OpCodes.Br, isStringType);

        // Boolean type handling
        il.MarkLabel(isBooleanType);
        // if (hasInlineValue && strict) throw
        var boolNoError = il.DefineLabel();
        il.Emit(OpCodes.Ldloc, hasInlineValueLocal);
        il.Emit(OpCodes.Brfalse, boolNoError);
        il.Emit(OpCodes.Ldarg, 6); // strict
        il.Emit(OpCodes.Brfalse, boolNoError);

        // throw new Exception("Option '--{name}' does not take an argument")
        il.Emit(OpCodes.Ldstr, "Option '--");
        il.Emit(OpCodes.Ldloc, nameLocal);
        il.Emit(OpCodes.Ldstr, "' does not take an argument");
        il.Emit(OpCodes.Call, _types.String.GetMethod("Concat", [typeof(string), typeof(string), typeof(string)])!);
        il.Emit(OpCodes.Newobj, _types.ExceptionCtorString);
        il.Emit(OpCodes.Throw);

        il.MarkLabel(boolNoError);
        // value = !isNegated (box bool)
        il.Emit(OpCodes.Ldloc, isNegatedLocal);
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Ceq);
        il.Emit(OpCodes.Box, typeof(bool));
        il.Emit(OpCodes.Stloc, valueLocal);

        // index++
        il.Emit(OpCodes.Ldloc, indexLocal);
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Add);
        il.Emit(OpCodes.Stloc, indexLocal);
        il.Emit(OpCodes.Br, checkMultiple);

        // String type handling
        il.MarkLabel(isStringType);

        // if (isNegated && strict) throw
        var stringNoNegateError = il.DefineLabel();
        il.Emit(OpCodes.Ldloc, isNegatedLocal);
        il.Emit(OpCodes.Brfalse, stringNoNegateError);
        il.Emit(OpCodes.Ldarg, 6); // strict
        il.Emit(OpCodes.Brfalse, stringNoNegateError);

        il.Emit(OpCodes.Ldstr, "Option '--");
        il.Emit(OpCodes.Ldloc, nameLocal);
        il.Emit(OpCodes.Ldstr, "' cannot be negated");
        il.Emit(OpCodes.Call, _types.String.GetMethod("Concat", [typeof(string), typeof(string), typeof(string)])!);
        il.Emit(OpCodes.Newobj, _types.ExceptionCtorString);
        il.Emit(OpCodes.Throw);

        il.MarkLabel(stringNoNegateError);

        // if (hasInlineValue) { value = inlineValue; index++ }
        var noInlineForString = il.DefineLabel();
        il.Emit(OpCodes.Ldloc, hasInlineValueLocal);
        il.Emit(OpCodes.Brfalse, noInlineForString);

        il.Emit(OpCodes.Ldloc, inlineValueLocal);
        il.Emit(OpCodes.Stloc, valueLocal);
        il.Emit(OpCodes.Ldloc, indexLocal);
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Add);
        il.Emit(OpCodes.Stloc, indexLocal);
        il.Emit(OpCodes.Br, checkMultiple);

        // else if (index + 1 < argsArray.Count)
        il.MarkLabel(noInlineForString);
        var noNextArg = il.DefineLabel();
        il.Emit(OpCodes.Ldloc, indexLocal);
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Add);
        il.Emit(OpCodes.Ldarg_2); // argsArray
        il.Emit(OpCodes.Callvirt, _types.ListOfObjectNullable.GetProperty("Count")!.GetGetMethod()!);
        il.Emit(OpCodes.Bge, noNextArg);

        // value = argsArray[index + 1]?.ToString() ?? ""
        il.Emit(OpCodes.Ldarg_2);
        il.Emit(OpCodes.Ldloc, indexLocal);
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Add);
        il.Emit(OpCodes.Callvirt, _types.ListOfObjectNullable.GetMethod("get_Item", [typeof(int)])!);
        il.Emit(OpCodes.Dup);
        var notNullArg = il.DefineLabel();
        il.Emit(OpCodes.Brtrue, notNullArg);
        il.Emit(OpCodes.Pop);
        il.Emit(OpCodes.Ldstr, "");
        var afterNullCheck = il.DefineLabel();
        il.Emit(OpCodes.Br, afterNullCheck);
        il.MarkLabel(notNullArg);
        il.Emit(OpCodes.Callvirt, _types.Object.GetMethod("ToString", Type.EmptyTypes)!);
        il.Emit(OpCodes.Dup);
        var notNullStr = il.DefineLabel();
        il.Emit(OpCodes.Brtrue, notNullStr);
        il.Emit(OpCodes.Pop);
        il.Emit(OpCodes.Ldstr, "");
        il.MarkLabel(notNullStr);
        il.MarkLabel(afterNullCheck);
        il.Emit(OpCodes.Stloc, valueLocal);

        // index += 2
        il.Emit(OpCodes.Ldloc, indexLocal);
        il.Emit(OpCodes.Ldc_I4_2);
        il.Emit(OpCodes.Add);
        il.Emit(OpCodes.Stloc, indexLocal);
        il.Emit(OpCodes.Br, checkMultiple);

        // No next argument available
        il.MarkLabel(noNextArg);
        // if (strict) throw
        var noStrictError = il.DefineLabel();
        il.Emit(OpCodes.Ldarg, 6); // strict
        il.Emit(OpCodes.Brfalse, noStrictError);

        il.Emit(OpCodes.Ldstr, "Option '--");
        il.Emit(OpCodes.Ldloc, nameLocal);
        il.Emit(OpCodes.Ldstr, "' requires an argument");
        il.Emit(OpCodes.Call, _types.String.GetMethod("Concat", [typeof(string), typeof(string), typeof(string)])!);
        il.Emit(OpCodes.Newobj, _types.ExceptionCtorString);
        il.Emit(OpCodes.Throw);

        il.MarkLabel(noStrictError);
        il.Emit(OpCodes.Ldstr, "");
        il.Emit(OpCodes.Stloc, valueLocal);
        il.Emit(OpCodes.Ldloc, indexLocal);
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Add);
        il.Emit(OpCodes.Stloc, indexLocal);
        il.Emit(OpCodes.Br, checkMultiple);

        // Unknown option handling
        il.MarkLabel(unknownOption);
        // if (strict) throw
        var noUnknownError = il.DefineLabel();
        il.Emit(OpCodes.Ldarg, 6); // strict
        il.Emit(OpCodes.Brfalse, noUnknownError);

        il.Emit(OpCodes.Ldstr, "Unknown option '--");
        il.Emit(OpCodes.Ldloc, originalNameLocal);
        il.Emit(OpCodes.Ldstr, "'");
        il.Emit(OpCodes.Call, _types.String.GetMethod("Concat", [typeof(string), typeof(string), typeof(string)])!);
        il.Emit(OpCodes.Newobj, _types.ExceptionCtorString);
        il.Emit(OpCodes.Throw);

        il.MarkLabel(noUnknownError);
        // values[name] = !isNegated
        il.Emit(OpCodes.Ldarg, 4); // values
        il.Emit(OpCodes.Ldloc, nameLocal);
        il.Emit(OpCodes.Ldloc, isNegatedLocal);
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Ceq);
        il.Emit(OpCodes.Box, typeof(bool));
        il.Emit(OpCodes.Callvirt, _types.DictionaryStringObject.GetMethod("set_Item", [typeof(string), typeof(object)])!);

        // return index + 1
        il.Emit(OpCodes.Ldloc, indexLocal);
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Add);
        il.Emit(OpCodes.Ret);

        // Check if multiple values
        il.MarkLabel(checkMultiple);
        il.Emit(OpCodes.Ldloc, multipleLocal);
        il.Emit(OpCodes.Brfalse, notMultiple);

        // Multiple: add to list
        // if (!values.TryGetValue(name, out existing) || existing is not IList<object?>)
        var hasExistingList = il.DefineLabel();
        var createNewList = il.DefineLabel();
        il.Emit(OpCodes.Ldarg, 4); // values
        il.Emit(OpCodes.Ldloc, nameLocal);
        il.Emit(OpCodes.Ldloca, existingLocal);
        il.Emit(OpCodes.Callvirt, _types.DictionaryStringObject.GetMethod("TryGetValue")!);
        il.Emit(OpCodes.Brfalse, createNewList);

        // Check if existing is list
        il.Emit(OpCodes.Ldloc, existingLocal);
        il.Emit(OpCodes.Isinst, _types.ListOfObjectNullable);
        il.Emit(OpCodes.Brtrue, hasExistingList);

        // Create new list
        il.MarkLabel(createNewList);
        il.Emit(OpCodes.Newobj, _types.ListObjectNullableDefaultCtor);
        il.Emit(OpCodes.Stloc, listLocal);

        // values[name] = list
        il.Emit(OpCodes.Ldarg, 4);
        il.Emit(OpCodes.Ldloc, nameLocal);
        il.Emit(OpCodes.Ldloc, listLocal);
        il.Emit(OpCodes.Callvirt, _types.DictionaryStringObject.GetMethod("set_Item", [typeof(string), typeof(object)])!);
        var afterListSetup = il.DefineLabel();
        il.Emit(OpCodes.Br, afterListSetup);

        il.MarkLabel(hasExistingList);
        il.Emit(OpCodes.Ldloc, existingLocal);
        il.Emit(OpCodes.Castclass, _types.ListOfObjectNullable);
        il.Emit(OpCodes.Stloc, listLocal);

        il.MarkLabel(afterListSetup);
        // list.Add(value)
        il.Emit(OpCodes.Ldloc, listLocal);
        il.Emit(OpCodes.Ldloc, valueLocal);
        il.Emit(OpCodes.Callvirt, _types.ListOfObjectNullable.GetMethod("Add", [typeof(object)])!);
        il.Emit(OpCodes.Br, addTokens);

        // Not multiple: just set value
        il.MarkLabel(notMultiple);
        il.Emit(OpCodes.Ldarg, 4); // values
        il.Emit(OpCodes.Ldloc, nameLocal);
        il.Emit(OpCodes.Ldloc, valueLocal);
        il.Emit(OpCodes.Callvirt, _types.DictionaryStringObject.GetMethod("set_Item", [typeof(string), typeof(object)])!);

        // Add tokens if returnTokens is true
        il.MarkLabel(addTokens);
        il.Emit(OpCodes.Ldarg, 8); // returnTokens
        il.Emit(OpCodes.Brfalse, returnIndex);

        // tokens.Add(token dict) - simplified, just add basic token
        var tokenDict = il.DeclareLocal(_types.DictionaryStringObject);
        il.Emit(OpCodes.Newobj, _types.DictionaryStringObjectNullableCtor);
        il.Emit(OpCodes.Stloc, tokenDict);

        // token["kind"] = "option"
        il.Emit(OpCodes.Ldloc, tokenDict);
        il.Emit(OpCodes.Ldstr, "kind");
        il.Emit(OpCodes.Ldstr, "option");
        il.Emit(OpCodes.Callvirt, _types.DictionaryStringObject.GetMethod("set_Item", [typeof(string), typeof(object)])!);

        // token["name"] = name
        il.Emit(OpCodes.Ldloc, tokenDict);
        il.Emit(OpCodes.Ldstr, "name");
        il.Emit(OpCodes.Ldloc, nameLocal);
        il.Emit(OpCodes.Callvirt, _types.DictionaryStringObject.GetMethod("set_Item", [typeof(string), typeof(object)])!);

        // tokens.Add(token)
        il.Emit(OpCodes.Ldarg, 5); // tokens
        il.Emit(OpCodes.Ldloc, tokenDict);
        il.Emit(OpCodes.Callvirt, _types.ListOfObjectNullable.GetMethod("Add", [typeof(object)])!);

        // Return index
        il.MarkLabel(returnIndex);
        il.Emit(OpCodes.Ldloc, indexLocal);
        il.Emit(OpCodes.Ret);
    }

    /// <summary>
    /// Emits ParseShortOptions body - parses -v and -abc style short options.
    /// </summary>
    private void EmitUtilParseShortOptionsBody(EmittedRuntime runtime)
    {
        var il = runtime.UtilParseShortOptions.GetILGenerator();
        var optionsDefType = typeof(Dictionary<string, Dictionary<string, object?>>);

        // Locals
        var shortOptsLocal = il.DeclareLocal(_types.String);      // loc.0 - arg[1..]
        var jLocal = il.DeclareLocal(_types.Int32);               // loc.1 - inner loop index
        var shortCharLocal = il.DeclareLocal(_types.String);      // loc.2 - current short char as string
        var optNameLocal = il.DeclareLocal(_types.String);        // loc.3 - found option name
        var optDefLocal = il.DeclareLocal(typeof(Dictionary<string, object?>)); // loc.4
        var optTypeLocal = il.DeclareLocal(_types.String);        // loc.5
        var multipleLocal = il.DeclareLocal(_types.Boolean);      // loc.6
        var valueLocal = il.DeclareLocal(_types.Object);          // loc.7
        var indexLocal = il.DeclareLocal(_types.Int32);           // loc.8 - working copy
        var keysLocal = il.DeclareLocal(typeof(List<string>));    // loc.9
        var kLocal = il.DeclareLocal(_types.Int32);               // loc.10 - keys loop index
        var keyLocal = il.DeclareLocal(_types.String);            // loc.11
        var defLocal = il.DeclareLocal(typeof(Dictionary<string, object?>)); // loc.12
        var shortValLocal = il.DeclareLocal(_types.Object);       // loc.13
        var typeValLocal = il.DeclareLocal(_types.Object);        // loc.14
        var mValLocal = il.DeclareLocal(_types.Object);           // loc.15
        var existingLocal = il.DeclareLocal(_types.Object);       // loc.16
        var listLocal = il.DeclareLocal(_types.ListOfObjectNullable); // loc.17

        // Labels
        var outerLoopStart = il.DefineLabel();
        var outerLoopCondition = il.DefineLabel();
        var innerLoopStart = il.DefineLabel();
        var innerLoopCondition = il.DefineLabel();
        var foundOption = il.DefineLabel();
        var afterInnerLoop = il.DefineLabel();
        var unknownShort = il.DefineLabel();
        var isBoolType = il.DefineLabel();
        var isStrType = il.DefineLabel();
        var checkMultiple = il.DefineLabel();
        var notMultiple = il.DefineLabel();
        var addTokens = il.DefineLabel();
        var continueOuter = il.DefineLabel();
        var returnIndex = il.DefineLabel();

        // index = arg1
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Stloc, indexLocal);

        // shortOpts = arg.Substring(1)
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Callvirt, _types.String.GetMethod("Substring", [typeof(int)])!);
        il.Emit(OpCodes.Stloc, shortOptsLocal);

        // j = 0
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Stloc, jLocal);
        il.Emit(OpCodes.Br, outerLoopCondition);

        // Outer loop: for each character in shortOpts
        il.MarkLabel(outerLoopStart);

        // shortChar = shortOpts[j].ToString()
        il.Emit(OpCodes.Ldloc, shortOptsLocal);
        il.Emit(OpCodes.Ldloc, jLocal);
        il.Emit(OpCodes.Callvirt, _types.String.GetMethod("get_Chars", [typeof(int)])!);
        var charLocal = il.DeclareLocal(typeof(char));
        il.Emit(OpCodes.Stloc, charLocal);
        il.Emit(OpCodes.Ldloca, charLocal);
        il.Emit(OpCodes.Call, typeof(char).GetMethod("ToString", Type.EmptyTypes)!);
        il.Emit(OpCodes.Stloc, shortCharLocal);

        // optName = null; optDef = null
        il.Emit(OpCodes.Ldnull);
        il.Emit(OpCodes.Stloc, optNameLocal);
        il.Emit(OpCodes.Ldnull);
        il.Emit(OpCodes.Stloc, optDefLocal);

        // Inner loop: find matching option in optionsDef
        // keys = new List<string>(optionsDef.Keys)
        il.Emit(OpCodes.Ldarg_3); // optionsDef
        il.Emit(OpCodes.Callvirt, optionsDefType.GetProperty("Keys")!.GetGetMethod()!);
        il.Emit(OpCodes.Newobj, _types.ListStringFromEnumerableCtor);
        il.Emit(OpCodes.Stloc, keysLocal);

        // k = 0
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Stloc, kLocal);
        il.Emit(OpCodes.Br, innerLoopCondition);

        // Inner loop body
        il.MarkLabel(innerLoopStart);

        // key = keys[k]
        il.Emit(OpCodes.Ldloc, keysLocal);
        il.Emit(OpCodes.Ldloc, kLocal);
        il.Emit(OpCodes.Callvirt, _types.ListStringGetItem);
        il.Emit(OpCodes.Stloc, keyLocal);

        // def = optionsDef[key]
        il.Emit(OpCodes.Ldarg_3);
        il.Emit(OpCodes.Ldloc, keyLocal);
        il.Emit(OpCodes.Callvirt, optionsDefType.GetMethod("get_Item", [typeof(string)])!);
        il.Emit(OpCodes.Stloc, defLocal);

        // if (def.TryGetValue("short", out shortVal) && shortVal?.ToString() == shortChar)
        var nextKey = il.DefineLabel();
        il.Emit(OpCodes.Ldloc, defLocal);
        il.Emit(OpCodes.Ldstr, "short");
        il.Emit(OpCodes.Ldloca, shortValLocal);
        il.Emit(OpCodes.Callvirt, _types.DictionaryStringObjectNullableTryGetValue);
        il.Emit(OpCodes.Brfalse, nextKey);

        il.Emit(OpCodes.Ldloc, shortValLocal);
        il.Emit(OpCodes.Brfalse, nextKey);

        il.Emit(OpCodes.Ldloc, shortValLocal);
        il.Emit(OpCodes.Callvirt, _types.Object.GetMethod("ToString", Type.EmptyTypes)!);
        il.Emit(OpCodes.Ldloc, shortCharLocal);
        il.Emit(OpCodes.Call, _types.String.GetMethod("op_Equality", [typeof(string), typeof(string)])!);
        il.Emit(OpCodes.Brfalse, nextKey);

        // Found match
        il.Emit(OpCodes.Ldloc, keyLocal);
        il.Emit(OpCodes.Stloc, optNameLocal);
        il.Emit(OpCodes.Ldloc, defLocal);
        il.Emit(OpCodes.Stloc, optDefLocal);
        il.Emit(OpCodes.Br, foundOption);

        il.MarkLabel(nextKey);
        // k++
        il.Emit(OpCodes.Ldloc, kLocal);
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Add);
        il.Emit(OpCodes.Stloc, kLocal);

        // Inner loop condition: k < keys.Count
        il.MarkLabel(innerLoopCondition);
        il.Emit(OpCodes.Ldloc, kLocal);
        il.Emit(OpCodes.Ldloc, keysLocal);
        il.Emit(OpCodes.Callvirt, typeof(List<string>).GetProperty("Count")!.GetGetMethod()!);
        il.Emit(OpCodes.Blt, innerLoopStart);

        // After inner loop - no match found
        il.MarkLabel(afterInnerLoop);
        // if (optName == null)
        il.Emit(OpCodes.Ldloc, optNameLocal);
        il.Emit(OpCodes.Brfalse, unknownShort);
        il.Emit(OpCodes.Br, foundOption);

        // Unknown short option
        il.MarkLabel(unknownShort);
        // if (strict) throw
        var noUnknownError = il.DefineLabel();
        il.Emit(OpCodes.Ldarg, 6); // strict
        il.Emit(OpCodes.Brfalse, noUnknownError);

        il.Emit(OpCodes.Ldstr, "Unknown option '-");
        il.Emit(OpCodes.Ldloc, shortCharLocal);
        il.Emit(OpCodes.Ldstr, "'");
        il.Emit(OpCodes.Call, _types.String.GetMethod("Concat", [typeof(string), typeof(string), typeof(string)])!);
        il.Emit(OpCodes.Newobj, _types.ExceptionCtorString);
        il.Emit(OpCodes.Throw);

        il.MarkLabel(noUnknownError);
        // continue to next char
        il.Emit(OpCodes.Br, continueOuter);

        // Found option - process it
        il.MarkLabel(foundOption);

        // optType = optDef.TryGetValue("type", ...) ? ... : "boolean"
        var defaultType = il.DefineLabel();
        var afterType = il.DefineLabel();

        il.Emit(OpCodes.Ldloc, optDefLocal);
        il.Emit(OpCodes.Ldstr, "type");
        il.Emit(OpCodes.Ldloca, typeValLocal);
        il.Emit(OpCodes.Callvirt, _types.DictionaryStringObjectNullableTryGetValue);
        il.Emit(OpCodes.Brfalse, defaultType);

        il.Emit(OpCodes.Ldloc, typeValLocal);
        il.Emit(OpCodes.Brfalse, defaultType);

        il.Emit(OpCodes.Ldloc, typeValLocal);
        il.Emit(OpCodes.Callvirt, _types.Object.GetMethod("ToString", Type.EmptyTypes)!);
        il.Emit(OpCodes.Stloc, optTypeLocal);
        il.Emit(OpCodes.Br, afterType);

        il.MarkLabel(defaultType);
        il.Emit(OpCodes.Ldstr, "boolean");
        il.Emit(OpCodes.Stloc, optTypeLocal);

        il.MarkLabel(afterType);

        // multiple = optDef.TryGetValue("multiple", ...) && ...
        var afterMultiple = il.DefineLabel();
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Stloc, multipleLocal);

        il.Emit(OpCodes.Ldloc, optDefLocal);
        il.Emit(OpCodes.Ldstr, "multiple");
        il.Emit(OpCodes.Ldloca, mValLocal);
        il.Emit(OpCodes.Callvirt, _types.DictionaryStringObjectNullableTryGetValue);
        il.Emit(OpCodes.Brfalse, afterMultiple);

        il.Emit(OpCodes.Ldloc, mValLocal);
        il.Emit(OpCodes.Isinst, typeof(bool));
        il.Emit(OpCodes.Brfalse, afterMultiple);

        il.Emit(OpCodes.Ldloc, mValLocal);
        il.Emit(OpCodes.Unbox_Any, typeof(bool));
        il.Emit(OpCodes.Stloc, multipleLocal);

        il.MarkLabel(afterMultiple);

        // if (optType == "boolean") value = true
        il.Emit(OpCodes.Ldloc, optTypeLocal);
        il.Emit(OpCodes.Ldstr, "boolean");
        il.Emit(OpCodes.Call, _types.String.GetMethod("op_Equality", [typeof(string), typeof(string)])!);
        il.Emit(OpCodes.Brtrue, isBoolType);
        il.Emit(OpCodes.Br, isStrType);

        il.MarkLabel(isBoolType);
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Box, typeof(bool));
        il.Emit(OpCodes.Stloc, valueLocal);
        il.Emit(OpCodes.Br, checkMultiple);

        // String type - get value from rest of shortOpts or next arg
        il.MarkLabel(isStrType);

        // if (j + 1 < shortOpts.Length) { value = shortOpts[(j+1)..]; j = shortOpts.Length }
        var noInlineShort = il.DefineLabel();
        il.Emit(OpCodes.Ldloc, jLocal);
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Add);
        il.Emit(OpCodes.Ldloc, shortOptsLocal);
        il.Emit(OpCodes.Callvirt, _types.String.GetProperty("Length")!.GetGetMethod()!);
        il.Emit(OpCodes.Bge, noInlineShort);

        // value = shortOpts.Substring(j + 1)
        il.Emit(OpCodes.Ldloc, shortOptsLocal);
        il.Emit(OpCodes.Ldloc, jLocal);
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Add);
        il.Emit(OpCodes.Callvirt, _types.String.GetMethod("Substring", [typeof(int)])!);
        il.Emit(OpCodes.Stloc, valueLocal);

        // j = shortOpts.Length (exit outer loop after this)
        il.Emit(OpCodes.Ldloc, shortOptsLocal);
        il.Emit(OpCodes.Callvirt, _types.String.GetProperty("Length")!.GetGetMethod()!);
        il.Emit(OpCodes.Stloc, jLocal);
        il.Emit(OpCodes.Br, checkMultiple);

        // else if (index + 1 < argsArray.Count)
        il.MarkLabel(noInlineShort);
        var noNextArg = il.DefineLabel();
        il.Emit(OpCodes.Ldloc, indexLocal);
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Add);
        il.Emit(OpCodes.Ldarg_2); // argsArray
        il.Emit(OpCodes.Callvirt, _types.ListOfObjectNullable.GetProperty("Count")!.GetGetMethod()!);
        il.Emit(OpCodes.Bge, noNextArg);

        // value = argsArray[index + 1]?.ToString() ?? ""
        il.Emit(OpCodes.Ldarg_2);
        il.Emit(OpCodes.Ldloc, indexLocal);
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Add);
        il.Emit(OpCodes.Callvirt, _types.ListOfObjectNullable.GetMethod("get_Item", [typeof(int)])!);
        il.Emit(OpCodes.Dup);
        var notNullArg = il.DefineLabel();
        il.Emit(OpCodes.Brtrue, notNullArg);
        il.Emit(OpCodes.Pop);
        il.Emit(OpCodes.Ldstr, "");
        var afterNull = il.DefineLabel();
        il.Emit(OpCodes.Br, afterNull);
        il.MarkLabel(notNullArg);
        il.Emit(OpCodes.Callvirt, _types.Object.GetMethod("ToString", Type.EmptyTypes)!);
        il.Emit(OpCodes.Dup);
        var notNullStr = il.DefineLabel();
        il.Emit(OpCodes.Brtrue, notNullStr);
        il.Emit(OpCodes.Pop);
        il.Emit(OpCodes.Ldstr, "");
        il.MarkLabel(notNullStr);
        il.MarkLabel(afterNull);
        il.Emit(OpCodes.Stloc, valueLocal);

        // index++
        il.Emit(OpCodes.Ldloc, indexLocal);
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Add);
        il.Emit(OpCodes.Stloc, indexLocal);
        il.Emit(OpCodes.Br, checkMultiple);

        // No argument available
        il.MarkLabel(noNextArg);
        // if (strict) throw
        var noStrictErr = il.DefineLabel();
        il.Emit(OpCodes.Ldarg, 6); // strict
        il.Emit(OpCodes.Brfalse, noStrictErr);

        il.Emit(OpCodes.Ldstr, "Option '-");
        il.Emit(OpCodes.Ldloc, shortCharLocal);
        il.Emit(OpCodes.Ldstr, "' requires an argument");
        il.Emit(OpCodes.Call, _types.String.GetMethod("Concat", [typeof(string), typeof(string), typeof(string)])!);
        il.Emit(OpCodes.Newobj, _types.ExceptionCtorString);
        il.Emit(OpCodes.Throw);

        il.MarkLabel(noStrictErr);
        il.Emit(OpCodes.Ldstr, "");
        il.Emit(OpCodes.Stloc, valueLocal);

        // Check multiple
        il.MarkLabel(checkMultiple);
        il.Emit(OpCodes.Ldloc, multipleLocal);
        il.Emit(OpCodes.Brfalse, notMultiple);

        // Multiple: add to list
        var hasExistingList = il.DefineLabel();
        var createNewList = il.DefineLabel();
        var afterListSetup = il.DefineLabel();

        il.Emit(OpCodes.Ldarg, 4); // values
        il.Emit(OpCodes.Ldloc, optNameLocal);
        il.Emit(OpCodes.Ldloca, existingLocal);
        il.Emit(OpCodes.Callvirt, _types.DictionaryStringObject.GetMethod("TryGetValue")!);
        il.Emit(OpCodes.Brfalse, createNewList);

        il.Emit(OpCodes.Ldloc, existingLocal);
        il.Emit(OpCodes.Isinst, _types.ListOfObjectNullable);
        il.Emit(OpCodes.Brtrue, hasExistingList);

        il.MarkLabel(createNewList);
        il.Emit(OpCodes.Newobj, _types.ListObjectNullableDefaultCtor);
        il.Emit(OpCodes.Stloc, listLocal);

        il.Emit(OpCodes.Ldarg, 4);
        il.Emit(OpCodes.Ldloc, optNameLocal);
        il.Emit(OpCodes.Ldloc, listLocal);
        il.Emit(OpCodes.Callvirt, _types.DictionaryStringObject.GetMethod("set_Item", [typeof(string), typeof(object)])!);
        il.Emit(OpCodes.Br, afterListSetup);

        il.MarkLabel(hasExistingList);
        il.Emit(OpCodes.Ldloc, existingLocal);
        il.Emit(OpCodes.Castclass, _types.ListOfObjectNullable);
        il.Emit(OpCodes.Stloc, listLocal);

        il.MarkLabel(afterListSetup);
        il.Emit(OpCodes.Ldloc, listLocal);
        il.Emit(OpCodes.Ldloc, valueLocal);
        il.Emit(OpCodes.Callvirt, _types.ListOfObjectNullable.GetMethod("Add", [typeof(object)])!);
        il.Emit(OpCodes.Br, addTokens);

        // Not multiple
        il.MarkLabel(notMultiple);
        il.Emit(OpCodes.Ldarg, 4);
        il.Emit(OpCodes.Ldloc, optNameLocal);
        il.Emit(OpCodes.Ldloc, valueLocal);
        il.Emit(OpCodes.Callvirt, _types.DictionaryStringObject.GetMethod("set_Item", [typeof(string), typeof(object)])!);

        // Add tokens if needed
        il.MarkLabel(addTokens);
        il.Emit(OpCodes.Ldarg, 7); // returnTokens
        il.Emit(OpCodes.Brfalse, continueOuter);

        // Simplified token
        var tokenDict = il.DeclareLocal(_types.DictionaryStringObject);
        il.Emit(OpCodes.Newobj, _types.DictionaryStringObjectNullableCtor);
        il.Emit(OpCodes.Stloc, tokenDict);

        il.Emit(OpCodes.Ldloc, tokenDict);
        il.Emit(OpCodes.Ldstr, "kind");
        il.Emit(OpCodes.Ldstr, "option");
        il.Emit(OpCodes.Callvirt, _types.DictionaryStringObject.GetMethod("set_Item", [typeof(string), typeof(object)])!);

        il.Emit(OpCodes.Ldloc, tokenDict);
        il.Emit(OpCodes.Ldstr, "name");
        il.Emit(OpCodes.Ldloc, optNameLocal);
        il.Emit(OpCodes.Callvirt, _types.DictionaryStringObject.GetMethod("set_Item", [typeof(string), typeof(object)])!);

        il.Emit(OpCodes.Ldarg, 5); // tokens
        il.Emit(OpCodes.Ldloc, tokenDict);
        il.Emit(OpCodes.Callvirt, _types.ListOfObjectNullable.GetMethod("Add", [typeof(object)])!);

        // Continue outer loop
        il.MarkLabel(continueOuter);
        il.Emit(OpCodes.Ldloc, jLocal);
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Add);
        il.Emit(OpCodes.Stloc, jLocal);

        // Outer loop condition: j < shortOpts.Length
        il.MarkLabel(outerLoopCondition);
        il.Emit(OpCodes.Ldloc, jLocal);
        il.Emit(OpCodes.Ldloc, shortOptsLocal);
        il.Emit(OpCodes.Callvirt, _types.String.GetProperty("Length")!.GetGetMethod()!);
        il.Emit(OpCodes.Blt, outerLoopStart);

        // Return index + 1
        il.MarkLabel(returnIndex);
        il.Emit(OpCodes.Ldloc, indexLocal);
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Add);
        il.Emit(OpCodes.Ret);
    }
}
