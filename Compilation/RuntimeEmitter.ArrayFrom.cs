using System.Reflection;
using System.Reflection.Emit;

namespace SharpTS.Compilation;

public partial class RuntimeEmitter
{
    /// <summary>
    /// Emits ArrayFrom: creates an array from an iterable with optional map function.
    /// Signature: List&lt;object&gt; ArrayFrom(object iterable, object mapFn, $TSSymbol iteratorSymbol, Type runtimeType)
    /// </summary>
    private void EmitArrayFrom(TypeBuilder typeBuilder, EmittedRuntime runtime)
    {
        var method = typeBuilder.DefineMethod(
            "ArrayFrom",
            MethodAttributes.Public | MethodAttributes.Static,
            _types.ListOfObject,
            [_types.Object, _types.Object, runtime.TSSymbolType, _types.Type]
        );
        runtime.ArrayFrom = method;

        var il = method.GetILGenerator();

        // Locals
        var resultLocal = il.DeclareLocal(_types.ListOfObject);     // The result list from IterateToList or the mapped result
        var indexLocal = il.DeclareLocal(_types.Int32);             // Loop counter
        var mappedResultLocal = il.DeclareLocal(_types.ListOfObject); // Mapped result when mapFn is provided

        // Labels
        var noMapFnLabel = il.DefineLabel();
        var loopStartLabel = il.DefineLabel();
        var loopEndLabel = il.DefineLabel();
        var returnLabel = il.DefineLabel();

        // Call IterateToList(iterable, iteratorSymbol, runtimeType) to get the initial list
        il.Emit(OpCodes.Ldarg_0);  // iterable
        il.Emit(OpCodes.Ldarg_2);  // iteratorSymbol
        il.Emit(OpCodes.Ldarg_3);  // runtimeType
        il.Emit(OpCodes.Call, runtime.IterateToList);
        il.Emit(OpCodes.Stloc, resultLocal);

        // if (mapFn == null) return result;
        il.Emit(OpCodes.Ldarg_1);  // mapFn
        il.Emit(OpCodes.Brfalse, noMapFnLabel);

        // Create mapped result list
        il.Emit(OpCodes.Newobj, _types.GetConstructor(_types.ListOfObject, _types.EmptyTypes));
        il.Emit(OpCodes.Stloc, mappedResultLocal);

        // int i = 0
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Stloc, indexLocal);

        // Loop: for (int i = 0; i < result.Count; i++)
        il.MarkLabel(loopStartLabel);
        il.Emit(OpCodes.Ldloc, indexLocal);
        il.Emit(OpCodes.Ldloc, resultLocal);
        il.Emit(OpCodes.Callvirt, _types.GetProperty(_types.ListOfObject, "Count").GetGetMethod()!);
        il.Emit(OpCodes.Bge, loopEndLabel);

        // Create args array: [result[i], (double)i]
        il.Emit(OpCodes.Ldc_I4_2);
        il.Emit(OpCodes.Newarr, _types.Object);

