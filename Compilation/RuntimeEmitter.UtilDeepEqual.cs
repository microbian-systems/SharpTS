using System.Reflection;
using System.Reflection.Emit;
using System.Text;

namespace SharpTS.Compilation;

/// <summary>
/// Emits deep equality comparison helpers (IsDeepStrictEqual, DeepEqualImpl).
/// </summary>
public partial class RuntimeEmitter
{
    /// <summary>
    /// Defines the signature for UtilDeepEqualImpl helper.
    /// </summary>
    private void DefineUtilDeepEqualSignature(TypeBuilder typeBuilder, EmittedRuntime runtime)
    {
        // DeepEqualImpl(object a, object b, Dictionary<object, object> seen) -> bool
        runtime.UtilDeepEqualImpl = typeBuilder.DefineMethod(
            "UtilDeepEqualImpl",
            MethodAttributes.Public | MethodAttributes.Static,
            _types.Boolean,
            [_types.Object, _types.Object, typeof(Dictionary<object, object>)]);
    }

    /// <summary>
    /// Emits IsDeepStrictEqual entry point - creates seen dictionary and calls impl.
    /// </summary>
    private void EmitUtilIsDeepStrictEqualBody(EmittedRuntime runtime)
    {
        var il = runtime.UtilIsDeepStrictEqual.GetILGenerator();

        // var seen = new Dictionary<object, object>($ReferenceEqualityComparer.Instance)
        var seenLocal = il.DeclareLocal(typeof(Dictionary<object, object>));
        il.Emit(OpCodes.Ldsfld, runtime.ReferenceEqualityComparerInstance);
        il.Emit(OpCodes.Newobj, typeof(Dictionary<object, object>)
            .GetConstructor([typeof(IEqualityComparer<object>)])!);
        il.Emit(OpCodes.Stloc, seenLocal);

        // return DeepEqualImpl(a, b, seen)
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Ldloc, seenLocal);
        il.Emit(OpCodes.Call, runtime.UtilDeepEqualImpl);
        il.Emit(OpCodes.Ret);
    }

    /// <summary>
    /// Emits the recursive DeepEqualImpl body with full comparison logic.
    /// </summary>
    private void EmitUtilDeepEqualImplBody(EmittedRuntime runtime)
    {
        var il = runtime.UtilDeepEqualImpl.GetILGenerator();

        var returnTrue = il.DefineLabel();
        var returnFalse = il.DefineLabel();
        var checkNulls = il.DefineLabel();
        var checkTypes = il.DefineLabel();

        // if (ReferenceEquals(a, b)) return true
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Beq, returnTrue);

        // if (a == null || b == null) return false
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Brfalse, returnFalse);
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Brfalse, returnFalse);

        // Check string
        var checkDouble = il.DefineLabel();
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Isinst, _types.String);
        il.Emit(OpCodes.Brfalse, checkDouble);
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Isinst, _types.String);
        il.Emit(OpCodes.Brfalse, returnFalse);
        // Both are strings - compare
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Castclass, _types.String);
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Castclass, _types.String);
        il.Emit(OpCodes.Call, _types.StringOpEquality);
        il.Emit(OpCodes.Ret);

        // Check double
        il.MarkLabel(checkDouble);
        var checkBool = il.DefineLabel();
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Isinst, typeof(double));
        il.Emit(OpCodes.Brfalse, checkBool);
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Isinst, typeof(double));
        il.Emit(OpCodes.Brfalse, returnFalse);
        // Both are doubles - compare (with NaN handling)
        EmitDoubleComparison(il, returnTrue, returnFalse);

        // Check bool
        il.MarkLabel(checkBool);
        var checkArray = il.DefineLabel();
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Isinst, typeof(bool));
        il.Emit(OpCodes.Brfalse, checkArray);
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Isinst, typeof(bool));
        il.Emit(OpCodes.Brfalse, returnFalse);
        // Both are bools - compare
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Unbox_Any, typeof(bool));
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Unbox_Any, typeof(bool));
        il.Emit(OpCodes.Ceq);
        il.Emit(OpCodes.Ret);

        // Check array (IList<object?>)
        il.MarkLabel(checkArray);
        var checkDict = il.DefineLabel();
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Isinst, _types.ListOfObjectNullable);
        il.Emit(OpCodes.Brfalse, checkDict);
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Isinst, _types.ListOfObjectNullable);
        il.Emit(OpCodes.Brfalse, returnFalse);
        // Both are arrays - check cycle then compare elements
        EmitCycleCheckAndArrayComparison(il, runtime, returnTrue, returnFalse);

        // Check dictionary (Dictionary<string, object?>)
        il.MarkLabel(checkDict);
        var defaultCompare = il.DefineLabel();
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Isinst, _types.DictionaryStringObject);
        il.Emit(OpCodes.Brfalse, defaultCompare);
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Isinst, _types.DictionaryStringObject);
        il.Emit(OpCodes.Brfalse, returnFalse);
        // Both are dicts - check cycle then compare entries
        EmitCycleCheckAndDictComparison(il, runtime, returnTrue, returnFalse);

        // Default: use Object.Equals
        il.MarkLabel(defaultCompare);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Call, _types.ObjectStaticEquals);
        il.Emit(OpCodes.Ret);

        // Return labels
        il.MarkLabel(returnTrue);
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Ret);

        il.MarkLabel(returnFalse);
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Ret);
    }

    /// <summary>
    /// Emits double comparison with NaN handling (NaN === NaN is true for deep equality).
    /// </summary>
    private void EmitDoubleComparison(ILGenerator il, Label returnTrue, Label returnFalse)
    {
        var d1 = il.DeclareLocal(typeof(double));
        var d2 = il.DeclareLocal(typeof(double));

        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Unbox_Any, typeof(double));
        il.Emit(OpCodes.Stloc, d1);
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Unbox_Any, typeof(double));
        il.Emit(OpCodes.Stloc, d2);

        // Check if both are NaN
        var notBothNan = il.DefineLabel();
        il.Emit(OpCodes.Ldloc, d1);
        il.Emit(OpCodes.Call, _types.DoubleIsNaN);
        il.Emit(OpCodes.Brfalse, notBothNan);
        il.Emit(OpCodes.Ldloc, d2);
        il.Emit(OpCodes.Call, _types.DoubleIsNaN);
        il.Emit(OpCodes.Brtrue, returnTrue);

        il.MarkLabel(notBothNan);
        // Normal comparison
        il.Emit(OpCodes.Ldloc, d1);
        il.Emit(OpCodes.Ldloc, d2);
        il.Emit(OpCodes.Ceq);
        il.Emit(OpCodes.Ret);
    }

    /// <summary>
    /// Emits cycle check and array element-by-element comparison.
    /// </summary>
    private void EmitCycleCheckAndArrayComparison(ILGenerator il, EmittedRuntime runtime, Label returnTrue, Label returnFalse)
    {
        var listA = il.DeclareLocal(_types.ListOfObjectNullable);
        var listB = il.DeclareLocal(_types.ListOfObjectNullable);
        var count = il.DeclareLocal(_types.Int32);
        var i = il.DeclareLocal(_types.Int32);

        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Castclass, _types.ListOfObjectNullable);
        il.Emit(OpCodes.Stloc, listA);
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Castclass, _types.ListOfObjectNullable);
        il.Emit(OpCodes.Stloc, listB);

        // Cycle check: if seen.TryGetValue(a, out var prev) return ReferenceEquals(b, prev)
        var notInSeen = il.DefineLabel();
        var prevLocal = il.DeclareLocal(_types.Object);
        il.Emit(OpCodes.Ldarg_2); // seen
        il.Emit(OpCodes.Ldarg_0); // a
        il.Emit(OpCodes.Ldloca, prevLocal);
        il.Emit(OpCodes.Callvirt, _types.DictionaryObjectObjectTryGetValue);
        il.Emit(OpCodes.Brfalse, notInSeen);
        // Found in seen - return ReferenceEquals(b, prev)
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Ldloc, prevLocal);
        il.Emit(OpCodes.Ceq);
        il.Emit(OpCodes.Ret);

        il.MarkLabel(notInSeen);
        // Add to seen: seen[a] = b
        il.Emit(OpCodes.Ldarg_2);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Callvirt, typeof(Dictionary<object, object>).GetMethod("set_Item")!);

        // Check counts match
        il.Emit(OpCodes.Ldloc, listA);
        il.Emit(OpCodes.Callvirt, _types.ListOfObjectNullable.GetProperty("Count")!.GetGetMethod()!);
        il.Emit(OpCodes.Ldloc, listB);
        il.Emit(OpCodes.Callvirt, _types.ListOfObjectNullable.GetProperty("Count")!.GetGetMethod()!);
        il.Emit(OpCodes.Bne_Un, returnFalse);

        il.Emit(OpCodes.Ldloc, listA);
        il.Emit(OpCodes.Callvirt, _types.ListOfObjectNullable.GetProperty("Count")!.GetGetMethod()!);
        il.Emit(OpCodes.Stloc, count);

        // Loop: for (i = 0; i < count; i++)
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Stloc, i);
        var loopStart = il.DefineLabel();
        var loopCond = il.DefineLabel();
        il.Emit(OpCodes.Br, loopCond);

        il.MarkLabel(loopStart);
        // if (!DeepEqualImpl(listA[i], listB[i], seen)) return false
        il.Emit(OpCodes.Ldloc, listA);
        il.Emit(OpCodes.Ldloc, i);
        il.Emit(OpCodes.Callvirt, _types.ListOfObjectNullable.GetMethod("get_Item", [typeof(int)])!);
        il.Emit(OpCodes.Ldloc, listB);
        il.Emit(OpCodes.Ldloc, i);
        il.Emit(OpCodes.Callvirt, _types.ListOfObjectNullable.GetMethod("get_Item", [typeof(int)])!);
        il.Emit(OpCodes.Ldarg_2); // seen
        il.Emit(OpCodes.Call, runtime.UtilDeepEqualImpl);
        il.Emit(OpCodes.Brfalse, returnFalse);

        il.Emit(OpCodes.Ldloc, i);
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Add);
        il.Emit(OpCodes.Stloc, i);

        il.MarkLabel(loopCond);
        il.Emit(OpCodes.Ldloc, i);
        il.Emit(OpCodes.Ldloc, count);
        il.Emit(OpCodes.Blt, loopStart);

        // All elements match
        il.Emit(OpCodes.Br, returnTrue);
    }

    /// <summary>
    /// Emits cycle check and dictionary key-value comparison.
    /// </summary>
    private void EmitCycleCheckAndDictComparison(ILGenerator il, EmittedRuntime runtime, Label returnTrue, Label returnFalse)
    {
        var dictA = il.DeclareLocal(_types.DictionaryStringObject);
        var dictB = il.DeclareLocal(_types.DictionaryStringObject);
        var keys = il.DeclareLocal(typeof(List<string>));
        var count = il.DeclareLocal(_types.Int32);
        var i = il.DeclareLocal(_types.Int32);
        var key = il.DeclareLocal(_types.String);

        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Castclass, _types.DictionaryStringObject);
        il.Emit(OpCodes.Stloc, dictA);
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Castclass, _types.DictionaryStringObject);
        il.Emit(OpCodes.Stloc, dictB);

        // Cycle check
        var notInSeen = il.DefineLabel();
        var prevLocal = il.DeclareLocal(_types.Object);
        il.Emit(OpCodes.Ldarg_2);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldloca, prevLocal);
        il.Emit(OpCodes.Callvirt, _types.DictionaryObjectObjectTryGetValue);
        il.Emit(OpCodes.Brfalse, notInSeen);
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Ldloc, prevLocal);
        il.Emit(OpCodes.Ceq);
        il.Emit(OpCodes.Ret);

        il.MarkLabel(notInSeen);
        il.Emit(OpCodes.Ldarg_2);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Callvirt, typeof(Dictionary<object, object>).GetMethod("set_Item")!);

        // Check counts match
        il.Emit(OpCodes.Ldloc, dictA);
        il.Emit(OpCodes.Callvirt, _types.DictionaryStringObject.GetProperty("Count")!.GetGetMethod()!);
        il.Emit(OpCodes.Ldloc, dictB);
        il.Emit(OpCodes.Callvirt, _types.DictionaryStringObject.GetProperty("Count")!.GetGetMethod()!);
        il.Emit(OpCodes.Bne_Un, returnFalse);

        // Get keys as list
        il.Emit(OpCodes.Ldloc, dictA);
        il.Emit(OpCodes.Callvirt, _types.DictionaryStringObject.GetProperty("Keys")!.GetGetMethod()!);
        il.Emit(OpCodes.Newobj, _types.ListStringFromEnumerableCtor);
        il.Emit(OpCodes.Stloc, keys);

        il.Emit(OpCodes.Ldloc, keys);
        il.Emit(OpCodes.Callvirt, typeof(List<string>).GetProperty("Count")!.GetGetMethod()!);
        il.Emit(OpCodes.Stloc, count);

        // Loop through keys
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Stloc, i);
        var loopStart = il.DefineLabel();
        var loopCond = il.DefineLabel();
        il.Emit(OpCodes.Br, loopCond);

        il.MarkLabel(loopStart);
        // key = keys[i]
        il.Emit(OpCodes.Ldloc, keys);
        il.Emit(OpCodes.Ldloc, i);
        il.Emit(OpCodes.Callvirt, _types.ListStringGetItem);
        il.Emit(OpCodes.Stloc, key);

        // if (!dictB.ContainsKey(key)) return false
        il.Emit(OpCodes.Ldloc, dictB);
        il.Emit(OpCodes.Ldloc, key);
        il.Emit(OpCodes.Callvirt, _types.DictionaryStringObject.GetMethod("ContainsKey", [typeof(string)])!);
        il.Emit(OpCodes.Brfalse, returnFalse);

        // if (!DeepEqualImpl(dictA[key], dictB[key], seen)) return false
        il.Emit(OpCodes.Ldloc, dictA);
        il.Emit(OpCodes.Ldloc, key);
        il.Emit(OpCodes.Callvirt, _types.DictionaryStringObject.GetMethod("get_Item", [typeof(string)])!);
        il.Emit(OpCodes.Ldloc, dictB);
        il.Emit(OpCodes.Ldloc, key);
        il.Emit(OpCodes.Callvirt, _types.DictionaryStringObject.GetMethod("get_Item", [typeof(string)])!);
        il.Emit(OpCodes.Ldarg_2);
        il.Emit(OpCodes.Call, runtime.UtilDeepEqualImpl);
        il.Emit(OpCodes.Brfalse, returnFalse);

        il.Emit(OpCodes.Ldloc, i);
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Add);
        il.Emit(OpCodes.Stloc, i);

        il.MarkLabel(loopCond);
        il.Emit(OpCodes.Ldloc, i);
        il.Emit(OpCodes.Ldloc, count);
        il.Emit(OpCodes.Blt, loopStart);

        // All entries match
        il.Emit(OpCodes.Br, returnTrue);
    }
}
