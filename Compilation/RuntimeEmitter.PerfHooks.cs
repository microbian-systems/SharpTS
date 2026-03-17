using System.Diagnostics;
using System.Reflection;
using System.Reflection.Emit;

namespace SharpTS.Compilation;

/// <summary>
/// perf_hooks module support for standalone assemblies.
/// Provides high-resolution timing, performance marks, measures, and observer.
/// </summary>
public partial class RuntimeEmitter
{
    private void EmitPerfHooksMethods(TypeBuilder typeBuilder, EmittedRuntime runtime)
    {
        // Static fields for timing (lazy-initialized on first call)
        var startTicksField = typeBuilder.DefineField(
            "_perfHooksStartTicks",
            _types.Int64,
            FieldAttributes.Private | FieldAttributes.Static
        );
        runtime.PerfHooksStartTicks = startTicksField;

        var ticksPerMsField = typeBuilder.DefineField(
            "_perfHooksTicksPerMs",
            _types.Double,
            FieldAttributes.Private | FieldAttributes.Static
        );
        runtime.PerfHooksTicksPerMs = ticksPerMsField;

        var initializedField = typeBuilder.DefineField(
            "_perfHooksInitialized",
            _types.Boolean,
            FieldAttributes.Private | FieldAttributes.Static
        );

        // Static field for entries storage: List<object?> of $Object entries
        var entriesField = typeBuilder.DefineField(
            "_perfHooksEntries",
            _types.ListOfObjectNullable,
            FieldAttributes.Private | FieldAttributes.Static
        );
        runtime.PerfHooksEntries = entriesField;

        // Static field for observers: List<object?> of object[] { callback, entryTypes HashSet, connected bool }
        var observersField = typeBuilder.DefineField(
            "_perfHooksObservers",
            _types.ListOfObjectNullable,
            FieldAttributes.Private | FieldAttributes.Static
        );
        runtime.PerfHooksObservers = observersField;

        EmitPerfHooksEnsureInitialized(typeBuilder, runtime, startTicksField, ticksPerMsField, initializedField);
        EmitPerfHooksPerformanceNow(typeBuilder, runtime, startTicksField, ticksPerMsField, initializedField);

        // Emit helper to create entry dictionaries
        EmitPerfHooksCreateEntry(typeBuilder, runtime);

        // Emit helper to ensure entries list exists
        EmitPerfHooksEnsureEntries(typeBuilder, runtime, entriesField);

        // Emit helper to notify observers
        EmitPerfHooksNotifyObservers(typeBuilder, runtime, entriesField, observersField);

        // Emit mark/measure/query/clear wrappers
        EmitPerfHooksMarkWrapper(typeBuilder, runtime, entriesField);
        EmitPerfHooksMeasureWrapper(typeBuilder, runtime, entriesField);
        EmitPerfHooksGetEntriesWrapper(typeBuilder, runtime, entriesField);
        EmitPerfHooksGetEntriesByNameWrapper(typeBuilder, runtime, entriesField);
        EmitPerfHooksGetEntriesByTypeWrapper(typeBuilder, runtime, entriesField);
        EmitPerfHooksClearMarksWrapper(typeBuilder, runtime, entriesField);
        EmitPerfHooksClearMeasuresWrapper(typeBuilder, runtime, entriesField);

        // Emit observer constructor
        EmitPerfHooksCreateObserverWrapper(typeBuilder, runtime, observersField);

        // Emit the performance object getter (must be last - depends on all wrappers)
        EmitPerfHooksGetPerformance(typeBuilder, runtime);
    }

    /// <summary>
    /// Emits a helper method to lazily initialize perf_hooks fields.
    /// </summary>
    private MethodBuilder EmitPerfHooksEnsureInitialized(
        TypeBuilder typeBuilder,
        EmittedRuntime runtime,
        FieldBuilder startTicksField,
        FieldBuilder ticksPerMsField,
        FieldBuilder initializedField)
    {
        var method = typeBuilder.DefineMethod(
            "PerfHooksEnsureInitialized",
            MethodAttributes.Private | MethodAttributes.Static,
            null,
            Type.EmptyTypes
        );

        var il = method.GetILGenerator();

        var alreadyInitializedLabel = il.DefineLabel();

        // if (_perfHooksInitialized) return;
        il.Emit(OpCodes.Ldsfld, initializedField);
        il.Emit(OpCodes.Brtrue_S, alreadyInitializedLabel);

        // _perfHooksStartTicks = Stopwatch.GetTimestamp();
        il.Emit(OpCodes.Call, _types.StopwatchGetTimestamp);
        il.Emit(OpCodes.Stsfld, startTicksField);

        // _perfHooksTicksPerMs = Stopwatch.Frequency / 1000.0;
        il.Emit(OpCodes.Ldsfld, typeof(Stopwatch).GetField("Frequency")!);
        il.Emit(OpCodes.Conv_R8);
        il.Emit(OpCodes.Ldc_R8, 1000.0);
        il.Emit(OpCodes.Div);
        il.Emit(OpCodes.Stsfld, ticksPerMsField);

        // _perfHooksInitialized = true;
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Stsfld, initializedField);

        il.MarkLabel(alreadyInitializedLabel);
        il.Emit(OpCodes.Ret);

