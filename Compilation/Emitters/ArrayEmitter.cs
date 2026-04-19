using System.Reflection.Emit;
using SharpTS.Parsing;

namespace SharpTS.Compilation.Emitters;

/// <summary>
/// Emitter strategy for array method calls and property access.
/// Handles all TypeScript array methods like push, pop, map, filter, etc.
/// </summary>
public sealed class ArrayEmitter : ITypeEmitterStrategy
{
    /// <summary>
    /// Attempts to emit IL for a method call on an array receiver.
    /// </summary>
    public bool TryEmitMethodCall(IEmitterContext emitter, Expr receiver, string methodName, List<Expr> arguments)
    {
        var ctx = emitter.Context;
        var il = ctx.IL;

        // Emit the array object
        emitter.EmitExpression(receiver);
        emitter.EmitBoxIfNeeded(receiver);

        // Handle both List<object> and $Array types
        // For $Array, extract the Elements property
        EmitGetListFromArrayOrList(il, ctx);

        switch (methodName)
        {
            case "pop":
                il.Emit(OpCodes.Call, ctx.Runtime!.ArrayPop);
                return true;

            case "shift":
                il.Emit(OpCodes.Call, ctx.Runtime!.ArrayShift);
                return true;

            case "unshift":
                // JS `arr.unshift(a, b, c)` prepends all args in order: [a,b,c,...orig].
                // ArrayUnshift(list, el) inserts one element at the start, so iterate
                // in reverse to preserve the final order.
                EmitVariadicListMutation(emitter, arguments, ctx.Runtime!.ArrayUnshift, reverse: true);
                return true;

            case "push":
                // JS `arr.push(a, b, c)` appends all args. Iterate forward.
                EmitVariadicListMutation(emitter, arguments, ctx.Runtime!.ArrayPush, reverse: false);
                return true;

            case "slice":
                EmitArgsArray(emitter, arguments);
                il.Emit(OpCodes.Call, ctx.Runtime!.ArraySlice);
                return true;

            case "map":
                EmitSingleArgOrNull(emitter, arguments);
                il.Emit(OpCodes.Call, ctx.Runtime!.ArrayMap);
                return true;

            case "filter":
                EmitSingleArgOrNull(emitter, arguments);
                il.Emit(OpCodes.Call, ctx.Runtime!.ArrayFilter);
                return true;

            case "forEach":
                EmitSingleArgOrNull(emitter, arguments);
                il.Emit(OpCodes.Call, ctx.Runtime!.ArrayForEach);
                il.Emit(OpCodes.Ldnull); // forEach returns undefined
                return true;

            case "find":
                EmitSingleArgOrNull(emitter, arguments);
                il.Emit(OpCodes.Call, ctx.Runtime!.ArrayFind);
                return true;

            case "findIndex":
                EmitSingleArgOrNull(emitter, arguments);
                il.Emit(OpCodes.Call, ctx.Runtime!.ArrayFindIndex);
                il.Emit(OpCodes.Box, ctx.Types.Double);
                return true;

            case "some":
                EmitSingleArgOrNull(emitter, arguments);
                il.Emit(OpCodes.Call, ctx.Runtime!.ArraySome);
                return true;

            case "every":
                EmitSingleArgOrNull(emitter, arguments);
                il.Emit(OpCodes.Call, ctx.Runtime!.ArrayEvery);
                return true;

            case "reduce":
                EmitArgsArray(emitter, arguments);
                il.Emit(OpCodes.Call, ctx.Runtime!.ArrayReduce);
                return true;

            case "reduceRight":
                EmitArgsArray(emitter, arguments);
                il.Emit(OpCodes.Call, ctx.Runtime!.ArrayReduceRight);
                return true;

            case "join":
                EmitSingleArgOrNull(emitter, arguments);
                il.Emit(OpCodes.Call, ctx.Runtime!.ArrayJoin);
                return true;

            case "concat":
                EmitSingleArgOrNull(emitter, arguments);
                il.Emit(OpCodes.Call, ctx.Runtime!.ArrayConcat);
                return true;

            case "reverse":
                il.Emit(OpCodes.Call, ctx.Runtime!.ArrayReverse);
                return true;

            case "flat":
                EmitSingleArgOrNull(emitter, arguments);
                il.Emit(OpCodes.Call, ctx.Runtime!.ArrayFlat);
                return true;

            case "flatMap":
                EmitSingleArgOrNull(emitter, arguments);
                il.Emit(OpCodes.Call, ctx.Runtime!.ArrayFlatMap);
                return true;

            case "includes":
                EmitSingleArgOrNull(emitter, arguments);
                il.Emit(OpCodes.Call, ctx.Runtime!.ArrayIncludes);
                return true;

            case "indexOf":
                EmitSingleArgOrNull(emitter, arguments);
                il.Emit(OpCodes.Call, ctx.Runtime!.ArrayIndexOf);
                il.Emit(OpCodes.Box, ctx.Types.Double);
                return true;

            case "sort":
                EmitSingleArgOrNull(emitter, arguments);
                il.Emit(OpCodes.Call, ctx.Runtime!.ArraySort);
                return true;

            case "toSorted":
                EmitSingleArgOrNull(emitter, arguments);
                il.Emit(OpCodes.Call, ctx.Runtime!.ArrayToSorted);
                return true;

            case "splice":
                EmitArgsArray(emitter, arguments);
                il.Emit(OpCodes.Call, ctx.Runtime!.ArraySplice);
                return true;

            case "toSpliced":
                EmitArgsArray(emitter, arguments);
                il.Emit(OpCodes.Call, ctx.Runtime!.ArrayToSpliced);
                return true;

            case "findLast":
                EmitSingleArgOrNull(emitter, arguments);
                il.Emit(OpCodes.Call, ctx.Runtime!.ArrayFindLast);
                return true;

            case "findLastIndex":
                EmitSingleArgOrNull(emitter, arguments);
                il.Emit(OpCodes.Call, ctx.Runtime!.ArrayFindLastIndex);
                il.Emit(OpCodes.Box, ctx.Types.Double);
                return true;

            case "toReversed":
                il.Emit(OpCodes.Call, ctx.Runtime!.ArrayToReversed);
                return true;

            case "with":
                EmitArgsArray(emitter, arguments);
                il.Emit(OpCodes.Call, ctx.Runtime!.ArrayWith);
                return true;

            case "at":
                EmitSingleArgOrNull(emitter, arguments);
                il.Emit(OpCodes.Call, ctx.Runtime!.ArrayAt);
                return true;

            case "fill":
                EmitArgsArray(emitter, arguments);
                il.Emit(OpCodes.Call, ctx.Runtime!.ArrayFill);
                return true;

            case "copyWithin":
                EmitArgsArray(emitter, arguments);
                il.Emit(OpCodes.Call, ctx.Runtime!.ArrayCopyWithin);
                return true;

            case "entries":
                il.Emit(OpCodes.Call, ctx.Runtime!.ArrayEntries);
                return true;

            case "keys":
                il.Emit(OpCodes.Call, ctx.Runtime!.ArrayKeys);
                return true;

            case "values":
                il.Emit(OpCodes.Call, ctx.Runtime!.ArrayValues);
                return true;

            default:
                return false;
        }
    }

