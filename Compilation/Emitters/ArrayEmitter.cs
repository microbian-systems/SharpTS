using System.Reflection.Emit;
using SharpTS.Compilation.ArrowInlining;
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
                // Inline fast path: literal non-capturing expression-body
                // arrow — emit body IL directly into the loop, eliminating
                // the per-iteration Func<>.Invoke. Issue #96 M2.
                if (arguments.Count == 1
                    && ArrowInlinabilityCheck.TryGetEligibleArrow(emitter, arguments[0], expectedParamCount: 1, out var inlinedAfMap))
                {
                    InlinedBodyEmitter.EmitInlinedMap(emitter, inlinedAfMap);
                    break;
                }
                // Fast path: arr.map(literal-non-capturing-arrow) — direct
                // delegate dispatch, skipping $TSFunction allocation and the
                // MethodInvoker boundary. See issue #96 Phase A.
                if (TryEmitDirectDelegateCall(emitter, arguments, ctx.Runtime!.ArrayMapDirect))
                    break;
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
                // Inline fast path. Issue #96 M2.
                if (arguments.Count == 1
                    && ArrowInlinabilityCheck.TryGetEligibleArrow(emitter, arguments[0], expectedParamCount: 1, out var inlinedAfFilter))
                {
                    InlinedBodyEmitter.EmitInlinedFilter(emitter, inlinedAfFilter);
                    break;
                }
                if (TryEmitDirectDelegateCall(emitter, arguments, ctx.Runtime!.ArrayFilterDirect, ctx.Runtime!.ArrayFilterDirectBool))
                    break;
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
                // Inline fast path: literal non-capturing arrow with an
                // expression body — emit the body's IL directly into the
                // loop, eliminating the Func<>.Invoke virtual call that the
                // Direct path still pays. Issue #96 M1.
                if (arguments.Count == 1
                    && ArrowInlinabilityCheck.TryGetEligibleArrow(emitter, arguments[0], expectedParamCount: 1, out var inlinedAf))
                {
                    InlinedBodyEmitter.EmitInlinedForEach(emitter, inlinedAf);
                    il.Emit(OpCodes.Ldnull); // forEach returns undefined; inliner is void
                    break;
                }
                if (TryEmitDirectDelegateCall(emitter, arguments, ctx.Runtime!.ArrayForEachDirect))
                {
                    il.Emit(OpCodes.Ldnull); // forEach returns undefined; helper is void
                    break;
                }
                var saved = EmitCallbackAndStashThisArg(emitter, arguments);
                il.Emit(OpCodes.Call, ctx.Runtime!.ArrayForEach);
                EmitRestoreCallbackThisArg(emitter, saved);
                il.Emit(OpCodes.Ldnull); // forEach returns undefined
                break;
            }

            case "find":
            {
                // Inline fast path. Issue #96 M3.
                if (arguments.Count == 1
                    && ArrowInlinabilityCheck.TryGetEligibleArrow(emitter, arguments[0], expectedParamCount: 1, out var inlinedAfFind))
                {
                    InlinedBodyEmitter.EmitInlinedShortCircuit(emitter, inlinedAfFind, InlinedBodyEmitter.ShortCircuitKind.Find);
                    break;
                }
                if (TryEmitDirectDelegateCall(emitter, arguments, ctx.Runtime!.ArrayFindDirect, ctx.Runtime!.ArrayFindDirectBool))
                    break;
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
                // Inline fast path: leaves raw double on stack; box after. Issue #96 M3.
                if (arguments.Count == 1
                    && ArrowInlinabilityCheck.TryGetEligibleArrow(emitter, arguments[0], expectedParamCount: 1, out var inlinedAfFindIdx))
                {
                    InlinedBodyEmitter.EmitInlinedShortCircuit(emitter, inlinedAfFindIdx, InlinedBodyEmitter.ShortCircuitKind.FindIndex);
                    il.Emit(OpCodes.Box, ctx.Types.Double);
                    break;
                }
                if (TryEmitDirectDelegateCall(emitter, arguments, ctx.Runtime!.ArrayFindIndexDirect, ctx.Runtime!.ArrayFindIndexDirectBool))
                {
                    il.Emit(OpCodes.Box, ctx.Types.Double); // helper returns double
                    break;
                }
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
                if (arguments.Count == 1
                    && ArrowInlinabilityCheck.TryGetEligibleArrow(emitter, arguments[0], expectedParamCount: 1, out var inlinedAfSome))
                {
                    InlinedBodyEmitter.EmitInlinedShortCircuit(emitter, inlinedAfSome, InlinedBodyEmitter.ShortCircuitKind.Some);
                    break;
                }
                if (TryEmitDirectDelegateCall(emitter, arguments, ctx.Runtime!.ArraySomeDirect, ctx.Runtime!.ArraySomeDirectBool))
                    break;
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
                if (arguments.Count == 1
                    && ArrowInlinabilityCheck.TryGetEligibleArrow(emitter, arguments[0], expectedParamCount: 1, out var inlinedAfEvery))
                {
                    InlinedBodyEmitter.EmitInlinedShortCircuit(emitter, inlinedAfEvery, InlinedBodyEmitter.ShortCircuitKind.Every);
                    break;
                }
                if (TryEmitDirectDelegateCall(emitter, arguments, ctx.Runtime!.ArrayEveryDirect, ctx.Runtime!.ArrayEveryDirectBool))
                    break;
                var saved = EmitCallbackAndStashThisArg(emitter, arguments);
                il.Emit(OpCodes.Call, ctx.Runtime!.ArrayEvery);
                var resLocal = il.DeclareLocal(ctx.Types.Object);
                il.Emit(OpCodes.Stloc, resLocal);
                EmitRestoreCallbackThisArg(emitter, saved);
                il.Emit(OpCodes.Ldloc, resLocal);
                break;
            }

            case "reduce":
            {
                // Inline fast path: 2-arg form (initial value present) with
                // a 2-param literal arrow (acc, el). Issue #96 M4. The
                // no-initial-value form takes the slow path (would need a
                // first-present-slot scan).
                if (arguments.Count == 2
                    && ArrowInlinabilityCheck.TryGetEligibleArrow(emitter, arguments[0], expectedParamCount: 2, out var inlinedAfReduce))
                {
                    InlinedBodyEmitter.EmitInlinedReduce(emitter, inlinedAfReduce, arguments[1], reverse: false);
                    break;
                }
                if (TryEmitReduceDirectCall(emitter, arguments))
                    break;
                EmitArgsArray(emitter, arguments);
                il.Emit(OpCodes.Call, ctx.Runtime!.ArrayReduce);
                break;
            }

            case "reduceRight":
            {
                // Inline fast path: 2-arg form. Issue #96 M4. No Direct
                // helper exists for reduceRight today — capturing arrows
                // and no-initial form take the slow path.
                if (arguments.Count == 2
                    && ArrowInlinabilityCheck.TryGetEligibleArrow(emitter, arguments[0], expectedParamCount: 2, out var inlinedAfReduceRight))
                {
                    InlinedBodyEmitter.EmitInlinedReduce(emitter, inlinedAfReduceRight, arguments[1], reverse: true);
                    break;
                }
                EmitArgsArray(emitter, arguments);
                il.Emit(OpCodes.Call, ctx.Runtime!.ArrayReduceRight);
                break;
            }

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
            {
                // Inline fast path. Issue #96 M3. No Direct helper exists for
                // findLast yet — capturing/typed arrows still take the slow
                // path; that's a deferred optimization.
                if (arguments.Count == 1
                    && ArrowInlinabilityCheck.TryGetEligibleArrow(emitter, arguments[0], expectedParamCount: 1, out var inlinedAfFindLast))
                {
                    InlinedBodyEmitter.EmitInlinedShortCircuit(emitter, inlinedAfFindLast, InlinedBodyEmitter.ShortCircuitKind.FindLast);
                    break;
                }
                var saved = EmitCallbackAndStashThisArg(emitter, arguments);
                il.Emit(OpCodes.Call, ctx.Runtime!.ArrayFindLast);
                var resLocal = il.DeclareLocal(ctx.Types.Object);
                il.Emit(OpCodes.Stloc, resLocal);
                EmitRestoreCallbackThisArg(emitter, saved);
                il.Emit(OpCodes.Ldloc, resLocal);
                break;
            }

            case "findLastIndex":
            {
                if (arguments.Count == 1
                    && ArrowInlinabilityCheck.TryGetEligibleArrow(emitter, arguments[0], expectedParamCount: 1, out var inlinedAfFindLastIdx))
                {
                    InlinedBodyEmitter.EmitInlinedShortCircuit(emitter, inlinedAfFindLastIdx, InlinedBodyEmitter.ShortCircuitKind.FindLastIndex);
                    il.Emit(OpCodes.Box, ctx.Types.Double);
                    break;
                }
                var saved = EmitCallbackAndStashThisArg(emitter, arguments);
                il.Emit(OpCodes.Call, ctx.Runtime!.ArrayFindLastIndex);
                var resLocal = il.DeclareLocal(ctx.Types.Double);
                il.Emit(OpCodes.Stloc, resLocal);
                EmitRestoreCallbackThisArg(emitter, saved);
                il.Emit(OpCodes.Ldloc, resLocal);
                il.Emit(OpCodes.Box, ctx.Types.Double);
                break;
            }

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
    /// Issue #96 Phase A — direct delegate dispatch fast path.
    /// Detects <c>arr.map(literal-non-capturing-arrow)</c> at the call site
    /// and emits IL that invokes the arrow's static method through a
    /// <c>Func&lt;object, object&gt;</c> delegate (<c>ldftn</c> + delegate
    /// ctor) instead of allocating <c>$TSFunction</c> and dispatching through
    /// <c>MethodInvoker</c>.
    ///
    /// Stack on entry: <c>[List&lt;object&gt;]</c> (the unwrapped receiver).
    /// On success: emits IL ending with <c>[List&lt;object&gt;]</c> on the
    /// stack (the helper's return value).
    /// On failure (returns false): stack unchanged; caller falls through to
    /// the slow path.
    ///
    /// Preconditions enforced here:
    /// - Exactly one argument (no <c>thisArg</c> — bypassed for arrows
    ///   anyway, but skipping keeps observable evaluation semantics for the
    ///   slow path).
    /// - Argument is <c>Expr.ArrowFunction</c> (literal, not a variable
    ///   binding).
    /// - Arity 0 or 1 (Map's callback gets element + index + receiver, but
    ///   arrows can ignore any of those).
    /// - Not async, not generator, no <c>__this</c> param.
    /// - No type annotations on params — <see cref="ParameterTypeResolver"/>
    ///   falls back to <c>object</c> when annotation is null, guaranteeing
    ///   the static method's parameter types match <c>Func&lt;object, object&gt;</c>.
    /// - Static method's <c>ReturnType</c> is exactly <c>object</c> — even
    ///   without an AST return annotation, type inference from the body
    ///   (<c>x =&gt; x*2</c> → returnType <c>double</c>) can resolve to a
    ///   non-object type, which would mismatch <c>Func&lt;object, object&gt;</c>
    ///   and produce undefined-behavior signature mismatch at delegate
    ///   construction. Inspecting <see cref="MethodBuilder.ReturnType"/>
    ///   directly catches this; it's set at <c>DefineMethod</c> time and
    ///   reliable pre-CreateType.
    /// - Arrow is non-capturing (per <see cref="ClosureAnalyzer"/>) — implies
    ///   no <c>arguments</c> reference and no display-class allocation.
    /// </summary>
    /// <summary>
    /// Resolves the callback expression at an iterator-helper call site to a
    /// literal <see cref="Expr.ArrowFunction"/> AST node. Accepts both the
    /// inline form (<c>arr.map(x =&gt; …)</c>) and the const-bound form
    /// (<c>const sq = x =&gt; …; arr.map(sq)</c>) — for the latter we walk
    /// to the registered top-level const→arrow map. Returns null if the
    /// callback isn't statically resolvable to a literal arrow.
    /// </summary>
    private static Expr.ArrowFunction? ResolveCallbackArrow(IEmitterContext emitter, Expr callbackArg)
    {
        if (callbackArg is Expr.ArrowFunction af) return af;
        if (callbackArg is Expr.Variable v
            && emitter.Context.ConstArrowBindings.TryGetValue(v.Name.Lexeme, out var bound))
        {
            return bound;
        }
        return null;
    }

    private static bool TryEmitDirectDelegateCall(IEmitterContext emitter, List<Expr> arguments, System.Reflection.Emit.MethodBuilder helperMethod, System.Reflection.Emit.MethodBuilder? boolHelper = null)
    {
        var ctx = emitter.Context;
        if (arguments.Count != 1) return false;
        var af = ResolveCallbackArrow(emitter, arguments[0]);
        if (af is null) return false;
        if (af.HasOwnThis || af.IsAsync || af.IsGenerator) return false;
        if (af.Parameters.Count > 1) return false;
        foreach (var p in af.Parameters)
        {
            if (p.Type != null) return false;
            if (p.IsRest || p.IsOptional) return false;
            if (p.DefaultValue != null) return false;
        }
        if (!ctx.ArrowMethods.TryGetValue(af, out var staticMethod)) return false;

        // Static method's parameter types are guaranteed to be `object` by the
        // AST-no-annotation check (ParameterTypeResolver falls back to object).
        // Return type is the discriminator: type inference may yield `object`
        // (any-typed bodies) or `bool` (predicate bodies like `v => v > 10`).
        // The first uses Func<object, object>; the second uses Func<object, bool>
        // where the helper variant exists. Phase B: capturing arrows now go
        // through emitter.TryEmitArrowAsDelegate, which builds the display
        // instance + binds the delegate to its Invoke method.
        var ret = staticMethod.ReturnType;
        System.Reflection.Emit.MethodBuilder chosenHelper;
        Type funcType;
        if (ret == ctx.Types.Object)
        {
            chosenHelper = helperMethod;
            funcType = typeof(Func<object, object>);
        }
        else if (ret == ctx.Types.Boolean && boolHelper != null)
        {
            chosenHelper = boolHelper;
            funcType = typeof(Func<object, bool>);
        }
        else
        {
            return false;
        }

        if (!emitter.TryEmitArrowAsDelegate(af, funcType))
            return false;
        ctx.IL.Emit(OpCodes.Call, chosenHelper);
        return true;
    }

    /// <summary>
    /// Reduce-specific fast path: the callback is binary
    /// <c>(acc, element) =&gt; newAcc</c>, requires <c>Func&lt;object, object,
    /// object&gt;</c>, and initial value must be present (scope-out for the
    /// no-initial form's kPresent semantics). Stack on entry: <c>[list]</c>.
    /// On success, the call site has already pushed the receiver; this
    /// emits the delegate construction, the initial-value, and the call.
    /// </summary>
    private static bool TryEmitReduceDirectCall(IEmitterContext emitter, List<Expr> arguments)
    {
        var ctx = emitter.Context;
        // Reduce direct form requires the 2-arg shape: (callback, initialValue).
        // No-initial would need scan-for-first-present; punt to the slow path.
        if (arguments.Count != 2) return false;
        var af = ResolveCallbackArrow(emitter, arguments[0]);
        if (af is null) return false;
        if (af.HasOwnThis || af.IsAsync || af.IsGenerator) return false;
        if (af.Parameters.Count != 2) return false;
        foreach (var p in af.Parameters)
        {
            if (p.Type != null) return false;
            if (p.IsRest || p.IsOptional) return false;
            if (p.DefaultValue != null) return false;
        }
        if (!ctx.ArrowMethods.TryGetValue(af, out var staticMethod)) return false;
        if (staticMethod.ReturnType != ctx.Types.Object) return false;

        var func3 = typeof(Func<object, object, object>);
        // Stack: [list]. Build delegate (capturing or non-capturing).
        if (!emitter.TryEmitArrowAsDelegate(af, func3))
            return false;
        // Push initial value (boxed object).
        emitter.EmitExpression(arguments[1]);
        emitter.EmitBoxIfNeeded(arguments[1]);
        ctx.IL.Emit(OpCodes.Call, ctx.Runtime!.ArrayReduceDirect);
        return true;
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