        return method;
    }

    /// <summary>
    /// Emits PerformanceNow: returns high-resolution time in milliseconds.
    /// Signature: double PerformanceNow()
    /// </summary>
    private void EmitPerfHooksPerformanceNow(
        TypeBuilder typeBuilder,
        EmittedRuntime runtime,
        FieldBuilder startTicksField,
        FieldBuilder ticksPerMsField,
        FieldBuilder initializedField)
    {
        var method = typeBuilder.DefineMethod(
            "PerformanceNow",
            MethodAttributes.Public | MethodAttributes.Static,
            _types.Double,
            Type.EmptyTypes
        );
        runtime.PerfHooksPerformanceNow = method;

        var il = method.GetILGenerator();

        var alreadyInitializedLabel = il.DefineLabel();

        // Lazy init check - inline for performance
        il.Emit(OpCodes.Ldsfld, initializedField);
        il.Emit(OpCodes.Brtrue_S, alreadyInitializedLabel);

        // Initialize if needed
        il.Emit(OpCodes.Call, _types.StopwatchGetTimestamp);
        il.Emit(OpCodes.Stsfld, startTicksField);
        il.Emit(OpCodes.Ldsfld, typeof(Stopwatch).GetField("Frequency")!);
        il.Emit(OpCodes.Conv_R8);
        il.Emit(OpCodes.Ldc_R8, 1000.0);
        il.Emit(OpCodes.Div);
        il.Emit(OpCodes.Stsfld, ticksPerMsField);
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Stsfld, initializedField);

        il.MarkLabel(alreadyInitializedLabel);

        // long elapsed = Stopwatch.GetTimestamp() - _perfHooksStartTicks;
        il.Emit(OpCodes.Call, _types.StopwatchGetTimestamp);
        il.Emit(OpCodes.Ldsfld, startTicksField);
        il.Emit(OpCodes.Sub);

        // return elapsed / _perfHooksTicksPerMs;
        il.Emit(OpCodes.Conv_R8);
        il.Emit(OpCodes.Ldsfld, ticksPerMsField);
        il.Emit(OpCodes.Div);
        il.Emit(OpCodes.Ret);
    }

    /// <summary>
    /// Emits PerfHooksEnsureEntries: ensures the entries list is initialized.
    /// Signature: void PerfHooksEnsureEntries()
    /// </summary>
    private void EmitPerfHooksEnsureEntries(TypeBuilder typeBuilder, EmittedRuntime runtime, FieldBuilder entriesField)
    {
        var method = typeBuilder.DefineMethod(
            "PerfHooksEnsureEntries",
            MethodAttributes.Private | MethodAttributes.Static,
            null,
            Type.EmptyTypes
        );
        runtime.PerfHooksEnsureEntries = method;

        var il = method.GetILGenerator();
        var doneLabel = il.DefineLabel();

        il.Emit(OpCodes.Ldsfld, entriesField);
        il.Emit(OpCodes.Brtrue_S, doneLabel);
        il.Emit(OpCodes.Newobj, _types.ListObjectNullableDefaultCtor);
        il.Emit(OpCodes.Stsfld, entriesField);
        il.MarkLabel(doneLabel);
        il.Emit(OpCodes.Ret);
    }

    /// <summary>
    /// Emits PerfHooksCreateEntry: creates a performance entry as a $Object.
    /// Signature: object PerfHooksCreateEntry(string name, string entryType, double startTime, double duration)
    /// </summary>
    private void EmitPerfHooksCreateEntry(TypeBuilder typeBuilder, EmittedRuntime runtime)
    {
        var method = typeBuilder.DefineMethod(
            "PerfHooksCreateEntry",
            MethodAttributes.Public | MethodAttributes.Static,
            _types.Object,
            [_types.String, _types.String, _types.Double, _types.Double]
        );
        runtime.PerfHooksCreateEntry = method;

        var il = method.GetILGenerator();
        var dictSetItem = _types.GetMethod(_types.DictionaryStringObject, "set_Item", _types.String, _types.Object);

        // var dict = new Dictionary<string, object?>();
        il.Emit(OpCodes.Newobj, _types.GetDefaultConstructor(_types.DictionaryStringObject));
        var dictLocal = il.DeclareLocal(_types.DictionaryStringObject);
        il.Emit(OpCodes.Stloc, dictLocal);

        // dict["name"] = name
        il.Emit(OpCodes.Ldloc, dictLocal);
        il.Emit(OpCodes.Ldstr, "name");
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Callvirt, dictSetItem);

        // dict["entryType"] = entryType
        il.Emit(OpCodes.Ldloc, dictLocal);
        il.Emit(OpCodes.Ldstr, "entryType");
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Callvirt, dictSetItem);

        // dict["startTime"] = startTime (boxed)
        il.Emit(OpCodes.Ldloc, dictLocal);
        il.Emit(OpCodes.Ldstr, "startTime");
        il.Emit(OpCodes.Ldarg_2);
        il.Emit(OpCodes.Box, _types.Double);
        il.Emit(OpCodes.Callvirt, dictSetItem);

        // dict["duration"] = duration (boxed)
        il.Emit(OpCodes.Ldloc, dictLocal);
        il.Emit(OpCodes.Ldstr, "duration");
        il.Emit(OpCodes.Ldarg_3);
        il.Emit(OpCodes.Box, _types.Double);
        il.Emit(OpCodes.Callvirt, dictSetItem);

        // return new $Object(dict)
        il.Emit(OpCodes.Ldloc, dictLocal);
        il.Emit(OpCodes.Newobj, runtime.TSObjectCtor);
        il.Emit(OpCodes.Ret);
    }

    /// <summary>
    /// Emits PerfHooksGetEntryField: gets a string field from a $Object entry.
    /// Used internally to read name/entryType fields for filtering.
    /// Signature: string PerfHooksGetEntryField(object entry, string fieldName)
    /// </summary>
    private MethodBuilder EmitPerfHooksGetEntryField(TypeBuilder typeBuilder, EmittedRuntime runtime)
    {
        var method = typeBuilder.DefineMethod(
            "PerfHooksGetEntryField",
            MethodAttributes.Private | MethodAttributes.Static,
            _types.String,
            [_types.Object, _types.String]
        );

        var il = method.GetILGenerator();
        var notObjectLabel = il.DefineLabel();
        var foundLabel = il.DefineLabel();

        // Cast entry to $Object type, get fields dictionary
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Isinst, runtime.TSObjectType);
        var objLocal = il.DeclareLocal(runtime.TSObjectType);
        il.Emit(OpCodes.Stloc, objLocal);
        il.Emit(OpCodes.Ldloc, objLocal);
        il.Emit(OpCodes.Brfalse, notObjectLabel);

        // Get fields dictionary
        il.Emit(OpCodes.Ldloc, objLocal);
        il.Emit(OpCodes.Callvirt, runtime.TSObjectFieldsGetter);
        var dictLocal = il.DeclareLocal(_types.DictionaryStringObject);
        il.Emit(OpCodes.Stloc, dictLocal);

        // TryGetValue
        il.Emit(OpCodes.Ldloc, dictLocal);
        il.Emit(OpCodes.Ldarg_1);
        var valueLocal = il.DeclareLocal(_types.Object);
        il.Emit(OpCodes.Ldloca, valueLocal);
        il.Emit(OpCodes.Callvirt, _types.GetMethod(_types.DictionaryStringObject, "TryGetValue", _types.String, _types.Object.MakeByRefType()));
        il.Emit(OpCodes.Brfalse, notObjectLabel);

        // Return value as string
        il.Emit(OpCodes.Ldloc, valueLocal);
        il.Emit(OpCodes.Isinst, _types.String);
        il.Emit(OpCodes.Ret);

        il.MarkLabel(notObjectLabel);
        il.Emit(OpCodes.Ldnull);
        il.Emit(OpCodes.Ret);

        return method;
    }

    /// <summary>
    /// Emits PerfHooksGetEntryDouble: gets a double field from a $Object entry.
    /// Signature: double PerfHooksGetEntryDouble(object entry, string fieldName)
    /// </summary>
    private MethodBuilder EmitPerfHooksGetEntryDouble(TypeBuilder typeBuilder, EmittedRuntime runtime)
    {
        var method = typeBuilder.DefineMethod(
            "PerfHooksGetEntryDouble",
            MethodAttributes.Private | MethodAttributes.Static,
            _types.Double,
            [_types.Object, _types.String]
        );

        var il = method.GetILGenerator();
        var notFoundLabel = il.DefineLabel();

        // Cast entry to $Object type
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Isinst, runtime.TSObjectType);
        var objLocal = il.DeclareLocal(runtime.TSObjectType);
        il.Emit(OpCodes.Stloc, objLocal);
        il.Emit(OpCodes.Ldloc, objLocal);
        il.Emit(OpCodes.Brfalse, notFoundLabel);

        // Get fields dictionary and TryGetValue
        il.Emit(OpCodes.Ldloc, objLocal);
        il.Emit(OpCodes.Callvirt, runtime.TSObjectFieldsGetter);
        il.Emit(OpCodes.Ldarg_1);
        var valueLocal = il.DeclareLocal(_types.Object);
        il.Emit(OpCodes.Ldloca, valueLocal);
        il.Emit(OpCodes.Callvirt, _types.GetMethod(_types.DictionaryStringObject, "TryGetValue", _types.String, _types.Object.MakeByRefType()));
        il.Emit(OpCodes.Brfalse, notFoundLabel);

        // Unbox double
        il.Emit(OpCodes.Ldloc, valueLocal);
        il.Emit(OpCodes.Isinst, typeof(double));
        il.Emit(OpCodes.Brfalse, notFoundLabel);
        il.Emit(OpCodes.Ldloc, valueLocal);
        il.Emit(OpCodes.Unbox_Any, _types.Double);
        il.Emit(OpCodes.Ret);

        il.MarkLabel(notFoundLabel);
        il.Emit(OpCodes.Ldc_R8, 0.0);
        il.Emit(OpCodes.Ret);

        return method;
    }

    /// <summary>
    /// Emits PerfHooksFindMark: finds the most recent mark with the given name.
    /// Signature: object PerfHooksFindMark(string name)
    /// </summary>
    private MethodBuilder EmitPerfHooksFindMark(TypeBuilder typeBuilder, EmittedRuntime runtime,
        FieldBuilder entriesField, MethodBuilder getEntryField)
    {
        var method = typeBuilder.DefineMethod(
            "PerfHooksFindMark",
            MethodAttributes.Private | MethodAttributes.Static,
            _types.Object,
            [_types.String]
        );

        var il = method.GetILGenerator();
        var listCountProp = _types.GetProperty(_types.ListOfObjectNullable, "Count");
        var listItemProp = _types.GetProperty(_types.ListOfObjectNullable, "Item");
        var stringEquals = _types.GetMethod(_types.String, "op_Equality", _types.String, _types.String);

        var notFoundLabel = il.DefineLabel();
        var loopStartLabel = il.DefineLabel();
        var loopBodyLabel = il.DefineLabel();
        var continueLabel = il.DefineLabel();

        // Check if entries exist
        il.Emit(OpCodes.Ldsfld, entriesField);
        il.Emit(OpCodes.Brfalse, notFoundLabel);

        // int i = entries.Count - 1
        var iLocal = il.DeclareLocal(_types.Int32);
        il.Emit(OpCodes.Ldsfld, entriesField);
        il.Emit(OpCodes.Callvirt, listCountProp.GetGetMethod()!);
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Sub);
        il.Emit(OpCodes.Stloc, iLocal);
        il.Emit(OpCodes.Br, loopStartLabel);

        // Loop body
        il.MarkLabel(loopBodyLabel);
        var entryLocal = il.DeclareLocal(_types.Object);
        il.Emit(OpCodes.Ldsfld, entriesField);
        il.Emit(OpCodes.Ldloc, iLocal);
        il.Emit(OpCodes.Callvirt, listItemProp.GetGetMethod()!);
        il.Emit(OpCodes.Stloc, entryLocal);

        // Check entryType == "mark"
        il.Emit(OpCodes.Ldloc, entryLocal);
        il.Emit(OpCodes.Ldstr, "entryType");
        il.Emit(OpCodes.Call, getEntryField);
        il.Emit(OpCodes.Ldstr, "mark");
        il.Emit(OpCodes.Call, stringEquals);
        il.Emit(OpCodes.Brfalse, continueLabel);

        // Check name matches
        il.Emit(OpCodes.Ldloc, entryLocal);
        il.Emit(OpCodes.Ldstr, "name");
        il.Emit(OpCodes.Call, getEntryField);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Call, stringEquals);
        il.Emit(OpCodes.Brfalse, continueLabel);

        // Found it - return entry
        il.Emit(OpCodes.Ldloc, entryLocal);
        il.Emit(OpCodes.Ret);

        // i--
        il.MarkLabel(continueLabel);
        il.Emit(OpCodes.Ldloc, iLocal);
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Sub);
        il.Emit(OpCodes.Stloc, iLocal);

        // while (i >= 0)
        il.MarkLabel(loopStartLabel);
        il.Emit(OpCodes.Ldloc, iLocal);
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Bge, loopBodyLabel);

        il.MarkLabel(notFoundLabel);
        il.Emit(OpCodes.Ldnull);
        il.Emit(OpCodes.Ret);

        return method;
    }

    /// <summary>
    /// Emits PerfHooksNotifyObservers: notifies registered observers of a new entry.
    /// Signature: void PerfHooksNotifyObservers(object entry, string entryType)
    /// </summary>
    private void EmitPerfHooksNotifyObservers(TypeBuilder typeBuilder, EmittedRuntime runtime,
        FieldBuilder entriesField, FieldBuilder observersField)
    {
        var method = typeBuilder.DefineMethod(
            "PerfHooksNotifyObservers",
            MethodAttributes.Private | MethodAttributes.Static,
            null,
            [_types.Object, _types.String]
        );
        runtime.PerfHooksNotifyObservers = method;

        var il = method.GetILGenerator();
        var listCountProp = _types.GetProperty(_types.ListOfObjectNullable, "Count");
        var listItemProp = _types.GetProperty(_types.ListOfObjectNullable, "Item");
        var hashSetContains = typeof(HashSet<string>).GetMethod("Contains", [typeof(string)])!;

        var retLabel = il.DefineLabel();
        var loopStartLabel = il.DefineLabel();
        var loopBodyLabel = il.DefineLabel();
        var continueLabel = il.DefineLabel();

        // if (_perfHooksObservers == null) return;
        il.Emit(OpCodes.Ldsfld, observersField);
        il.Emit(OpCodes.Brfalse, retLabel);

        // int i = 0
        var iLocal = il.DeclareLocal(_types.Int32);
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Stloc, iLocal);
        il.Emit(OpCodes.Br, loopStartLabel);

        il.MarkLabel(loopBodyLabel);
        // var obs = observers[i] as object[]
        il.Emit(OpCodes.Ldsfld, observersField);
        il.Emit(OpCodes.Ldloc, iLocal);
        il.Emit(OpCodes.Callvirt, listItemProp.GetGetMethod()!);
        il.Emit(OpCodes.Isinst, _types.ObjectArray);
        var obsLocal = il.DeclareLocal(_types.ObjectArray);
        il.Emit(OpCodes.Stloc, obsLocal);
        il.Emit(OpCodes.Ldloc, obsLocal);
        il.Emit(OpCodes.Brfalse, continueLabel);

        // Check connected: obs[2] as bool
        il.Emit(OpCodes.Ldloc, obsLocal);
        il.Emit(OpCodes.Ldc_I4_2);
        il.Emit(OpCodes.Ldelem_Ref);
        il.Emit(OpCodes.Isinst, typeof(bool));
        il.Emit(OpCodes.Brfalse, continueLabel);
        il.Emit(OpCodes.Ldloc, obsLocal);
        il.Emit(OpCodes.Ldc_I4_2);
        il.Emit(OpCodes.Ldelem_Ref);
        il.Emit(OpCodes.Unbox_Any, _types.Boolean);
        il.Emit(OpCodes.Brfalse, continueLabel);

        // Check entryTypes contains this type: ((HashSet<string>)obs[1]).Contains(entryType)
        il.Emit(OpCodes.Ldloc, obsLocal);
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Ldelem_Ref);
        il.Emit(OpCodes.Isinst, typeof(HashSet<string>));
        var hashSetLocal = il.DeclareLocal(typeof(HashSet<string>));
        il.Emit(OpCodes.Stloc, hashSetLocal);
        il.Emit(OpCodes.Ldloc, hashSetLocal);
        il.Emit(OpCodes.Brfalse, continueLabel);
        il.Emit(OpCodes.Ldloc, hashSetLocal);
        il.Emit(OpCodes.Ldarg_1); // entryType
        il.Emit(OpCodes.Callvirt, hashSetContains);
        il.Emit(OpCodes.Brfalse, continueLabel);

        // Build entryList: create a List<object?> with this single entry, wrap in $Array representation
        // Then create a wrapper object with getEntries() method
        // Create args array for callback: [entryListObj]
        // Call callback via InvokeValue

        // Create entry list: new List<object?> { entry }
        il.Emit(OpCodes.Newobj, _types.ListObjectNullableDefaultCtor);
        var entryListLocal = il.DeclareLocal(_types.ListOfObjectNullable);
        il.Emit(OpCodes.Stloc, entryListLocal);
        il.Emit(OpCodes.Ldloc, entryListLocal);
        il.Emit(OpCodes.Ldarg_0); // entry
        il.Emit(OpCodes.Callvirt, _types.GetMethod(_types.ListOfObjectNullable, "Add", _types.Object));

        // Create getEntries wrapper as $TSFunction
        // We need a method that returns entryListLocal. But we can't capture locals in IL.
        // Instead, create a dictionary with "getEntries" returning the array directly.
        var dictSetItem = _types.GetMethod(_types.DictionaryStringObject, "set_Item", _types.String, _types.Object);
        il.Emit(OpCodes.Newobj, _types.GetDefaultConstructor(_types.DictionaryStringObject));
        var listDictLocal = il.DeclareLocal(_types.DictionaryStringObject);
        il.Emit(OpCodes.Stloc, listDictLocal);
        il.Emit(OpCodes.Ldloc, listDictLocal);
        il.Emit(OpCodes.Ldstr, "getEntries");
        // Store the entries array directly - when called, GetFieldsProperty will return it
        // and InvokeMethodValue will handle it as a callable (List<object?> is not callable though)
        // Better approach: store the list as-is and let consumer access it
        il.Emit(OpCodes.Ldloc, entryListLocal);
        il.Emit(OpCodes.Callvirt, dictSetItem);

        // Wrap in $Object
        il.Emit(OpCodes.Ldloc, listDictLocal);
        il.Emit(OpCodes.Newobj, runtime.TSObjectCtor);
        var entryListObjLocal = il.DeclareLocal(_types.Object);
        il.Emit(OpCodes.Stloc, entryListObjLocal);

        // Call callback: obs[0] is the callback function
        // Create args array: [entryListObj]
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Newarr, _types.Object);
        il.Emit(OpCodes.Dup);
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Ldloc, entryListObjLocal);
        il.Emit(OpCodes.Stelem_Ref);

        // Call InvokeValue(callback, argsArray)
        // Stack at this point: [arr]
        var argsLocal = il.DeclareLocal(_types.ObjectArray);
        il.Emit(OpCodes.Stloc, argsLocal); // store arr → argsLocal

        // Get callback: obs[0]
        il.Emit(OpCodes.Ldloc, obsLocal);
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Ldelem_Ref);
        // Stack: [callback]

        il.Emit(OpCodes.Ldloc, argsLocal);
        // Stack: [callback, arr]
        il.Emit(OpCodes.Call, runtime.InvokeValue);
        il.Emit(OpCodes.Pop); // discard return value

        il.MarkLabel(continueLabel);
        il.Emit(OpCodes.Ldloc, iLocal);
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Add);
        il.Emit(OpCodes.Stloc, iLocal);

        il.MarkLabel(loopStartLabel);
        il.Emit(OpCodes.Ldloc, iLocal);
        il.Emit(OpCodes.Ldsfld, observersField);
        il.Emit(OpCodes.Callvirt, listCountProp.GetGetMethod()!);
        il.Emit(OpCodes.Blt, loopBodyLabel);

        il.MarkLabel(retLabel);
        il.Emit(OpCodes.Ret);
    }

    /// <summary>
    /// Emits PerfHooksMarkWrapper: performance.mark(name, options?).
    /// Signature: object PerfHooksMarkWrapper(object[] args)
    /// </summary>
    private void EmitPerfHooksMarkWrapper(TypeBuilder typeBuilder, EmittedRuntime runtime, FieldBuilder entriesField)
    {
        var getEntryField = EmitPerfHooksGetEntryField(typeBuilder, runtime);
        var getEntryDouble = EmitPerfHooksGetEntryDouble(typeBuilder, runtime);
        var findMark = EmitPerfHooksFindMark(typeBuilder, runtime, entriesField, getEntryField);
        runtime.PerfHooksGetEntryField = getEntryField;
        runtime.PerfHooksGetEntryDouble = getEntryDouble;
        runtime.PerfHooksFindMark = findMark;

        var method = typeBuilder.DefineMethod(
            "PerfHooksMarkWrapper",
            MethodAttributes.Public | MethodAttributes.Static,
            _types.Object,
            [_types.ObjectArray]
        );
        runtime.PerfHooksMark = method;

        var il = method.GetILGenerator();
        var dictTryGetValue = _types.GetMethod(_types.DictionaryStringObject, "TryGetValue", _types.String, _types.Object.MakeByRefType());
        var noOptionsLabel = il.DefineLabel();
        var createEntryLabel = il.DefineLabel();

        // Ensure entries list
        il.Emit(OpCodes.Call, runtime.PerfHooksEnsureEntries);

        // string name = args[0] as string ?? ""
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Ldelem_Ref);
        il.Emit(OpCodes.Isinst, _types.String);
        var nameLocal = il.DeclareLocal(_types.String);
        il.Emit(OpCodes.Dup);
        il.Emit(OpCodes.Stloc, nameLocal);
        il.Emit(OpCodes.Brtrue_S, noOptionsLabel);
        il.Emit(OpCodes.Ldstr, "");
        il.Emit(OpCodes.Stloc, nameLocal);

        il.MarkLabel(noOptionsLabel);

        // double startTime = PerformanceNow()
        il.Emit(OpCodes.Call, runtime.PerfHooksPerformanceNow);
        var startTimeLocal = il.DeclareLocal(_types.Double);
        il.Emit(OpCodes.Stloc, startTimeLocal);

        // Check for options.startTime: if args.Length > 1 && args[1] is $Object
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldlen);
        il.Emit(OpCodes.Conv_I4);
        il.Emit(OpCodes.Ldc_I4_2);
        il.Emit(OpCodes.Blt, createEntryLabel);

        // Get options dictionary: try $Object.Fields first, then raw Dictionary
        var markOptsDictLocal = il.DeclareLocal(_types.DictionaryStringObject);
        var tryMarkDictLabel = il.DefineLabel();
        var haveMarkDictLabel = il.DefineLabel();

        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Ldelem_Ref);
        il.Emit(OpCodes.Isinst, runtime.TSObjectType);
        var optsLocal = il.DeclareLocal(runtime.TSObjectType);
        il.Emit(OpCodes.Stloc, optsLocal);
        il.Emit(OpCodes.Ldloc, optsLocal);
        il.Emit(OpCodes.Brfalse, tryMarkDictLabel);
        il.Emit(OpCodes.Ldloc, optsLocal);
        il.Emit(OpCodes.Callvirt, runtime.TSObjectFieldsGetter);
        il.Emit(OpCodes.Stloc, markOptsDictLocal);
        il.Emit(OpCodes.Br, haveMarkDictLabel);

        il.MarkLabel(tryMarkDictLabel);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Ldelem_Ref);
        il.Emit(OpCodes.Isinst, _types.DictionaryStringObject);
        il.Emit(OpCodes.Stloc, markOptsDictLocal);
        il.Emit(OpCodes.Ldloc, markOptsDictLocal);
        il.Emit(OpCodes.Brfalse, createEntryLabel);

        il.MarkLabel(haveMarkDictLabel);

        // Get opts["startTime"]
        il.Emit(OpCodes.Ldloc, markOptsDictLocal);
        il.Emit(OpCodes.Ldstr, "startTime");
        var stValueLocal = il.DeclareLocal(_types.Object);
        il.Emit(OpCodes.Ldloca, stValueLocal);
        il.Emit(OpCodes.Callvirt, dictTryGetValue);
        il.Emit(OpCodes.Brfalse, createEntryLabel);
        il.Emit(OpCodes.Ldloc, stValueLocal);
        il.Emit(OpCodes.Isinst, typeof(double));
        il.Emit(OpCodes.Brfalse, createEntryLabel);
        il.Emit(OpCodes.Ldloc, stValueLocal);
        il.Emit(OpCodes.Unbox_Any, _types.Double);
        il.Emit(OpCodes.Stloc, startTimeLocal);

        il.MarkLabel(createEntryLabel);

        // Create entry
        il.Emit(OpCodes.Ldloc, nameLocal);
        il.Emit(OpCodes.Ldstr, "mark");
        il.Emit(OpCodes.Ldloc, startTimeLocal);
        il.Emit(OpCodes.Ldc_R8, 0.0);
        il.Emit(OpCodes.Call, runtime.PerfHooksCreateEntry);
        var entryLocal = il.DeclareLocal(_types.Object);
        il.Emit(OpCodes.Stloc, entryLocal);

        // Add to entries list
        il.Emit(OpCodes.Ldsfld, entriesField);
        il.Emit(OpCodes.Ldloc, entryLocal);
        il.Emit(OpCodes.Callvirt, _types.GetMethod(_types.ListOfObjectNullable, "Add", _types.Object));

        // Notify observers
        il.Emit(OpCodes.Ldloc, entryLocal);
        il.Emit(OpCodes.Ldstr, "mark");
        il.Emit(OpCodes.Call, runtime.PerfHooksNotifyObservers);

        // Return entry
        il.Emit(OpCodes.Ldloc, entryLocal);
        il.Emit(OpCodes.Ret);
    }

    /// <summary>
    /// Emits PerfHooksMeasureWrapper: performance.measure(name, startMark?, endMark?).
    /// Signature: object PerfHooksMeasureWrapper(object[] args)
    /// </summary>
    private void EmitPerfHooksMeasureWrapper(TypeBuilder typeBuilder, EmittedRuntime runtime, FieldBuilder entriesField)
    {
        var method = typeBuilder.DefineMethod(
            "PerfHooksMeasureWrapper",
            MethodAttributes.Public | MethodAttributes.Static,
            _types.Object,
            [_types.ObjectArray]
        );
        runtime.PerfHooksMeasure = method;

        var il = method.GetILGenerator();
        var noStartMarkLabel = il.DefineLabel();
        var checkEndMarkLabel = il.DefineLabel();
        var noEndMarkLabel = il.DefineLabel();
        var createLabel = il.DefineLabel();

        // Ensure entries list
        il.Emit(OpCodes.Call, runtime.PerfHooksEnsureEntries);

        // string name = args[0] as string ?? ""
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Ldelem_Ref);
        il.Emit(OpCodes.Isinst, _types.String);
        var nameLocal = il.DeclareLocal(_types.String);
        il.Emit(OpCodes.Dup);
        il.Emit(OpCodes.Stloc, nameLocal);
        var hasNameLabel = il.DefineLabel();
        il.Emit(OpCodes.Brtrue_S, hasNameLabel);
        il.Emit(OpCodes.Ldstr, "");
        il.Emit(OpCodes.Stloc, nameLocal);
        il.MarkLabel(hasNameLabel);

        // double startTime = 0, endTime = PerformanceNow()
        var startTimeLocal = il.DeclareLocal(_types.Double);
        il.Emit(OpCodes.Ldc_R8, 0.0);
        il.Emit(OpCodes.Stloc, startTimeLocal);
        var endTimeLocal = il.DeclareLocal(_types.Double);
        il.Emit(OpCodes.Call, runtime.PerfHooksPerformanceNow);
        il.Emit(OpCodes.Stloc, endTimeLocal);

        // Check for startMark: args.Length > 1 && args[1] is string
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldlen);
        il.Emit(OpCodes.Conv_I4);
        il.Emit(OpCodes.Ldc_I4_2);
        il.Emit(OpCodes.Blt, checkEndMarkLabel);

        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Ldelem_Ref);
        il.Emit(OpCodes.Isinst, _types.String);
        var startMarkNameLocal = il.DeclareLocal(_types.String);
        il.Emit(OpCodes.Stloc, startMarkNameLocal);
        il.Emit(OpCodes.Ldloc, startMarkNameLocal);
        il.Emit(OpCodes.Brfalse, checkEndMarkLabel);

        // Find start mark and get its startTime
        il.Emit(OpCodes.Ldloc, startMarkNameLocal);
        il.Emit(OpCodes.Call, runtime.PerfHooksFindMark);
        var startMarkLocal = il.DeclareLocal(_types.Object);
        il.Emit(OpCodes.Stloc, startMarkLocal);
        il.Emit(OpCodes.Ldloc, startMarkLocal);
        il.Emit(OpCodes.Brfalse, checkEndMarkLabel);
        il.Emit(OpCodes.Ldloc, startMarkLocal);
        il.Emit(OpCodes.Ldstr, "startTime");
        il.Emit(OpCodes.Call, runtime.PerfHooksGetEntryDouble);
        il.Emit(OpCodes.Stloc, startTimeLocal);

        // Check for endMark: args.Length > 2 && args[2] is string
        il.MarkLabel(checkEndMarkLabel);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldlen);
        il.Emit(OpCodes.Conv_I4);
        il.Emit(OpCodes.Ldc_I4_3);
        il.Emit(OpCodes.Blt, createLabel);

        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldc_I4_2);
        il.Emit(OpCodes.Ldelem_Ref);
        il.Emit(OpCodes.Isinst, _types.String);
        var endMarkNameLocal = il.DeclareLocal(_types.String);
        il.Emit(OpCodes.Stloc, endMarkNameLocal);
        il.Emit(OpCodes.Ldloc, endMarkNameLocal);
        il.Emit(OpCodes.Brfalse, createLabel);

        il.Emit(OpCodes.Ldloc, endMarkNameLocal);
        il.Emit(OpCodes.Call, runtime.PerfHooksFindMark);
        var endMarkLocal = il.DeclareLocal(_types.Object);
        il.Emit(OpCodes.Stloc, endMarkLocal);
        il.Emit(OpCodes.Ldloc, endMarkLocal);
        il.Emit(OpCodes.Brfalse, createLabel);
        il.Emit(OpCodes.Ldloc, endMarkLocal);
        il.Emit(OpCodes.Ldstr, "startTime");
        il.Emit(OpCodes.Call, runtime.PerfHooksGetEntryDouble);
        il.Emit(OpCodes.Stloc, endTimeLocal);

        il.MarkLabel(createLabel);

        // duration = endTime - startTime
        il.Emit(OpCodes.Ldloc, nameLocal);
        il.Emit(OpCodes.Ldstr, "measure");
        il.Emit(OpCodes.Ldloc, startTimeLocal);
        il.Emit(OpCodes.Ldloc, endTimeLocal);
        il.Emit(OpCodes.Ldloc, startTimeLocal);
        il.Emit(OpCodes.Sub); // duration = endTime - startTime
        il.Emit(OpCodes.Call, runtime.PerfHooksCreateEntry);
        var entryLocal = il.DeclareLocal(_types.Object);
        il.Emit(OpCodes.Stloc, entryLocal);

        // Add to entries list
        il.Emit(OpCodes.Ldsfld, entriesField);
        il.Emit(OpCodes.Ldloc, entryLocal);
        il.Emit(OpCodes.Callvirt, _types.GetMethod(_types.ListOfObjectNullable, "Add", _types.Object));

        // Notify observers
        il.Emit(OpCodes.Ldloc, entryLocal);
        il.Emit(OpCodes.Ldstr, "measure");
        il.Emit(OpCodes.Call, runtime.PerfHooksNotifyObservers);

        il.Emit(OpCodes.Ldloc, entryLocal);
        il.Emit(OpCodes.Ret);
    }

    /// <summary>
    /// Emits PerfHooksGetEntriesWrapper: performance.getEntries().
    /// Signature: object PerfHooksGetEntriesWrapper(object[] args)
    /// </summary>
    private void EmitPerfHooksGetEntriesWrapper(TypeBuilder typeBuilder, EmittedRuntime runtime, FieldBuilder entriesField)
    {
        var method = typeBuilder.DefineMethod(
            "PerfHooksGetEntriesWrapper",
            MethodAttributes.Public | MethodAttributes.Static,
            _types.Object,
            [_types.ObjectArray]
        );
        runtime.PerfHooksGetEntries = method;

        var il = method.GetILGenerator();
        var emptyLabel = il.DefineLabel();

        // if entries == null, return empty list
        il.Emit(OpCodes.Ldsfld, entriesField);
        il.Emit(OpCodes.Brfalse, emptyLabel);

        // Return new List<object?>(entries)
        il.Emit(OpCodes.Ldsfld, entriesField);
        il.Emit(OpCodes.Newobj, _types.ListObjectFromEnumerableCtor);
        il.Emit(OpCodes.Ret);

        il.MarkLabel(emptyLabel);
        il.Emit(OpCodes.Newobj, _types.ListObjectNullableDefaultCtor);
        il.Emit(OpCodes.Ret);
    }

    /// <summary>
    /// Emits a generic filter method for entries.
    /// Signature: object PerfHooksFilterEntries(object[] args)
    /// args[0] = name to match (or null to skip), args[1] = type to match (or null to skip)
    /// </summary>
    private MethodBuilder EmitPerfHooksFilterEntries(TypeBuilder typeBuilder, EmittedRuntime runtime, FieldBuilder entriesField)
    {
        var method = typeBuilder.DefineMethod(
            "PerfHooksFilterEntries",
            MethodAttributes.Private | MethodAttributes.Static,
            _types.Object,
            [_types.String, _types.String] // filterName, filterType
        );
        runtime.PerfHooksFilterEntries = method;

        var il = method.GetILGenerator();
        var listCountProp = _types.GetProperty(_types.ListOfObjectNullable, "Count");
        var listItemProp = _types.GetProperty(_types.ListOfObjectNullable, "Item");
        var stringEquals = _types.GetMethod(_types.String, "op_Equality", _types.String, _types.String);

        var emptyLabel = il.DefineLabel();
        var loopStartLabel = il.DefineLabel();
        var loopBodyLabel = il.DefineLabel();
        var continueLabel = il.DefineLabel();
        var checkTypeLabel = il.DefineLabel();
        var addLabel = il.DefineLabel();

        // result = new List<object?>()
        il.Emit(OpCodes.Newobj, _types.ListObjectNullableDefaultCtor);
        var resultLocal = il.DeclareLocal(_types.ListOfObjectNullable);
        il.Emit(OpCodes.Stloc, resultLocal);

        // if entries == null, return empty
        il.Emit(OpCodes.Ldsfld, entriesField);
        il.Emit(OpCodes.Brfalse, emptyLabel);

        // for (int i = 0; i < entries.Count; i++)
        var iLocal = il.DeclareLocal(_types.Int32);
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Stloc, iLocal);
        il.Emit(OpCodes.Br, loopStartLabel);

        il.MarkLabel(loopBodyLabel);
        var entryLocal = il.DeclareLocal(_types.Object);
        il.Emit(OpCodes.Ldsfld, entriesField);
        il.Emit(OpCodes.Ldloc, iLocal);
        il.Emit(OpCodes.Callvirt, listItemProp.GetGetMethod()!);
        il.Emit(OpCodes.Stloc, entryLocal);

        // Check name filter
        il.Emit(OpCodes.Ldarg_0); // filterName
        il.Emit(OpCodes.Brfalse, checkTypeLabel); // null = skip name check

        il.Emit(OpCodes.Ldloc, entryLocal);
        il.Emit(OpCodes.Ldstr, "name");
        il.Emit(OpCodes.Call, runtime.PerfHooksGetEntryField);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Call, stringEquals);
        il.Emit(OpCodes.Brfalse, continueLabel);

        // Check type filter
        il.MarkLabel(checkTypeLabel);
        il.Emit(OpCodes.Ldarg_1); // filterType
        il.Emit(OpCodes.Brfalse, addLabel); // null = skip type check

        il.Emit(OpCodes.Ldloc, entryLocal);
        il.Emit(OpCodes.Ldstr, "entryType");
        il.Emit(OpCodes.Call, runtime.PerfHooksGetEntryField);
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Call, stringEquals);
        il.Emit(OpCodes.Brfalse, continueLabel);

        // Add to result
        il.MarkLabel(addLabel);
        il.Emit(OpCodes.Ldloc, resultLocal);
        il.Emit(OpCodes.Ldloc, entryLocal);
        il.Emit(OpCodes.Callvirt, _types.GetMethod(_types.ListOfObjectNullable, "Add", _types.Object));

        il.MarkLabel(continueLabel);
        il.Emit(OpCodes.Ldloc, iLocal);
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Add);
        il.Emit(OpCodes.Stloc, iLocal);

        il.MarkLabel(loopStartLabel);
        il.Emit(OpCodes.Ldloc, iLocal);
        il.Emit(OpCodes.Ldsfld, entriesField);
        il.Emit(OpCodes.Callvirt, listCountProp.GetGetMethod()!);
        il.Emit(OpCodes.Blt, loopBodyLabel);

        il.MarkLabel(emptyLabel);
        il.Emit(OpCodes.Ldloc, resultLocal);
        il.Emit(OpCodes.Ret);

        return method;
    }

    /// <summary>
    /// Emits PerfHooksGetEntriesByNameWrapper: performance.getEntriesByName(name, type?).
    /// Signature: object PerfHooksGetEntriesByNameWrapper(object[] args)
    /// </summary>
    private void EmitPerfHooksGetEntriesByNameWrapper(TypeBuilder typeBuilder, EmittedRuntime runtime, FieldBuilder entriesField)
    {
        var filterMethod = EmitPerfHooksFilterEntries(typeBuilder, runtime, entriesField);

        var method = typeBuilder.DefineMethod(
            "PerfHooksGetEntriesByNameWrapper",
            MethodAttributes.Public | MethodAttributes.Static,
            _types.Object,
            [_types.ObjectArray]
        );
        runtime.PerfHooksGetEntriesByName = method;

        var il = method.GetILGenerator();
        var noTypeLabel = il.DefineLabel();

        // name = args[0] as string
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Ldelem_Ref);
        il.Emit(OpCodes.Isinst, _types.String);
        var nameLocal = il.DeclareLocal(_types.String);
        il.Emit(OpCodes.Stloc, nameLocal);

        // type = args.Length > 1 ? args[1] as string : null
        var typeLocal = il.DeclareLocal(_types.String);
        il.Emit(OpCodes.Ldnull);
        il.Emit(OpCodes.Stloc, typeLocal);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldlen);
        il.Emit(OpCodes.Conv_I4);
        il.Emit(OpCodes.Ldc_I4_2);
        il.Emit(OpCodes.Blt, noTypeLabel);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Ldelem_Ref);
        il.Emit(OpCodes.Isinst, _types.String);
        il.Emit(OpCodes.Stloc, typeLocal);

        il.MarkLabel(noTypeLabel);
        il.Emit(OpCodes.Ldloc, nameLocal);
        il.Emit(OpCodes.Ldloc, typeLocal);
        il.Emit(OpCodes.Call, filterMethod);
        il.Emit(OpCodes.Ret);
    }

    /// <summary>
    /// Emits PerfHooksGetEntriesByTypeWrapper: performance.getEntriesByType(type).
    /// Signature: object PerfHooksGetEntriesByTypeWrapper(object[] args)
    /// </summary>
    private void EmitPerfHooksGetEntriesByTypeWrapper(TypeBuilder typeBuilder, EmittedRuntime runtime, FieldBuilder entriesField)
    {
        var method = typeBuilder.DefineMethod(
            "PerfHooksGetEntriesByTypeWrapper",
            MethodAttributes.Public | MethodAttributes.Static,
            _types.Object,
            [_types.ObjectArray]
        );
        runtime.PerfHooksGetEntriesByType = method;

        var il = method.GetILGenerator();

        // FilterEntries(null, args[0] as string)
        il.Emit(OpCodes.Ldnull); // no name filter
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Ldelem_Ref);
        il.Emit(OpCodes.Isinst, _types.String);
        // Reuse PerfHooksFilterEntries - but we need to call it. It should already be emitted.
        // Actually we need to reference the filter method. Let me store it on runtime.
        // For now, inline: call the same filter method
        il.Emit(OpCodes.Call, runtime.PerfHooksFilterEntries);
        il.Emit(OpCodes.Ret);
    }

    /// <summary>
    /// Emits a generic clear method for entries.
    /// Signature: void PerfHooksClearByType(string entryType, string name)
    /// name can be null to clear all of that type.
    /// </summary>
    private MethodBuilder EmitPerfHooksClearByType(TypeBuilder typeBuilder, EmittedRuntime runtime, FieldBuilder entriesField)
    {
        var method = typeBuilder.DefineMethod(
            "PerfHooksClearByType",
            MethodAttributes.Private | MethodAttributes.Static,
            null,
            [_types.String, _types.String] // entryType, name (nullable)
        );

        var il = method.GetILGenerator();
        var listCountProp = _types.GetProperty(_types.ListOfObjectNullable, "Count");
        var listItemProp = _types.GetProperty(_types.ListOfObjectNullable, "Item");
        var listRemoveAt = _types.GetMethod(_types.ListOfObjectNullable, "RemoveAt", _types.Int32);
        var stringEquals = _types.GetMethod(_types.String, "op_Equality", _types.String, _types.String);

        var retLabel = il.DefineLabel();
        var loopStartLabel = il.DefineLabel();
        var loopBodyLabel = il.DefineLabel();
        var skipLabel = il.DefineLabel();
        var checkNameLabel = il.DefineLabel();

        // if entries == null, return
        il.Emit(OpCodes.Ldsfld, entriesField);
        il.Emit(OpCodes.Brfalse, retLabel);

        // Iterate backwards to safely remove
        var iLocal = il.DeclareLocal(_types.Int32);
        il.Emit(OpCodes.Ldsfld, entriesField);
        il.Emit(OpCodes.Callvirt, listCountProp.GetGetMethod()!);
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Sub);
        il.Emit(OpCodes.Stloc, iLocal);
        il.Emit(OpCodes.Br, loopStartLabel);

        il.MarkLabel(loopBodyLabel);
        var entryLocal = il.DeclareLocal(_types.Object);
        il.Emit(OpCodes.Ldsfld, entriesField);
        il.Emit(OpCodes.Ldloc, iLocal);
        il.Emit(OpCodes.Callvirt, listItemProp.GetGetMethod()!);
        il.Emit(OpCodes.Stloc, entryLocal);

        // Check entryType matches
        il.Emit(OpCodes.Ldloc, entryLocal);
        il.Emit(OpCodes.Ldstr, "entryType");
        il.Emit(OpCodes.Call, runtime.PerfHooksGetEntryField);
        il.Emit(OpCodes.Ldarg_0); // entryType to match
        il.Emit(OpCodes.Call, stringEquals);
        il.Emit(OpCodes.Brfalse, skipLabel);

        // Check name filter if provided
        il.Emit(OpCodes.Ldarg_1); // name
        il.Emit(OpCodes.Brfalse, checkNameLabel); // null = remove all of this type

        il.Emit(OpCodes.Ldloc, entryLocal);
        il.Emit(OpCodes.Ldstr, "name");
        il.Emit(OpCodes.Call, runtime.PerfHooksGetEntryField);
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Call, stringEquals);
        il.Emit(OpCodes.Brfalse, skipLabel);

        // Remove this entry
        il.MarkLabel(checkNameLabel);
        il.Emit(OpCodes.Ldsfld, entriesField);
        il.Emit(OpCodes.Ldloc, iLocal);
        il.Emit(OpCodes.Callvirt, listRemoveAt);

        il.MarkLabel(skipLabel);
        il.Emit(OpCodes.Ldloc, iLocal);
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Sub);
        il.Emit(OpCodes.Stloc, iLocal);

        il.MarkLabel(loopStartLabel);
        il.Emit(OpCodes.Ldloc, iLocal);
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Bge, loopBodyLabel);

        il.MarkLabel(retLabel);
        il.Emit(OpCodes.Ret);

        return method;
    }

    /// <summary>
    /// Emits PerfHooksClearMarksWrapper: performance.clearMarks(name?).
    /// Signature: object PerfHooksClearMarksWrapper(object[] args)
    /// </summary>
    private void EmitPerfHooksClearMarksWrapper(TypeBuilder typeBuilder, EmittedRuntime runtime, FieldBuilder entriesField)
    {
        var clearMethod = EmitPerfHooksClearByType(typeBuilder, runtime, entriesField);
        runtime.PerfHooksClearByType = clearMethod;

        var method = typeBuilder.DefineMethod(
            "PerfHooksClearMarksWrapper",
            MethodAttributes.Public | MethodAttributes.Static,
            _types.Object,
            [_types.ObjectArray]
        );
        runtime.PerfHooksClearMarks = method;

        var il = method.GetILGenerator();
        var noNameLabel = il.DefineLabel();

        // entryType = "mark"
        il.Emit(OpCodes.Ldstr, "mark");

        // name = args.Length > 0 ? args[0] as string : null
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldlen);
        il.Emit(OpCodes.Conv_I4);
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Blt, noNameLabel);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Ldelem_Ref);
        il.Emit(OpCodes.Isinst, _types.String);
        il.Emit(OpCodes.Call, clearMethod);
        il.Emit(OpCodes.Ldnull);
        il.Emit(OpCodes.Ret);

        il.MarkLabel(noNameLabel);
        il.Emit(OpCodes.Ldnull); // no name filter
        il.Emit(OpCodes.Call, clearMethod);
        il.Emit(OpCodes.Ldnull);
        il.Emit(OpCodes.Ret);
    }

    /// <summary>
    /// Emits PerfHooksClearMeasuresWrapper: performance.clearMeasures(name?).
    /// Signature: object PerfHooksClearMeasuresWrapper(object[] args)
    /// </summary>
    private void EmitPerfHooksClearMeasuresWrapper(TypeBuilder typeBuilder, EmittedRuntime runtime, FieldBuilder entriesField)
    {
        var method = typeBuilder.DefineMethod(
            "PerfHooksClearMeasuresWrapper",
            MethodAttributes.Public | MethodAttributes.Static,
            _types.Object,
            [_types.ObjectArray]
        );
        runtime.PerfHooksClearMeasures = method;

        var il = method.GetILGenerator();
        var noNameLabel = il.DefineLabel();

        il.Emit(OpCodes.Ldstr, "measure");

        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldlen);
        il.Emit(OpCodes.Conv_I4);
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Blt, noNameLabel);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Ldelem_Ref);
        il.Emit(OpCodes.Isinst, _types.String);
        il.Emit(OpCodes.Call, runtime.PerfHooksClearByType);
        il.Emit(OpCodes.Ldnull);
        il.Emit(OpCodes.Ret);

        il.MarkLabel(noNameLabel);
        il.Emit(OpCodes.Ldnull);
        il.Emit(OpCodes.Call, runtime.PerfHooksClearByType);
        il.Emit(OpCodes.Ldnull);
        il.Emit(OpCodes.Ret);
    }

    /// <summary>
    /// Emits PerfHooksCreateObserverWrapper: new PerformanceObserver(callback).
    /// Signature: object PerfHooksCreateObserverWrapper(object[] args)
    /// Returns a $Object with observe() and disconnect() methods.
    /// </summary>
    private void EmitPerfHooksCreateObserverWrapper(TypeBuilder typeBuilder, EmittedRuntime runtime, FieldBuilder observersField)
    {
        var method = typeBuilder.DefineMethod(
            "PerfHooksCreateObserverWrapper",
            MethodAttributes.Public | MethodAttributes.Static,
            _types.Object,
            [_types.ObjectArray]
        );
        runtime.PerfHooksCreateObserver = method;

        var il = method.GetILGenerator();
        var dictSetItem = _types.GetMethod(_types.DictionaryStringObject, "set_Item", _types.String, _types.Object);

        // Create observer registration: object[] { callback, new HashSet<string>(), false }
        il.Emit(OpCodes.Ldc_I4_3);
        il.Emit(OpCodes.Newarr, _types.Object);
        var regLocal = il.DeclareLocal(_types.ObjectArray);
        il.Emit(OpCodes.Stloc, regLocal);

        // reg[0] = callback (args[0])
        il.Emit(OpCodes.Ldloc, regLocal);
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Ldelem_Ref);
        il.Emit(OpCodes.Stelem_Ref);

        // reg[1] = new HashSet<string>()
        il.Emit(OpCodes.Ldloc, regLocal);
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Newobj, typeof(HashSet<string>).GetConstructor(Type.EmptyTypes)!);
        il.Emit(OpCodes.Stelem_Ref);

        // reg[2] = false (not connected yet)
        il.Emit(OpCodes.Ldloc, regLocal);
        il.Emit(OpCodes.Ldc_I4_2);
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Box, _types.Boolean);
        il.Emit(OpCodes.Stelem_Ref);

        // Now emit the observe and disconnect wrapper methods that capture 'regLocal' via closure
        // Since we can't capture locals in IL, we pass reg as part of the TSFunction target
        // Actually, we'll create unique methods per observer instance. That's complex.
        // Simpler approach: store reg in a well-known place and have observe/disconnect take it as arg.
        // Best approach: use the observer object itself (a $Object) with reg stored as a hidden field.

        // Create observe method: takes options, extracts entryTypes, sets reg[1] and reg[2]
        var observeWrapper = EmitPerfHooksObserveWrapper(typeBuilder, runtime, observersField);
        var disconnectWrapper = EmitPerfHooksDisconnectWrapper(typeBuilder, runtime);

        // Create $Object with observe and disconnect as $TSFunction
        // But we need the functions to have access to `reg`. We'll use the TSFunction target mechanism:
        // TSFunction stores a target object and when invoked, prepends it to args.
        // So we set target = reg, and the wrapper methods expect reg as first arg in args[].

        il.Emit(OpCodes.Newobj, _types.GetDefaultConstructor(_types.DictionaryStringObject));
        var dictLocal = il.DeclareLocal(_types.DictionaryStringObject);
        il.Emit(OpCodes.Stloc, dictLocal);

        // observe: new $TSFunction(reg, observeWrapper)
        il.Emit(OpCodes.Ldloc, dictLocal);
        il.Emit(OpCodes.Ldstr, "observe");
        il.Emit(OpCodes.Ldloc, regLocal); // target = reg
        il.Emit(OpCodes.Ldtoken, observeWrapper);
        il.Emit(OpCodes.Call, _types.MethodBaseGetMethodFromHandle);
        il.Emit(OpCodes.Castclass, typeof(MethodInfo));
        il.Emit(OpCodes.Newobj, runtime.TSFunctionCtor);
        il.Emit(OpCodes.Callvirt, dictSetItem);

        // disconnect: new $TSFunction(reg, disconnectWrapper)
        il.Emit(OpCodes.Ldloc, dictLocal);
        il.Emit(OpCodes.Ldstr, "disconnect");
        il.Emit(OpCodes.Ldloc, regLocal); // target = reg
        il.Emit(OpCodes.Ldtoken, disconnectWrapper);
        il.Emit(OpCodes.Call, _types.MethodBaseGetMethodFromHandle);
        il.Emit(OpCodes.Castclass, typeof(MethodInfo));
        il.Emit(OpCodes.Newobj, runtime.TSFunctionCtor);
        il.Emit(OpCodes.Callvirt, dictSetItem);

        // Wrap in $Object
        il.Emit(OpCodes.Ldloc, dictLocal);
        il.Emit(OpCodes.Newobj, runtime.TSObjectCtor);
        il.Emit(OpCodes.Ret);
    }

    /// <summary>
    /// Emits the observe method for PerformanceObserver.
    /// When called via TSFunction with target=reg, args[0] = reg, args[1] = options.
    /// Signature: object PerfHooksObserve(object[] args)
    /// </summary>
    private MethodBuilder EmitPerfHooksObserveWrapper(TypeBuilder typeBuilder, EmittedRuntime runtime, FieldBuilder observersField)
    {
        var method = typeBuilder.DefineMethod(
            "PerfHooksObserve",
            MethodAttributes.Public | MethodAttributes.Static,
            _types.Object,
            [_types.ObjectArray]
        );

        var il = method.GetILGenerator();
        var hashSetAdd = typeof(HashSet<string>).GetMethod("Add", [typeof(string)])!;
        var hashSetClear = typeof(HashSet<string>).GetMethod("Clear")!;
        var listCountProp = _types.GetProperty(_types.ListOfObjectNullable, "Count");
        var listItemProp = _types.GetProperty(_types.ListOfObjectNullable, "Item");
        var dictTryGetValue = _types.GetMethod(_types.DictionaryStringObject, "TryGetValue", _types.String, _types.Object.MakeByRefType());

        var retLabel = il.DefineLabel();

        // reg = args[0] as object[]
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Ldelem_Ref);
        il.Emit(OpCodes.Isinst, _types.ObjectArray);
        var regLocal = il.DeclareLocal(_types.ObjectArray);
        il.Emit(OpCodes.Stloc, regLocal);
        il.Emit(OpCodes.Ldloc, regLocal);
        il.Emit(OpCodes.Brfalse, retLabel);

        // options = args[1] — extract the dictionary (may be $Object or raw Dictionary<string, object?>)
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldlen);
        il.Emit(OpCodes.Conv_I4);
        il.Emit(OpCodes.Ldc_I4_2);
        il.Emit(OpCodes.Blt, retLabel);

        // Get the options dictionary: first try $Object.Fields, then raw Dictionary
        var optionsDictLocal = il.DeclareLocal(_types.DictionaryStringObject);
        var tryDictLabel = il.DefineLabel();
        var haveDictLabel = il.DefineLabel();

        // Try $Object first
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Ldelem_Ref);
        il.Emit(OpCodes.Isinst, runtime.TSObjectType);
        var optionsObjLocal = il.DeclareLocal(runtime.TSObjectType);
        il.Emit(OpCodes.Stloc, optionsObjLocal);
        il.Emit(OpCodes.Ldloc, optionsObjLocal);
        il.Emit(OpCodes.Brfalse, tryDictLabel);

        // Got $Object — extract Fields dictionary
        il.Emit(OpCodes.Ldloc, optionsObjLocal);
        il.Emit(OpCodes.Callvirt, runtime.TSObjectFieldsGetter);
        il.Emit(OpCodes.Stloc, optionsDictLocal);
        il.Emit(OpCodes.Br, haveDictLabel);

        // Try raw Dictionary<string, object?>
        il.MarkLabel(tryDictLabel);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Ldelem_Ref);
        il.Emit(OpCodes.Isinst, _types.DictionaryStringObject);
        il.Emit(OpCodes.Stloc, optionsDictLocal);
        il.Emit(OpCodes.Ldloc, optionsDictLocal);
        il.Emit(OpCodes.Brfalse, retLabel);

        il.MarkLabel(haveDictLabel);

        // Get entryTypes from the dictionary
        il.Emit(OpCodes.Ldloc, optionsDictLocal);
        il.Emit(OpCodes.Ldstr, "entryTypes");
        var entryTypesValueLocal = il.DeclareLocal(_types.Object);
        il.Emit(OpCodes.Ldloca, entryTypesValueLocal);
        il.Emit(OpCodes.Callvirt, dictTryGetValue);
        il.Emit(OpCodes.Brfalse, retLabel);

        // entryTypes should be a List<object?> (array)
        il.Emit(OpCodes.Ldloc, entryTypesValueLocal);
        il.Emit(OpCodes.Isinst, _types.ListOfObjectNullable);
        var entryTypesListLocal = il.DeclareLocal(_types.ListOfObjectNullable);
        il.Emit(OpCodes.Stloc, entryTypesListLocal);
        il.Emit(OpCodes.Ldloc, entryTypesListLocal);
        il.Emit(OpCodes.Brfalse, retLabel);

        // Clear existing entry types and add new ones
        il.Emit(OpCodes.Ldloc, regLocal);
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Ldelem_Ref);
        il.Emit(OpCodes.Castclass, typeof(HashSet<string>));
        var hashSetLocal = il.DeclareLocal(typeof(HashSet<string>));
        il.Emit(OpCodes.Stloc, hashSetLocal);
        il.Emit(OpCodes.Ldloc, hashSetLocal);
        il.Emit(OpCodes.Callvirt, hashSetClear);

        // Loop through entryTypes and add strings
        var loopStartLabel = il.DefineLabel();
        var loopBodyLabel = il.DefineLabel();
        var loopContinueLabel = il.DefineLabel();
        var iLocal = il.DeclareLocal(_types.Int32);
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Stloc, iLocal);
        il.Emit(OpCodes.Br, loopStartLabel);

        il.MarkLabel(loopBodyLabel);
        il.Emit(OpCodes.Ldloc, entryTypesListLocal);
        il.Emit(OpCodes.Ldloc, iLocal);
        il.Emit(OpCodes.Callvirt, listItemProp.GetGetMethod()!);
        il.Emit(OpCodes.Isinst, _types.String);
        var itemLocal = il.DeclareLocal(_types.String);
        il.Emit(OpCodes.Stloc, itemLocal);
        il.Emit(OpCodes.Ldloc, itemLocal);
        il.Emit(OpCodes.Brfalse, loopContinueLabel);
        il.Emit(OpCodes.Ldloc, hashSetLocal);
        il.Emit(OpCodes.Ldloc, itemLocal);
        il.Emit(OpCodes.Callvirt, hashSetAdd);
        il.Emit(OpCodes.Pop); // discard bool return

        il.MarkLabel(loopContinueLabel);
        il.Emit(OpCodes.Ldloc, iLocal);
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Add);
        il.Emit(OpCodes.Stloc, iLocal);

        il.MarkLabel(loopStartLabel);
        il.Emit(OpCodes.Ldloc, iLocal);
        il.Emit(OpCodes.Ldloc, entryTypesListLocal);
        il.Emit(OpCodes.Callvirt, listCountProp.GetGetMethod()!);
        il.Emit(OpCodes.Blt, loopBodyLabel);

        // Set connected = true: reg[2] = true
        il.Emit(OpCodes.Ldloc, regLocal);
        il.Emit(OpCodes.Ldc_I4_2);
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Box, _types.Boolean);
        il.Emit(OpCodes.Stelem_Ref);

        // Ensure observers list exists and add registration
        var observersExistLabel = il.DefineLabel();
        il.Emit(OpCodes.Ldsfld, observersField);
        il.Emit(OpCodes.Brtrue, observersExistLabel);
        il.Emit(OpCodes.Newobj, _types.ListObjectNullableDefaultCtor);
        il.Emit(OpCodes.Stsfld, observersField);
        il.MarkLabel(observersExistLabel);

        il.Emit(OpCodes.Ldsfld, observersField);
        il.Emit(OpCodes.Ldloc, regLocal);
        il.Emit(OpCodes.Callvirt, _types.GetMethod(_types.ListOfObjectNullable, "Add", _types.Object));

        il.MarkLabel(retLabel);
        il.Emit(OpCodes.Ldnull);
        il.Emit(OpCodes.Ret);

        return method;
    }

    /// <summary>
    /// Emits the disconnect method for PerformanceObserver.
    /// args[0] = reg (observer registration array)
    /// Signature: object PerfHooksDisconnect(object[] args)
    /// </summary>
    private MethodBuilder EmitPerfHooksDisconnectWrapper(TypeBuilder typeBuilder, EmittedRuntime runtime)
    {
        var method = typeBuilder.DefineMethod(
            "PerfHooksDisconnect",
            MethodAttributes.Public | MethodAttributes.Static,
            _types.Object,
            [_types.ObjectArray]
        );

        var il = method.GetILGenerator();
        var retLabel = il.DefineLabel();

        // reg = args[0] as object[]
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Ldelem_Ref);
        il.Emit(OpCodes.Isinst, _types.ObjectArray);
        var regLocal = il.DeclareLocal(_types.ObjectArray);
        il.Emit(OpCodes.Stloc, regLocal);
        il.Emit(OpCodes.Ldloc, regLocal);
        il.Emit(OpCodes.Brfalse, retLabel);

        // reg[2] = false (disconnected)
        il.Emit(OpCodes.Ldloc, regLocal);
        il.Emit(OpCodes.Ldc_I4_2);
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Box, _types.Boolean);
        il.Emit(OpCodes.Stelem_Ref);

        il.MarkLabel(retLabel);
        il.Emit(OpCodes.Ldnull);
        il.Emit(OpCodes.Ret);

        return method;
    }

    /// <summary>
    /// Emits GetPerformance: returns the performance object with all methods.
    /// Signature: object GetPerformance()
    /// </summary>
    private void EmitPerfHooksGetPerformance(TypeBuilder typeBuilder, EmittedRuntime runtime)
    {
        // Wrapper methods for TSFunction
        var nowWrapper = EmitPerformanceNowWrapper(typeBuilder, runtime);

        var method = typeBuilder.DefineMethod(
            "PerfHooksGetPerformance",
            MethodAttributes.Public | MethodAttributes.Static,
            _types.Object,
            Type.EmptyTypes
        );
        runtime.PerfHooksGetPerformance = method;

        var il = method.GetILGenerator();
        var dictSetItem = _types.GetMethod(_types.DictionaryStringObject, "set_Item", _types.String, _types.Object);

        // Create a Dictionary<string, object?> for the performance object
        il.Emit(OpCodes.Newobj, _types.GetDefaultConstructor(_types.DictionaryStringObject));
        var dictLocal = il.DeclareLocal(_types.DictionaryStringObject);
        il.Emit(OpCodes.Stloc, dictLocal);

        // Add timeOrigin property
        il.Emit(OpCodes.Ldloc, dictLocal);
        il.Emit(OpCodes.Ldstr, "timeOrigin");
        il.Emit(OpCodes.Call, typeof(DateTimeOffset).GetProperty("UtcNow")!.GetGetMethod()!);
        var dtLocal = il.DeclareLocal(typeof(DateTimeOffset));
        il.Emit(OpCodes.Stloc, dtLocal);
        il.Emit(OpCodes.Ldloca, dtLocal);
        il.Emit(OpCodes.Call, typeof(DateTimeOffset).GetMethod("ToUnixTimeMilliseconds")!);
        il.Emit(OpCodes.Conv_R8);
        il.Emit(OpCodes.Call, runtime.PerfHooksPerformanceNow);
        il.Emit(OpCodes.Sub);
        il.Emit(OpCodes.Box, _types.Double);
        il.Emit(OpCodes.Callvirt, dictSetItem);

        // Helper to add a TSFunction wrapping a static method
        void AddMethod(string name, MethodBuilder wrapper)
        {
            il.Emit(OpCodes.Ldloc, dictLocal);
            il.Emit(OpCodes.Ldstr, name);
            il.Emit(OpCodes.Ldnull); // target
            il.Emit(OpCodes.Ldtoken, wrapper);
            il.Emit(OpCodes.Call, _types.MethodBaseGetMethodFromHandle);
            il.Emit(OpCodes.Castclass, typeof(MethodInfo));
            il.Emit(OpCodes.Newobj, runtime.TSFunctionCtor);
            il.Emit(OpCodes.Callvirt, dictSetItem);
        }

        AddMethod("now", nowWrapper);
        AddMethod("mark", runtime.PerfHooksMark);
        AddMethod("measure", runtime.PerfHooksMeasure);
        AddMethod("getEntries", runtime.PerfHooksGetEntries);
        AddMethod("getEntriesByName", runtime.PerfHooksGetEntriesByName);
        AddMethod("getEntriesByType", runtime.PerfHooksGetEntriesByType);
        AddMethod("clearMarks", runtime.PerfHooksClearMarks);
        AddMethod("clearMeasures", runtime.PerfHooksClearMeasures);

        // Wrap in $Object and return
        il.Emit(OpCodes.Ldloc, dictLocal);
        il.Emit(OpCodes.Newobj, runtime.TSObjectCtor);
        il.Emit(OpCodes.Ret);
    }

    /// <summary>
    /// Emits a wrapper for PerformanceNow that takes object[] args.
    /// Signature: object PerformanceNowWrapper(object[] args)
    /// </summary>
    private MethodBuilder EmitPerformanceNowWrapper(TypeBuilder typeBuilder, EmittedRuntime runtime)
    {
        var method = typeBuilder.DefineMethod(
            "PerformanceNowWrapper",
            MethodAttributes.Public | MethodAttributes.Static,
            _types.Object,
            [_types.ObjectArray]
        );

        var il = method.GetILGenerator();

        // Call PerformanceNow() and box the result
        il.Emit(OpCodes.Call, runtime.PerfHooksPerformanceNow);
        il.Emit(OpCodes.Box, _types.Double);
        il.Emit(OpCodes.Ret);

        return method;
    }
}