    /// <summary>
    /// Attempts to emit IL for a property get on an array receiver.
    /// Handles .length by emitting direct List&lt;T&gt;.Count access,
    /// bypassing the full GetProperty runtime dispatch chain.
    /// </summary>
    public bool TryEmitPropertyGet(IEmitterContext emitter, Expr receiver, string propertyName)
    {
        if (propertyName != "length") return false;

        var ctx = emitter.Context;
        var il = ctx.IL;

        // Resolve the descriptor from the receiver's static type
        var desc = ArrayElements.Resolve(ctx.TypeMap?.Get(receiver));
        if (desc == null) return false;

        // Hoisted path: use cached typed local from loop preamble
        if (receiver is Expr.Variable arrVar)
        {
            var hoisted = ctx.TryGetHoistedArray(arrVar.Name.Lexeme);
            if (hoisted.HasValue)
            {
                var h = hoisted.Value;
                var listType = h.Descriptor.GetListType(ctx.Types);
                var fallbackLabel = il.DefineLabel();
                var endLabel = il.DefineLabel();

                il.Emit(OpCodes.Ldloc, h.TypedLocal);
                il.Emit(OpCodes.Brfalse, fallbackLabel);

                il.Emit(OpCodes.Ldloc, h.TypedLocal);
                il.Emit(OpCodes.Callvirt, ctx.Types.GetProperty(listType, "Count").GetGetMethod()!);
                il.Emit(OpCodes.Conv_R8);
                il.Emit(OpCodes.Box, ctx.Types.Double);
                il.Emit(OpCodes.Br, endLabel);

                il.MarkLabel(fallbackLabel);
                emitter.EmitExpression(receiver);
                emitter.EmitBoxIfNeeded(receiver);
                il.Emit(OpCodes.Call, ctx.Runtime!.GetLength);
                il.Emit(OpCodes.Conv_R8);
                il.Emit(OpCodes.Box, ctx.Types.Double);

                il.MarkLabel(endLabel);
                return true;
            }
        }

        // Non-hoisted path: per-access isinst guard
        var listTypeNH = desc.GetListType(ctx.Types);

        emitter.EmitExpression(receiver);
        emitter.EmitBoxIfNeeded(receiver);

        var objLocal = il.DeclareLocal(ctx.Types.Object);
        il.Emit(OpCodes.Stloc, objLocal);

        var fallbackLabelNH = il.DefineLabel();
        var endLabelNH = il.DefineLabel();

        il.Emit(OpCodes.Ldloc, objLocal);
        il.Emit(OpCodes.Isinst, listTypeNH);
        il.Emit(OpCodes.Brfalse, fallbackLabelNH);

        il.Emit(OpCodes.Ldloc, objLocal);
        il.Emit(OpCodes.Castclass, listTypeNH);
        il.Emit(OpCodes.Callvirt, ctx.Types.GetProperty(listTypeNH, "Count").GetGetMethod()!);
        il.Emit(OpCodes.Conv_R8);
        il.Emit(OpCodes.Box, ctx.Types.Double);
        il.Emit(OpCodes.Br, endLabelNH);

        il.MarkLabel(fallbackLabelNH);
        il.Emit(OpCodes.Ldloc, objLocal);
        il.Emit(OpCodes.Call, ctx.Runtime!.GetLength);
        il.Emit(OpCodes.Conv_R8);
        il.Emit(OpCodes.Box, ctx.Types.Double);

        il.MarkLabel(endLabelNH);
        return true;
    }

