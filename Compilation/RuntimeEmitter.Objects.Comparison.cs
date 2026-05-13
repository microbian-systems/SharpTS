using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;

namespace SharpTS.Compilation;

public partial class RuntimeEmitter
{
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

        // Check class instance via $IHasFields
        il.MarkLabel(checkClassLabel);
        var fieldsDictLocal = il.DeclareLocal(dictType);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Isinst, runtime.IHasFieldsInterface);
        il.Emit(OpCodes.Brfalse, returnFalseLabel);

        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Castclass, runtime.IHasFieldsInterface);
        il.Emit(OpCodes.Callvirt, runtime.IHasFieldsFieldsGetter);
        il.Emit(OpCodes.Stloc, fieldsDictLocal);
        il.Emit(OpCodes.Ldloc, fieldsDictLocal);
        il.Emit(OpCodes.Brfalse, returnFalseLabel);

        // Check if _fields contains key (using original key)
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

        // ECMA-262 §20.1.2.1 Object.assign step 1: Let to be ? ToObject(target).
        // ToObject(null/undefined) throws TypeError. Target-Null.js / Target-
        // Undefined.js in test262 verify.
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Brfalse, targetNullLabel);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Isinst, runtime.UndefinedType);
        il.Emit(OpCodes.Brtrue, targetNullLabel);

        // ECMA-262 §7.1.18 ToObject: coerce primitive target (string, number,
        // bool, symbol) to a wrapper object. OnlyOneArgument.js +
        // Target-{Boolean,Number,String,Symbol}.js verify `Object.assign(prim)`
        // returns a wrapper. Store in a local that replaces arg0 going forward.
        var coercedTargetLocal = il.DeclareLocal(_types.Object);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Call, runtime.ToObjectMethod);
        il.Emit(OpCodes.Stloc, coercedTargetLocal);

        // Unwrap $Object → its _fields Dict so source-iteration writes land
        // on the wrapper's own slots (same trick ObjectDefineProperties uses
        // for receiver+props normalization). `new Object()` returns a
        // $Object; without this, Object.assign(newObj, "123") would skip the
        // Dict branch entirely and lose the source iteration.
        var notTSObjectTargetLabel = il.DefineLabel();
        il.Emit(OpCodes.Ldloc, coercedTargetLocal);
        il.Emit(OpCodes.Isinst, runtime.TSObjectType);
        il.Emit(OpCodes.Brfalse, notTSObjectTargetLabel);
        il.Emit(OpCodes.Ldloc, coercedTargetLocal);
        il.Emit(OpCodes.Castclass, runtime.TSObjectType);
        il.Emit(OpCodes.Callvirt, runtime.TSObjectFieldsGetter);
        il.Emit(OpCodes.Stloc, targetDictLocal);
        var afterTargetUnwrapLabel = il.DefineLabel();
        il.Emit(OpCodes.Br, afterTargetUnwrapLabel);
        il.MarkLabel(notTSObjectTargetLabel);

        // Check if coerced target is Dictionary<string, object>
        il.Emit(OpCodes.Ldloc, coercedTargetLocal);
        il.Emit(OpCodes.Isinst, dictType);
        il.Emit(OpCodes.Stloc, targetDictLocal);

        il.MarkLabel(afterTargetUnwrapLabel);
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

        // If source is a $Object (e.g. `new Constructor()` instance), unwrap
        // to its _fields Dict so the iteration sees its own keys. Aligns with
        // the same unwrap in ObjectDefineProperties.
        var notTSObjectSrcLabel = il.DefineLabel();
        il.Emit(OpCodes.Ldloc, sourceLocal);
        il.Emit(OpCodes.Isinst, runtime.TSObjectType);
        il.Emit(OpCodes.Brfalse, notTSObjectSrcLabel);
        il.Emit(OpCodes.Ldloc, sourceLocal);
        il.Emit(OpCodes.Castclass, runtime.TSObjectType);
        il.Emit(OpCodes.Callvirt, runtime.TSObjectFieldsGetter);
        il.Emit(OpCodes.Stloc, sourceLocal);
        il.MarkLabel(notTSObjectSrcLabel);

        // If source is a string, iterate indexed chars per ECMA-262 §20.1.2.1.
        // "abc" exposes own enumerable {0:"a", 1:"b", 2:"c"}; length is
        // non-enumerable so excluded. Pre-fix dropped string sources entirely.
        var notStringSrcLabel = il.DefineLabel();
        il.Emit(OpCodes.Ldloc, sourceLocal);
        il.Emit(OpCodes.Isinst, _types.String);
        il.Emit(OpCodes.Brfalse, notStringSrcLabel);
        {
            var srcStrLocal = il.DeclareLocal(_types.String);
            var srcIdxLocal = il.DeclareLocal(_types.Int32);
            var srcLenLocal = il.DeclareLocal(_types.Int32);
            il.Emit(OpCodes.Ldloc, sourceLocal);
            il.Emit(OpCodes.Castclass, _types.String);
            il.Emit(OpCodes.Stloc, srcStrLocal);
            il.Emit(OpCodes.Ldloc, srcStrLocal);
            il.Emit(OpCodes.Callvirt, _types.GetProperty(_types.String, "Length").GetGetMethod()!);
            il.Emit(OpCodes.Stloc, srcLenLocal);
            il.Emit(OpCodes.Ldc_I4_0);
            il.Emit(OpCodes.Stloc, srcIdxLocal);

            var strLoopStart = il.DefineLabel();
            var strLoopEnd = il.DefineLabel();
            il.MarkLabel(strLoopStart);
            il.Emit(OpCodes.Ldloc, srcIdxLocal);
            il.Emit(OpCodes.Ldloc, srcLenLocal);
            il.Emit(OpCodes.Bge, strLoopEnd);

            // target[srcIdx.ToString()] = srcStr[srcIdx].ToString();
            var charLocal = il.DeclareLocal(_types.Char);
            il.Emit(OpCodes.Ldloc, targetDictLocal);
            il.Emit(OpCodes.Ldloca, srcIdxLocal);
            il.Emit(OpCodes.Call, _types.GetMethodNoParams(_types.Int32, "ToString"));
            il.Emit(OpCodes.Ldloc, srcStrLocal);
            il.Emit(OpCodes.Ldloc, srcIdxLocal);
            il.Emit(OpCodes.Callvirt, _types.GetMethod(_types.String, "get_Chars", _types.Int32));
            il.Emit(OpCodes.Stloc, charLocal);
            il.Emit(OpCodes.Ldloca, charLocal);
            il.Emit(OpCodes.Call, _types.GetMethodNoParams(_types.Char, "ToString"));
            il.Emit(OpCodes.Callvirt, _types.GetMethod(dictType, "set_Item", _types.String, _types.Object));

            il.Emit(OpCodes.Ldloc, srcIdxLocal);
            il.Emit(OpCodes.Ldc_I4_1);
            il.Emit(OpCodes.Add);
            il.Emit(OpCodes.Stloc, srcIdxLocal);
            il.Emit(OpCodes.Br, strLoopStart);
            il.MarkLabel(strLoopEnd);
            il.Emit(OpCodes.Br, nextSource);
        }
        il.MarkLabel(notStringSrcLabel);

        // If source is a List<object?> (an Array per ECMA-262 §10.4.2),
        // synthesize a Dictionary<string,object?> with indices "0".."N-1"
        // and fall through to the dict-copy loop. "length" is non-enumerable
        // per §10.4.2.4 so it's excluded. Pre-fix dropped Array sources
        // entirely, missing both the assign and the spec-mandated TypeError
        // when target is a String exotic (test262 assignment-to-readonly...).
        var notListSrcLabel = il.DefineLabel();
        il.Emit(OpCodes.Ldloc, sourceLocal);
        il.Emit(OpCodes.Isinst, listType);
        il.Emit(OpCodes.Brfalse, notListSrcLabel);
        {
            var srcListLocal = il.DeclareLocal(listType);
            var srcListLenLocal = il.DeclareLocal(_types.Int32);
            var srcListIdxLocal = il.DeclareLocal(_types.Int32);
            var synthDictLocal = il.DeclareLocal(dictType);
            il.Emit(OpCodes.Ldloc, sourceLocal);
            il.Emit(OpCodes.Castclass, listType);
            il.Emit(OpCodes.Stloc, srcListLocal);
            il.Emit(OpCodes.Newobj, dictType.GetConstructor(Type.EmptyTypes)!);
            il.Emit(OpCodes.Stloc, synthDictLocal);
            il.Emit(OpCodes.Ldloc, srcListLocal);
            il.Emit(OpCodes.Callvirt, _types.GetProperty(listType, "Count").GetGetMethod()!);
            il.Emit(OpCodes.Stloc, srcListLenLocal);
            il.Emit(OpCodes.Ldc_I4_0);
            il.Emit(OpCodes.Stloc, srcListIdxLocal);

            var listLoopStart = il.DefineLabel();
            var listLoopEnd = il.DefineLabel();
            il.MarkLabel(listLoopStart);
            il.Emit(OpCodes.Ldloc, srcListIdxLocal);
            il.Emit(OpCodes.Ldloc, srcListLenLocal);
            il.Emit(OpCodes.Bge, listLoopEnd);

            // synthDict[idx.ToString()] = srcList[idx]
            il.Emit(OpCodes.Ldloc, synthDictLocal);
            il.Emit(OpCodes.Ldloca, srcListIdxLocal);
            il.Emit(OpCodes.Call, _types.GetMethodNoParams(_types.Int32, "ToString"));
            il.Emit(OpCodes.Ldloc, srcListLocal);
            il.Emit(OpCodes.Ldloc, srcListIdxLocal);
            il.Emit(OpCodes.Callvirt, _types.GetProperty(listType, "Item")!.GetGetMethod()!);
            il.Emit(OpCodes.Callvirt, _types.GetMethod(dictType, "set_Item", _types.String, _types.Object));

            il.Emit(OpCodes.Ldloc, srcListIdxLocal);
            il.Emit(OpCodes.Ldc_I4_1);
            il.Emit(OpCodes.Add);
            il.Emit(OpCodes.Stloc, srcListIdxLocal);
            il.Emit(OpCodes.Br, listLoopStart);
            il.MarkLabel(listLoopEnd);

            // Re-bind sourceLocal so the dict-copy loop below picks it up.
            il.Emit(OpCodes.Ldloc, synthDictLocal);
            il.Emit(OpCodes.Stloc, sourceLocal);
        }
        il.MarkLabel(notListSrcLabel);

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

        // Copy loop. Per ECMA-262 §20.1.2.1, only enumerable own keys propagate.
        // Filter via PDS-descriptor check (non-enumerable → skip). Also skip
        // `__`-prefixed internal markers from boxed-primitive wrappers.
        var kpKey = kvpType.GetProperty("Key")!.GetGetMethod()!;
        var kpValue = kvpType.GetProperty("Value")!.GetGetMethod()!;
        il.MarkLabel(copyLoopStart);
        il.Emit(OpCodes.Ldloca, enumeratorLocal);
        il.Emit(OpCodes.Call, typeof(Dictionary<string, object?>.Enumerator).GetMethod("MoveNext")!);
        il.Emit(OpCodes.Brfalse, copyLoopEnd);

        // Get current kvp
        il.Emit(OpCodes.Ldloca, enumeratorLocal);
        il.Emit(OpCodes.Call, typeof(Dictionary<string, object?>.Enumerator).GetProperty("Current")!.GetGetMethod()!);
        il.Emit(OpCodes.Stloc, kvpLocal);

        // Skip __-prefixed internal markers.
        il.Emit(OpCodes.Ldloca, kvpLocal);
        il.Emit(OpCodes.Call, kpKey);
        il.Emit(OpCodes.Ldstr, "__");
        il.Emit(OpCodes.Callvirt, _types.String.GetMethod("StartsWith", [_types.String])!);
        il.Emit(OpCodes.Brtrue, copyLoopStart);

        // Skip if PDS descriptor present with Enumerable=false. Use the
        // ORIGINAL source object (sourceLocal) for PDS lookup, since the
        // descriptor was installed against the wrapper, not against the
        // unwrapped _fields dict.
        var copyEnumOkLabel = il.DefineLabel();
        var copyKeyDescLocal = il.DeclareLocal(runtime.CompiledPropertyDescriptorType);
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Ldloc, sourceIndexLocal);
        il.Emit(OpCodes.Callvirt, _types.GetProperty(listType, "Item")!.GetGetMethod()!);
        il.Emit(OpCodes.Ldloca, kvpLocal);
        il.Emit(OpCodes.Call, kpKey);
        il.Emit(OpCodes.Call, runtime.PDSGetPropertyDescriptor);
        il.Emit(OpCodes.Stloc, copyKeyDescLocal);
        il.Emit(OpCodes.Ldloc, copyKeyDescLocal);
        il.Emit(OpCodes.Brfalse, copyEnumOkLabel);
        il.Emit(OpCodes.Ldloc, copyKeyDescLocal);
        il.Emit(OpCodes.Callvirt, runtime.CompiledPropertyDescriptorEnumerable.GetGetMethod()!);
        il.Emit(OpCodes.Brtrue, copyEnumOkLabel);
        il.Emit(OpCodes.Br, copyLoopStart);
        il.MarkLabel(copyEnumOkLabel);

        // ECMA-262 §20.1.2.1 step 5.c.i: invoke [[Set]] which, per §10.1.9
        // OrdinarySet, returns false (→ TypeError in this strict-mode call)
        // when target is frozen (writable=false on existing data property) or
        // when target is non-extensible and the key would be a new addition.
        // Check both conditions against the original target (arg0), since the
        // PDS lookup uses the wrapper identity, not the unwrapped _fields dict.
        var skipFrozenThrowLabel = il.DefineLabel();
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Call, runtime.PDSIsFrozen);
        il.Emit(OpCodes.Brfalse, skipFrozenThrowLabel);
        il.Emit(OpCodes.Ldstr, "Cannot assign to read only property in frozen object");
        il.Emit(OpCodes.Newobj, runtime.TSTypeErrorCtor);
        il.Emit(OpCodes.Call, runtime.CreateException);
        il.Emit(OpCodes.Throw);
        il.MarkLabel(skipFrozenThrowLabel);

        // Sealed: existing keys can be modified, new keys throw (non-extensible).
        var skipSealedCheckLabel = il.DefineLabel();
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Call, runtime.PDSIsSealed);
        il.Emit(OpCodes.Brfalse, skipSealedCheckLabel);
        // If key not in targetDict, throw.
        il.Emit(OpCodes.Ldloc, targetDictLocal);
        il.Emit(OpCodes.Ldloca, kvpLocal);
        il.Emit(OpCodes.Call, kpKey);
        il.Emit(OpCodes.Callvirt, _types.GetMethod(dictType, "ContainsKey", _types.String));
        il.Emit(OpCodes.Brtrue, skipSealedCheckLabel);
        il.Emit(OpCodes.Ldstr, "Cannot add property to sealed object");
        il.Emit(OpCodes.Newobj, runtime.TSTypeErrorCtor);
        il.Emit(OpCodes.Call, runtime.CreateException);
        il.Emit(OpCodes.Throw);
        il.MarkLabel(skipSealedCheckLabel);

        // String exotic object: indexed integer keys and "length" are
        // non-writable per ECMA-262 §10.4.3. Object.assign('a', [1]) wraps
        // 'a' via ToObject into a String wrapper, then tries to set "0" → 1
        // which must throw TypeError. Detect via __primitiveType="String" in
        // the target dict (the boxed-primitive marker set by NewBoxedPrimitive).
        var skipStringWrapperThrowLabel = il.DefineLabel();
        var primTypeLocal = il.DeclareLocal(_types.Object);
        il.Emit(OpCodes.Ldloc, targetDictLocal);
        il.Emit(OpCodes.Ldstr, "__primitiveType");
        il.Emit(OpCodes.Ldloca, primTypeLocal);
        il.Emit(OpCodes.Callvirt, _types.GetMethod(dictType, "TryGetValue", _types.String, _types.Object.MakeByRefType()));
        il.Emit(OpCodes.Brfalse, skipStringWrapperThrowLabel);
        il.Emit(OpCodes.Ldloc, primTypeLocal);
        il.Emit(OpCodes.Ldstr, "String");
        il.Emit(OpCodes.Call, _types.GetMethod(_types.Object, "Equals", _types.Object, _types.Object));
        il.Emit(OpCodes.Brfalse, skipStringWrapperThrowLabel);
        // Target IS a String wrapper. Check key is integer index OR "length"
        // (the spec's non-writable slots).
        var stringKeyLocal = il.DeclareLocal(_types.String);
        il.Emit(OpCodes.Ldloca, kvpLocal);
        il.Emit(OpCodes.Call, kpKey);
        il.Emit(OpCodes.Stloc, stringKeyLocal);
        // "length" key? → throw.
        il.Emit(OpCodes.Ldloc, stringKeyLocal);
        il.Emit(OpCodes.Ldstr, "length");
        il.Emit(OpCodes.Call, _types.GetMethod(_types.String, "op_Equality", _types.String, _types.String));
        var throwStringWrapperLabel = il.DefineLabel();
        il.Emit(OpCodes.Brtrue, throwStringWrapperLabel);
        // Integer index? Use Int32.TryParse; if parses and is in dict
        // (covered by NewBoxedPrimitive's pre-populated chars), throw.
        var idxParsedLocal = il.DeclareLocal(_types.Int32);
        il.Emit(OpCodes.Ldloc, stringKeyLocal);
        il.Emit(OpCodes.Ldloca, idxParsedLocal);
        il.Emit(OpCodes.Call, _types.GetMethod(_types.Int32, "TryParse", _types.String, _types.Int32.MakeByRefType()));
        il.Emit(OpCodes.Brfalse, skipStringWrapperThrowLabel);
        il.Emit(OpCodes.Ldloc, idxParsedLocal);
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Blt, skipStringWrapperThrowLabel);
        il.Emit(OpCodes.Ldloc, targetDictLocal);
        il.Emit(OpCodes.Ldloc, stringKeyLocal);
        il.Emit(OpCodes.Callvirt, _types.GetMethod(dictType, "ContainsKey", _types.String));
        il.Emit(OpCodes.Brfalse, skipStringWrapperThrowLabel);
        il.MarkLabel(throwStringWrapperLabel);
        il.Emit(OpCodes.Ldstr, "Cannot assign to read only property of String exotic object");
        il.Emit(OpCodes.Newobj, runtime.TSTypeErrorCtor);
        il.Emit(OpCodes.Call, runtime.CreateException);
        il.Emit(OpCodes.Throw);
        il.MarkLabel(skipStringWrapperThrowLabel);

        // target[kvp.Key] = kvp.Value
        il.Emit(OpCodes.Ldloc, targetDictLocal);
        il.Emit(OpCodes.Ldloca, kvpLocal);
        il.Emit(OpCodes.Call, kpKey);
        il.Emit(OpCodes.Ldloca, kvpLocal);
        il.Emit(OpCodes.Call, kpValue);
        il.Emit(OpCodes.Callvirt, _types.GetMethod(dictType, "set_Item", _types.String, _types.Object));

        il.Emit(OpCodes.Br, copyLoopStart);

        il.MarkLabel(copyLoopEnd);
        // Dispose enumerator (it's a struct, so just call Dispose)
        il.Emit(OpCodes.Ldloca, enumeratorLocal);
        il.Emit(OpCodes.Call, typeof(Dictionary<string, object?>.Enumerator).GetMethod("Dispose")!);

        // ECMA-262 §20.1.2.1 step 5.c: OwnPropertyKeys includes BOTH string
        // keys (handled above) AND symbol keys. Iterate the source's
        // per-object symbol dict so \`Object.assign(t, {[sym]: v})\` propagates
        // sym → t's symbol dict. Use ORIGINAL arg1[sourceIndex] (not sourceLocal
        // which may be the unwrapped \$Object._fields) for the symbol-dict
        // identity. Frozen target still throws here per the spec.
        var symbolSourceLocal = il.DeclareLocal(_types.Object);
        var symbolDictLocal = il.DeclareLocal(_types.DictionaryObjectObject);
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Ldloc, sourceIndexLocal);
        il.Emit(OpCodes.Callvirt, _types.GetProperty(listType, "Item")!.GetGetMethod()!);
        il.Emit(OpCodes.Stloc, symbolSourceLocal);
        var skipSymbolIterationLabel = il.DefineLabel();
        il.Emit(OpCodes.Ldloc, symbolSourceLocal);
        il.Emit(OpCodes.Brfalse, skipSymbolIterationLabel);
        il.Emit(OpCodes.Ldloc, symbolSourceLocal);
        il.Emit(OpCodes.Call, runtime.GetSymbolDictMethod);
        il.Emit(OpCodes.Stloc, symbolDictLocal);
        il.Emit(OpCodes.Ldloc, symbolDictLocal);
        il.Emit(OpCodes.Callvirt, _types.GetProperty(_types.DictionaryObjectObject, "Count").GetGetMethod()!);
        il.Emit(OpCodes.Brfalse, skipSymbolIterationLabel);

        // Iterate (key, value) pairs of symbol dict. Reuse a fresh enumerator
        // to avoid colliding with the (now-disposed) string-key one.
        var symKvpType = typeof(KeyValuePair<object, object?>);
        var symEnumType = typeof(Dictionary<object, object?>.Enumerator);
        var symEnumLocal = il.DeclareLocal(symEnumType);
        var symKvpLocal = il.DeclareLocal(symKvpType);
        il.Emit(OpCodes.Ldloc, symbolDictLocal);
        il.Emit(OpCodes.Callvirt, _types.GetMethodNoParams(_types.DictionaryObjectObject, "GetEnumerator"));
        il.Emit(OpCodes.Stloc, symEnumLocal);

        var targetSymDictLocal = il.DeclareLocal(_types.DictionaryObjectObject);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Call, runtime.GetSymbolDictMethod);
        il.Emit(OpCodes.Stloc, targetSymDictLocal);

        var symLoopStart = il.DefineLabel();
        var symLoopEnd = il.DefineLabel();
        var symKpKey = symKvpType.GetProperty("Key")!.GetGetMethod()!;
        var symKpValue = symKvpType.GetProperty("Value")!.GetGetMethod()!;
        il.MarkLabel(symLoopStart);
        il.Emit(OpCodes.Ldloca, symEnumLocal);
        il.Emit(OpCodes.Call, symEnumType.GetMethod("MoveNext")!);
        il.Emit(OpCodes.Brfalse, symLoopEnd);
        il.Emit(OpCodes.Ldloca, symEnumLocal);
        il.Emit(OpCodes.Call, symEnumType.GetProperty("Current")!.GetGetMethod()!);
        il.Emit(OpCodes.Stloc, symKvpLocal);

        // Frozen target → throw (same rationale as the string-key path).
        var skipSymFrozenThrowLabel = il.DefineLabel();
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Call, runtime.PDSIsFrozen);
        il.Emit(OpCodes.Brfalse, skipSymFrozenThrowLabel);
        il.Emit(OpCodes.Ldstr, "Cannot assign to read only property in frozen object");
        il.Emit(OpCodes.Newobj, runtime.TSTypeErrorCtor);
        il.Emit(OpCodes.Call, runtime.CreateException);
        il.Emit(OpCodes.Throw);
        il.MarkLabel(skipSymFrozenThrowLabel);

        // Sealed target + new key → throw.
        var skipSymSealedCheckLabel = il.DefineLabel();
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Call, runtime.PDSIsSealed);
        il.Emit(OpCodes.Brfalse, skipSymSealedCheckLabel);
        il.Emit(OpCodes.Ldloc, targetSymDictLocal);
        il.Emit(OpCodes.Ldloca, symKvpLocal);
        il.Emit(OpCodes.Call, symKpKey);
        il.Emit(OpCodes.Callvirt, _types.GetMethod(_types.DictionaryObjectObject, "ContainsKey", _types.Object));
        il.Emit(OpCodes.Brtrue, skipSymSealedCheckLabel);
        il.Emit(OpCodes.Ldstr, "Cannot add property to sealed object");
        il.Emit(OpCodes.Newobj, runtime.TSTypeErrorCtor);
        il.Emit(OpCodes.Call, runtime.CreateException);
        il.Emit(OpCodes.Throw);
        il.MarkLabel(skipSymSealedCheckLabel);

        // targetSymDict[symKey] = symValue
        il.Emit(OpCodes.Ldloc, targetSymDictLocal);
        il.Emit(OpCodes.Ldloca, symKvpLocal);
        il.Emit(OpCodes.Call, symKpKey);
        il.Emit(OpCodes.Ldloca, symKvpLocal);
        il.Emit(OpCodes.Call, symKpValue);
        il.Emit(OpCodes.Callvirt, _types.GetMethod(_types.DictionaryObjectObject, "set_Item", _types.Object, _types.Object));
        il.Emit(OpCodes.Br, symLoopStart);

        il.MarkLabel(symLoopEnd);
        il.Emit(OpCodes.Ldloca, symEnumLocal);
        il.Emit(OpCodes.Call, symEnumType.GetMethod("Dispose")!);
        il.MarkLabel(skipSymbolIterationLabel);

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

        // Target is null/undefined - throw TypeError (ECMA-262 §20.1.2.1 step 1).
        il.MarkLabel(targetNullLabel);
        il.Emit(OpCodes.Ldstr, "Cannot convert undefined or null to object");
        il.Emit(OpCodes.Newobj, runtime.TSTypeErrorCtor);
        il.Emit(OpCodes.Call, runtime.CreateException);
        il.Emit(OpCodes.Throw);

        // Return coerced target (wrapped if a primitive was passed).
        il.MarkLabel(returnLabel);
        il.Emit(OpCodes.Ldloc, coercedTargetLocal);
        il.Emit(OpCodes.Ret);
    }
}
