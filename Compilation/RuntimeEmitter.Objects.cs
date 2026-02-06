using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using System.Text;

namespace SharpTS.Compilation;

public partial class RuntimeEmitter
{
    private void EmitCreateObject(TypeBuilder typeBuilder, EmittedRuntime runtime)
    {
        var method = typeBuilder.DefineMethod(
            "CreateObject",
            MethodAttributes.Public | MethodAttributes.Static,
            _types.DictionaryStringObject,
            [_types.DictionaryStringObject]
        );
        runtime.CreateObject = method;

        var il = method.GetILGenerator();
        // Just return the dictionary as-is (it's already created)
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ret);
    }

    private void EmitMergeIntoObject(TypeBuilder typeBuilder, EmittedRuntime runtime)
    {
        var method = typeBuilder.DefineMethod(
            "MergeIntoObject",
            MethodAttributes.Public | MethodAttributes.Static,
            _types.Void,
            [_types.DictionaryStringObject, _types.Object]
        );
        runtime.MergeIntoObject = method;

        var il = method.GetILGenerator();
        var dictLabel = il.DefineLabel();

        // Check if source is dict
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Isinst, _types.DictionaryStringObject);
        il.Emit(OpCodes.Brtrue, dictLabel);

        // Not a dict - do nothing
        il.Emit(OpCodes.Ret);

        il.MarkLabel(dictLabel);
        // Iterate and copy
        // We need the Enumerator type for Dictionary<string, object>
        // Since TypeProvider might not expose nested types directly, we resolve it from the Dictionary type
        var dictType = _types.DictionaryStringObject;
        var enumeratorType = typeof(Dictionary<string, object>.Enumerator);
        var keyValuePairType = _types.KeyValuePairStringObject;

        var enumeratorLocal = il.DeclareLocal(enumeratorType);
        var loopStart = il.DefineLabel();
        var loopEnd = il.DefineLabel();

        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Castclass, dictType);
        il.Emit(OpCodes.Callvirt, _types.GetMethodNoParams(dictType, "GetEnumerator"));
        il.Emit(OpCodes.Stloc, enumeratorLocal);

        il.MarkLabel(loopStart);
        il.Emit(OpCodes.Ldloca, enumeratorLocal);
        il.Emit(OpCodes.Call, _types.GetMethodNoParams(enumeratorType, "MoveNext"));
        il.Emit(OpCodes.Brfalse, loopEnd);

        // Get current and add to target
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldloca, enumeratorLocal);
        il.Emit(OpCodes.Call, _types.GetProperty(enumeratorType, "Current")!.GetGetMethod()!);
        var kvpLocal = il.DeclareLocal(keyValuePairType);
        il.Emit(OpCodes.Stloc, kvpLocal);
        il.Emit(OpCodes.Ldloca, kvpLocal);
        il.Emit(OpCodes.Call, _types.GetProperty(keyValuePairType, "Key")!.GetGetMethod()!);
        il.Emit(OpCodes.Ldloca, kvpLocal);
        il.Emit(OpCodes.Call, _types.GetProperty(keyValuePairType, "Value")!.GetGetMethod()!);
        il.Emit(OpCodes.Callvirt, _types.GetMethod(dictType, "set_Item", _types.String, _types.Object));

        il.Emit(OpCodes.Br, loopStart);

        il.MarkLabel(loopEnd);
        il.Emit(OpCodes.Ret);
    }

    private void EmitMergeIntoTSObject(TypeBuilder typeBuilder, EmittedRuntime runtime)
    {
        // public static void MergeIntoTSObject($Object target, object? source)
        // Merges properties from source (Dictionary or $Object) into target $Object
        var method = typeBuilder.DefineMethod(
            "MergeIntoTSObject",
            MethodAttributes.Public | MethodAttributes.Static,
            _types.Void,
            [runtime.TSObjectType, _types.Object]
        );
        runtime.MergeIntoTSObject = method;

        var il = method.GetILGenerator();
        var dictLabel = il.DefineLabel();
        var tsObjectLabel = il.DefineLabel();
        var endLabel = il.DefineLabel();

        // Check if source is Dictionary<string, object?>
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Isinst, _types.DictionaryStringObject);
        il.Emit(OpCodes.Brtrue, dictLabel);

        // Check if source is $Object
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Isinst, runtime.TSObjectType);
        il.Emit(OpCodes.Brtrue, tsObjectLabel);

        // Not a dict or $Object - do nothing
        il.Emit(OpCodes.Ret);

        // Handle Dictionary source
        il.MarkLabel(dictLabel);
        {
            var dictType = _types.DictionaryStringObject;
            var enumeratorType = typeof(Dictionary<string, object>.Enumerator);
            var keyValuePairType = _types.KeyValuePairStringObject;

            var enumeratorLocal = il.DeclareLocal(enumeratorType);
            var kvpLocal = il.DeclareLocal(keyValuePairType);
            var loopStart = il.DefineLabel();
            var loopEnd = il.DefineLabel();

            il.Emit(OpCodes.Ldarg_1);
            il.Emit(OpCodes.Castclass, dictType);
            il.Emit(OpCodes.Callvirt, _types.GetMethodNoParams(dictType, "GetEnumerator"));
            il.Emit(OpCodes.Stloc, enumeratorLocal);

            il.MarkLabel(loopStart);
            il.Emit(OpCodes.Ldloca, enumeratorLocal);
            il.Emit(OpCodes.Call, _types.GetMethodNoParams(enumeratorType, "MoveNext"));
            il.Emit(OpCodes.Brfalse, loopEnd);

            // target.SetProperty(key, value)
            il.Emit(OpCodes.Ldarg_0); // target
            il.Emit(OpCodes.Ldloca, enumeratorLocal);
            il.Emit(OpCodes.Call, _types.GetProperty(enumeratorType, "Current")!.GetGetMethod()!);
            il.Emit(OpCodes.Stloc, kvpLocal);
            il.Emit(OpCodes.Ldloca, kvpLocal);
            il.Emit(OpCodes.Call, _types.GetProperty(keyValuePairType, "Key")!.GetGetMethod()!);
            il.Emit(OpCodes.Ldloca, kvpLocal);
            il.Emit(OpCodes.Call, _types.GetProperty(keyValuePairType, "Value")!.GetGetMethod()!);
            il.Emit(OpCodes.Callvirt, runtime.TSObjectSetProperty);

            il.Emit(OpCodes.Br, loopStart);

            il.MarkLabel(loopEnd);
            il.Emit(OpCodes.Br, endLabel);
        }

        // Handle $Object source - iterate using PropertyNames (IEnumerable<string>)
        il.MarkLabel(tsObjectLabel);
        {
            // PropertyNames returns IEnumerable<string> (specifically Dictionary.KeyCollection)
            // We iterate using the IEnumerator<string> interface
            var sourceLocal = il.DeclareLocal(runtime.TSObjectType);
            var enumeratorLocal = il.DeclareLocal(typeof(IEnumerator<string>));
            var keyLocal = il.DeclareLocal(_types.String);
            var loopStart = il.DefineLabel();
            var loopEnd = il.DefineLabel();

            // Store source as $Object
            il.Emit(OpCodes.Ldarg_1);
            il.Emit(OpCodes.Castclass, runtime.TSObjectType);
            il.Emit(OpCodes.Stloc, sourceLocal);

            // Get PropertyNames and GetEnumerator
            il.Emit(OpCodes.Ldloc, sourceLocal);
            il.Emit(OpCodes.Callvirt, runtime.TSObjectGetKeys); // Returns IEnumerable<string>
            il.Emit(OpCodes.Callvirt, typeof(IEnumerable<string>).GetMethod("GetEnumerator")!);
            il.Emit(OpCodes.Stloc, enumeratorLocal);

            il.MarkLabel(loopStart);
            // if (!enumerator.MoveNext()) break
            il.Emit(OpCodes.Ldloc, enumeratorLocal);
            il.Emit(OpCodes.Callvirt, typeof(IEnumerator).GetMethod("MoveNext")!);
            il.Emit(OpCodes.Brfalse, loopEnd);

            // key = enumerator.Current
            il.Emit(OpCodes.Ldloc, enumeratorLocal);
            il.Emit(OpCodes.Callvirt, typeof(IEnumerator<string>).GetProperty("Current")!.GetGetMethod()!);
            il.Emit(OpCodes.Stloc, keyLocal);

            // target.SetProperty(key, source.GetProperty(key))
            il.Emit(OpCodes.Ldarg_0); // target
            il.Emit(OpCodes.Ldloc, keyLocal);
            il.Emit(OpCodes.Ldloc, sourceLocal);
            il.Emit(OpCodes.Ldloc, keyLocal);
            il.Emit(OpCodes.Callvirt, runtime.TSObjectGetProperty);
            il.Emit(OpCodes.Callvirt, runtime.TSObjectSetProperty);

            il.Emit(OpCodes.Br, loopStart);

            il.MarkLabel(loopEnd);
        }

        il.MarkLabel(endLabel);
        il.Emit(OpCodes.Ret);
    }

    private void EmitRandom(TypeBuilder typeBuilder, EmittedRuntime runtime, FieldBuilder randomField)
    {
        var method = typeBuilder.DefineMethod(
            "Random",
            MethodAttributes.Public | MethodAttributes.Static,
            _types.Double,
            _types.EmptyTypes
        );
        runtime.Random = method;

        var il = method.GetILGenerator();
        il.Emit(OpCodes.Ldsfld, randomField);
        il.Emit(OpCodes.Callvirt, _types.GetMethodNoParams(_types.Random, "NextDouble"));
        il.Emit(OpCodes.Ret);
    }

    private void EmitGetEnumMemberName(TypeBuilder typeBuilder, EmittedRuntime runtime)
    {
        var method = typeBuilder.DefineMethod(
            "GetEnumMemberName",
            MethodAttributes.Public | MethodAttributes.Static,
            _types.String,
            [_types.String, _types.Double, _types.DoubleArray, _types.StringArray]
        );
        runtime.GetEnumMemberName = method;

        var il = method.GetILGenerator();
        // Simple linear search through keys to find matching value
        var indexLocal = il.DeclareLocal(_types.Int32);
        var loopStart = il.DefineLabel();
        var loopEnd = il.DefineLabel();

        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Stloc, indexLocal);

        il.MarkLabel(loopStart);
        il.Emit(OpCodes.Ldloc, indexLocal);
        il.Emit(OpCodes.Ldarg_2);
        il.Emit(OpCodes.Ldlen);
        il.Emit(OpCodes.Conv_I4);
        il.Emit(OpCodes.Bge, loopEnd);

        // Check if keys[i] == value
        il.Emit(OpCodes.Ldarg_2);
        il.Emit(OpCodes.Ldloc, indexLocal);
        il.Emit(OpCodes.Ldelem_R8);
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Ceq);
        var notMatchLabel = il.DefineLabel();
        il.Emit(OpCodes.Brfalse, notMatchLabel);

        // Found - return values[i]
        il.Emit(OpCodes.Ldarg_3);
        il.Emit(OpCodes.Ldloc, indexLocal);
        il.Emit(OpCodes.Ldelem_Ref);
        il.Emit(OpCodes.Ret);

        il.MarkLabel(notMatchLabel);
        il.Emit(OpCodes.Ldloc, indexLocal);
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Add);
        il.Emit(OpCodes.Stloc, indexLocal);
        il.Emit(OpCodes.Br, loopStart);

        il.MarkLabel(loopEnd);
        // Not found - throw
        il.Emit(OpCodes.Ldstr, "Value not found in enum");
        il.Emit(OpCodes.Newobj, _types.GetConstructor(_types.Exception, _types.String));
        il.Emit(OpCodes.Throw);
    }

    private void EmitConcatTemplate(TypeBuilder typeBuilder, EmittedRuntime runtime)
    {
        var method = typeBuilder.DefineMethod(
            "ConcatTemplate",
            MethodAttributes.Public | MethodAttributes.Static,
            _types.String,
            [_types.ObjectArray]
        );
        runtime.ConcatTemplate = method;

        var il = method.GetILGenerator();

        // Use StringBuilder to concatenate stringified parts
        var sbLocal = il.DeclareLocal(_types.StringBuilder);
        var indexLocal = il.DeclareLocal(_types.Int32);
        var lengthLocal = il.DeclareLocal(_types.Int32);

        // sb = new StringBuilder()
        il.Emit(OpCodes.Newobj, _types.GetDefaultConstructor(_types.StringBuilder));
        il.Emit(OpCodes.Stloc, sbLocal);

        // length = parts.Length
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldlen);
        il.Emit(OpCodes.Conv_I4);
        il.Emit(OpCodes.Stloc, lengthLocal);

        // index = 0
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Stloc, indexLocal);

        var loopStart = il.DefineLabel();
        var loopEnd = il.DefineLabel();

        il.MarkLabel(loopStart);
        // if (index >= length) goto end
        il.Emit(OpCodes.Ldloc, indexLocal);
        il.Emit(OpCodes.Ldloc, lengthLocal);
        il.Emit(OpCodes.Bge, loopEnd);

        // sb.Append(Stringify(parts[index]))
        il.Emit(OpCodes.Ldloc, sbLocal);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldloc, indexLocal);
        il.Emit(OpCodes.Ldelem_Ref);
        il.Emit(OpCodes.Call, runtime.Stringify);
        il.Emit(OpCodes.Callvirt, _types.GetMethod(_types.StringBuilder, "Append", _types.String));
        il.Emit(OpCodes.Pop); // discard StringBuilder return value

        // index++
        il.Emit(OpCodes.Ldloc, indexLocal);
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Add);
        il.Emit(OpCodes.Stloc, indexLocal);
        il.Emit(OpCodes.Br, loopStart);

        il.MarkLabel(loopEnd);
        // return sb.ToString()
        il.Emit(OpCodes.Ldloc, sbLocal);
        il.Emit(OpCodes.Callvirt, _types.GetMethodNoParams(_types.StringBuilder, "ToString"));
        il.Emit(OpCodes.Ret);
    }

    /// <summary>
    /// Emits the $TemplateStringsList class for tagged template literals.
    /// This is a List&lt;object&gt; subclass with a "raw" property for accessing raw strings.
    /// </summary>
    internal void EmitTemplateStringsListClass(ModuleBuilder moduleBuilder, EmittedRuntime runtime)
    {
        // Define class: public sealed class $TemplateStringsList : List<object>
        var typeBuilder = moduleBuilder.DefineType(
            "$TemplateStringsList",
            TypeAttributes.Public | TypeAttributes.Sealed | TypeAttributes.BeforeFieldInit,
            _types.ListOfObject
        );

        // Field: private readonly List<object> _rawStrings
        var rawStringsField = typeBuilder.DefineField(
            "_rawStrings",
            _types.ListOfObject,
            FieldAttributes.Private | FieldAttributes.InitOnly
        );

        // Constructor: public $TemplateStringsList(object[] cookedStrings, string[] rawStrings)
        var ctorBuilder = typeBuilder.DefineConstructor(
            MethodAttributes.Public,
            CallingConventions.Standard,
            [_types.ObjectArray, _types.StringArray]
        );

        var ctorIL = ctorBuilder.GetILGenerator();

        // Call base constructor: List<object>()
        ctorIL.Emit(OpCodes.Ldarg_0);
        ctorIL.Emit(OpCodes.Call, _types.GetDefaultConstructor(_types.ListOfObject));

        // Add cooked strings to the list
        // foreach (var s in cookedStrings) Add(s ?? "undefined")
        var iLocal = ctorIL.DeclareLocal(_types.Int32);
        var cookedLoopStart = ctorIL.DefineLabel();
        var cookedLoopEnd = ctorIL.DefineLabel();

        ctorIL.Emit(OpCodes.Ldc_I4_0);
        ctorIL.Emit(OpCodes.Stloc, iLocal);

        ctorIL.MarkLabel(cookedLoopStart);
        ctorIL.Emit(OpCodes.Ldloc, iLocal);
        ctorIL.Emit(OpCodes.Ldarg_1); // cookedStrings
        ctorIL.Emit(OpCodes.Ldlen);
        ctorIL.Emit(OpCodes.Conv_I4);
        ctorIL.Emit(OpCodes.Bge, cookedLoopEnd);

        // this.Add(cookedStrings[i] ?? "undefined")
        ctorIL.Emit(OpCodes.Ldarg_0);
        ctorIL.Emit(OpCodes.Ldarg_1);
        ctorIL.Emit(OpCodes.Ldloc, iLocal);
        ctorIL.Emit(OpCodes.Ldelem_Ref);

        // If null, replace with "undefined"
        var notNullLabel = ctorIL.DefineLabel();
        var afterNullCheck = ctorIL.DefineLabel();
        ctorIL.Emit(OpCodes.Dup);
        ctorIL.Emit(OpCodes.Brtrue, notNullLabel);
        ctorIL.Emit(OpCodes.Pop);
        ctorIL.Emit(OpCodes.Ldstr, "undefined");
        ctorIL.Emit(OpCodes.Br, afterNullCheck);
        ctorIL.MarkLabel(notNullLabel);
        ctorIL.MarkLabel(afterNullCheck);

        ctorIL.Emit(OpCodes.Callvirt, _types.GetMethod(_types.ListOfObject, "Add", _types.Object));

        ctorIL.Emit(OpCodes.Ldloc, iLocal);
        ctorIL.Emit(OpCodes.Ldc_I4_1);
        ctorIL.Emit(OpCodes.Add);
        ctorIL.Emit(OpCodes.Stloc, iLocal);
        ctorIL.Emit(OpCodes.Br, cookedLoopStart);

        ctorIL.MarkLabel(cookedLoopEnd);

        // Create _rawStrings list from rawStrings array
        // _rawStrings = new List<object>()
        ctorIL.Emit(OpCodes.Ldarg_0);
        ctorIL.Emit(OpCodes.Newobj, _types.GetConstructor(_types.ListOfObject, _types.EmptyTypes));
        ctorIL.Emit(OpCodes.Stfld, rawStringsField);

        // foreach (var s in rawStrings) _rawStrings.Add(s)
        var rawLoopStart = ctorIL.DefineLabel();
        var rawLoopEnd = ctorIL.DefineLabel();

        ctorIL.Emit(OpCodes.Ldc_I4_0);
        ctorIL.Emit(OpCodes.Stloc, iLocal);

        ctorIL.MarkLabel(rawLoopStart);
        ctorIL.Emit(OpCodes.Ldloc, iLocal);
        ctorIL.Emit(OpCodes.Ldarg_2); // rawStrings
        ctorIL.Emit(OpCodes.Ldlen);
        ctorIL.Emit(OpCodes.Conv_I4);
        ctorIL.Emit(OpCodes.Bge, rawLoopEnd);

        // _rawStrings.Add(rawStrings[i])
        ctorIL.Emit(OpCodes.Ldarg_0);
        ctorIL.Emit(OpCodes.Ldfld, rawStringsField);
        ctorIL.Emit(OpCodes.Ldarg_2);
        ctorIL.Emit(OpCodes.Ldloc, iLocal);
        ctorIL.Emit(OpCodes.Ldelem_Ref);
        ctorIL.Emit(OpCodes.Callvirt, _types.GetMethod(_types.ListOfObject, "Add", _types.Object));

        ctorIL.Emit(OpCodes.Ldloc, iLocal);
        ctorIL.Emit(OpCodes.Ldc_I4_1);
        ctorIL.Emit(OpCodes.Add);
        ctorIL.Emit(OpCodes.Stloc, iLocal);
        ctorIL.Emit(OpCodes.Br, rawLoopStart);

        ctorIL.MarkLabel(rawLoopEnd);
        ctorIL.Emit(OpCodes.Ret);

        // Property: public List<object> raw => _rawStrings
        var rawGetter = typeBuilder.DefineMethod(
            "get_raw",
            MethodAttributes.Public | MethodAttributes.SpecialName | MethodAttributes.HideBySig,
            _types.ListOfObject,
            Type.EmptyTypes
        );
        var rawGetterIL = rawGetter.GetILGenerator();
        rawGetterIL.Emit(OpCodes.Ldarg_0);
        rawGetterIL.Emit(OpCodes.Ldfld, rawStringsField);
        rawGetterIL.Emit(OpCodes.Ret);

        var rawProp = typeBuilder.DefineProperty(
            "raw",
            PropertyAttributes.None,
            _types.ListOfObject,
            null
        );
        rawProp.SetGetMethod(rawGetter);

        // Create the type
        var createdType = typeBuilder.CreateType()!;
        runtime.TemplateStringsListType = createdType;
        runtime.TemplateStringsListCtor = createdType.GetConstructor([_types.ObjectArray, _types.StringArray])!;
        runtime.TemplateStringsListRawGetter = createdType.GetProperty("raw")!.GetGetMethod()!;
    }

    private void EmitInvokeTaggedTemplate(TypeBuilder typeBuilder, EmittedRuntime runtime)
    {
        // InvokeTaggedTemplate(tag: object, cooked: object[], raw: string[], expressions: object[]) -> object?
        var method = typeBuilder.DefineMethod(
            "InvokeTaggedTemplate",
            MethodAttributes.Public | MethodAttributes.Static,
            _types.Object,
            [_types.Object, _types.ObjectArray, _types.StringArray, _types.ObjectArray]
        );
        runtime.InvokeTaggedTemplate = method;

        var il = method.GetILGenerator();

        // Create strings array: new $TemplateStringsList(cooked, raw)
        var stringsLocal = il.DeclareLocal(runtime.TemplateStringsListType);
        il.Emit(OpCodes.Ldarg_1); // cooked
        il.Emit(OpCodes.Ldarg_2); // raw
        il.Emit(OpCodes.Newobj, runtime.TemplateStringsListCtor);
        il.Emit(OpCodes.Stloc, stringsLocal);

        // Build args array: new object[1 + expressions.Length]
        var argsLocal = il.DeclareLocal(_types.ObjectArray);
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Ldarg_3); // expressions
        il.Emit(OpCodes.Ldlen);
        il.Emit(OpCodes.Conv_I4);
        il.Emit(OpCodes.Add);
        il.Emit(OpCodes.Newarr, _types.Object);
        il.Emit(OpCodes.Stloc, argsLocal);

        // args[0] = stringsArray
        il.Emit(OpCodes.Ldloc, argsLocal);
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Ldloc, stringsLocal);
        il.Emit(OpCodes.Stelem_Ref);

        // Copy expressions to args[1..]
        // for (int i = 0; i < expressions.Length; i++) args[i + 1] = expressions[i]
        var iLocal = il.DeclareLocal(_types.Int32);
        var copyLoopStart = il.DefineLabel();
        var copyLoopEnd = il.DefineLabel();

        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Stloc, iLocal);

        il.MarkLabel(copyLoopStart);
        il.Emit(OpCodes.Ldloc, iLocal);
        il.Emit(OpCodes.Ldarg_3); // expressions
        il.Emit(OpCodes.Ldlen);
        il.Emit(OpCodes.Conv_I4);
        il.Emit(OpCodes.Bge, copyLoopEnd);

        // args[i + 1] = expressions[i]
        il.Emit(OpCodes.Ldloc, argsLocal);
        il.Emit(OpCodes.Ldloc, iLocal);
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Add);
        il.Emit(OpCodes.Ldarg_3);
        il.Emit(OpCodes.Ldloc, iLocal);
        il.Emit(OpCodes.Ldelem_Ref);
        il.Emit(OpCodes.Stelem_Ref);

        il.Emit(OpCodes.Ldloc, iLocal);
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Add);
        il.Emit(OpCodes.Stloc, iLocal);
        il.Emit(OpCodes.Br, copyLoopStart);

        il.MarkLabel(copyLoopEnd);

        // Call tag.Invoke(args) - check for $TSFunction, Delegate, or generic Invoke method
        var tsFuncLabel = il.DefineLabel();
        var delegateLabel = il.DefineLabel();
        var reflectionLabel = il.DefineLabel();
        var errorLabel = il.DefineLabel();

        // Check if tag is $TSFunction
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Isinst, runtime.TSFunctionType);
        il.Emit(OpCodes.Brtrue, tsFuncLabel);

        // Check if tag is Delegate
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Isinst, _types.Delegate);
        il.Emit(OpCodes.Brtrue, delegateLabel);

        // Check if tag is not null (use reflection)
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Brtrue, reflectionLabel);

        // tag is null - throw error
        il.MarkLabel(errorLabel);
        il.Emit(OpCodes.Ldstr, "TypeError: Tagged template tag must be a function.");
        il.Emit(OpCodes.Newobj, _types.GetConstructor(_types.Exception, _types.String));
        il.Emit(OpCodes.Throw);

        // $TSFunction case: call func.Invoke(args)
        il.MarkLabel(tsFuncLabel);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Castclass, runtime.TSFunctionType);
        il.Emit(OpCodes.Ldloc, argsLocal);
        il.Emit(OpCodes.Callvirt, runtime.TSFunctionInvoke);
        il.Emit(OpCodes.Ret);

        // Delegate case: call del.DynamicInvoke(args)
        il.MarkLabel(delegateLabel);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Castclass, _types.Delegate);
        il.Emit(OpCodes.Ldloc, argsLocal);
        il.Emit(OpCodes.Callvirt, _types.GetMethod(_types.Delegate, "DynamicInvoke", _types.ObjectArray));
        il.Emit(OpCodes.Ret);

        // Reflection case: tag.GetType().GetMethod("Invoke")?.Invoke(tag, [args])
        il.MarkLabel(reflectionLabel);
        var invokeMethodLocal = il.DeclareLocal(_types.MethodInfo);

        // var invokeMethod = tag.GetType().GetMethod("Invoke", new Type[] { typeof(object[]) })
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Callvirt, _types.GetMethodNoParams(_types.Object, "GetType"));
        il.Emit(OpCodes.Ldstr, "Invoke");
        // Create Type[] { typeof(object[]) }
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Newarr, _types.Type);
        il.Emit(OpCodes.Dup);
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Ldtoken, _types.ObjectArray);
        il.Emit(OpCodes.Call, _types.TypeGetTypeFromHandle);
        il.Emit(OpCodes.Stelem_Ref);
        // Call GetMethod(string, Type[])
        var getMethodWithTypes = typeof(Type).GetMethod("GetMethod", [typeof(string), typeof(Type[])])!;
        il.Emit(OpCodes.Callvirt, getMethodWithTypes);
        il.Emit(OpCodes.Stloc, invokeMethodLocal);

        // if (invokeMethod != null)
        il.Emit(OpCodes.Ldloc, invokeMethodLocal);
        il.Emit(OpCodes.Brfalse, errorLabel);

        // return invokeMethod.Invoke(tag, new object[] { args })
        il.Emit(OpCodes.Ldloc, invokeMethodLocal);
        il.Emit(OpCodes.Ldarg_0);
        // Create new object[] { args }
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Newarr, _types.Object);
        il.Emit(OpCodes.Dup);
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Ldloc, argsLocal);
        il.Emit(OpCodes.Stelem_Ref);
        il.Emit(OpCodes.Callvirt, _types.GetMethod(_types.MethodInfo, "Invoke", _types.Object, _types.ObjectArray));
        il.Emit(OpCodes.Ret);
    }

    private void EmitObjectRest(TypeBuilder typeBuilder, EmittedRuntime runtime)
    {
        // Accept object instead of Dictionary to support both object literals and class instances
        var method = typeBuilder.DefineMethod(
            "ObjectRest",
            MethodAttributes.Public | MethodAttributes.Static,
            _types.DictionaryStringObject,
            [_types.Object, _types.ListOfObject]
        );
        runtime.ObjectRest = method;

        var il = method.GetILGenerator();

        var dictLabel = il.DefineLabel();
        var emptyLabel = il.DefineLabel();
        var processLabel = il.DefineLabel();

        // Locals for class instance path
        var fieldInfoLocal = il.DeclareLocal(_types.FieldInfo);
        var fieldsLocal = il.DeclareLocal(_types.Object);
        var sourceDictLocal = il.DeclareLocal(_types.DictionaryStringObject);

        // Check if arg0 is Dictionary<string, object>
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Isinst, _types.DictionaryStringObject);
        il.Emit(OpCodes.Brtrue, dictLabel);

        // Check if obj is not null (for class instance path)
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Brfalse, emptyLabel);

        // Class instance path: get _fields via reflection
        // var fieldInfo = obj.GetType().GetField("_fields", BindingFlags.NonPublic | BindingFlags.Instance);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Callvirt, _types.GetMethodNoParams(_types.Object, "GetType"));
        il.Emit(OpCodes.Ldstr, "_fields");
        il.Emit(OpCodes.Ldc_I4, (int)(BindingFlags.NonPublic | BindingFlags.Instance));
        il.Emit(OpCodes.Callvirt, _types.GetMethod(_types.Type, "GetField", _types.String, _types.BindingFlags));
        il.Emit(OpCodes.Stloc, fieldInfoLocal);

        // if (fieldInfo == null) goto empty
        il.Emit(OpCodes.Ldloc, fieldInfoLocal);
        il.Emit(OpCodes.Brfalse, emptyLabel);

        // var fields = fieldInfo.GetValue(obj);
        il.Emit(OpCodes.Ldloc, fieldInfoLocal);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Callvirt, _types.GetMethod(_types.FieldInfo, "GetValue", _types.Object));
        il.Emit(OpCodes.Stloc, fieldsLocal);

        // if (fields == null) goto empty
        il.Emit(OpCodes.Ldloc, fieldsLocal);
        il.Emit(OpCodes.Brfalse, emptyLabel);

        // var sourceDict = fields as Dictionary<string, object>;
        il.Emit(OpCodes.Ldloc, fieldsLocal);
        il.Emit(OpCodes.Isinst, _types.DictionaryStringObject);
        il.Emit(OpCodes.Stloc, sourceDictLocal);

        // if (sourceDict == null) goto empty
        il.Emit(OpCodes.Ldloc, sourceDictLocal);
        il.Emit(OpCodes.Brfalse, emptyLabel);

        // sourceDict now has the dictionary from class instance
        il.Emit(OpCodes.Br, processLabel);

        // Dictionary path: cast arg0 directly
        il.MarkLabel(dictLabel);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Castclass, _types.DictionaryStringObject);
        il.Emit(OpCodes.Stloc, sourceDictLocal);
        il.Emit(OpCodes.Br, processLabel);

        // Empty result fallback
        il.MarkLabel(emptyLabel);
        il.Emit(OpCodes.Newobj, _types.GetDefaultConstructor(_types.DictionaryStringObject));
        il.Emit(OpCodes.Ret);

        // Process the source dictionary (now in sourceDictLocal)
        il.MarkLabel(processLabel);

        // Create result dictionary
        var resultLocal = il.DeclareLocal(_types.DictionaryStringObject);
        il.Emit(OpCodes.Newobj, _types.GetDefaultConstructor(_types.DictionaryStringObject));
        il.Emit(OpCodes.Stloc, resultLocal);

        // Create HashSet<string> from excludeKeys
        var excludeSetLocal = il.DeclareLocal(_types.HashSetOfString);
        il.Emit(OpCodes.Newobj, _types.GetDefaultConstructor(_types.HashSetOfString));
        il.Emit(OpCodes.Stloc, excludeSetLocal);

        // Add each exclude key to the set
        var excludeIndexLocal = il.DeclareLocal(_types.Int32);
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Stloc, excludeIndexLocal);

        var excludeLoopStart = il.DefineLabel();
        var excludeLoopEnd = il.DefineLabel();

        il.MarkLabel(excludeLoopStart);
        il.Emit(OpCodes.Ldloc, excludeIndexLocal);
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Callvirt, _types.GetProperty(_types.ListOfObject, "Count").GetGetMethod()!);
        il.Emit(OpCodes.Bge, excludeLoopEnd);

        // Get excludeKeys[i] and add to set if not null
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Ldloc, excludeIndexLocal);
        il.Emit(OpCodes.Callvirt, _types.GetProperty(_types.ListOfObject, "Item").GetGetMethod()!);
        var keyLocal = il.DeclareLocal(_types.Object);
        il.Emit(OpCodes.Stloc, keyLocal);

        var skipAdd = il.DefineLabel();
        il.Emit(OpCodes.Ldloc, keyLocal);
        il.Emit(OpCodes.Brfalse, skipAdd);

        il.Emit(OpCodes.Ldloc, excludeSetLocal);
        il.Emit(OpCodes.Ldloc, keyLocal);
        il.Emit(OpCodes.Callvirt, _types.GetMethodNoParams(_types.Object, "ToString"));
        il.Emit(OpCodes.Callvirt, _types.GetMethod(_types.HashSetOfString, "Add", _types.String));
        il.Emit(OpCodes.Pop); // discard bool return

        il.MarkLabel(skipAdd);
        il.Emit(OpCodes.Ldloc, excludeIndexLocal);
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Add);
        il.Emit(OpCodes.Stloc, excludeIndexLocal);
        il.Emit(OpCodes.Br, excludeLoopStart);

        il.MarkLabel(excludeLoopEnd);

        // Iterate over source dictionary keys using sourceDictLocal
        // We need the KeyCollection.Enumerator
        var keyCollectionType = typeof(Dictionary<string, object>.KeyCollection);
        var keysEnumType = typeof(Dictionary<string, object>.KeyCollection.Enumerator);

        var keysEnumLocal = il.DeclareLocal(keysEnumType);
        il.Emit(OpCodes.Ldloc, sourceDictLocal);
        il.Emit(OpCodes.Callvirt, _types.GetProperty(_types.DictionaryStringObject, "Keys")!.GetGetMethod()!);
        il.Emit(OpCodes.Callvirt, _types.GetMethodNoParams(keyCollectionType, "GetEnumerator"));
        il.Emit(OpCodes.Stloc, keysEnumLocal);

        var dictLoopStart = il.DefineLabel();
        var dictLoopEnd = il.DefineLabel();

        il.MarkLabel(dictLoopStart);
        // MoveNext
        il.Emit(OpCodes.Ldloca, keysEnumLocal);
        il.Emit(OpCodes.Call, _types.GetMethodNoParams(keysEnumType, "MoveNext"));
        il.Emit(OpCodes.Brfalse, dictLoopEnd);

        // Get Current key
        il.Emit(OpCodes.Ldloca, keysEnumLocal);
        il.Emit(OpCodes.Call, _types.GetProperty(keysEnumType, "Current")!.GetGetMethod()!);
        var currentKeyLocal = il.DeclareLocal(_types.String);
        il.Emit(OpCodes.Stloc, currentKeyLocal);

        // Check if key is in excludeSet
        il.Emit(OpCodes.Ldloc, excludeSetLocal);
        il.Emit(OpCodes.Ldloc, currentKeyLocal);
        il.Emit(OpCodes.Callvirt, _types.GetMethod(_types.HashSetOfString, "Contains", _types.String));
        var skipKey = il.DefineLabel();
        il.Emit(OpCodes.Brtrue, skipKey);

        // Add to result: result[key] = sourceDict[key]
        il.Emit(OpCodes.Ldloc, resultLocal);
        il.Emit(OpCodes.Ldloc, currentKeyLocal);
        il.Emit(OpCodes.Ldloc, sourceDictLocal);
        il.Emit(OpCodes.Ldloc, currentKeyLocal);
        il.Emit(OpCodes.Callvirt, _types.GetProperty(_types.DictionaryStringObject, "Item")!.GetGetMethod()!);
        il.Emit(OpCodes.Callvirt, _types.GetProperty(_types.DictionaryStringObject, "Item")!.GetSetMethod()!);

        il.MarkLabel(skipKey);
        il.Emit(OpCodes.Br, dictLoopStart);

        il.MarkLabel(dictLoopEnd);

        // Dispose enumerator
        il.Emit(OpCodes.Ldloca, keysEnumLocal);
        il.Emit(OpCodes.Constrained, keysEnumType);
        il.Emit(OpCodes.Callvirt, _types.GetMethodNoParams(_types.IDisposable, "Dispose"));

        il.Emit(OpCodes.Ldloc, resultLocal);
        il.Emit(OpCodes.Ret);
    }

    private void EmitGetValues(TypeBuilder typeBuilder, EmittedRuntime runtime)
    {
        var method = typeBuilder.DefineMethod(
            "GetValues",
            MethodAttributes.Public | MethodAttributes.Static,
            _types.ListOfObject,
            [_types.Object]
        );
        runtime.GetValues = method;

        var il = method.GetILGenerator();
        var dictType = _types.DictionaryStringObject;
        var listType = _types.ListOfObject;
        var kvpType = _types.KeyValuePairStringObject;
        var enumeratorType = _types.MakeGenericType(typeof(Dictionary<,>.Enumerator).GetGenericTypeDefinition(), _types.String, _types.Object);

        var resultLocal = il.DeclareLocal(listType);
        var dictLocal = il.DeclareLocal(dictType);
        var typeLocal = il.DeclareLocal(_types.Type);
        var fieldsLocal = il.DeclareLocal(_types.FieldInfoArray);
        var indexLocal = il.DeclareLocal(_types.Int32);
        var fieldLocal = il.DeclareLocal(_types.FieldInfo);
        var fieldNameLocal = il.DeclareLocal(_types.String);
        var fieldsFieldLocal = il.DeclareLocal(_types.FieldInfo);
        var fieldsDictLocal = il.DeclareLocal(dictType);
        var seenKeysLocal = il.DeclareLocal(_types.HashSetOfString);
        var enumeratorLocal = il.DeclareLocal(enumeratorType);
        var currentLocal = il.DeclareLocal(kvpType);

        var reflectionLabel = il.DefineLabel();
        var returnEmptyLabel = il.DefineLabel();
        var fieldLoopStartLabel = il.DefineLabel();
        var fieldLoopEndLabel = il.DefineLabel();
        var skipFieldLabel = il.DefineLabel();
        var returnResultLabel = il.DefineLabel();
        var fieldsLoopStart = il.DefineLabel();
        var fieldsLoopEnd = il.DefineLabel();
        var skipDuplicateLabel = il.DefineLabel();

        // if (obj is Dictionary<string, object?> dict)
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Isinst, dictType);
        il.Emit(OpCodes.Stloc, dictLocal);
        il.Emit(OpCodes.Ldloc, dictLocal);
        il.Emit(OpCodes.Brfalse, reflectionLabel);

        // Create result list and add all values from dictionary
        il.Emit(OpCodes.Newobj, listType.GetConstructor(Type.EmptyTypes)!);
        il.Emit(OpCodes.Stloc, resultLocal);

        il.Emit(OpCodes.Ldloc, dictLocal);
        il.Emit(OpCodes.Callvirt, dictType.GetMethod("GetEnumerator")!);
        il.Emit(OpCodes.Stloc, enumeratorLocal);

        var dictLoopStart = il.DefineLabel();
        var dictLoopEnd = il.DefineLabel();
        il.MarkLabel(dictLoopStart);
        il.Emit(OpCodes.Ldloca, enumeratorLocal);
        il.Emit(OpCodes.Call, enumeratorType.GetMethod("MoveNext")!);
        il.Emit(OpCodes.Brfalse, dictLoopEnd);

        il.Emit(OpCodes.Ldloc, resultLocal);
        il.Emit(OpCodes.Ldloca, enumeratorLocal);
        il.Emit(OpCodes.Call, enumeratorType.GetProperty("Current")!.GetGetMethod()!);
        il.Emit(OpCodes.Stloc, currentLocal);
        il.Emit(OpCodes.Ldloca, currentLocal);
        il.Emit(OpCodes.Call, kvpType.GetProperty("Value")!.GetGetMethod()!);
        il.Emit(OpCodes.Callvirt, listType.GetMethod("Add")!);
        il.Emit(OpCodes.Br, dictLoopStart);

        il.MarkLabel(dictLoopEnd);
        il.Emit(OpCodes.Ldloca, enumeratorLocal);
        il.Emit(OpCodes.Call, enumeratorType.GetMethod("Dispose")!);
        il.Emit(OpCodes.Ldloc, resultLocal);
        il.Emit(OpCodes.Ret);

        // Reflection for class instances
        il.MarkLabel(reflectionLabel);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Brfalse, returnEmptyLabel);

        // var result = new List<object?>(); var seenKeys = new HashSet<string>();
        il.Emit(OpCodes.Newobj, listType.GetConstructor(Type.EmptyTypes)!);
        il.Emit(OpCodes.Stloc, resultLocal);
        il.Emit(OpCodes.Newobj, _types.HashSetOfString.GetConstructor(Type.EmptyTypes)!);
        il.Emit(OpCodes.Stloc, seenKeysLocal);

        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Callvirt, _types.Object.GetMethod("GetType")!);
        il.Emit(OpCodes.Stloc, typeLocal);

        // var fields = type.GetFields(BindingFlags.NonPublic | BindingFlags.Instance);
        il.Emit(OpCodes.Ldloc, typeLocal);
        il.Emit(OpCodes.Ldc_I4, (int)(System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance));
        il.Emit(OpCodes.Callvirt, _types.Type.GetMethod("GetFields", [_types.BindingFlags])!);
        il.Emit(OpCodes.Stloc, fieldsLocal);

        // Iterate backing fields
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Stloc, indexLocal);

        il.MarkLabel(fieldLoopStartLabel);
        il.Emit(OpCodes.Ldloc, indexLocal);
        il.Emit(OpCodes.Ldloc, fieldsLocal);
        il.Emit(OpCodes.Ldlen);
        il.Emit(OpCodes.Conv_I4);
        il.Emit(OpCodes.Bge, fieldLoopEndLabel);

        il.Emit(OpCodes.Ldloc, fieldsLocal);
        il.Emit(OpCodes.Ldloc, indexLocal);
        il.Emit(OpCodes.Ldelem_Ref);
        il.Emit(OpCodes.Stloc, fieldLocal);

        il.Emit(OpCodes.Ldloc, fieldLocal);
        il.Emit(OpCodes.Callvirt, _types.FieldInfo.GetProperty("Name")!.GetGetMethod()!);
        il.Emit(OpCodes.Stloc, fieldNameLocal);

        // if (field.Name.StartsWith("__"))
        il.Emit(OpCodes.Ldloc, fieldNameLocal);
        il.Emit(OpCodes.Ldstr, "__");
        il.Emit(OpCodes.Callvirt, _types.String.GetMethod("StartsWith", [_types.String])!);
        il.Emit(OpCodes.Brfalse, skipFieldLabel);

        // seenKeys.Add(field.Name.Substring(2));
        il.Emit(OpCodes.Ldloc, seenKeysLocal);
        il.Emit(OpCodes.Ldloc, fieldNameLocal);
        il.Emit(OpCodes.Ldc_I4_2);
        il.Emit(OpCodes.Callvirt, _types.String.GetMethod("Substring", [_types.Int32])!);
        il.Emit(OpCodes.Callvirt, _types.HashSetOfString.GetMethod("Add")!);
        il.Emit(OpCodes.Pop);

        // values.Add(field.GetValue(obj));
        il.Emit(OpCodes.Ldloc, resultLocal);
        il.Emit(OpCodes.Ldloc, fieldLocal);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Callvirt, _types.FieldInfo.GetMethod("GetValue", [_types.Object])!);
        il.Emit(OpCodes.Callvirt, listType.GetMethod("Add")!);

        il.MarkLabel(skipFieldLabel);
        il.Emit(OpCodes.Ldloc, indexLocal);
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Add);
        il.Emit(OpCodes.Stloc, indexLocal);
        il.Emit(OpCodes.Br, fieldLoopStartLabel);

        il.MarkLabel(fieldLoopEndLabel);

        // Get _fields dictionary
        il.Emit(OpCodes.Ldloc, typeLocal);
        il.Emit(OpCodes.Ldstr, "_fields");
        il.Emit(OpCodes.Ldc_I4, (int)(System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance));
        il.Emit(OpCodes.Callvirt, _types.Type.GetMethod("GetField", [_types.String, _types.BindingFlags])!);
        il.Emit(OpCodes.Stloc, fieldsFieldLocal);

        il.Emit(OpCodes.Ldloc, fieldsFieldLocal);
        il.Emit(OpCodes.Brfalse, returnResultLabel);

        il.Emit(OpCodes.Ldloc, fieldsFieldLocal);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Callvirt, _types.FieldInfo.GetMethod("GetValue", [_types.Object])!);
        il.Emit(OpCodes.Isinst, dictType);
        il.Emit(OpCodes.Stloc, fieldsDictLocal);
        il.Emit(OpCodes.Ldloc, fieldsDictLocal);
        il.Emit(OpCodes.Brfalse, returnResultLabel);

        // Iterate _fields and add values for keys not in seenKeys
        il.Emit(OpCodes.Ldloc, fieldsDictLocal);
        il.Emit(OpCodes.Callvirt, dictType.GetMethod("GetEnumerator")!);
        var fieldsDictEnumeratorLocal = il.DeclareLocal(enumeratorType);
        il.Emit(OpCodes.Stloc, fieldsDictEnumeratorLocal);

        il.MarkLabel(fieldsLoopStart);
        il.Emit(OpCodes.Ldloca, fieldsDictEnumeratorLocal);
        il.Emit(OpCodes.Call, enumeratorType.GetMethod("MoveNext")!);
        il.Emit(OpCodes.Brfalse, fieldsLoopEnd);

        il.Emit(OpCodes.Ldloca, fieldsDictEnumeratorLocal);
        il.Emit(OpCodes.Call, enumeratorType.GetProperty("Current")!.GetGetMethod()!);
        il.Emit(OpCodes.Stloc, currentLocal);

        // if (!seenKeys.Contains(kvp.Key))
        il.Emit(OpCodes.Ldloc, seenKeysLocal);
        il.Emit(OpCodes.Ldloca, currentLocal);
        il.Emit(OpCodes.Call, kvpType.GetProperty("Key")!.GetGetMethod()!);
        il.Emit(OpCodes.Callvirt, _types.HashSetOfString.GetMethod("Contains")!);
        il.Emit(OpCodes.Brtrue, skipDuplicateLabel);

        il.Emit(OpCodes.Ldloc, resultLocal);
        il.Emit(OpCodes.Ldloca, currentLocal);
        il.Emit(OpCodes.Call, kvpType.GetProperty("Value")!.GetGetMethod()!);
        il.Emit(OpCodes.Callvirt, listType.GetMethod("Add")!);

        il.MarkLabel(skipDuplicateLabel);
        il.Emit(OpCodes.Br, fieldsLoopStart);

        il.MarkLabel(fieldsLoopEnd);
        il.Emit(OpCodes.Ldloca, fieldsDictEnumeratorLocal);
        il.Emit(OpCodes.Call, enumeratorType.GetMethod("Dispose")!);

        il.MarkLabel(returnResultLabel);
        il.Emit(OpCodes.Ldloc, resultLocal);
        il.Emit(OpCodes.Ret);

        il.MarkLabel(returnEmptyLabel);
        il.Emit(OpCodes.Newobj, listType.GetConstructor(Type.EmptyTypes)!);
        il.Emit(OpCodes.Ret);
    }

    private void EmitGetEntries(TypeBuilder typeBuilder, EmittedRuntime runtime)
    {
        var method = typeBuilder.DefineMethod(
            "GetEntries",
            MethodAttributes.Public | MethodAttributes.Static,
            _types.ListOfObject,
            [_types.Object]
        );
        runtime.GetEntries = method;

        var il = method.GetILGenerator();
        var dictType = _types.DictionaryStringObject;
        var listType = _types.ListOfObject;
        var kvpType = _types.KeyValuePairStringObject;
        var enumeratorType = _types.MakeGenericType(typeof(Dictionary<,>.Enumerator).GetGenericTypeDefinition(), _types.String, _types.Object);

        var resultLocal = il.DeclareLocal(listType);
        var dictLocal = il.DeclareLocal(dictType);
        var typeLocal = il.DeclareLocal(_types.Type);
        var fieldsLocal = il.DeclareLocal(_types.FieldInfoArray);
        var indexLocal = il.DeclareLocal(_types.Int32);
        var fieldLocal = il.DeclareLocal(_types.FieldInfo);
        var fieldNameLocal = il.DeclareLocal(_types.String);
        var fieldsFieldLocal = il.DeclareLocal(_types.FieldInfo);
        var fieldsDictLocal = il.DeclareLocal(dictType);
        var seenKeysLocal = il.DeclareLocal(_types.HashSetOfString);
        var enumeratorLocal = il.DeclareLocal(enumeratorType);
        var currentLocal = il.DeclareLocal(kvpType);
        var entryLocal = il.DeclareLocal(listType);
        var propNameLocal = il.DeclareLocal(_types.String);

        var reflectionLabel = il.DefineLabel();
        var returnEmptyLabel = il.DefineLabel();
        var fieldLoopStartLabel = il.DefineLabel();
        var fieldLoopEndLabel = il.DefineLabel();
        var skipFieldLabel = il.DefineLabel();
        var returnResultLabel = il.DefineLabel();
        var fieldsLoopStart = il.DefineLabel();
        var fieldsLoopEnd = il.DefineLabel();
        var skipDuplicateLabel = il.DefineLabel();

        // if (obj is Dictionary<string, object?> dict)
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Isinst, dictType);
        il.Emit(OpCodes.Stloc, dictLocal);
        il.Emit(OpCodes.Ldloc, dictLocal);
        il.Emit(OpCodes.Brfalse, reflectionLabel);

        // Create result list and add [key, value] entries
        il.Emit(OpCodes.Newobj, listType.GetConstructor(Type.EmptyTypes)!);
        il.Emit(OpCodes.Stloc, resultLocal);

        il.Emit(OpCodes.Ldloc, dictLocal);
        il.Emit(OpCodes.Callvirt, dictType.GetMethod("GetEnumerator")!);
        il.Emit(OpCodes.Stloc, enumeratorLocal);

        var dictLoopStart = il.DefineLabel();
        var dictLoopEnd = il.DefineLabel();
        il.MarkLabel(dictLoopStart);
        il.Emit(OpCodes.Ldloca, enumeratorLocal);
        il.Emit(OpCodes.Call, enumeratorType.GetMethod("MoveNext")!);
        il.Emit(OpCodes.Brfalse, dictLoopEnd);

        il.Emit(OpCodes.Ldloca, enumeratorLocal);
        il.Emit(OpCodes.Call, enumeratorType.GetProperty("Current")!.GetGetMethod()!);
        il.Emit(OpCodes.Stloc, currentLocal);

        // var entry = new List<object?> { kvp.Key, kvp.Value };
        il.Emit(OpCodes.Newobj, listType.GetConstructor(Type.EmptyTypes)!);
        il.Emit(OpCodes.Stloc, entryLocal);
        il.Emit(OpCodes.Ldloc, entryLocal);
        il.Emit(OpCodes.Ldloca, currentLocal);
        il.Emit(OpCodes.Call, kvpType.GetProperty("Key")!.GetGetMethod()!);
        il.Emit(OpCodes.Callvirt, listType.GetMethod("Add")!);
        il.Emit(OpCodes.Ldloc, entryLocal);
        il.Emit(OpCodes.Ldloca, currentLocal);
        il.Emit(OpCodes.Call, kvpType.GetProperty("Value")!.GetGetMethod()!);
        il.Emit(OpCodes.Callvirt, listType.GetMethod("Add")!);

        il.Emit(OpCodes.Ldloc, resultLocal);
        il.Emit(OpCodes.Ldloc, entryLocal);
        il.Emit(OpCodes.Callvirt, listType.GetMethod("Add")!);
        il.Emit(OpCodes.Br, dictLoopStart);

        il.MarkLabel(dictLoopEnd);
        il.Emit(OpCodes.Ldloca, enumeratorLocal);
        il.Emit(OpCodes.Call, enumeratorType.GetMethod("Dispose")!);
        il.Emit(OpCodes.Ldloc, resultLocal);
        il.Emit(OpCodes.Ret);

        // Reflection for class instances
        il.MarkLabel(reflectionLabel);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Brfalse, returnEmptyLabel);

        il.Emit(OpCodes.Newobj, listType.GetConstructor(Type.EmptyTypes)!);
        il.Emit(OpCodes.Stloc, resultLocal);
        il.Emit(OpCodes.Newobj, _types.HashSetOfString.GetConstructor(Type.EmptyTypes)!);
        il.Emit(OpCodes.Stloc, seenKeysLocal);

        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Callvirt, _types.Object.GetMethod("GetType")!);
        il.Emit(OpCodes.Stloc, typeLocal);

        il.Emit(OpCodes.Ldloc, typeLocal);
        il.Emit(OpCodes.Ldc_I4, (int)(System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance));
        il.Emit(OpCodes.Callvirt, _types.Type.GetMethod("GetFields", [_types.BindingFlags])!);
        il.Emit(OpCodes.Stloc, fieldsLocal);

        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Stloc, indexLocal);

        il.MarkLabel(fieldLoopStartLabel);
        il.Emit(OpCodes.Ldloc, indexLocal);
        il.Emit(OpCodes.Ldloc, fieldsLocal);
        il.Emit(OpCodes.Ldlen);
        il.Emit(OpCodes.Conv_I4);
        il.Emit(OpCodes.Bge, fieldLoopEndLabel);

        il.Emit(OpCodes.Ldloc, fieldsLocal);
        il.Emit(OpCodes.Ldloc, indexLocal);
        il.Emit(OpCodes.Ldelem_Ref);
        il.Emit(OpCodes.Stloc, fieldLocal);

        il.Emit(OpCodes.Ldloc, fieldLocal);
        il.Emit(OpCodes.Callvirt, _types.FieldInfo.GetProperty("Name")!.GetGetMethod()!);
        il.Emit(OpCodes.Stloc, fieldNameLocal);

        il.Emit(OpCodes.Ldloc, fieldNameLocal);
        il.Emit(OpCodes.Ldstr, "__");
        il.Emit(OpCodes.Callvirt, _types.String.GetMethod("StartsWith", [_types.String])!);
        il.Emit(OpCodes.Brfalse, skipFieldLabel);

        // propName = field.Name.Substring(2)
        il.Emit(OpCodes.Ldloc, fieldNameLocal);
        il.Emit(OpCodes.Ldc_I4_2);
        il.Emit(OpCodes.Callvirt, _types.String.GetMethod("Substring", [_types.Int32])!);
        il.Emit(OpCodes.Stloc, propNameLocal);

        il.Emit(OpCodes.Ldloc, seenKeysLocal);
        il.Emit(OpCodes.Ldloc, propNameLocal);
        il.Emit(OpCodes.Callvirt, _types.HashSetOfString.GetMethod("Add")!);
        il.Emit(OpCodes.Pop);

        // var entry = new List<object?> { propName, field.GetValue(obj) };
        il.Emit(OpCodes.Newobj, listType.GetConstructor(Type.EmptyTypes)!);
        il.Emit(OpCodes.Stloc, entryLocal);
        il.Emit(OpCodes.Ldloc, entryLocal);
        il.Emit(OpCodes.Ldloc, propNameLocal);
        il.Emit(OpCodes.Callvirt, listType.GetMethod("Add")!);
        il.Emit(OpCodes.Ldloc, entryLocal);
        il.Emit(OpCodes.Ldloc, fieldLocal);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Callvirt, _types.FieldInfo.GetMethod("GetValue", [_types.Object])!);
        il.Emit(OpCodes.Callvirt, listType.GetMethod("Add")!);

        il.Emit(OpCodes.Ldloc, resultLocal);
        il.Emit(OpCodes.Ldloc, entryLocal);
        il.Emit(OpCodes.Callvirt, listType.GetMethod("Add")!);

        il.MarkLabel(skipFieldLabel);
        il.Emit(OpCodes.Ldloc, indexLocal);
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Add);
        il.Emit(OpCodes.Stloc, indexLocal);
        il.Emit(OpCodes.Br, fieldLoopStartLabel);

        il.MarkLabel(fieldLoopEndLabel);

        // Get _fields dictionary
        il.Emit(OpCodes.Ldloc, typeLocal);
        il.Emit(OpCodes.Ldstr, "_fields");
        il.Emit(OpCodes.Ldc_I4, (int)(System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance));
        il.Emit(OpCodes.Callvirt, _types.Type.GetMethod("GetField", [_types.String, _types.BindingFlags])!);
        il.Emit(OpCodes.Stloc, fieldsFieldLocal);

        il.Emit(OpCodes.Ldloc, fieldsFieldLocal);
        il.Emit(OpCodes.Brfalse, returnResultLabel);

        il.Emit(OpCodes.Ldloc, fieldsFieldLocal);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Callvirt, _types.FieldInfo.GetMethod("GetValue", [_types.Object])!);
        il.Emit(OpCodes.Isinst, dictType);
        il.Emit(OpCodes.Stloc, fieldsDictLocal);
        il.Emit(OpCodes.Ldloc, fieldsDictLocal);
        il.Emit(OpCodes.Brfalse, returnResultLabel);

        var fieldsDictEnumeratorLocal = il.DeclareLocal(enumeratorType);
        il.Emit(OpCodes.Ldloc, fieldsDictLocal);
        il.Emit(OpCodes.Callvirt, dictType.GetMethod("GetEnumerator")!);
        il.Emit(OpCodes.Stloc, fieldsDictEnumeratorLocal);

        il.MarkLabel(fieldsLoopStart);
        il.Emit(OpCodes.Ldloca, fieldsDictEnumeratorLocal);
        il.Emit(OpCodes.Call, enumeratorType.GetMethod("MoveNext")!);
        il.Emit(OpCodes.Brfalse, fieldsLoopEnd);

        il.Emit(OpCodes.Ldloca, fieldsDictEnumeratorLocal);
        il.Emit(OpCodes.Call, enumeratorType.GetProperty("Current")!.GetGetMethod()!);
        il.Emit(OpCodes.Stloc, currentLocal);

        il.Emit(OpCodes.Ldloc, seenKeysLocal);
        il.Emit(OpCodes.Ldloca, currentLocal);
        il.Emit(OpCodes.Call, kvpType.GetProperty("Key")!.GetGetMethod()!);
        il.Emit(OpCodes.Callvirt, _types.HashSetOfString.GetMethod("Contains")!);
        il.Emit(OpCodes.Brtrue, skipDuplicateLabel);

        il.Emit(OpCodes.Newobj, listType.GetConstructor(Type.EmptyTypes)!);
        il.Emit(OpCodes.Stloc, entryLocal);
        il.Emit(OpCodes.Ldloc, entryLocal);
        il.Emit(OpCodes.Ldloca, currentLocal);
        il.Emit(OpCodes.Call, kvpType.GetProperty("Key")!.GetGetMethod()!);
        il.Emit(OpCodes.Callvirt, listType.GetMethod("Add")!);
        il.Emit(OpCodes.Ldloc, entryLocal);
        il.Emit(OpCodes.Ldloca, currentLocal);
        il.Emit(OpCodes.Call, kvpType.GetProperty("Value")!.GetGetMethod()!);
        il.Emit(OpCodes.Callvirt, listType.GetMethod("Add")!);

        il.Emit(OpCodes.Ldloc, resultLocal);
        il.Emit(OpCodes.Ldloc, entryLocal);
        il.Emit(OpCodes.Callvirt, listType.GetMethod("Add")!);

        il.MarkLabel(skipDuplicateLabel);
        il.Emit(OpCodes.Br, fieldsLoopStart);

        il.MarkLabel(fieldsLoopEnd);
        il.Emit(OpCodes.Ldloca, fieldsDictEnumeratorLocal);
        il.Emit(OpCodes.Call, enumeratorType.GetMethod("Dispose")!);

        il.MarkLabel(returnResultLabel);
        il.Emit(OpCodes.Ldloc, resultLocal);
        il.Emit(OpCodes.Ret);

        il.MarkLabel(returnEmptyLabel);
        il.Emit(OpCodes.Newobj, listType.GetConstructor(Type.EmptyTypes)!);
        il.Emit(OpCodes.Ret);
    }

    private void EmitIsArray(TypeBuilder typeBuilder, EmittedRuntime runtime)
    {
        var method = typeBuilder.DefineMethod(
            "IsArray",
            MethodAttributes.Public | MethodAttributes.Static,
            _types.Boolean,
            [_types.Object]
        );
        runtime.IsArray = method;

        var il = method.GetILGenerator();
        var trueLabel = il.DefineLabel();
        var endLabel = il.DefineLabel();

        // Check if IList<object?> (covers List<object?>, $Array, and any other array-like type)
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Isinst, _types.IListOfObject);
        il.Emit(OpCodes.Brtrue, trueLabel);

        // False
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Br, endLabel);

        // True
        il.MarkLabel(trueLabel);
        il.Emit(OpCodes.Ldc_I4_1);

        il.MarkLabel(endLabel);
        il.Emit(OpCodes.Ret);
    }

    /// <summary>
    /// Emits Object.fromEntries(entries) - converts iterable of [key, value] pairs to object.
    /// Signature: Dictionary&lt;string, object&gt; ObjectFromEntries(object entries, $TSSymbol iteratorSymbol, Type runtimeType)
    /// </summary>
    private void EmitObjectFromEntries(TypeBuilder typeBuilder, EmittedRuntime runtime)
    {
        var method = typeBuilder.DefineMethod(
            "ObjectFromEntries",
            MethodAttributes.Public | MethodAttributes.Static,
            _types.DictionaryStringObject,
            [_types.Object, runtime.TSSymbolType, _types.Type]
        );
        runtime.ObjectFromEntries = method;

        var il = method.GetILGenerator();
        var dictType = _types.DictionaryStringObject;
        var listType = _types.ListOfObject;

        // Locals
        var resultLocal = il.DeclareLocal(dictType);
        var iterableLocal = il.DeclareLocal(listType);
        var indexLocal = il.DeclareLocal(_types.Int32);
        var entryLocal = il.DeclareLocal(_types.Object);
        var entryListLocal = il.DeclareLocal(listType);
        var keyLocal = il.DeclareLocal(_types.String);
        var valueLocal = il.DeclareLocal(_types.Object);

        var loopStartLabel = il.DefineLabel();
        var loopEndLabel = il.DefineLabel();
        var notNullLabel = il.DefineLabel();
        var throwLabel = il.DefineLabel();

        // Check for null input
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Brtrue, notNullLabel);

        // Input is null - throw exception
        il.Emit(OpCodes.Ldstr, "Runtime Error: Object.fromEntries() requires an iterable argument");
        il.Emit(OpCodes.Newobj, _types.GetConstructor(_types.Exception, _types.String));
        il.Emit(OpCodes.Throw);

        il.MarkLabel(notNullLabel);

        // Convert input to list using IterateToList(entries, iteratorSymbol, runtimeType)
        il.Emit(OpCodes.Ldarg_0);  // entries
        il.Emit(OpCodes.Ldarg_1);  // iteratorSymbol
        il.Emit(OpCodes.Ldarg_2);  // runtimeType
        il.Emit(OpCodes.Call, runtime.IterateToList);
        il.Emit(OpCodes.Stloc, iterableLocal);

        // Create result dictionary
        il.Emit(OpCodes.Newobj, _types.GetDefaultConstructor(dictType));
        il.Emit(OpCodes.Stloc, resultLocal);

        // Initialize loop counter
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Stloc, indexLocal);

        // Loop start
        il.MarkLabel(loopStartLabel);

        // Check if index < iterable.Count
        il.Emit(OpCodes.Ldloc, indexLocal);
        il.Emit(OpCodes.Ldloc, iterableLocal);
        il.Emit(OpCodes.Callvirt, _types.GetProperty(listType, "Count")!.GetGetMethod()!);
        il.Emit(OpCodes.Bge, loopEndLabel);

        // Get entry = iterable[index]
        il.Emit(OpCodes.Ldloc, iterableLocal);
        il.Emit(OpCodes.Ldloc, indexLocal);
        il.Emit(OpCodes.Callvirt, _types.GetProperty(listType, "Item")!.GetGetMethod()!);
        il.Emit(OpCodes.Stloc, entryLocal);

        // Cast entry to List<object>
        il.Emit(OpCodes.Ldloc, entryLocal);
        il.Emit(OpCodes.Isinst, listType);
        il.Emit(OpCodes.Stloc, entryListLocal);

        // If not a list, throw
        il.Emit(OpCodes.Ldloc, entryListLocal);
        il.Emit(OpCodes.Brfalse, throwLabel);

        // Check if list has at least 2 elements
        il.Emit(OpCodes.Ldloc, entryListLocal);
        il.Emit(OpCodes.Callvirt, _types.GetProperty(listType, "Count")!.GetGetMethod()!);
        il.Emit(OpCodes.Ldc_I4_2);
        il.Emit(OpCodes.Blt, throwLabel);

        // Get key = entryList[0]?.ToString() ?? ""
        il.Emit(OpCodes.Ldloc, entryListLocal);
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Callvirt, _types.GetProperty(listType, "Item")!.GetGetMethod()!);
        var keyNullLabel = il.DefineLabel();
        var keyDoneLabel = il.DefineLabel();
        il.Emit(OpCodes.Dup);
        il.Emit(OpCodes.Brfalse, keyNullLabel);
        il.Emit(OpCodes.Callvirt, _types.GetMethodNoParams(_types.Object, "ToString"));
        il.Emit(OpCodes.Br, keyDoneLabel);
        il.MarkLabel(keyNullLabel);
        il.Emit(OpCodes.Pop);
        il.Emit(OpCodes.Ldstr, "");
        il.MarkLabel(keyDoneLabel);
        il.Emit(OpCodes.Stloc, keyLocal);

        // Get value = entryList[1]
        il.Emit(OpCodes.Ldloc, entryListLocal);
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Callvirt, _types.GetProperty(listType, "Item")!.GetGetMethod()!);
        il.Emit(OpCodes.Stloc, valueLocal);

        // result[key] = value
        il.Emit(OpCodes.Ldloc, resultLocal);
        il.Emit(OpCodes.Ldloc, keyLocal);
        il.Emit(OpCodes.Ldloc, valueLocal);
        il.Emit(OpCodes.Callvirt, _types.GetMethod(dictType, "set_Item", _types.String, _types.Object));

        // Increment index
        il.Emit(OpCodes.Ldloc, indexLocal);
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Add);
        il.Emit(OpCodes.Stloc, indexLocal);
        il.Emit(OpCodes.Br, loopStartLabel);

        // Throw error for invalid entry
        il.MarkLabel(throwLabel);
        il.Emit(OpCodes.Ldstr, "Runtime Error: Object.fromEntries() requires [key, value] pairs");
        il.Emit(OpCodes.Newobj, _types.GetConstructor(_types.Exception, _types.String));
        il.Emit(OpCodes.Throw);

        // Return result
        il.MarkLabel(loopEndLabel);
        il.Emit(OpCodes.Ldloc, resultLocal);
        il.Emit(OpCodes.Ret);
    }

    /// <summary>
    /// Emits Object.hasOwn(obj, key) - checks if object has own property.
    /// </summary>
    private void EmitObjectHasOwn(TypeBuilder typeBuilder, EmittedRuntime runtime)
    {
        var method = typeBuilder.DefineMethod(
            "ObjectHasOwn",
            MethodAttributes.Public | MethodAttributes.Static,
            _types.Boolean,
            [_types.Object, _types.Object]
        );
        runtime.ObjectHasOwn = method;

        var il = method.GetILGenerator();
        var dictType = _types.DictionaryStringObject;

        var checkClassLabel = il.DefineLabel();
        var returnFalseLabel = il.DefineLabel();
        var endLabel = il.DefineLabel();
        var keyStringLocal = il.DeclareLocal(_types.String);
        var keyPascalLocal = il.DeclareLocal(_types.String);

        // Convert key to string: key?.ToString() ?? ""
        il.Emit(OpCodes.Ldarg_1);
        var keyNullLabel = il.DefineLabel();
        var keyDoneLabel = il.DefineLabel();
        il.Emit(OpCodes.Dup);
        il.Emit(OpCodes.Brfalse, keyNullLabel);
        il.Emit(OpCodes.Callvirt, _types.GetMethodNoParams(_types.Object, "ToString"));
        il.Emit(OpCodes.Br, keyDoneLabel);
        il.MarkLabel(keyNullLabel);
        il.Emit(OpCodes.Pop);
        il.Emit(OpCodes.Ldstr, "");
        il.MarkLabel(keyDoneLabel);
        il.Emit(OpCodes.Stloc, keyStringLocal);

        // Convert key to PascalCase for backing field lookup
        il.Emit(OpCodes.Ldloc, keyStringLocal);
        il.Emit(OpCodes.Call, runtime.ToPascalCase);
        il.Emit(OpCodes.Stloc, keyPascalLocal);

        // Check if obj is null
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Brfalse, returnFalseLabel);

        // Check if obj is Dictionary<string, object>
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Isinst, dictType);
        il.Emit(OpCodes.Brfalse, checkClassLabel);

        // It's a dictionary - call ContainsKey
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Castclass, dictType);
        il.Emit(OpCodes.Ldloc, keyStringLocal);
        il.Emit(OpCodes.Callvirt, _types.GetMethod(dictType, "ContainsKey", _types.String));
        il.Emit(OpCodes.Br, endLabel);

        // Check class instance
        il.MarkLabel(checkClassLabel);

        // Use reflection to find field named "__<PascalKey>" or check _fields dictionary
        var typeLocal = il.DeclareLocal(_types.Type);
        var fieldsLocal = il.DeclareLocal(_types.FieldInfoArray);
        var indexLocal = il.DeclareLocal(_types.Int32);
        var fieldLocal = il.DeclareLocal(_types.FieldInfo);
        var fieldNameLocal = il.DeclareLocal(_types.String);
        var expectedNameLocal = il.DeclareLocal(_types.String);

        // expectedName = "__" + keyPascal (PascalCase)
        il.Emit(OpCodes.Ldstr, "__");
        il.Emit(OpCodes.Ldloc, keyPascalLocal);
        il.Emit(OpCodes.Call, _types.GetMethod(_types.String, "Concat", _types.String, _types.String));
        il.Emit(OpCodes.Stloc, expectedNameLocal);

        // type = obj.GetType()
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Callvirt, _types.GetMethodNoParams(_types.Object, "GetType"));
        il.Emit(OpCodes.Stloc, typeLocal);

        // fields = type.GetFields(BindingFlags.NonPublic | BindingFlags.Instance)
        il.Emit(OpCodes.Ldloc, typeLocal);
        il.Emit(OpCodes.Ldc_I4, (int)(BindingFlags.NonPublic | BindingFlags.Instance));
        il.Emit(OpCodes.Callvirt, _types.Type.GetMethod("GetFields", [_types.BindingFlags])!);
        il.Emit(OpCodes.Stloc, fieldsLocal);

        // Loop through fields to find matching __<PascalKey> field
        var fieldLoopStart = il.DefineLabel();
        var fieldLoopEnd = il.DefineLabel();
        var nextField = il.DefineLabel();

        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Stloc, indexLocal);

        il.MarkLabel(fieldLoopStart);
        il.Emit(OpCodes.Ldloc, indexLocal);
        il.Emit(OpCodes.Ldloc, fieldsLocal);
        il.Emit(OpCodes.Ldlen);
        il.Emit(OpCodes.Conv_I4);
        il.Emit(OpCodes.Bge, fieldLoopEnd);

        il.Emit(OpCodes.Ldloc, fieldsLocal);
        il.Emit(OpCodes.Ldloc, indexLocal);
        il.Emit(OpCodes.Ldelem_Ref);
        il.Emit(OpCodes.Stloc, fieldLocal);

        il.Emit(OpCodes.Ldloc, fieldLocal);
        il.Emit(OpCodes.Callvirt, _types.FieldInfo.GetProperty("Name")!.GetGetMethod()!);
        il.Emit(OpCodes.Stloc, fieldNameLocal);

        // if (fieldName == expectedName) return true
        il.Emit(OpCodes.Ldloc, fieldNameLocal);
        il.Emit(OpCodes.Ldloc, expectedNameLocal);
        il.Emit(OpCodes.Call, _types.String.GetMethod("op_Equality", [_types.String, _types.String])!);
        var foundField = il.DefineLabel();
        il.Emit(OpCodes.Brtrue, foundField);

        il.MarkLabel(nextField);
        il.Emit(OpCodes.Ldloc, indexLocal);
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Add);
        il.Emit(OpCodes.Stloc, indexLocal);
        il.Emit(OpCodes.Br, fieldLoopStart);

        // Found matching field - return true
        il.MarkLabel(foundField);
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Br, endLabel);

        il.MarkLabel(fieldLoopEnd);

        // Check _fields dictionary
        var fieldsFieldLocal = il.DeclareLocal(_types.FieldInfo);
        var fieldsDictLocal = il.DeclareLocal(dictType);

        il.Emit(OpCodes.Ldloc, typeLocal);
        il.Emit(OpCodes.Ldstr, "_fields");
        il.Emit(OpCodes.Ldc_I4, (int)(BindingFlags.NonPublic | BindingFlags.Instance));
        il.Emit(OpCodes.Callvirt, _types.Type.GetMethod("GetField", [_types.String, _types.BindingFlags])!);
        il.Emit(OpCodes.Stloc, fieldsFieldLocal);

        il.Emit(OpCodes.Ldloc, fieldsFieldLocal);
        il.Emit(OpCodes.Brfalse, returnFalseLabel);

        il.Emit(OpCodes.Ldloc, fieldsFieldLocal);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Callvirt, _types.FieldInfo.GetMethod("GetValue", [_types.Object])!);
        il.Emit(OpCodes.Isinst, dictType);
        il.Emit(OpCodes.Stloc, fieldsDictLocal);

        il.Emit(OpCodes.Ldloc, fieldsDictLocal);
        il.Emit(OpCodes.Brfalse, returnFalseLabel);

        // Check if _fields contains key (using original key, not PascalCase)
        il.Emit(OpCodes.Ldloc, fieldsDictLocal);
        il.Emit(OpCodes.Ldloc, keyStringLocal);
        il.Emit(OpCodes.Callvirt, _types.GetMethod(dictType, "ContainsKey", _types.String));
        il.Emit(OpCodes.Br, endLabel);

        // Return false
        il.MarkLabel(returnFalseLabel);
        il.Emit(OpCodes.Ldc_I4_0);

        il.MarkLabel(endLabel);
        il.Emit(OpCodes.Ret);
    }

    /// <summary>
    /// Emits Object.is(value1, value2) - determines whether two values are the same value.
    /// Unlike === operator:
    /// - Object.is(NaN, NaN) returns true
    /// - Object.is(-0, +0) returns false
    /// Signature: bool ObjectIs(object value1, object value2)
    /// </summary>
    private void EmitObjectIs(TypeBuilder typeBuilder, EmittedRuntime runtime)
    {
        var method = typeBuilder.DefineMethod(
            "ObjectIs",
            MethodAttributes.Public | MethodAttributes.Static,
            _types.Boolean,
            [_types.Object, _types.Object]
        );
        runtime.ObjectIs = method;

        var il = method.GetILGenerator();

        var bothNullLabel = il.DefineLabel();
        var oneNullLabel = il.DefineLabel();
        var checkDoubleLabel = il.DefineLabel();
        var notBothDoubleLabel = il.DefineLabel();
        var checkNaNLabel = il.DefineLabel();
        var notNaNLabel = il.DefineLabel();
        var checkZeroLabel = il.DefineLabel();
        var notZeroLabel = il.DefineLabel();
        var returnTrueLabel = il.DefineLabel();
        var returnFalseLabel = il.DefineLabel();
        var checkStringLabel = il.DefineLabel();
        var notStringLabel = il.DefineLabel();
        var checkBoolLabel = il.DefineLabel();
        var notBoolLabel = il.DefineLabel();
        var referenceEqualLabel = il.DefineLabel();
        var endLabel = il.DefineLabel();

        var d1Local = il.DeclareLocal(_types.Double);
        var d2Local = il.DeclareLocal(_types.Double);

        // Check if both null
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Brtrue, checkDoubleLabel);
        // value1 is null
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Brfalse, returnTrueLabel);  // both null
        il.Emit(OpCodes.Br, returnFalseLabel);       // only value1 is null

        // Check if both are double
        il.MarkLabel(checkDoubleLabel);
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Brfalse, returnFalseLabel);  // value2 is null but value1 isn't

        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Isinst, _types.Double);
        il.Emit(OpCodes.Brfalse, checkStringLabel);

        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Isinst, _types.Double);
        il.Emit(OpCodes.Brfalse, returnFalseLabel);  // value1 is double but value2 isn't

        // Both are doubles - unbox them
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Unbox_Any, _types.Double);
        il.Emit(OpCodes.Stloc, d1Local);

        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Unbox_Any, _types.Double);
        il.Emit(OpCodes.Stloc, d2Local);

        // Check if both are NaN
        il.Emit(OpCodes.Ldloc, d1Local);
        il.Emit(OpCodes.Call, _types.GetMethod(typeof(double), "IsNaN", _types.Double));
        il.Emit(OpCodes.Brfalse, checkZeroLabel);

        il.Emit(OpCodes.Ldloc, d2Local);
        il.Emit(OpCodes.Call, _types.GetMethod(typeof(double), "IsNaN", _types.Double));
        il.Emit(OpCodes.Brtrue, returnTrueLabel);  // Both NaN -> true
        il.Emit(OpCodes.Br, returnFalseLabel);     // Only d1 is NaN -> false

        // Check if both are zero (need to distinguish +0 and -0)
        il.MarkLabel(checkZeroLabel);
        il.Emit(OpCodes.Ldloc, d1Local);
        il.Emit(OpCodes.Ldc_R8, 0.0);
        il.Emit(OpCodes.Bne_Un, notZeroLabel);

        il.Emit(OpCodes.Ldloc, d2Local);
        il.Emit(OpCodes.Ldc_R8, 0.0);
        il.Emit(OpCodes.Bne_Un, returnFalseLabel);  // d1 is 0 but d2 isn't

        // Both are zero - compare 1/d1 == 1/d2 to distinguish +0 and -0
        il.Emit(OpCodes.Ldc_R8, 1.0);
        il.Emit(OpCodes.Ldloc, d1Local);
        il.Emit(OpCodes.Div);
        il.Emit(OpCodes.Ldc_R8, 1.0);
        il.Emit(OpCodes.Ldloc, d2Local);
        il.Emit(OpCodes.Div);
        il.Emit(OpCodes.Ceq);
        il.Emit(OpCodes.Br, endLabel);

        // Not zero - normal double comparison
        il.MarkLabel(notZeroLabel);
        il.Emit(OpCodes.Ldloc, d1Local);
        il.Emit(OpCodes.Ldloc, d2Local);
        il.Emit(OpCodes.Ceq);
        il.Emit(OpCodes.Br, endLabel);

        // Check if both are string
        il.MarkLabel(checkStringLabel);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Isinst, _types.String);
        il.Emit(OpCodes.Brfalse, checkBoolLabel);

        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Isinst, _types.String);
        il.Emit(OpCodes.Brfalse, returnFalseLabel);

        // Both strings - compare with string.Equals
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Castclass, _types.String);
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Castclass, _types.String);
        il.Emit(OpCodes.Call, _types.GetMethod(_types.String, "Equals", _types.String, _types.String));
        il.Emit(OpCodes.Br, endLabel);

        // Check if both are bool
        il.MarkLabel(checkBoolLabel);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Isinst, _types.Boolean);
        il.Emit(OpCodes.Brfalse, referenceEqualLabel);

        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Isinst, _types.Boolean);
        il.Emit(OpCodes.Brfalse, returnFalseLabel);

        // Both booleans - compare
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Unbox_Any, _types.Boolean);
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Unbox_Any, _types.Boolean);
        il.Emit(OpCodes.Ceq);
        il.Emit(OpCodes.Br, endLabel);

        // Reference equality for objects
        il.MarkLabel(referenceEqualLabel);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Ceq);
        il.Emit(OpCodes.Br, endLabel);

        // Return true
        il.MarkLabel(returnTrueLabel);
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Br, endLabel);

        // Return false
        il.MarkLabel(returnFalseLabel);
        il.Emit(OpCodes.Ldc_I4_0);

        il.MarkLabel(endLabel);
        il.Emit(OpCodes.Ret);
    }

    /// <summary>
    /// Emits Object.assign(target, sources) - copies properties from sources to target.
    /// Signature: object ObjectAssign(object target, List&lt;object&gt; sources)
    /// </summary>
    private void EmitObjectAssign(TypeBuilder typeBuilder, EmittedRuntime runtime)
    {
        var method = typeBuilder.DefineMethod(
            "ObjectAssign",
            MethodAttributes.Public | MethodAttributes.Static,
            _types.Object,
            [_types.Object, _types.ListOfObject]
        );
        runtime.ObjectAssign = method;

        var il = method.GetILGenerator();
        var dictType = _types.DictionaryStringObject;
        var listType = _types.ListOfObject;
        var kvpType = typeof(KeyValuePair<string, object?>);

        // Locals
        var targetDictLocal = il.DeclareLocal(dictType);
        var sourceIndexLocal = il.DeclareLocal(_types.Int32);
        var sourceLocal = il.DeclareLocal(_types.Object);
        var sourceDictLocal = il.DeclareLocal(dictType);
        var enumeratorLocal = il.DeclareLocal(typeof(Dictionary<string, object?>.Enumerator));
        var kvpLocal = il.DeclareLocal(kvpType);

        var targetNullLabel = il.DefineLabel();
        var notDictLabel = il.DefineLabel();
        var sourceLoopStart = il.DefineLabel();
        var sourceLoopEnd = il.DefineLabel();
        var nextSource = il.DefineLabel();
        var sourceNotDict = il.DefineLabel();
        var copyLoopStart = il.DefineLabel();
        var copyLoopEnd = il.DefineLabel();
        var returnLabel = il.DefineLabel();

        // Check if target is null
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Brfalse, targetNullLabel);

        // Check if target is Dictionary<string, object>
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Isinst, dictType);
        il.Emit(OpCodes.Stloc, targetDictLocal);
        il.Emit(OpCodes.Ldloc, targetDictLocal);
        il.Emit(OpCodes.Brfalse, notDictLabel);

        // Iterate over sources
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Stloc, sourceIndexLocal);

        il.MarkLabel(sourceLoopStart);
        // Check if sourceIndex < sources.Count
        il.Emit(OpCodes.Ldloc, sourceIndexLocal);
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Callvirt, _types.GetProperty(listType, "Count")!.GetGetMethod()!);
        il.Emit(OpCodes.Bge, sourceLoopEnd);

        // Get source = sources[sourceIndex]
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Ldloc, sourceIndexLocal);
        il.Emit(OpCodes.Callvirt, _types.GetProperty(listType, "Item")!.GetGetMethod()!);
        il.Emit(OpCodes.Stloc, sourceLocal);

        // If source is null, skip
        il.Emit(OpCodes.Ldloc, sourceLocal);
        il.Emit(OpCodes.Brfalse, nextSource);

        // Check if source is Dictionary<string, object>
        il.Emit(OpCodes.Ldloc, sourceLocal);
        il.Emit(OpCodes.Isinst, dictType);
        il.Emit(OpCodes.Stloc, sourceDictLocal);
        il.Emit(OpCodes.Ldloc, sourceDictLocal);
        il.Emit(OpCodes.Brfalse, nextSource);  // Skip non-dict sources for now

        // Get enumerator for source dictionary
        il.Emit(OpCodes.Ldloc, sourceDictLocal);
        il.Emit(OpCodes.Callvirt, dictType.GetMethod("GetEnumerator")!);
        il.Emit(OpCodes.Stloc, enumeratorLocal);

        // Copy loop
        il.MarkLabel(copyLoopStart);
        il.Emit(OpCodes.Ldloca, enumeratorLocal);
        il.Emit(OpCodes.Call, typeof(Dictionary<string, object?>.Enumerator).GetMethod("MoveNext")!);
        il.Emit(OpCodes.Brfalse, copyLoopEnd);

        // Get current kvp
        il.Emit(OpCodes.Ldloca, enumeratorLocal);
        il.Emit(OpCodes.Call, typeof(Dictionary<string, object?>.Enumerator).GetProperty("Current")!.GetGetMethod()!);
        il.Emit(OpCodes.Stloc, kvpLocal);

        // target[kvp.Key] = kvp.Value
        il.Emit(OpCodes.Ldloc, targetDictLocal);
        il.Emit(OpCodes.Ldloca, kvpLocal);
        il.Emit(OpCodes.Call, kvpType.GetProperty("Key")!.GetGetMethod()!);
        il.Emit(OpCodes.Ldloca, kvpLocal);
        il.Emit(OpCodes.Call, kvpType.GetProperty("Value")!.GetGetMethod()!);
        il.Emit(OpCodes.Callvirt, _types.GetMethod(dictType, "set_Item", _types.String, _types.Object));

        il.Emit(OpCodes.Br, copyLoopStart);

        il.MarkLabel(copyLoopEnd);
        // Dispose enumerator (it's a struct, so just call Dispose)
        il.Emit(OpCodes.Ldloca, enumeratorLocal);
        il.Emit(OpCodes.Call, typeof(Dictionary<string, object?>.Enumerator).GetMethod("Dispose")!);

        il.MarkLabel(nextSource);
        // Increment sourceIndex
        il.Emit(OpCodes.Ldloc, sourceIndexLocal);
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Add);
        il.Emit(OpCodes.Stloc, sourceIndexLocal);
        il.Emit(OpCodes.Br, sourceLoopStart);

        il.MarkLabel(sourceLoopEnd);
        il.Emit(OpCodes.Br, returnLabel);

        // Target is not a dictionary - just return it unchanged for now
        il.MarkLabel(notDictLabel);
        il.Emit(OpCodes.Br, returnLabel);

        // Target is null - throw exception
        il.MarkLabel(targetNullLabel);
        il.Emit(OpCodes.Ldstr, "Runtime Error: Object.assign() requires a target object");
        il.Emit(OpCodes.Newobj, _types.GetConstructor(_types.Exception, _types.String));
        il.Emit(OpCodes.Throw);

        // Return target
        il.MarkLabel(returnLabel);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ret);
    }

    /// <summary>
    /// Emits Object.freeze(obj) - freezes an object to prevent property changes.
    /// Uses PropertyDescriptorStore to track frozen objects for compiled code.
    /// Signature: object ObjectFreeze(object obj)
    /// </summary>
    private void EmitObjectFreeze(TypeBuilder typeBuilder, EmittedRuntime runtime, FieldBuilder frozenObjectsField, FieldBuilder sealedObjectsField)
    {
        var method = typeBuilder.DefineMethod(
            "ObjectFreeze",
            MethodAttributes.Public | MethodAttributes.Static,
            _types.Object,
            [_types.Object]
        );
        runtime.ObjectFreeze = method;

        var il = method.GetILGenerator();
        var returnLabel = il.DefineLabel();

        // If obj is null, just return it
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Brfalse, returnLabel);

        // Call $PropertyDescriptorStore.Freeze(obj) - fully standalone, no reflection
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Call, runtime.PDSFreeze);

        // Also add to legacy frozen objects table for backward compatibility
        il.Emit(OpCodes.Ldsfld, frozenObjectsField);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldc_I4_1);  // true
        il.Emit(OpCodes.Box, _types.Boolean);
        var addOrUpdateMethod = typeof(System.Runtime.CompilerServices.ConditionalWeakTable<object, object>)
            .GetMethod("AddOrUpdate");
        if (addOrUpdateMethod != null)
        {
            il.Emit(OpCodes.Callvirt, addOrUpdateMethod);
        }
        else
        {
            var setItem = _types.ConditionalWeakTable.GetMethod("set_Item")
                ?? _types.ConditionalWeakTable.GetProperty("Item")?.GetSetMethod();
            if (setItem != null)
            {
                il.Emit(OpCodes.Callvirt, setItem);
            }
            else
            {
                il.Emit(OpCodes.Pop);
                il.Emit(OpCodes.Pop);
                il.Emit(OpCodes.Pop);
            }
        }

        // Also add to sealed objects table (frozen implies sealed)
        il.Emit(OpCodes.Ldsfld, sealedObjectsField);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Box, _types.Boolean);
        if (addOrUpdateMethod != null)
        {
            il.Emit(OpCodes.Callvirt, addOrUpdateMethod);
        }
        else
        {
            var setItem = _types.ConditionalWeakTable.GetMethod("set_Item")
                ?? _types.ConditionalWeakTable.GetProperty("Item")?.GetSetMethod();
            if (setItem != null)
            {
                il.Emit(OpCodes.Callvirt, setItem);
            }
            else
            {
                il.Emit(OpCodes.Pop);
                il.Emit(OpCodes.Pop);
                il.Emit(OpCodes.Pop);
            }
        }

        il.MarkLabel(returnLabel);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ret);
    }

    /// <summary>
    /// Emits Object.seal(obj) - seals an object to prevent property addition/removal.
    /// Uses PropertyDescriptorStore to track sealed objects for compiled code.
    /// Signature: object ObjectSeal(object obj)
    /// </summary>
    private void EmitObjectSeal(TypeBuilder typeBuilder, EmittedRuntime runtime, FieldBuilder sealedObjectsField)
    {
        var method = typeBuilder.DefineMethod(
            "ObjectSeal",
            MethodAttributes.Public | MethodAttributes.Static,
            _types.Object,
            [_types.Object]
        );
        runtime.ObjectSeal = method;

        var il = method.GetILGenerator();
        var returnLabel = il.DefineLabel();

        // If obj is null, just return it
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Brfalse, returnLabel);

        // Call $PropertyDescriptorStore.Seal(obj) - fully standalone, no reflection
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Call, runtime.PDSSeal);

        // Also add to legacy sealed objects table for backward compatibility
        il.Emit(OpCodes.Ldsfld, sealedObjectsField);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Box, _types.Boolean);
        var addOrUpdateMethod = typeof(System.Runtime.CompilerServices.ConditionalWeakTable<object, object>)
            .GetMethod("AddOrUpdate");
        if (addOrUpdateMethod != null)
        {
            il.Emit(OpCodes.Callvirt, addOrUpdateMethod);
        }
        else
        {
            var setItem = _types.ConditionalWeakTable.GetMethod("set_Item")
                ?? _types.ConditionalWeakTable.GetProperty("Item")?.GetSetMethod();
            if (setItem != null)
            {
                il.Emit(OpCodes.Callvirt, setItem);
            }
            else
            {
                il.Emit(OpCodes.Pop);
                il.Emit(OpCodes.Pop);
                il.Emit(OpCodes.Pop);
            }
        }

        il.MarkLabel(returnLabel);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ret);
    }

    /// <summary>
    /// Emits Object.isFrozen(obj) - checks if an object is frozen.
    /// Signature: bool ObjectIsFrozen(object obj)
    /// </summary>
    private void EmitObjectIsFrozen(TypeBuilder typeBuilder, EmittedRuntime runtime, FieldBuilder frozenObjectsField)
    {
        var method = typeBuilder.DefineMethod(
            "ObjectIsFrozen",
            MethodAttributes.Public | MethodAttributes.Static,
            _types.Boolean,
            [_types.Object]
        );
        runtime.ObjectIsFrozen = method;

        var il = method.GetILGenerator();
        var returnTrueLabel = il.DefineLabel();
        var checkTableLabel = il.DefineLabel();
        var checkStringLabel = il.DefineLabel();
        var checkNumberLabel = il.DefineLabel();
        var checkBooleanLabel = il.DefineLabel();

        // If obj is null, return true (primitives are frozen by definition)
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Brtrue, checkStringLabel);
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Ret);

        // If obj is string, return true (immutable)
        il.MarkLabel(checkStringLabel);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Isinst, _types.String);
        il.Emit(OpCodes.Brfalse, checkNumberLabel);
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Ret);

        // If obj is double (boxed number), return true (immutable)
        il.MarkLabel(checkNumberLabel);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Isinst, _types.Double);
        il.Emit(OpCodes.Brfalse, checkBooleanLabel);
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Ret);

        // If obj is bool (boxed boolean), return true (immutable)
        il.MarkLabel(checkBooleanLabel);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Isinst, _types.Boolean);
        il.Emit(OpCodes.Brfalse, checkTableLabel);
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Ret);

        il.MarkLabel(checkTableLabel);
        // Check if obj is in frozen objects table
        var valueLocal = il.DeclareLocal(_types.Object);
        il.Emit(OpCodes.Ldsfld, frozenObjectsField);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldloca, valueLocal);
        var tryGetValue = _types.ConditionalWeakTable.GetMethod("TryGetValue");
        il.Emit(OpCodes.Callvirt, tryGetValue!);
        il.Emit(OpCodes.Ret);
    }

    /// <summary>
    /// Emits Object.isSealed(obj) - checks if an object is sealed.
    /// Signature: bool ObjectIsSealed(object obj)
    /// </summary>
    private void EmitObjectIsSealed(TypeBuilder typeBuilder, EmittedRuntime runtime, FieldBuilder sealedObjectsField)
    {
        var method = typeBuilder.DefineMethod(
            "ObjectIsSealed",
            MethodAttributes.Public | MethodAttributes.Static,
            _types.Boolean,
            [_types.Object]
        );
        runtime.ObjectIsSealed = method;

        var il = method.GetILGenerator();
        var checkTableLabel = il.DefineLabel();
        var checkStringLabel = il.DefineLabel();
        var checkNumberLabel = il.DefineLabel();
        var checkBooleanLabel = il.DefineLabel();

        // If obj is null, return true (primitives are sealed by definition)
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Brtrue, checkStringLabel);
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Ret);

        // If obj is string, return true (immutable)
        il.MarkLabel(checkStringLabel);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Isinst, _types.String);
        il.Emit(OpCodes.Brfalse, checkNumberLabel);
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Ret);

        // If obj is double (boxed number), return true (immutable)
        il.MarkLabel(checkNumberLabel);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Isinst, _types.Double);
        il.Emit(OpCodes.Brfalse, checkBooleanLabel);
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Ret);

        // If obj is bool (boxed boolean), return true (immutable)
        il.MarkLabel(checkBooleanLabel);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Isinst, _types.Boolean);
        il.Emit(OpCodes.Brfalse, checkTableLabel);
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Ret);

        il.MarkLabel(checkTableLabel);
        // Check if obj is in sealed objects table
        var valueLocal = il.DeclareLocal(_types.Object);
        il.Emit(OpCodes.Ldsfld, sealedObjectsField);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldloca, valueLocal);
        var tryGetValue = _types.ConditionalWeakTable.GetMethod("TryGetValue");
        il.Emit(OpCodes.Callvirt, tryGetValue!);
        il.Emit(OpCodes.Ret);
    }

    /// <summary>
    /// Emits Object.defineProperty(obj, prop, descriptor) - defines or modifies a property.
    /// Signature: object ObjectDefineProperty(object obj, object prop, object descriptor)
    /// Creates a $CompiledPropertyDescriptor and registers it in the emitted $PropertyDescriptorStore.
    /// </summary>
    private void EmitObjectDefineProperty(TypeBuilder typeBuilder, EmittedRuntime runtime)
    {
        var method = typeBuilder.DefineMethod(
            "ObjectDefineProperty",
            MethodAttributes.Public | MethodAttributes.Static,
            _types.Object,
            [_types.Object, _types.Object, _types.Object]
        );
        runtime.ObjectDefineProperty = method;

        var il = method.GetILGenerator();

        // Emit standalone property descriptor creation and registration
        // This avoids any runtime dependency on SharpTS.dll

        var descriptorLocal = il.DeclareLocal(runtime.CompiledPropertyDescriptorType);
        var dictLocal = il.DeclareLocal(_types.DictionaryStringObject);
        var propNameLocal = il.DeclareLocal(_types.String);
        var valueLocal = il.DeclareLocal(_types.Object);
        var notDictLabel = il.DefineLabel();
        var setDescriptorDoneLabel = il.DefineLabel();

        // propName = prop.ToString()
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Callvirt, _types.GetMethod(_types.Object, "ToString"));
        il.Emit(OpCodes.Stloc, propNameLocal);

        // Check if object is frozen - if so, throw TypeError
        var notFrozenLabel = il.DefineLabel();
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Call, runtime.PDSIsFrozen);
        il.Emit(OpCodes.Brfalse, notFrozenLabel);

        // Throw TypeError: Cannot define property on frozen object
        il.Emit(OpCodes.Ldstr, "Cannot define property: object is not extensible");
        il.Emit(OpCodes.Newobj, runtime.TSTypeErrorCtor);
        il.Emit(OpCodes.Call, runtime.CreateException);  // Wrap in .NET exception
        il.Emit(OpCodes.Throw);

        il.MarkLabel(notFrozenLabel);

        // Check if object is sealed and property doesn't exist
        var notSealedLabel = il.DefineLabel();
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Call, runtime.PDSIsSealed);
        il.Emit(OpCodes.Brfalse, notSealedLabel);

        // Object is sealed - check if property already exists (can modify existing)
        var canAddLabel = il.DefineLabel();
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldloc, propNameLocal);
        il.Emit(OpCodes.Call, runtime.PDSCanAddProperty);
        il.Emit(OpCodes.Brtrue, canAddLabel);

        // Can't add - throw TypeError
        il.Emit(OpCodes.Ldstr, "Cannot define property: object is not extensible");
        il.Emit(OpCodes.Newobj, runtime.TSTypeErrorCtor);
        il.Emit(OpCodes.Call, runtime.CreateException);  // Wrap in .NET exception
        il.Emit(OpCodes.Throw);

        il.MarkLabel(canAddLabel);
        il.MarkLabel(notSealedLabel);

        // Create new $CompiledPropertyDescriptor
        il.Emit(OpCodes.Newobj, runtime.CompiledPropertyDescriptorCtor);
        il.Emit(OpCodes.Stloc, descriptorLocal);

        // Check if descriptor is Dictionary<string, object?>
        il.Emit(OpCodes.Ldarg_2);
        il.Emit(OpCodes.Isinst, _types.DictionaryStringObject);
        il.Emit(OpCodes.Stloc, dictLocal);
        il.Emit(OpCodes.Ldloc, dictLocal);
        il.Emit(OpCodes.Brfalse, setDescriptorDoneLabel);

        // Extract properties from descriptor dictionary
        var dictTryGetValue = _types.GetMethod(_types.DictionaryStringObject, "TryGetValue", _types.String, _types.Object.MakeByRefType());

        // Try to get "value" property
        var noValueLabel = il.DefineLabel();
        il.Emit(OpCodes.Ldloc, dictLocal);
        il.Emit(OpCodes.Ldstr, "value");
        il.Emit(OpCodes.Ldloca, valueLocal);
        il.Emit(OpCodes.Callvirt, dictTryGetValue);
        il.Emit(OpCodes.Brfalse, noValueLabel);
        il.Emit(OpCodes.Ldloc, descriptorLocal);
        il.Emit(OpCodes.Ldloc, valueLocal);
        il.Emit(OpCodes.Callvirt, runtime.CompiledPropertyDescriptorValue.GetSetMethod()!);
        il.MarkLabel(noValueLabel);

        // Try to get "writable" property
        var noWritableLabel = il.DefineLabel();
        il.Emit(OpCodes.Ldloc, dictLocal);
        il.Emit(OpCodes.Ldstr, "writable");
        il.Emit(OpCodes.Ldloca, valueLocal);
        il.Emit(OpCodes.Callvirt, dictTryGetValue);
        il.Emit(OpCodes.Brfalse, noWritableLabel);
        // Convert to bool and set
        il.Emit(OpCodes.Ldloc, descriptorLocal);
        il.Emit(OpCodes.Ldloc, valueLocal);
        il.Emit(OpCodes.Call, runtime.IsTruthy);  // Convert to bool
        il.Emit(OpCodes.Callvirt, runtime.CompiledPropertyDescriptorWritable.GetSetMethod()!);
        il.MarkLabel(noWritableLabel);

        // Try to get "get" property (getter)
        var noGetterLabel = il.DefineLabel();
        il.Emit(OpCodes.Ldloc, dictLocal);
        il.Emit(OpCodes.Ldstr, "get");
        il.Emit(OpCodes.Ldloca, valueLocal);
        il.Emit(OpCodes.Callvirt, dictTryGetValue);
        il.Emit(OpCodes.Brfalse, noGetterLabel);
        il.Emit(OpCodes.Ldloc, descriptorLocal);
        il.Emit(OpCodes.Ldloc, valueLocal);
        il.Emit(OpCodes.Callvirt, runtime.CompiledPropertyDescriptorGetter.GetSetMethod()!);
        il.MarkLabel(noGetterLabel);

        // Try to get "set" property (setter)
        var noSetterLabel = il.DefineLabel();
        il.Emit(OpCodes.Ldloc, dictLocal);
        il.Emit(OpCodes.Ldstr, "set");
        il.Emit(OpCodes.Ldloca, valueLocal);
        il.Emit(OpCodes.Callvirt, dictTryGetValue);
        il.Emit(OpCodes.Brfalse, noSetterLabel);
        il.Emit(OpCodes.Ldloc, descriptorLocal);
        il.Emit(OpCodes.Ldloc, valueLocal);
        il.Emit(OpCodes.Callvirt, runtime.CompiledPropertyDescriptorSetter.GetSetMethod()!);
        il.MarkLabel(noSetterLabel);

        // Try to get "enumerable" property
        var noEnumerableLabel = il.DefineLabel();
        il.Emit(OpCodes.Ldloc, dictLocal);
        il.Emit(OpCodes.Ldstr, "enumerable");
        il.Emit(OpCodes.Ldloca, valueLocal);
        il.Emit(OpCodes.Callvirt, dictTryGetValue);
        il.Emit(OpCodes.Brfalse, noEnumerableLabel);
        il.Emit(OpCodes.Ldloc, descriptorLocal);
        il.Emit(OpCodes.Ldloc, valueLocal);
        il.Emit(OpCodes.Call, runtime.IsTruthy);
        il.Emit(OpCodes.Callvirt, runtime.CompiledPropertyDescriptorEnumerable.GetSetMethod()!);
        il.MarkLabel(noEnumerableLabel);

        // Try to get "configurable" property
        var noConfigurableLabel = il.DefineLabel();
        il.Emit(OpCodes.Ldloc, dictLocal);
        il.Emit(OpCodes.Ldstr, "configurable");
        il.Emit(OpCodes.Ldloca, valueLocal);
        il.Emit(OpCodes.Callvirt, dictTryGetValue);
        il.Emit(OpCodes.Brfalse, noConfigurableLabel);
        il.Emit(OpCodes.Ldloc, descriptorLocal);
        il.Emit(OpCodes.Ldloc, valueLocal);
        il.Emit(OpCodes.Call, runtime.IsTruthy);
        il.Emit(OpCodes.Callvirt, runtime.CompiledPropertyDescriptorConfigurable.GetSetMethod()!);
        il.MarkLabel(noConfigurableLabel);

        il.MarkLabel(setDescriptorDoneLabel);

        // Call $PropertyDescriptorStore.DefineProperty(obj, propName, descriptor)
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldloc, propNameLocal);
        il.Emit(OpCodes.Ldloc, descriptorLocal);
        il.Emit(OpCodes.Call, runtime.PDSDefineProperty);
        il.Emit(OpCodes.Pop);  // Discard bool result

        // Also set the value on the object if it's a data property (has value, not getter)
        // if (descriptor has "value" && obj is Dictionary)
        var skipValueSetLabel = il.DefineLabel();
        var endLabel = il.DefineLabel();

        // Check if descriptor has a value (not an accessor property)
        il.Emit(OpCodes.Ldloc, descriptorLocal);
        il.Emit(OpCodes.Callvirt, runtime.CompiledPropertyDescriptorValue.GetGetMethod()!);
        il.Emit(OpCodes.Brfalse, skipValueSetLabel);

        // Check if getter is set (accessor property - don't set value directly)
        il.Emit(OpCodes.Ldloc, descriptorLocal);
        il.Emit(OpCodes.Callvirt, runtime.CompiledPropertyDescriptorGetter.GetGetMethod()!);
        il.Emit(OpCodes.Brtrue, skipValueSetLabel);

        // Set value on object if it's a dictionary
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Isinst, _types.DictionaryStringObject);
        il.Emit(OpCodes.Brfalse, skipValueSetLabel);

        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Castclass, _types.DictionaryStringObject);
        il.Emit(OpCodes.Ldloc, propNameLocal);
        il.Emit(OpCodes.Ldloc, descriptorLocal);
        il.Emit(OpCodes.Callvirt, runtime.CompiledPropertyDescriptorValue.GetGetMethod()!);
        il.Emit(OpCodes.Callvirt, _types.GetMethod(_types.DictionaryStringObject, "set_Item"));
        il.Emit(OpCodes.Br, endLabel);

        il.MarkLabel(skipValueSetLabel);

        il.MarkLabel(endLabel);
        // Return the object
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ret);
    }

    /// <summary>
    /// Emits Object.getOwnPropertyDescriptor(obj, prop) - gets a property descriptor.
    /// Signature: object ObjectGetOwnPropertyDescriptor(object obj, object prop)
    /// Returns a JavaScript object with descriptor properties.
    /// </summary>
    private void EmitObjectGetOwnPropertyDescriptor(TypeBuilder typeBuilder, EmittedRuntime runtime)
    {
        var method = typeBuilder.DefineMethod(
            "ObjectGetOwnPropertyDescriptor",
            MethodAttributes.Public | MethodAttributes.Static,
            _types.Object,
            [_types.Object, _types.Object]
        );
        runtime.ObjectGetOwnPropertyDescriptor = method;

        var il = method.GetILGenerator();

        var propNameLocal = il.DeclareLocal(_types.String);
        var descriptorLocal = il.DeclareLocal(runtime.CompiledPropertyDescriptorType);
        var resultDictLocal = il.DeclareLocal(_types.DictionaryStringObject);
        var valueLocal = il.DeclareLocal(_types.Object);
        var returnNullLabel = il.DefineLabel();
        var checkObjPropertyLabel = il.DefineLabel();
        var hasDescriptorLabel = il.DefineLabel();
        var endLabel = il.DefineLabel();

        // propName = prop.ToString()
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Callvirt, _types.GetMethod(_types.Object, "ToString"));
        il.Emit(OpCodes.Stloc, propNameLocal);

        // Try to get descriptor from $PropertyDescriptorStore
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldloc, propNameLocal);
        il.Emit(OpCodes.Call, runtime.PDSGetPropertyDescriptor);
        il.Emit(OpCodes.Stloc, descriptorLocal);

        // If descriptor is not null, convert it to a JS object
        il.Emit(OpCodes.Ldloc, descriptorLocal);
        il.Emit(OpCodes.Brtrue, hasDescriptorLabel);

        // No descriptor - check if it's an array first
        var notListLabel = il.DefineLabel();
        var notTSArrayLabel = il.DefineLabel();
        var isListLabel = il.DefineLabel();
        var handleArrayLabel = il.DefineLabel();
        var listLocal = il.DeclareLocal(_types.ListOfObject);
        var indexLocal = il.DeclareLocal(_types.Int32);

        // Check for List<object?>
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Isinst, _types.ListOfObject);
        il.Emit(OpCodes.Stloc, listLocal);
        il.Emit(OpCodes.Ldloc, listLocal);
        il.Emit(OpCodes.Brtrue, isListLabel);

        // Check for $Array (SharpTSArray)
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Isinst, runtime.TSArrayType);
        il.Emit(OpCodes.Brfalse, notTSArrayLabel);

        // It's $Array - get Elements list
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Castclass, runtime.TSArrayType);
        il.Emit(OpCodes.Callvirt, runtime.TSArrayElementsGetter);
        il.Emit(OpCodes.Stloc, listLocal);
        il.Emit(OpCodes.Br, handleArrayLabel);

        il.MarkLabel(isListLabel);
        // listLocal already has the list

        il.MarkLabel(handleArrayLabel);
        // Handle array property - check if propName is "length" or numeric index

        // Check for "length" property
        var notLengthLabel = il.DefineLabel();
        il.Emit(OpCodes.Ldloc, propNameLocal);
        il.Emit(OpCodes.Ldstr, "length");
        il.Emit(OpCodes.Call, _types.GetMethod(_types.String, "op_Equality", _types.String, _types.String));
        il.Emit(OpCodes.Brfalse, notLengthLabel);

        // Return length descriptor: { value: length, writable: true, enumerable: false, configurable: false }
        il.Emit(OpCodes.Newobj, _types.DictionaryStringObjectCtor);
        il.Emit(OpCodes.Stloc, resultDictLocal);

        // value = list.Count
        il.Emit(OpCodes.Ldloc, resultDictLocal);
        il.Emit(OpCodes.Ldstr, "value");
        il.Emit(OpCodes.Ldloc, listLocal);
        il.Emit(OpCodes.Callvirt, _types.GetProperty(_types.ListOfObject, "Count").GetGetMethod()!);
        il.Emit(OpCodes.Conv_R8);
        il.Emit(OpCodes.Box, _types.Double);
        il.Emit(OpCodes.Callvirt, _types.GetMethod(_types.DictionaryStringObject, "set_Item"));

        // writable = true
        il.Emit(OpCodes.Ldloc, resultDictLocal);
        il.Emit(OpCodes.Ldstr, "writable");
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Box, _types.Boolean);
        il.Emit(OpCodes.Callvirt, _types.GetMethod(_types.DictionaryStringObject, "set_Item"));

        // enumerable = false
        il.Emit(OpCodes.Ldloc, resultDictLocal);
        il.Emit(OpCodes.Ldstr, "enumerable");
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Box, _types.Boolean);
        il.Emit(OpCodes.Callvirt, _types.GetMethod(_types.DictionaryStringObject, "set_Item"));

        // configurable = false
        il.Emit(OpCodes.Ldloc, resultDictLocal);
        il.Emit(OpCodes.Ldstr, "configurable");
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Box, _types.Boolean);
        il.Emit(OpCodes.Callvirt, _types.GetMethod(_types.DictionaryStringObject, "set_Item"));

        il.Emit(OpCodes.Ldloc, resultDictLocal);
        il.Emit(OpCodes.Br, endLabel);

        il.MarkLabel(notLengthLabel);
        // Check if it's a numeric index
        var notNumericIndexLabel = il.DefineLabel();
        il.Emit(OpCodes.Ldloc, propNameLocal);
        il.Emit(OpCodes.Ldloca, indexLocal);
        il.Emit(OpCodes.Call, _types.GetMethod(_types.Int32, "TryParse", _types.String, _types.Int32.MakeByRefType()));
        il.Emit(OpCodes.Brfalse, notNumericIndexLabel);

        // Check if index is in bounds
        il.Emit(OpCodes.Ldloc, indexLocal);
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Blt, returnNullLabel);
        il.Emit(OpCodes.Ldloc, indexLocal);
        il.Emit(OpCodes.Ldloc, listLocal);
        il.Emit(OpCodes.Callvirt, _types.GetProperty(_types.ListOfObject, "Count").GetGetMethod()!);
        il.Emit(OpCodes.Bge, returnNullLabel);

        // Return element descriptor: { value: element, writable: true, enumerable: true, configurable: true }
        il.Emit(OpCodes.Newobj, _types.DictionaryStringObjectCtor);
        il.Emit(OpCodes.Stloc, resultDictLocal);

        // value = list[index]
        il.Emit(OpCodes.Ldloc, resultDictLocal);
        il.Emit(OpCodes.Ldstr, "value");
        il.Emit(OpCodes.Ldloc, listLocal);
        il.Emit(OpCodes.Ldloc, indexLocal);
        il.Emit(OpCodes.Callvirt, _types.GetMethod(_types.ListOfObject, "get_Item", _types.Int32));
        il.Emit(OpCodes.Callvirt, _types.GetMethod(_types.DictionaryStringObject, "set_Item"));

        // writable = true
        il.Emit(OpCodes.Ldloc, resultDictLocal);
        il.Emit(OpCodes.Ldstr, "writable");
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Box, _types.Boolean);
        il.Emit(OpCodes.Callvirt, _types.GetMethod(_types.DictionaryStringObject, "set_Item"));

        // enumerable = true
        il.Emit(OpCodes.Ldloc, resultDictLocal);
        il.Emit(OpCodes.Ldstr, "enumerable");
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Box, _types.Boolean);
        il.Emit(OpCodes.Callvirt, _types.GetMethod(_types.DictionaryStringObject, "set_Item"));

        // configurable = true
        il.Emit(OpCodes.Ldloc, resultDictLocal);
        il.Emit(OpCodes.Ldstr, "configurable");
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Box, _types.Boolean);
        il.Emit(OpCodes.Callvirt, _types.GetMethod(_types.DictionaryStringObject, "set_Item"));

        il.Emit(OpCodes.Ldloc, resultDictLocal);
        il.Emit(OpCodes.Br, endLabel);

        il.MarkLabel(notNumericIndexLabel);
        // Not length or numeric index on array - return null
        il.Emit(OpCodes.Br, returnNullLabel);

        il.MarkLabel(notTSArrayLabel);

        // No descriptor - check if property exists on the object directly (Dictionary case)
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Isinst, _types.DictionaryStringObject);
        il.Emit(OpCodes.Brfalse, returnNullLabel);

        // Check if dictionary contains the key
        var dictContainsKeyLabel = il.DefineLabel();
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Castclass, _types.DictionaryStringObject);
        il.Emit(OpCodes.Ldloc, propNameLocal);
        il.Emit(OpCodes.Callvirt, _types.GetMethod(_types.DictionaryStringObject, "ContainsKey", _types.String));
        il.Emit(OpCodes.Brfalse, returnNullLabel);

        // Property exists on dict - create default data descriptor
        il.Emit(OpCodes.Newobj, _types.DictionaryStringObjectCtor);
        il.Emit(OpCodes.Stloc, resultDictLocal);

        // Get the value
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Castclass, _types.DictionaryStringObject);
        il.Emit(OpCodes.Ldloc, propNameLocal);
        il.Emit(OpCodes.Callvirt, _types.GetMethod(_types.DictionaryStringObject, "get_Item", _types.String));
        il.Emit(OpCodes.Stloc, valueLocal);

        // Set value property
        il.Emit(OpCodes.Ldloc, resultDictLocal);
        il.Emit(OpCodes.Ldstr, "value");
        il.Emit(OpCodes.Ldloc, valueLocal);
        il.Emit(OpCodes.Callvirt, _types.GetMethod(_types.DictionaryStringObject, "set_Item"));

        // Set writable = true
        il.Emit(OpCodes.Ldloc, resultDictLocal);
        il.Emit(OpCodes.Ldstr, "writable");
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Box, _types.Boolean);
        il.Emit(OpCodes.Callvirt, _types.GetMethod(_types.DictionaryStringObject, "set_Item"));

        // Set enumerable = true
        il.Emit(OpCodes.Ldloc, resultDictLocal);
        il.Emit(OpCodes.Ldstr, "enumerable");
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Box, _types.Boolean);
        il.Emit(OpCodes.Callvirt, _types.GetMethod(_types.DictionaryStringObject, "set_Item"));

        // Set configurable = true
        il.Emit(OpCodes.Ldloc, resultDictLocal);
        il.Emit(OpCodes.Ldstr, "configurable");
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Box, _types.Boolean);
        il.Emit(OpCodes.Callvirt, _types.GetMethod(_types.DictionaryStringObject, "set_Item"));

        il.Emit(OpCodes.Ldloc, resultDictLocal);
        il.Emit(OpCodes.Br, endLabel);

        // hasDescriptorLabel: Convert $CompiledPropertyDescriptor to JS object
        il.MarkLabel(hasDescriptorLabel);
        il.Emit(OpCodes.Newobj, _types.DictionaryStringObjectCtor);
        il.Emit(OpCodes.Stloc, resultDictLocal);

        // Check if it's an accessor property (has getter or setter)
        var isAccessorLabel = il.DefineLabel();
        var isDataLabel = il.DefineLabel();

        il.Emit(OpCodes.Ldloc, descriptorLocal);
        il.Emit(OpCodes.Callvirt, runtime.CompiledPropertyDescriptorGetter.GetGetMethod()!);
        il.Emit(OpCodes.Brtrue, isAccessorLabel);
        il.Emit(OpCodes.Ldloc, descriptorLocal);
        il.Emit(OpCodes.Callvirt, runtime.CompiledPropertyDescriptorSetter.GetGetMethod()!);
        il.Emit(OpCodes.Brtrue, isAccessorLabel);
        il.Emit(OpCodes.Br, isDataLabel);

        // Accessor property - set get and set
        il.MarkLabel(isAccessorLabel);

        // Set get property if not null
        var noGetLabel = il.DefineLabel();
        il.Emit(OpCodes.Ldloc, descriptorLocal);
        il.Emit(OpCodes.Callvirt, runtime.CompiledPropertyDescriptorGetter.GetGetMethod()!);
        il.Emit(OpCodes.Brfalse, noGetLabel);
        il.Emit(OpCodes.Ldloc, resultDictLocal);
        il.Emit(OpCodes.Ldstr, "get");
        il.Emit(OpCodes.Ldloc, descriptorLocal);
        il.Emit(OpCodes.Callvirt, runtime.CompiledPropertyDescriptorGetter.GetGetMethod()!);
        il.Emit(OpCodes.Callvirt, _types.GetMethod(_types.DictionaryStringObject, "set_Item"));
        il.MarkLabel(noGetLabel);

        // Set set property if not null
        var noSetLabel = il.DefineLabel();
        il.Emit(OpCodes.Ldloc, descriptorLocal);
        il.Emit(OpCodes.Callvirt, runtime.CompiledPropertyDescriptorSetter.GetGetMethod()!);
        il.Emit(OpCodes.Brfalse, noSetLabel);
        il.Emit(OpCodes.Ldloc, resultDictLocal);
        il.Emit(OpCodes.Ldstr, "set");
        il.Emit(OpCodes.Ldloc, descriptorLocal);
        il.Emit(OpCodes.Callvirt, runtime.CompiledPropertyDescriptorSetter.GetGetMethod()!);
        il.Emit(OpCodes.Callvirt, _types.GetMethod(_types.DictionaryStringObject, "set_Item"));
        il.MarkLabel(noSetLabel);

        var afterAccessorLabel = il.DefineLabel();
        il.Emit(OpCodes.Br, afterAccessorLabel);

        // Data property - set value and writable
        il.MarkLabel(isDataLabel);

        // Set value
        il.Emit(OpCodes.Ldloc, resultDictLocal);
        il.Emit(OpCodes.Ldstr, "value");
        il.Emit(OpCodes.Ldloc, descriptorLocal);
        il.Emit(OpCodes.Callvirt, runtime.CompiledPropertyDescriptorValue.GetGetMethod()!);
        il.Emit(OpCodes.Callvirt, _types.GetMethod(_types.DictionaryStringObject, "set_Item"));

        // Set writable
        il.Emit(OpCodes.Ldloc, resultDictLocal);
        il.Emit(OpCodes.Ldstr, "writable");
        il.Emit(OpCodes.Ldloc, descriptorLocal);
        il.Emit(OpCodes.Callvirt, runtime.CompiledPropertyDescriptorWritable.GetGetMethod()!);
        il.Emit(OpCodes.Box, _types.Boolean);
        il.Emit(OpCodes.Callvirt, _types.GetMethod(_types.DictionaryStringObject, "set_Item"));

        il.MarkLabel(afterAccessorLabel);

        // Set enumerable
        il.Emit(OpCodes.Ldloc, resultDictLocal);
        il.Emit(OpCodes.Ldstr, "enumerable");
        il.Emit(OpCodes.Ldloc, descriptorLocal);
        il.Emit(OpCodes.Callvirt, runtime.CompiledPropertyDescriptorEnumerable.GetGetMethod()!);
        il.Emit(OpCodes.Box, _types.Boolean);
        il.Emit(OpCodes.Callvirt, _types.GetMethod(_types.DictionaryStringObject, "set_Item"));

        // Set configurable
        il.Emit(OpCodes.Ldloc, resultDictLocal);
        il.Emit(OpCodes.Ldstr, "configurable");
        il.Emit(OpCodes.Ldloc, descriptorLocal);
        il.Emit(OpCodes.Callvirt, runtime.CompiledPropertyDescriptorConfigurable.GetGetMethod()!);
        il.Emit(OpCodes.Box, _types.Boolean);
        il.Emit(OpCodes.Callvirt, _types.GetMethod(_types.DictionaryStringObject, "set_Item"));

        il.Emit(OpCodes.Ldloc, resultDictLocal);
        il.Emit(OpCodes.Br, endLabel);

        // returnNullLabel: return undefined
        il.MarkLabel(returnNullLabel);
        il.Emit(OpCodes.Ldsfld, runtime.UndefinedInstance);

        il.MarkLabel(endLabel);
        il.Emit(OpCodes.Ret);
    }

    /// <summary>
    /// Emits Object.create(proto, propertiesObject?) - creates a new object with prototype.
    /// Signature: object ObjectCreate(object proto, object propertiesObject)
    /// Fully standalone - uses emitted $PropertyDescriptorStore for descriptor storage.
    /// </summary>
    private void EmitObjectCreate(TypeBuilder typeBuilder, EmittedRuntime runtime, FieldBuilder prototypeStoreField)
    {
        var method = typeBuilder.DefineMethod(
            "ObjectCreate",
            MethodAttributes.Public | MethodAttributes.Static,
            _types.Object,
            [_types.Object, _types.Object]
        );
        runtime.ObjectCreate = method;

        var il = method.GetILGenerator();

        // Locals
        var resultLocal = il.DeclareLocal(_types.DictionaryStringObject);
        var propsLocal = il.DeclareLocal(_types.DictionaryStringObject);
        var enumeratorLocal = il.DeclareLocal(typeof(Dictionary<string, object?>.Enumerator));
        var currentLocal = il.DeclareLocal(typeof(KeyValuePair<string, object?>));
        var propKeyLocal = il.DeclareLocal(_types.String);
        var propDescLocal = il.DeclareLocal(_types.Object);

        var noPropsLabel = il.DefineLabel();
        var loopStartLabel = il.DefineLabel();
        var loopEndLabel = il.DefineLabel();
        var returnLabel = il.DefineLabel();

        // result = new Dictionary<string, object?>()
        il.Emit(OpCodes.Newobj, _types.DictionaryStringObjectCtor);
        il.Emit(OpCodes.Stloc, resultLocal);

        // Set prototype: $PropertyDescriptorStore.SetPrototype(result, proto)
        il.Emit(OpCodes.Ldloc, resultLocal);
        il.Emit(OpCodes.Ldarg_0);  // proto
        il.Emit(OpCodes.Call, runtime.PDSSetPrototype);

        // Copy properties from prototype if it's a Dictionary (for Object.keys compatibility)
        var protoDictLocal = il.DeclareLocal(_types.DictionaryStringObject);
        var protoEnumeratorLocal = il.DeclareLocal(typeof(Dictionary<string, object?>.Enumerator));
        var protoCurrentLocal = il.DeclareLocal(typeof(KeyValuePair<string, object?>));
        var skipProtoCopyLabel = il.DefineLabel();
        var protoCopyLoopLabel = il.DefineLabel();
        var protoCopyDoneLabel = il.DefineLabel();

        // Check if proto is Dictionary<string, object?>
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Isinst, _types.DictionaryStringObject);
        il.Emit(OpCodes.Stloc, protoDictLocal);
        il.Emit(OpCodes.Ldloc, protoDictLocal);
        il.Emit(OpCodes.Brfalse, skipProtoCopyLabel);

        // Copy properties from prototype to result
        il.Emit(OpCodes.Ldloc, protoDictLocal);
        il.Emit(OpCodes.Callvirt, _types.GetMethod(_types.DictionaryStringObject, "GetEnumerator"));
        il.Emit(OpCodes.Stloc, protoEnumeratorLocal);

        il.MarkLabel(protoCopyLoopLabel);
        il.Emit(OpCodes.Ldloca, protoEnumeratorLocal);
        var protoMoveNext = typeof(Dictionary<string, object?>.Enumerator).GetMethod("MoveNext")!;
        il.Emit(OpCodes.Call, protoMoveNext);
        il.Emit(OpCodes.Brfalse, protoCopyDoneLabel);

        // Get current KVP
        il.Emit(OpCodes.Ldloca, protoEnumeratorLocal);
        var protoCurrent = typeof(Dictionary<string, object?>.Enumerator).GetProperty("Current")!.GetGetMethod()!;
        il.Emit(OpCodes.Call, protoCurrent);
        il.Emit(OpCodes.Stloc, protoCurrentLocal);

        // result[key] = value
        il.Emit(OpCodes.Ldloc, resultLocal);
        il.Emit(OpCodes.Ldloca, protoCurrentLocal);
        var protoKeyGetter = typeof(KeyValuePair<string, object?>).GetProperty("Key")!.GetGetMethod()!;
        il.Emit(OpCodes.Call, protoKeyGetter);
        il.Emit(OpCodes.Ldloca, protoCurrentLocal);
        var protoValueGetter = typeof(KeyValuePair<string, object?>).GetProperty("Value")!.GetGetMethod()!;
        il.Emit(OpCodes.Call, protoValueGetter);
        il.Emit(OpCodes.Callvirt, _types.GetMethod(_types.DictionaryStringObject, "set_Item"));

        il.Emit(OpCodes.Br, protoCopyLoopLabel);

        il.MarkLabel(protoCopyDoneLabel);
        il.Emit(OpCodes.Ldloca, protoEnumeratorLocal);
        var protoDispose = typeof(Dictionary<string, object?>.Enumerator).GetMethod("Dispose")!;
        il.Emit(OpCodes.Call, protoDispose);

        il.MarkLabel(skipProtoCopyLabel);

        // If propertiesObject is null, skip property definition
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Brfalse, noPropsLabel);

        // Cast propertiesObject to Dictionary<string, object?>
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Isinst, _types.DictionaryStringObject);
        il.Emit(OpCodes.Stloc, propsLocal);
        il.Emit(OpCodes.Ldloc, propsLocal);
        il.Emit(OpCodes.Brfalse, noPropsLabel);

        // Get enumerator: enumerator = props.GetEnumerator()
        il.Emit(OpCodes.Ldloc, propsLocal);
        il.Emit(OpCodes.Callvirt, _types.GetMethod(_types.DictionaryStringObject, "GetEnumerator"));
        il.Emit(OpCodes.Stloc, enumeratorLocal);

        // Loop start
        il.MarkLabel(loopStartLabel);

        // if (!enumerator.MoveNext()) goto loopEnd
        il.Emit(OpCodes.Ldloca, enumeratorLocal);
        var moveNextMethod = typeof(Dictionary<string, object?>.Enumerator).GetMethod("MoveNext")!;
        il.Emit(OpCodes.Call, moveNextMethod);
        il.Emit(OpCodes.Brfalse, loopEndLabel);

        // current = enumerator.Current
        il.Emit(OpCodes.Ldloca, enumeratorLocal);
        var currentGetter = typeof(Dictionary<string, object?>.Enumerator).GetProperty("Current")!.GetGetMethod()!;
        il.Emit(OpCodes.Call, currentGetter);
        il.Emit(OpCodes.Stloc, currentLocal);

        // propKey = current.Key
        il.Emit(OpCodes.Ldloca, currentLocal);
        var keyGetter = typeof(KeyValuePair<string, object?>).GetProperty("Key")!.GetGetMethod()!;
        il.Emit(OpCodes.Call, keyGetter);
        il.Emit(OpCodes.Stloc, propKeyLocal);

        // propDesc = current.Value
        il.Emit(OpCodes.Ldloca, currentLocal);
        var valueGetter = typeof(KeyValuePair<string, object?>).GetProperty("Value")!.GetGetMethod()!;
        il.Emit(OpCodes.Call, valueGetter);
        il.Emit(OpCodes.Stloc, propDescLocal);

        // Skip null descriptors
        var notNullDescLabel = il.DefineLabel();
        il.Emit(OpCodes.Ldloc, propDescLocal);
        il.Emit(OpCodes.Brtrue, notNullDescLabel);
        il.Emit(OpCodes.Br, loopStartLabel);  // Continue to next iteration

        il.MarkLabel(notNullDescLabel);

        // Call ObjectDefineProperty(result, propKey, propDesc) for this property
        il.Emit(OpCodes.Ldloc, resultLocal);
        il.Emit(OpCodes.Ldloc, propKeyLocal);
        il.Emit(OpCodes.Ldloc, propDescLocal);
        il.Emit(OpCodes.Call, runtime.ObjectDefineProperty);
        il.Emit(OpCodes.Pop);  // Discard return value

        // Continue loop
        il.Emit(OpCodes.Br, loopStartLabel);

        il.MarkLabel(loopEndLabel);

        // Dispose enumerator (it's a struct, but good practice)
        il.Emit(OpCodes.Ldloca, enumeratorLocal);
        var disposeMethod = typeof(Dictionary<string, object?>.Enumerator).GetMethod("Dispose")!;
        il.Emit(OpCodes.Call, disposeMethod);

        il.MarkLabel(noPropsLabel);

        // Return result
        il.Emit(OpCodes.Ldloc, resultLocal);
        il.Emit(OpCodes.Ret);
    }

    /// <summary>
    /// Emits Object.preventExtensions(obj) - prevents adding new properties.
    /// Signature: object ObjectPreventExtensions(object obj)
    /// Uses PropertyDescriptorStore for enforcement and local table for standalone checks.
    /// </summary>
    private void EmitObjectPreventExtensions(TypeBuilder typeBuilder, EmittedRuntime runtime,
        FieldBuilder nonExtensibleObjectsField, FieldBuilder frozenObjectsField, FieldBuilder sealedObjectsField)
    {
        var method = typeBuilder.DefineMethod(
            "ObjectPreventExtensions",
            MethodAttributes.Public | MethodAttributes.Static,
            _types.Object,
            [_types.Object]
        );
        runtime.ObjectPreventExtensions = method;

        var il = method.GetILGenerator();
        var returnLabel = il.DefineLabel();

        // If obj is null, just return it
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Brfalse, returnLabel);

        // Call $PropertyDescriptorStore.PreventExtensions(obj) - fully standalone, no reflection
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Call, runtime.PDSPreventExtensions);

        // Also add to local non-extensible objects table for standalone checks
        il.Emit(OpCodes.Ldsfld, nonExtensibleObjectsField);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldc_I4_1); // true
        il.Emit(OpCodes.Box, _types.Boolean);
        var addOrUpdateMethod = typeof(System.Runtime.CompilerServices.ConditionalWeakTable<object, object>)
            .GetMethod("AddOrUpdate");
        if (addOrUpdateMethod != null)
        {
            il.Emit(OpCodes.Callvirt, addOrUpdateMethod);
        }
        else
        {
            var setItem = _types.ConditionalWeakTable.GetMethod("set_Item")
                ?? _types.ConditionalWeakTable.GetProperty("Item")?.GetSetMethod();
            if (setItem != null)
            {
                il.Emit(OpCodes.Callvirt, setItem);
            }
            else
            {
                il.Emit(OpCodes.Pop);
                il.Emit(OpCodes.Pop);
                il.Emit(OpCodes.Pop);
            }
        }

        il.MarkLabel(returnLabel);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ret);
    }

    /// <summary>
    /// Emits Object.isExtensible(obj) - returns whether object can have new properties.
    /// Signature: bool ObjectIsExtensible(object obj)
    /// Checks both PropertyDescriptorStore and local tables for compatibility.
    /// Returns false for primitives, frozen, sealed, or explicitly non-extensible objects.
    /// </summary>
    private void EmitObjectIsExtensible(TypeBuilder typeBuilder, EmittedRuntime runtime,
        FieldBuilder nonExtensibleObjectsField, FieldBuilder frozenObjectsField, FieldBuilder sealedObjectsField)
    {
        var method = typeBuilder.DefineMethod(
            "ObjectIsExtensible",
            MethodAttributes.Public | MethodAttributes.Static,
            _types.Boolean,
            [_types.Object]
        );
        runtime.ObjectIsExtensible = method;

        var il = method.GetILGenerator();
        var returnFalseLabel = il.DefineLabel();
        var checkStringLabel = il.DefineLabel();
        var checkNumberLabel = il.DefineLabel();
        var checkBooleanLabel = il.DefineLabel();
        var checkPropertyStoreLabel = il.DefineLabel();
        var checkLocalTablesLabel = il.DefineLabel();
        var checkFrozenLabel = il.DefineLabel();
        var checkSealedLabel = il.DefineLabel();

        var valueLocal = il.DeclareLocal(_types.Object);

        // If obj is null, return false (primitives are not extensible)
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Brtrue, checkStringLabel);
        il.Emit(OpCodes.Br, returnFalseLabel);

        // If obj is string, return false (immutable)
        il.MarkLabel(checkStringLabel);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Isinst, _types.String);
        il.Emit(OpCodes.Brtrue, returnFalseLabel);

        // If obj is double (boxed number), return false (immutable)
        il.MarkLabel(checkNumberLabel);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Isinst, _types.Double);
        il.Emit(OpCodes.Brtrue, returnFalseLabel);

        // If obj is bool (boxed boolean), return false (immutable)
        il.MarkLabel(checkBooleanLabel);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Isinst, _types.Boolean);
        il.Emit(OpCodes.Brtrue, returnFalseLabel);

        // Check $PropertyDescriptorStore.IsExtensible(obj) - fully standalone, no reflection
        il.MarkLabel(checkPropertyStoreLabel);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Call, runtime.PDSIsExtensible);
        il.Emit(OpCodes.Brfalse, returnFalseLabel); // Not extensible per property store

        // Also check local tables for backward compatibility
        // Check if obj is in the non-extensible objects table
        il.MarkLabel(checkLocalTablesLabel);
        il.Emit(OpCodes.Ldsfld, nonExtensibleObjectsField);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldloca, valueLocal);
        var tryGetValue = _types.ConditionalWeakTable.GetMethod("TryGetValue");
        il.Emit(OpCodes.Callvirt, tryGetValue!);
        il.Emit(OpCodes.Brtrue, returnFalseLabel); // Found = not extensible

        // Check if obj is in the frozen objects table
        il.MarkLabel(checkFrozenLabel);
        il.Emit(OpCodes.Ldsfld, frozenObjectsField);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldloca, valueLocal);
        il.Emit(OpCodes.Callvirt, tryGetValue!);
        il.Emit(OpCodes.Brtrue, returnFalseLabel); // Frozen = not extensible

        // Check if obj is in the sealed objects table
        il.MarkLabel(checkSealedLabel);
        il.Emit(OpCodes.Ldsfld, sealedObjectsField);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldloca, valueLocal);
        il.Emit(OpCodes.Callvirt, tryGetValue!);
        il.Emit(OpCodes.Brtrue, returnFalseLabel); // Sealed = not extensible

        // Not in any table, object is extensible
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Ret);

        // Return false
        il.MarkLabel(returnFalseLabel);
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Ret);
    }

    /// <summary>
    /// Emits Object.getOwnPropertySymbols(obj) - returns array of symbol-keyed properties.
    /// Signature: object GetOwnPropertySymbols(object obj)
    /// Uses the compiled assembly's GetSymbolDict to retrieve symbol keys.
    /// </summary>
    private void EmitGetOwnPropertySymbols(TypeBuilder typeBuilder, EmittedRuntime runtime)
    {
        var method = typeBuilder.DefineMethod(
            "GetOwnPropertySymbols",
            MethodAttributes.Public | MethodAttributes.Static,
            _types.Object,
            [_types.Object]
        );
        runtime.GetOwnPropertySymbols = method;

        var il = method.GetILGenerator();

        // Create the result list
        // var result = new List<object?>();
        var resultLocal = il.DeclareLocal(_types.ListOfObjectNullable);
        il.Emit(OpCodes.Newobj, _types.GetDefaultConstructor(_types.ListOfObjectNullable));
        il.Emit(OpCodes.Stloc, resultLocal);

        // Get symbol dictionary: var symbolDict = GetSymbolDict(obj);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Call, runtime.GetSymbolDictMethod);
        var symbolDictLocal = il.DeclareLocal(_types.DictionaryObjectObject);
        il.Emit(OpCodes.Stloc, symbolDictLocal);

        // Get keys and iterate: foreach (var key in symbolDict.Keys) result.Add(key);
        // symbolDict.Keys
        il.Emit(OpCodes.Ldloc, symbolDictLocal);
        var keysProperty = _types.DictionaryObjectObject.GetProperty("Keys")!.GetGetMethod()!;
        il.Emit(OpCodes.Callvirt, keysProperty);

        // Get enumerator
        var keysCollectionType = keysProperty.ReturnType;
        var getEnumeratorMethod = keysCollectionType.GetMethod("GetEnumerator")!;
        il.Emit(OpCodes.Callvirt, getEnumeratorMethod);
        var enumeratorType = getEnumeratorMethod.ReturnType;
        var enumeratorLocal = il.DeclareLocal(enumeratorType);
        il.Emit(OpCodes.Stloc, enumeratorLocal);

        // Loop: while (enumerator.MoveNext())
        var loopStart = il.DefineLabel();
        var loopEnd = il.DefineLabel();

        il.MarkLabel(loopStart);
        il.Emit(OpCodes.Ldloca, enumeratorLocal);
        var moveNextMethod = enumeratorType.GetMethod("MoveNext")!;
        il.Emit(OpCodes.Call, moveNextMethod);
        il.Emit(OpCodes.Brfalse, loopEnd);

        // result.Add(enumerator.Current);
        il.Emit(OpCodes.Ldloc, resultLocal);
        il.Emit(OpCodes.Ldloca, enumeratorLocal);
        var currentProperty = enumeratorType.GetProperty("Current")!.GetGetMethod()!;
        il.Emit(OpCodes.Call, currentProperty);
        var addMethod = _types.ListOfObjectNullable.GetMethod("Add", [_types.Object])!;
        il.Emit(OpCodes.Callvirt, addMethod);

        il.Emit(OpCodes.Br, loopStart);
        il.MarkLabel(loopEnd);

        // Return result
        il.Emit(OpCodes.Ldloc, resultLocal);
        il.Emit(OpCodes.Ret);
    }

    /// <summary>
    /// Emits Object.getPrototypeOf(obj) - returns the prototype of an object.
    /// Signature: object ObjectGetPrototypeOf(object obj)
    /// Checks PropertyDescriptorStore first, then local table for compatibility.
    /// </summary>
    private void EmitObjectGetPrototypeOf(TypeBuilder typeBuilder, EmittedRuntime runtime, FieldBuilder prototypeStoreField)
    {
        var method = typeBuilder.DefineMethod(
            "ObjectGetPrototypeOf",
            MethodAttributes.Public | MethodAttributes.Static,
            _types.Object,
            [_types.Object]
        );
        runtime.ObjectGetPrototypeOf = method;

        var il = method.GetILGenerator();
        var checkLocalTableLabel = il.DefineLabel();
        var foundInLocalLabel = il.DefineLabel();

        var resultLocal = il.DeclareLocal(_types.Object);
        var tempLocal = il.DeclareLocal(_types.Object);

        // Check $PropertyDescriptorStore.GetPrototype(obj) - fully standalone, no reflection
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Call, runtime.PDSGetPrototype);
        il.Emit(OpCodes.Stloc, resultLocal);

        // If result is not null, return it
        il.Emit(OpCodes.Ldloc, resultLocal);
        il.Emit(OpCodes.Brfalse, checkLocalTableLabel);
        il.Emit(OpCodes.Ldloc, resultLocal);
        il.Emit(OpCodes.Ret);

        // Also check local _prototypeStore table for backward compatibility
        il.MarkLabel(checkLocalTableLabel);
        il.Emit(OpCodes.Ldsfld, prototypeStoreField);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldloca, tempLocal);
        var tryGetValue = _types.ConditionalWeakTable.GetMethod("TryGetValue");
        il.Emit(OpCodes.Callvirt, tryGetValue!);
        il.Emit(OpCodes.Brtrue, foundInLocalLabel);

        // Not found in either: return null
        il.Emit(OpCodes.Ldnull);
        il.Emit(OpCodes.Ret);

        // Found in local table: return it
        il.MarkLabel(foundInLocalLabel);
        il.Emit(OpCodes.Ldloc, tempLocal);
        il.Emit(OpCodes.Ret);
    }

    /// <summary>
    /// Emits Object.setPrototypeOf(obj, proto) - sets the prototype of an object.
    /// Signature: object ObjectSetPrototypeOf(object obj, object proto)
    /// Uses reflection to call RuntimeSetPrototypeOf helper for complex object type handling.
    /// Also stores in local prototype table for standalone checks.
    /// </summary>
    private void EmitObjectSetPrototypeOf(TypeBuilder typeBuilder, EmittedRuntime runtime,
        FieldBuilder prototypeStoreField, FieldBuilder nonExtensibleObjectsField)
    {
        var method = typeBuilder.DefineMethod(
            "ObjectSetPrototypeOf",
            MethodAttributes.Public | MethodAttributes.Static,
            _types.Object,
            [_types.Object, _types.Object]
        );
        runtime.ObjectSetPrototypeOf = method;

        var il = method.GetILGenerator();

        // Check if object is null - if so, skip checks
        var nullCheckDoneLabel = il.DefineLabel();
        var notExtensibleLabel = il.DefineLabel();
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Brfalse, nullCheckDoneLabel);

        // Check if object is extensible - if not, throw TypeError
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Call, runtime.ObjectIsExtensible);
        il.Emit(OpCodes.Brtrue, nullCheckDoneLabel);  // Object is extensible, proceed

        // Object is not extensible - throw TypeError
        il.Emit(OpCodes.Ldstr, "Cannot set prototype of non-extensible object");
        il.Emit(OpCodes.Newobj, runtime.TSTypeErrorCtor);
        il.Emit(OpCodes.Call, runtime.CreateException);
        il.Emit(OpCodes.Throw);

        il.MarkLabel(nullCheckDoneLabel);

        // Use reflection to call ObjectBuiltIns.RuntimeSetPrototypeOf at runtime
        var typeLocal = il.DeclareLocal(_types.Type);
        var methodLocal = il.DeclareLocal(_types.MethodInfo);
        var argsLocal = il.DeclareLocal(_types.ObjectArray);
        var skipReflectionCallLabel = il.DefineLabel();
        var localStoreLabel = il.DefineLabel();

        // Get the type by name
        il.Emit(OpCodes.Ldstr, "SharpTS.Runtime.BuiltIns.ObjectBuiltIns, SharpTS");
        il.Emit(OpCodes.Call, _types.GetMethod(_types.Type, "GetType", _types.String));
        il.Emit(OpCodes.Stloc, typeLocal);

        // Check if type is null
        il.Emit(OpCodes.Ldloc, typeLocal);
        il.Emit(OpCodes.Brfalse, skipReflectionCallLabel);

        // Get the method
        il.Emit(OpCodes.Ldloc, typeLocal);
        il.Emit(OpCodes.Ldstr, "RuntimeSetPrototypeOf");
        il.Emit(OpCodes.Ldc_I4, (int)(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static));
        il.Emit(OpCodes.Callvirt, _types.GetMethod(_types.Type, "GetMethod", _types.String, _types.BindingFlags));
        il.Emit(OpCodes.Stloc, methodLocal);

        // Check if method is null
        il.Emit(OpCodes.Ldloc, methodLocal);
        il.Emit(OpCodes.Brfalse, skipReflectionCallLabel);

        // Create args array: new object[] { obj, proto }
        il.Emit(OpCodes.Ldc_I4_2);
        il.Emit(OpCodes.Newarr, _types.Object);
        il.Emit(OpCodes.Dup);
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Ldarg_0);  // obj
        il.Emit(OpCodes.Stelem_Ref);
        il.Emit(OpCodes.Dup);
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Ldarg_1);  // proto
        il.Emit(OpCodes.Stelem_Ref);
        il.Emit(OpCodes.Stloc, argsLocal);

        // Invoke: method.Invoke(null, args) - discard result since we also do local tracking
        il.Emit(OpCodes.Ldloc, methodLocal);
        il.Emit(OpCodes.Ldnull);
        il.Emit(OpCodes.Ldloc, argsLocal);
        il.Emit(OpCodes.Callvirt, _types.GetMethod(_types.MethodInfo, "Invoke", _types.Object, _types.ObjectArray));
        il.Emit(OpCodes.Pop);  // Discard result from reflection call

        il.MarkLabel(skipReflectionCallLabel);

        // Also store in $PropertyDescriptorStore for standalone operation
        var skipLocalStoreLabel = il.DefineLabel();
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Brfalse, skipLocalStoreLabel); // Skip if null

        // Call $PropertyDescriptorStore.SetPrototype(obj, proto)
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Call, runtime.PDSSetPrototype);

        // Also store in local prototype table for backward compatibility
        il.Emit(OpCodes.Ldsfld, prototypeStoreField);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldarg_1);
        var addOrUpdateMethod = typeof(System.Runtime.CompilerServices.ConditionalWeakTable<object, object>)
            .GetMethod("AddOrUpdate");
        if (addOrUpdateMethod != null)
        {
            il.Emit(OpCodes.Callvirt, addOrUpdateMethod);
        }
        else
        {
            // Fallback: Remove then Add
            var removeMethod = _types.ConditionalWeakTable.GetMethod("Remove", [_types.Object]);
            il.Emit(OpCodes.Pop); // Pop proto
            il.Emit(OpCodes.Pop); // Pop target
            il.Emit(OpCodes.Pop); // Pop table
            il.Emit(OpCodes.Ldsfld, prototypeStoreField);
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Callvirt, removeMethod!);
            il.Emit(OpCodes.Pop);
            il.Emit(OpCodes.Ldsfld, prototypeStoreField);
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Ldarg_1);
            var addMethod = _types.ConditionalWeakTable.GetMethod("Add");
            il.Emit(OpCodes.Callvirt, addMethod!);
        }

        il.MarkLabel(skipLocalStoreLabel);
        // Return obj (arg_0)
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ret);
    }
}