    /// <summary>
    /// Attempts to emit IL for a property set on an array receiver.
    /// Array properties are not directly settable.
    /// </summary>
    public bool TryEmitPropertySet(IEmitterContext emitter, Expr receiver, string propertyName, Expr value)
    {
        return false;
    }

    #region Helper Methods

    /// <summary>
    /// Emits code to convert an array value (either List&lt;object&gt; or $Array) to List&lt;object&gt;.
    /// The value is expected to be on the stack; leaves List&lt;object&gt; on the stack.
    /// </summary>
    private static void EmitGetListFromArrayOrList(ILGenerator il, CompilationContext ctx)
    {
        var objLocal = il.DeclareLocal(ctx.Types.Object);
        il.Emit(OpCodes.Stloc, objLocal);

        var isListLabel = il.DefineLabel();
        var endLabel = il.DefineLabel();

        // Check if it's already a List<object>
        il.Emit(OpCodes.Ldloc, objLocal);
        il.Emit(OpCodes.Isinst, ctx.Types.ListOfObject);
        il.Emit(OpCodes.Brtrue, isListLabel);

        // Check if it's $Array - get Elements
        var tsArrayLabel = il.DefineLabel();
        il.Emit(OpCodes.Ldloc, objLocal);
        il.Emit(OpCodes.Isinst, ctx.Runtime!.TSArrayType);
        il.Emit(OpCodes.Brtrue, tsArrayLabel);

        // Check if it's a typed array (List<double>, List<bool>) via IList interface.
        // Convert to List<object?> by iterating and boxing elements.
        // This is a slow path only hit when array methods are called on typed arrays.
        var ilistType = typeof(System.Collections.IList);
        var typedListLabel = il.DefineLabel();
        il.Emit(OpCodes.Ldloc, objLocal);
        il.Emit(OpCodes.Isinst, ilistType);
        il.Emit(OpCodes.Brtrue, typedListLabel);

        // Unknown type - push null as fallback (shouldn't happen)
        il.Emit(OpCodes.Ldnull);
        il.Emit(OpCodes.Br, endLabel);

        // $Array path
        il.MarkLabel(tsArrayLabel);
        il.Emit(OpCodes.Ldloc, objLocal);
        il.Emit(OpCodes.Castclass, ctx.Runtime!.TSArrayType);
        il.Emit(OpCodes.Callvirt, ctx.Runtime.TSArrayElementsGetter);
        il.Emit(OpCodes.Br, endLabel);

        // Typed list path: convert IList to List<object?> via boxing loop
        il.MarkLabel(typedListLabel);
        var ilistLocal = il.DeclareLocal(ilistType);
        il.Emit(OpCodes.Ldloc, objLocal);
        il.Emit(OpCodes.Castclass, ilistType);
        il.Emit(OpCodes.Stloc, ilistLocal);
        // new List<object?>(ilist.Count)
        il.Emit(OpCodes.Ldloc, ilistLocal);
        il.Emit(OpCodes.Callvirt, typeof(System.Collections.ICollection).GetProperty("Count")!.GetGetMethod()!);
        il.Emit(OpCodes.Newobj, ctx.Types.GetConstructor(ctx.Types.ListOfObject, ctx.Types.Int32));
        var resultLocal = il.DeclareLocal(ctx.Types.ListOfObject);
        il.Emit(OpCodes.Stloc, resultLocal);
        // for (int i = 0; i < ilist.Count; i++) result.Add(ilist[i]);
        var idxLocal = il.DeclareLocal(ctx.Types.Int32);
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Stloc, idxLocal);
        var loopCheck = il.DefineLabel();
        var loopBody = il.DefineLabel();
        il.Emit(OpCodes.Br, loopCheck);
        il.MarkLabel(loopBody);
        il.Emit(OpCodes.Ldloc, resultLocal);
        il.Emit(OpCodes.Ldloc, ilistLocal);
        il.Emit(OpCodes.Ldloc, idxLocal);
        il.Emit(OpCodes.Callvirt, ilistType.GetMethod("get_Item", [typeof(int)])!); // returns boxed object
        il.Emit(OpCodes.Callvirt, ctx.Types.ListOfObject.GetMethod("Add", [ctx.Types.Object])!);
        il.Emit(OpCodes.Ldloc, idxLocal);
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Add);
        il.Emit(OpCodes.Stloc, idxLocal);
        il.MarkLabel(loopCheck);
        il.Emit(OpCodes.Ldloc, idxLocal);
        il.Emit(OpCodes.Ldloc, ilistLocal);
        il.Emit(OpCodes.Callvirt, typeof(System.Collections.ICollection).GetProperty("Count")!.GetGetMethod()!);
        il.Emit(OpCodes.Blt, loopBody);
        il.Emit(OpCodes.Ldloc, resultLocal);
        il.Emit(OpCodes.Br, endLabel);

        // Already a List - cast it
        il.MarkLabel(isListLabel);
        il.Emit(OpCodes.Ldloc, objLocal);
        il.Emit(OpCodes.Castclass, ctx.Types.ListOfObject);

        il.MarkLabel(endLabel);
    }

    /// <summary>
    /// Emits a single argument or null if no arguments provided.
    /// </summary>
    private static void EmitSingleArgOrNull(IEmitterContext emitter, List<Expr> arguments)
    {
        var ctx = emitter.Context;
        var il = ctx.IL;

        if (arguments.Count > 0)
        {
            emitter.EmitExpression(arguments[0]);
            emitter.EmitBoxIfNeeded(arguments[0]);
        }
        else
        {
            il.Emit(OpCodes.Ldnull);
        }
    }

    /// <summary>
    /// Emits all arguments as an object array.
    /// </summary>
    private static void EmitArgsArray(IEmitterContext emitter, List<Expr> arguments)
    {
        var ctx = emitter.Context;
        var il = ctx.IL;

        il.Emit(OpCodes.Ldc_I4, arguments.Count);
        il.Emit(OpCodes.Newarr, ctx.Types.Object);
        for (int i = 0; i < arguments.Count; i++)
        {
            il.Emit(OpCodes.Dup);
            il.Emit(OpCodes.Ldc_I4, i);
            emitter.EmitExpression(arguments[i]);
            emitter.EmitBoxIfNeeded(arguments[i]);
            il.Emit(OpCodes.Stelem_Ref);
        }
    }

    /// <summary>
    /// Emits a loop calling a single-element list-mutation helper (e.g. ArrayPush /
    /// ArrayUnshift) once per argument, then leaves the boxed final <c>list.Count</c>
    /// on the stack as the method's return value (matches JS's `arr.push(...)` /
    /// `arr.unshift(...)` semantics which return the new length).
    ///
    /// Stack on entry: <c>[list]</c>. Stack on exit: <c>[boxed-double(count)]</c>.
    ///
    /// When <paramref name="reverse"/> is true, arguments are iterated last-to-first
    /// (needed for unshift so the final order matches JS: `unshift(a,b,c)` yields
    /// <c>[a,b,c,...orig]</c>).
    /// </summary>
    private static void EmitVariadicListMutation(IEmitterContext emitter, List<Expr> arguments, System.Reflection.Emit.MethodBuilder runtimeMethod, bool reverse)
    {
        var ctx = emitter.Context;
        var il = ctx.IL;

        // Stash the list in a local so we can reuse it across iterations and then
        // read its Count at the end.
        var listLocal = il.DeclareLocal(ctx.Types.ListOfObject);
        il.Emit(OpCodes.Stloc, listLocal);

        if (arguments.Count == 0)
        {
            // No args: per JS, `arr.push()` returns the current length unchanged.
            il.Emit(OpCodes.Ldloc, listLocal);
            il.Emit(OpCodes.Callvirt, ctx.Types.GetProperty(ctx.Types.ListOfObject, "Count").GetGetMethod()!);
            il.Emit(OpCodes.Conv_R8);
            il.Emit(OpCodes.Box, ctx.Types.Double);
            return;
        }

        for (int step = 0; step < arguments.Count; step++)
        {
            int i = reverse ? arguments.Count - 1 - step : step;
            il.Emit(OpCodes.Ldloc, listLocal);
            emitter.EmitExpression(arguments[i]);
            emitter.EmitBoxIfNeeded(arguments[i]);
            il.Emit(OpCodes.Call, runtimeMethod);
            il.Emit(OpCodes.Pop); // discard the per-element count return
        }

        // Final return value: (double)list.Count, boxed.
        il.Emit(OpCodes.Ldloc, listLocal);
        il.Emit(OpCodes.Callvirt, ctx.Types.GetProperty(ctx.Types.ListOfObject, "Count").GetGetMethod()!);
        il.Emit(OpCodes.Conv_R8);
        il.Emit(OpCodes.Box, ctx.Types.Double);
    }

    #endregion
}