        // args[0] = result[i]
        il.Emit(OpCodes.Dup);
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Ldloc, resultLocal);
        il.Emit(OpCodes.Ldloc, indexLocal);
        il.Emit(OpCodes.Callvirt, _types.GetProperty(_types.ListOfObject, "Item").GetGetMethod()!);
        il.Emit(OpCodes.Stelem_Ref);

        // args[1] = (double)i
        il.Emit(OpCodes.Dup);
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Ldloc, indexLocal);
        il.Emit(OpCodes.Conv_R8);
        il.Emit(OpCodes.Box, _types.Double);
        il.Emit(OpCodes.Stelem_Ref);

        // Store args array
        var argsLocal = il.DeclareLocal(_types.ObjectArray);
        il.Emit(OpCodes.Stloc, argsLocal);

        // Call mapFn with args via InvokeValue
        il.Emit(OpCodes.Ldarg_1);  // mapFn
        il.Emit(OpCodes.Ldloc, argsLocal);
        il.Emit(OpCodes.Call, runtime.InvokeValue);

        // Add result to mappedResult
        var callResultLocal = il.DeclareLocal(_types.Object);
        il.Emit(OpCodes.Stloc, callResultLocal);
        il.Emit(OpCodes.Ldloc, mappedResultLocal);
        il.Emit(OpCodes.Ldloc, callResultLocal);
        il.Emit(OpCodes.Callvirt, _types.GetMethod(_types.ListOfObject, "Add", _types.Object));

        // i++
        il.Emit(OpCodes.Ldloc, indexLocal);
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Add);
        il.Emit(OpCodes.Stloc, indexLocal);
        il.Emit(OpCodes.Br, loopStartLabel);

        il.MarkLabel(loopEndLabel);
        // Return mapped result
        il.Emit(OpCodes.Ldloc, mappedResultLocal);
        il.Emit(OpCodes.Br, returnLabel);

        // No map function - return the original result from IterateToList
        il.MarkLabel(noMapFnLabel);
        il.Emit(OpCodes.Ldloc, resultLocal);

        il.MarkLabel(returnLabel);
        il.Emit(OpCodes.Ret);
    }

    /// <summary>
    /// Emits ArrayOf: creates an array from arguments.
    /// Signature: List&lt;object&gt; ArrayOf(object[] args)
    /// </summary>
    private void EmitArrayOf(TypeBuilder typeBuilder, EmittedRuntime runtime)
    {
        var method = typeBuilder.DefineMethod(
            "ArrayOf",
            MethodAttributes.Public | MethodAttributes.Static,
            _types.ListOfObject,
            [_types.ObjectArray]
        );
        runtime.ArrayOf = method;

        var il = method.GetILGenerator();

        // Create new List<object>(args) using the IEnumerable<T> constructor
        il.Emit(OpCodes.Ldarg_0);  // args array
        il.Emit(OpCodes.Newobj, _types.GetConstructor(_types.ListOfObject, typeof(IEnumerable<object>)));
        il.Emit(OpCodes.Ret);
    }

    /// <summary>
    /// Emits the ECMAScript <c>Array</c> constructor (issue #61):
    /// <list type="bullet">
    /// <item><c>Array()</c> / <c>new Array()</c> → empty list.</item>
    /// <item><c>Array(n)</c> where <c>n</c> is a non-negative integer number →
    ///   length-n list pre-filled with <c>null</c> (JS <c>undefined</c> when read
    ///   back through property access — matches JS sparse-array semantics
    ///   closely enough for libraries that allocate-then-assign, e.g. lodash's
    ///   <c>Array(nativeCeil(length / size))</c> in <c>_.chunk</c>).</item>
    /// <item><c>Array(x)</c> where <c>x</c> is not a number → single-element list <c>[x]</c>.</item>
    /// <item><c>Array(a, b, c, …)</c> → list of all args.</item>
    /// </list>
    /// Signature: <c>List&lt;object&gt; ArrayConstructor(object[] args)</c>.
    /// For simplicity and matching the lodash happy-path, out-of-range numeric
    /// inputs (negative, non-integer, &gt; UInt32.MaxValue) are clamped to 0
    /// rather than throwing <c>RangeError</c> — the stricter spec behavior
    /// can be layered in later if a test demands it.
    /// </summary>
    private void EmitArrayConstructor(TypeBuilder typeBuilder, EmittedRuntime runtime)
    {
        var method = typeBuilder.DefineMethod(
            "ArrayConstructor",
            MethodAttributes.Public | MethodAttributes.Static,
            _types.ListOfObject,
            [_types.ObjectArray]
        );
        runtime.ArrayConstructor = method;

        var il = method.GetILGenerator();
        var lenLocal = il.DeclareLocal(_types.Int32);
        var argLocal = il.DeclareLocal(_types.Object);
        var dLocal = il.DeclareLocal(_types.Double);
        var nLocal = il.DeclareLocal(_types.Int32);
        var listLocal = il.DeclareLocal(_types.ListOfObject);
        var iLocal = il.DeclareLocal(_types.Int32);

        var emptyCaseLabel = il.DefineLabel();
        var singleArgLabel = il.DefineLabel();
        var multiArgLabel = il.DefineLabel();
        var notNumberLabel = il.DefineLabel();
        var fillLoopStart = il.DefineLabel();
        var fillLoopEnd = il.DefineLabel();

        // if (args == null) return new List<object>();
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Brfalse, emptyCaseLabel);

        // lenLocal = args.Length
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldlen);
        il.Emit(OpCodes.Conv_I4);
        il.Emit(OpCodes.Stloc, lenLocal);

        // if (len == 0) → empty
        il.Emit(OpCodes.Ldloc, lenLocal);
        il.Emit(OpCodes.Brfalse, emptyCaseLabel);

        // if (len == 1) → single-arg branch
        il.Emit(OpCodes.Ldloc, lenLocal);
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Beq, singleArgLabel);

        // else → multi-arg branch
        il.Emit(OpCodes.Br, multiArgLabel);

        // --- empty: return new List<object>() ---
        il.MarkLabel(emptyCaseLabel);
        il.Emit(OpCodes.Newobj, _types.GetDefaultConstructor(_types.ListOfObject));
        il.Emit(OpCodes.Ret);

        // --- single-arg: check numeric-vs-other ---
        il.MarkLabel(singleArgLabel);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Ldelem_Ref);
        il.Emit(OpCodes.Stloc, argLocal);

        // if (!(arg is double)) → single-element list
        il.Emit(OpCodes.Ldloc, argLocal);
        il.Emit(OpCodes.Isinst, _types.Double);
        il.Emit(OpCodes.Brfalse, notNumberLabel);

        // numeric: unbox to double, clamp to [0, UInt32.MaxValue] integer
        il.Emit(OpCodes.Ldloc, argLocal);
        il.Emit(OpCodes.Unbox_Any, _types.Double);
        il.Emit(OpCodes.Stloc, dLocal);

        // n = (d < 0 || d != floor(d) || d > uint.MaxValue) ? 0 : (int)d
        // Implemented as: n = 0 by default; if in range, n = (int)d
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Stloc, nLocal);

        var invalidRangeLabel = il.DefineLabel();
        // if (d < 0) → skip
        il.Emit(OpCodes.Ldloc, dLocal);
        il.Emit(OpCodes.Ldc_R8, 0.0);
        il.Emit(OpCodes.Blt_Un, invalidRangeLabel);
        // if (d > uint.MaxValue) → skip
        il.Emit(OpCodes.Ldloc, dLocal);
        il.Emit(OpCodes.Ldc_R8, (double)uint.MaxValue);
        il.Emit(OpCodes.Bgt_Un, invalidRangeLabel);
        // if (d != floor(d)) → skip (non-integer)
        il.Emit(OpCodes.Ldloc, dLocal);
        il.Emit(OpCodes.Ldloc, dLocal);
        il.Emit(OpCodes.Call, _types.GetMethod(_types.Math, "Floor", _types.Double));
        il.Emit(OpCodes.Bne_Un, invalidRangeLabel);
        // in range: n = (int)d
        il.Emit(OpCodes.Ldloc, dLocal);
        il.Emit(OpCodes.Conv_I4);
        il.Emit(OpCodes.Stloc, nLocal);
        il.MarkLabel(invalidRangeLabel);

        // list = new List<object>(n)
        il.Emit(OpCodes.Ldloc, nLocal);
        il.Emit(OpCodes.Newobj, _types.GetConstructor(_types.ListOfObject, _types.Int32));
        il.Emit(OpCodes.Stloc, listLocal);

        // for (i = 0; i < n; i++) list.Add(null)
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Stloc, iLocal);

        il.MarkLabel(fillLoopStart);
        il.Emit(OpCodes.Ldloc, iLocal);
        il.Emit(OpCodes.Ldloc, nLocal);
        il.Emit(OpCodes.Bge, fillLoopEnd);

        il.Emit(OpCodes.Ldloc, listLocal);
        il.Emit(OpCodes.Ldnull);
        il.Emit(OpCodes.Callvirt, _types.ListOfObject.GetMethod("Add")!);

        il.Emit(OpCodes.Ldloc, iLocal);
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Add);
        il.Emit(OpCodes.Stloc, iLocal);
        il.Emit(OpCodes.Br, fillLoopStart);

        il.MarkLabel(fillLoopEnd);
        il.Emit(OpCodes.Ldloc, listLocal);
        il.Emit(OpCodes.Ret);

        // --- single-arg, non-numeric: return [arg] ---
        il.MarkLabel(notNumberLabel);
        il.Emit(OpCodes.Newobj, _types.GetDefaultConstructor(_types.ListOfObject));
        il.Emit(OpCodes.Dup);
        il.Emit(OpCodes.Ldloc, argLocal);
        il.Emit(OpCodes.Callvirt, _types.ListOfObject.GetMethod("Add")!);
        il.Emit(OpCodes.Ret);

        // --- multi-arg: new List<object>(args) via IEnumerable<T> ctor ---
        il.MarkLabel(multiArgLabel);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Newobj, _types.GetConstructor(_types.ListOfObject, typeof(IEnumerable<object>)));
        il.Emit(OpCodes.Ret);
    }
}
