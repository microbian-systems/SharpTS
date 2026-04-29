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
        // For $Array, extract the Elements property.
        // The returned local holds the ORIGINAL receiver, used below for
        // identity-preserving methods (sort/reverse/fill/copyWithin) and to
        // wrap "new array" method results back into $Array so downstream
        // code sees a $Array whenever the input was one.
        var receiverLocal = EmitGetListFromArrayOrList(il, ctx);

        // Methods whose spec says "return this" — the caller expects the same
        // reference the receiver started with. Since we unwrapped to a List,
        // the runtime helper returns the inner List, not the $Array wrapper;
        // to preserve `arr === arr.sort()` we stash the wrapper and push it
        // back at the end.
        bool returnsReceiver = methodName is "sort" or "reverse" or "fill" or "copyWithin";
        // Methods whose spec says "return a new Array" — we want callers to
        // continue seeing a $Array after them (not a bare List<object?>), so
        // downstream array methods / runtime dispatch still match.
        bool returnsNewArray = methodName is
            "slice" or "concat" or "map" or "filter" or "flat" or "flatMap"
            or "splice" or "toReversed" or "toSorted" or "toSpliced" or "with";

        switch (methodName)
        {
            case "pop":
                il.Emit(OpCodes.Call, ctx.Runtime!.ArrayPop);
                break;

            case "shift":
                il.Emit(OpCodes.Call, ctx.Runtime!.ArrayShift);
                break;

            case "unshift":
                // JS `arr.unshift(a, b, c)` prepends all args in order: [a,b,c,...orig].
                // ArrayUnshift(list, el) inserts one element at the start, so iterate
                // in reverse to preserve the final order.
                EmitVariadicListMutation(emitter, arguments, ctx.Runtime!.ArrayUnshift, reverse: true);
                break;

            case "push":
                // JS `arr.push(a, b, c)` appends all args. Iterate forward.
                EmitVariadicListMutation(emitter, arguments, ctx.Runtime!.ArrayPush, reverse: false);
                break;

            case "slice":
                EmitArgsArray(emitter, arguments);
                il.Emit(OpCodes.Call, ctx.Runtime!.ArraySlice);
                break;

            case "map":
            {
                var saved = EmitCallbackAndStashThisArg(emitter, arguments);
                il.Emit(OpCodes.Call, ctx.Runtime!.ArrayMap);
                var resLocal = il.DeclareLocal(ctx.Types.ListOfObject);
                il.Emit(OpCodes.Stloc, resLocal);
                EmitRestoreCallbackThisArg(emitter, saved);
                il.Emit(OpCodes.Ldloc, resLocal);
                break;
            }

            case "filter":
            {
                var saved = EmitCallbackAndStashThisArg(emitter, arguments);
                il.Emit(OpCodes.Call, ctx.Runtime!.ArrayFilter);
                var resLocal = il.DeclareLocal(ctx.Types.ListOfObject);
                il.Emit(OpCodes.Stloc, resLocal);
                EmitRestoreCallbackThisArg(emitter, saved);
                il.Emit(OpCodes.Ldloc, resLocal);
                break;
            }

            case "forEach":
            {
                var saved = EmitCallbackAndStashThisArg(emitter, arguments);
                il.Emit(OpCodes.Call, ctx.Runtime!.ArrayForEach);
                EmitRestoreCallbackThisArg(emitter, saved);
                il.Emit(OpCodes.Ldnull); // forEach returns undefined
                break;
            }

            case "find":
            {
                var saved = EmitCallbackAndStashThisArg(emitter, arguments);
                il.Emit(OpCodes.Call, ctx.Runtime!.ArrayFind);
                var resLocal = il.DeclareLocal(ctx.Types.Object);
                il.Emit(OpCodes.Stloc, resLocal);
                EmitRestoreCallbackThisArg(emitter, saved);
                il.Emit(OpCodes.Ldloc, resLocal);
                break;
            }

            case "findIndex":
            {
                var saved = EmitCallbackAndStashThisArg(emitter, arguments);
                il.Emit(OpCodes.Call, ctx.Runtime!.ArrayFindIndex);
                var resLocal = il.DeclareLocal(ctx.Types.Double);
                il.Emit(OpCodes.Stloc, resLocal);
                EmitRestoreCallbackThisArg(emitter, saved);
                il.Emit(OpCodes.Ldloc, resLocal);
                il.Emit(OpCodes.Box, ctx.Types.Double);
                break;
            }

            case "some":
            {
                var saved = EmitCallbackAndStashThisArg(emitter, arguments);
                il.Emit(OpCodes.Call, ctx.Runtime!.ArraySome);
                var resLocal = il.DeclareLocal(ctx.Types.Object);
                il.Emit(OpCodes.Stloc, resLocal);
                EmitRestoreCallbackThisArg(emitter, saved);
                il.Emit(OpCodes.Ldloc, resLocal);
                break;
            }

            case "every":
            {
                var saved = EmitCallbackAndStashThisArg(emitter, arguments);
                il.Emit(OpCodes.Call, ctx.Runtime!.ArrayEvery);
                var resLocal = il.DeclareLocal(ctx.Types.Object);
                il.Emit(OpCodes.Stloc, resLocal);
                EmitRestoreCallbackThisArg(emitter, saved);
                il.Emit(OpCodes.Ldloc, resLocal);
                break;
            }

            case "reduce":
                EmitArgsArray(emitter, arguments);
                il.Emit(OpCodes.Call, ctx.Runtime!.ArrayReduce);
                break;

            case "reduceRight":
                EmitArgsArray(emitter, arguments);
                il.Emit(OpCodes.Call, ctx.Runtime!.ArrayReduceRight);
                break;

            case "join":
                EmitSingleArgOrNull(emitter, arguments);
                il.Emit(OpCodes.Call, ctx.Runtime!.ArrayJoin);
                break;

            case "concat":
                EmitArgsArray(emitter, arguments);
                il.Emit(OpCodes.Call, ctx.Runtime!.ArrayConcat);
                break;

            case "reverse":
                il.Emit(OpCodes.Call, ctx.Runtime!.ArrayReverse);
                break;

            case "flat":
                EmitSingleArgOrNull(emitter, arguments);
                il.Emit(OpCodes.Call, ctx.Runtime!.ArrayFlat);
                break;

            case "flatMap":
                EmitSingleArgOrNull(emitter, arguments);
                il.Emit(OpCodes.Call, ctx.Runtime!.ArrayFlatMap);
                break;

            case "includes":
                EmitSingleArgOrNull(emitter, arguments);
                il.Emit(OpCodes.Call, ctx.Runtime!.ArrayIncludes);
                break;

            case "indexOf":
            case "lastIndexOf":
                // searchElement (arg 0) + optional fromIndex (arg 1).
                EmitSingleArgOrNull(emitter, arguments);
                if (arguments.Count >= 2)
                {
                    emitter.EmitExpression(arguments[1]);
                    emitter.EmitBoxIfNeeded(arguments[1]);
                }
                else
                {
                    il.Emit(OpCodes.Ldnull);
                }
                il.Emit(OpCodes.Call, methodName == "indexOf" ? ctx.Runtime!.ArrayIndexOf : ctx.Runtime!.ArrayLastIndexOf);
                il.Emit(OpCodes.Box, ctx.Types.Double);
                break;

            case "sort":
                EmitSingleArgOrNull(emitter, arguments);
                il.Emit(OpCodes.Call, ctx.Runtime!.ArraySort);
                break;

            case "toSorted":
                EmitSingleArgOrNull(emitter, arguments);
                il.Emit(OpCodes.Call, ctx.Runtime!.ArrayToSorted);
                break;

            case "splice":
                EmitArgsArray(emitter, arguments);
                il.Emit(OpCodes.Call, ctx.Runtime!.ArraySplice);
                break;

            case "toSpliced":
                EmitArgsArray(emitter, arguments);
                il.Emit(OpCodes.Call, ctx.Runtime!.ArrayToSpliced);
                break;

            case "findLast":
                EmitSingleArgOrNull(emitter, arguments);
                il.Emit(OpCodes.Call, ctx.Runtime!.ArrayFindLast);
                break;

            case "findLastIndex":
                EmitSingleArgOrNull(emitter, arguments);
                il.Emit(OpCodes.Call, ctx.Runtime!.ArrayFindLastIndex);
                il.Emit(OpCodes.Box, ctx.Types.Double);
                break;

            case "toReversed":
                il.Emit(OpCodes.Call, ctx.Runtime!.ArrayToReversed);
                break;

            case "with":
                EmitArgsArray(emitter, arguments);
                il.Emit(OpCodes.Call, ctx.Runtime!.ArrayWith);
                break;

            case "at":
                EmitSingleArgOrNull(emitter, arguments);
                il.Emit(OpCodes.Call, ctx.Runtime!.ArrayAt);
                break;

            case "fill":
                EmitArgsArray(emitter, arguments);
                il.Emit(OpCodes.Call, ctx.Runtime!.ArrayFill);
                break;

            case "copyWithin":
                EmitArgsArray(emitter, arguments);
                il.Emit(OpCodes.Call, ctx.Runtime!.ArrayCopyWithin);
                break;

            case "entries":
                il.Emit(OpCodes.Call, ctx.Runtime!.ArrayEntries);
                break;

            case "keys":
                il.Emit(OpCodes.Call, ctx.Runtime!.ArrayKeys);
                break;

            case "values":
                il.Emit(OpCodes.Call, ctx.Runtime!.ArrayValues);
                break;

            // ECMA-262 23.1.3.32 / 23.1.3.33: Array.prototype.toString /
            // toLocaleString. Both invoke join with default separator.
            // Helper takes (object) — we have the List on stack, which is
            // also the ArrayLikeMaterialize pass-through for List, so
            // route there. Discards args (toString/toLocaleString ignore
            // their args per spec).
            case "toString":
            case "toLocaleString":
                il.Emit(OpCodes.Call, ctx.Runtime!.ArrayProtoToStringHelper);
                break;

            default:
                return false;
        }

        EmitPostCallAdjust(il, ctx, receiverLocal, returnsReceiver, returnsNewArray);
        return true;
    }

    /// <summary>
    /// After a call that leaves a List&lt;object?&gt; on the stack, adjust the
    /// top-of-stack so downstream code sees the expected JS value:
    /// - For "return this" methods: pop the list, push the saved <c>$Array</c>
    ///   receiver (or the bare list if receiver wasn't a <c>$Array</c>).
    /// - For "return new array" methods: wrap the list in a fresh <c>$Array</c>.
    /// Called from per-case branches in <see cref="TryEmitMethodCall"/>.
    /// </summary>
    private static void EmitPostCallAdjust(ILGenerator il, CompilationContext ctx, LocalBuilder receiverLocal, bool returnsReceiver, bool returnsNewArray)
    {
        if (returnsReceiver)
        {
            // Stack: [list (the mutated inner List<object?>)]
            // We want: the ORIGINAL receiver (the $Array wrapper if it was one).
            il.Emit(OpCodes.Pop);
            il.Emit(OpCodes.Ldloc, receiverLocal);
            return;
        }

        if (returnsNewArray)
        {
            // Stack: [list]  → want: [new $Array(list)]
            il.Emit(OpCodes.Newobj, ctx.Runtime!.TSArrayCtor);
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

                // $Arguments check: use _length, not Count, per ECMA-262 sloppy
                // arguments. Only emits the runtime check when ArgumentsType is
                // wired (production: always; tests may skip).
                if (ctx.Runtime?.ArgumentsType != null && ctx.Runtime?.ArgumentsLengthField != null)
                {
                    var notArgsLengthLabel = il.DefineLabel();
                    il.Emit(OpCodes.Ldloc, h.TypedLocal);
                    il.Emit(OpCodes.Isinst, ctx.Runtime!.ArgumentsType);
                    il.Emit(OpCodes.Brfalse, notArgsLengthLabel);
                    il.Emit(OpCodes.Ldloc, h.TypedLocal);
                    il.Emit(OpCodes.Castclass, ctx.Runtime!.ArgumentsType);
                    il.Emit(OpCodes.Ldfld, ctx.Runtime!.ArgumentsLengthField);
                    il.Emit(OpCodes.Conv_R8);
                    il.Emit(OpCodes.Box, ctx.Types.Double);
                    il.Emit(OpCodes.Br, endLabel);
                    il.MarkLabel(notArgsLengthLabel);
                }

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
        // Stage E.2 M2/M3: $Array inherits List<object?>, so `isinst List<object?>`
        // below matches $Array instances — but base `Count` only sees the dense
        // prefix, missing any sparse tail. Check $Array first and use its
        // LongLength getter (int-clamped Length would truncate lengths past
        // int.MaxValue; M3 acceptance demands `a.length === 2147483649` works).
        var tsArrayCheckLabel = il.DefineLabel();
        il.Emit(OpCodes.Ldloc, objLocal);
        il.Emit(OpCodes.Isinst, ctx.Runtime!.TSArrayType);
        il.Emit(OpCodes.Brfalse, tsArrayCheckLabel);
        il.Emit(OpCodes.Ldloc, objLocal);
        il.Emit(OpCodes.Castclass, ctx.Runtime!.TSArrayType);
        il.Emit(OpCodes.Callvirt, ctx.Runtime!.TSArrayLongLengthGetter);
        il.Emit(OpCodes.Conv_R8);
        il.Emit(OpCodes.Box, ctx.Types.Double);
        il.Emit(OpCodes.Br, endLabelNH);

        il.MarkLabel(tsArrayCheckLabel);

        // $Arguments check: use _length per ECMA-262 sloppy arguments.
        if (ctx.Runtime?.ArgumentsType != null && ctx.Runtime?.ArgumentsLengthField != null)
        {
            var notArgsLengthNH = il.DefineLabel();
            il.Emit(OpCodes.Ldloc, objLocal);
            il.Emit(OpCodes.Isinst, ctx.Runtime!.ArgumentsType);
            il.Emit(OpCodes.Brfalse, notArgsLengthNH);
            il.Emit(OpCodes.Ldloc, objLocal);
            il.Emit(OpCodes.Castclass, ctx.Runtime!.ArgumentsType);
            il.Emit(OpCodes.Ldfld, ctx.Runtime!.ArgumentsLengthField);
            il.Emit(OpCodes.Conv_R8);
            il.Emit(OpCodes.Box, ctx.Types.Double);
            il.Emit(OpCodes.Br, endLabelNH);
            il.MarkLabel(notArgsLengthNH);
        }

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
    /// Returns the local that stashes the ORIGINAL receiver — callers emitting
    /// identity-preserving methods (sort/reverse/fill/copyWithin) use this to
    /// push the receiver back after the runtime helper returns.
    /// </summary>
    private static LocalBuilder EmitGetListFromArrayOrList(ILGenerator il, CompilationContext ctx)
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
        return objLocal;
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
    /// Emits the callback (arg 0) and stashes optional thisArg (arg 1) into
    /// `$Runtime._currentCallbackThisArg` so EmitCallbackArgsAndInvoke uses it
    /// as the receiver when invoking the callback. Returns a local holding the
    /// previous thread-static value so the caller can restore it via
    /// <see cref="EmitRestoreCallbackThisArg"/> after the helper call. Null
    /// means no thisArg was passed; per ECMA-262 the callback's `this` becomes
    /// undefined (here passed as null to InvokeMethodValue, which is treated
    /// as no-receiver).
    /// </summary>
    private static LocalBuilder EmitCallbackAndStashThisArg(IEmitterContext emitter, List<Expr> arguments)
    {
        var ctx = emitter.Context;
        var il = ctx.IL;
        var runtime = ctx.Runtime!;

        // Emit callback (arg 0) onto the stack first.
        if (arguments.Count > 0)
        {
            emitter.EmitExpression(arguments[0]);
            emitter.EmitBoxIfNeeded(arguments[0]);
        }
        else
        {
            il.Emit(OpCodes.Ldnull);
        }

        // Save previous thread-static so nested forEach/map calls don't leak.
        var savedLocal = il.DeclareLocal(ctx.Types.Object);
        il.Emit(OpCodes.Ldsfld, runtime.CurrentCallbackThisArgField);
        il.Emit(OpCodes.Stloc, savedLocal);

        // Stash thisArg (arg 1) into the thread-static. When no thisArg is
        // provided, push $Undefined so strict-mode callbacks see
        // `this === undefined` per ECMA-262 (sloppy mode coerces undefined to
        // globalThis at the callback's prologue if needed).
        if (arguments.Count >= 2)
        {
            emitter.EmitExpression(arguments[1]);
            emitter.EmitBoxIfNeeded(arguments[1]);
        }
        else
        {
            il.Emit(OpCodes.Ldsfld, runtime.UndefinedInstance);
        }
        il.Emit(OpCodes.Stsfld, runtime.CurrentCallbackThisArgField);

        return savedLocal;
    }

    /// <summary>
    /// Restores the previous `$Runtime._currentCallbackThisArg` value saved
    /// by <see cref="EmitCallbackAndStashThisArg"/>. Stack-neutral.
    /// </summary>
    private static void EmitRestoreCallbackThisArg(IEmitterContext emitter, LocalBuilder savedLocal)
    {
        var ctx = emitter.Context;
        var il = ctx.IL;
        il.Emit(OpCodes.Ldloc, savedLocal);
        il.Emit(OpCodes.Stsfld, ctx.Runtime!.CurrentCallbackThisArgField);
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
