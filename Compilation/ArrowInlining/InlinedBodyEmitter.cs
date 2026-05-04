using System.Reflection.Emit;
using SharpTS.Compilation.Emitters;
using SharpTS.Parsing;

namespace SharpTS.Compilation.ArrowInlining;

/// <summary>
/// Emits IL for iterator-helper call sites with the literal arrow body
/// inlined directly into the loop. Replaces the per-iteration delegate
/// invocation with the arrow body's expression IL — eliminating the
/// <see cref="System.Func{T1, TResult}.Invoke"/> virtual call that the
/// Direct delegate path still pays.
/// </summary>
/// <remarks>
/// Eligibility is owned by <see cref="ArrowInlinabilityCheck"/>; this class
/// is the IL-emission half. Stack contracts are documented per method.
/// Body emission rebinds the arrow's parameter name to the loop's element
/// local via <see cref="LocalsManager.RegisterLocal"/> inside a fresh
/// scope, so <see cref="LocalVariableResolver"/> resolves
/// <c>Variable("x")</c> in the body to <c>Ldloc(elementLocal)</c>.
/// </remarks>
internal static class InlinedBodyEmitter
{
    /// <summary>
    /// Emits an inlined <c>Array.prototype.forEach</c> call. Spec ref:
    /// ECMA-262 23.1.3.10. Holes are skipped (callback not invoked).
    /// Callback's return value is discarded.
    /// </summary>
    /// <remarks>
    /// Stack on entry: <c>[List&lt;object?&gt;]</c> (the unwrapped receiver
    /// produced by <c>EmitGetListFromArrayOrList</c>).
    /// Stack on exit: empty. The caller is responsible for pushing
    /// <c>Ldnull</c> to balance the expression stack (matches the existing
    /// Direct/slow paths which both emit <c>Ldnull</c> after the call).
    /// </remarks>
    public static void EmitInlinedForEach(IEmitterContext emitter, Expr.ArrowFunction af)
    {
        var ctx = emitter.Context;
        var il = ctx.IL;
        var types = ctx.Types;
        var runtime = ctx.Runtime!;

        var listCountGetter = types.GetProperty(types.ListOfObject, "Count").GetGetMethod()!;
        var listIndexerGetter = types.GetProperty(types.ListOfObject, "Item").GetGetMethod()!;

        // Stash the list to a local so we can use it across iterations.
        var listLocal = il.DeclareLocal(types.ListOfObject);
        il.Emit(OpCodes.Stloc, listLocal);

        var indexLocal = il.DeclareLocal(types.Int32);
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Stloc, indexLocal);

        var elementLocal = il.DeclareLocal(types.Object);

        var loopStart = il.DefineLabel();
        var loopEnd = il.DefineLabel();
        var advanceLabel = il.DefineLabel();

        il.MarkLabel(loopStart);
        // if (i >= list.Count) goto loopEnd;
        // Per-iter Count read matches the existing Direct helper (visible
        // mutations to source.length are not protected against here either;
        // arrow bodies that could mutate length aren't eligible because
        // they'd capture or reference `arguments`).
        il.Emit(OpCodes.Ldloc, indexLocal);
        il.Emit(OpCodes.Ldloc, listLocal);
        il.Emit(OpCodes.Callvirt, listCountGetter);
        il.Emit(OpCodes.Bge, loopEnd);

        // element = list[i]; if (element is $ArrayHole) goto advance;
        il.Emit(OpCodes.Ldloc, listLocal);
        il.Emit(OpCodes.Ldloc, indexLocal);
        il.Emit(OpCodes.Callvirt, listIndexerGetter);
        il.Emit(OpCodes.Stloc, elementLocal);
        il.Emit(OpCodes.Ldloc, elementLocal);
        il.Emit(OpCodes.Isinst, runtime.ArrayHoleType);
        il.Emit(OpCodes.Brtrue, advanceLabel);

        // forEach handler: evaluate value (for side effects) and discard;
        // branch to advance. M5 block-body bodies install this handler so
        // every `return v;` rewrites to "evaluate v, drop, advance".
        Action<Expr?> forEachHandler = value =>
        {
            if (value != null)
            {
                emitter.EmitExpression(value);
                il.Emit(OpCodes.Pop);
            }
            il.Emit(OpCodes.Br, advanceLabel);
        };

        EmitBodyViaHandler(emitter, af, elementLocal, accLocal: null, forEachHandler);

        // advance: i++; goto loopStart
        il.MarkLabel(advanceLabel);
        il.Emit(OpCodes.Ldloc, indexLocal);
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Add);
        il.Emit(OpCodes.Stloc, indexLocal);
        il.Emit(OpCodes.Br, loopStart);

        il.MarkLabel(loopEnd);
    }

    /// <summary>
    /// Emits an inlined <c>Array.prototype.map</c> call. Spec ref: ECMA-262
    /// 23.1.3.20. Holes are preserved in the output (the callback is NOT
    /// invoked for hole slots; output[i] receives <c>$ArrayHole.Instance</c>
    /// directly). Body's value goes into <c>result.Add(...)</c>.
    /// </summary>
    /// <remarks>
    /// Stack on entry: <c>[List&lt;object?&gt;]</c>. Stack on exit:
    /// <c>[List&lt;object?&gt;]</c> (the result list). The caller's
    /// <c>EmitPostCallAdjust</c> wraps the list in a fresh <c>$Array</c>
    /// because <c>map</c> is a "returns new array" method.
    /// </remarks>
    public static void EmitInlinedMap(IEmitterContext emitter, Expr.ArrowFunction af)
    {
        var ctx = emitter.Context;
        var il = ctx.IL;
        var types = ctx.Types;
        var runtime = ctx.Runtime!;

        var listCountGetter = types.GetProperty(types.ListOfObject, "Count").GetGetMethod()!;
        var listIndexerGetter = types.GetProperty(types.ListOfObject, "Item").GetGetMethod()!;
        var listAdd = types.GetMethod(types.ListOfObject, "Add", types.Object);

        var listLocal = il.DeclareLocal(types.ListOfObject);
        il.Emit(OpCodes.Stloc, listLocal);

        var resultLocal = il.DeclareLocal(types.ListOfObject);
        il.Emit(OpCodes.Ldloc, listLocal);
        il.Emit(OpCodes.Callvirt, listCountGetter);
        il.Emit(OpCodes.Newobj, types.GetConstructor(types.ListOfObject, types.Int32));
        il.Emit(OpCodes.Stloc, resultLocal);

        var indexLocal = il.DeclareLocal(types.Int32);
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Stloc, indexLocal);

        var elementLocal = il.DeclareLocal(types.Object);

        var loopStart = il.DefineLabel();
        var loopEnd = il.DefineLabel();
        var advance = il.DefineLabel();
        var holeBranch = il.DefineLabel();

        il.MarkLabel(loopStart);
        il.Emit(OpCodes.Ldloc, indexLocal);
        il.Emit(OpCodes.Ldloc, listLocal);
        il.Emit(OpCodes.Callvirt, listCountGetter);
        il.Emit(OpCodes.Bge, loopEnd);

        // element = list[i]; if (element is $ArrayHole) goto holeBranch;
        il.Emit(OpCodes.Ldloc, listLocal);
        il.Emit(OpCodes.Ldloc, indexLocal);
        il.Emit(OpCodes.Callvirt, listIndexerGetter);
        il.Emit(OpCodes.Stloc, elementLocal);
        il.Emit(OpCodes.Ldloc, elementLocal);
        il.Emit(OpCodes.Isinst, runtime.ArrayHoleType);
        il.Emit(OpCodes.Brtrue, holeBranch);

        // Per-return handler for map: result.Add(value-or-undefined); Br advance.
        Action<Expr?> mapHandler = value =>
        {
            il.Emit(OpCodes.Ldloc, resultLocal);
            EmitValueOrUndefined(emitter, value, runtime);
            il.Emit(OpCodes.Callvirt, listAdd);
            il.Emit(OpCodes.Br, advance);
        };

        EmitBodyViaHandler(emitter, af, elementLocal, accLocal: null, mapHandler);

        // Hole branch: result.Add($ArrayHole.Instance) — preserves the hole
        // at the same position. ECMA-262 23.1.3.20 SerializeJSONArray
        // uses CreateDataPropertyOrThrow only when HasProperty is true; for
        // holes it skips, which we model with our sentinel.
        il.MarkLabel(holeBranch);
        il.Emit(OpCodes.Ldloc, resultLocal);
        il.Emit(OpCodes.Ldsfld, runtime.ArrayHoleInstance);
        il.Emit(OpCodes.Callvirt, listAdd);

        il.MarkLabel(advance);
        il.Emit(OpCodes.Ldloc, indexLocal);
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Add);
        il.Emit(OpCodes.Stloc, indexLocal);
        il.Emit(OpCodes.Br, loopStart);

        il.MarkLabel(loopEnd);
        il.Emit(OpCodes.Ldloc, resultLocal);
    }

    /// <summary>
    /// Pushes either <paramref name="value"/>'s expression result (boxed)
    /// or <c>$Undefined.Instance</c> when the value is null (implicit
    /// fall-off return). Used by per-helper handlers to materialize the
    /// callback's return value.
    /// </summary>
    private static void EmitValueOrUndefined(IEmitterContext emitter, Expr? value, EmittedRuntime runtime)
    {
        if (value != null)
        {
            emitter.EmitExpression(value);
            emitter.EnsureBoxed();
        }
        else
        {
            emitter.Context.IL.Emit(OpCodes.Ldsfld, runtime.UndefinedInstance);
        }
    }

    /// <summary>
    /// Emits the arrow body via the per-helper return handler. Unifies the
    /// expression-body path (single dispatch with the body expression)
    /// and the block-body path (install handler, walk allowed statements,
    /// dispatch fall-off as <c>return undefined</c>).
    /// </summary>
    /// <param name="elementLocal">Loop's element local; bound to the
    /// arrow's element parameter (param 0 for forEach/map/filter/find/etc.,
    /// param 1 for reduce/reduceRight).</param>
    /// <param name="accLocal">Accumulator local for reduce/reduceRight
    /// (bound to param 0). Null for the other helpers.</param>
    /// <param name="returnHandler">Per-helper rewrite called once for each
    /// <c>return</c> in the body (and once at fall-off).</param>
    private static void EmitBodyViaHandler(
        IEmitterContext emitter,
        Expr.ArrowFunction af,
        System.Reflection.Emit.LocalBuilder elementLocal,
        System.Reflection.Emit.LocalBuilder? accLocal,
        Action<Expr?> returnHandler)
    {
        var ctx = emitter.Context;
        ctx.Locals.EnterScope();
        try
        {
            // Bind parameters by position. For reduce: param 0 = acc,
            // param 1 = element. For others: param 0 = element.
            int paramIndex = 0;
            if (accLocal != null && af.Parameters.Count > paramIndex)
            {
                ctx.Locals.RegisterLocal(af.Parameters[paramIndex].Name.Lexeme, accLocal);
                paramIndex++;
            }
            if (af.Parameters.Count > paramIndex)
            {
                ctx.Locals.RegisterLocal(af.Parameters[paramIndex].Name.Lexeme, elementLocal);
            }

            if (af.ExpressionBody != null)
            {
                // Treat expression body as a single implicit `return expr`.
                returnHandler(af.ExpressionBody);
            }
            else
            {
                BlockBodyArrowEmitter.EmitBlock(emitter, af.BlockBody!, returnHandler);
                // Implicit fall-off (control reached end of block) =
                // `return undefined`. Emits unreachable IL after a
                // guaranteed-return body but stays correct otherwise.
                returnHandler(null);
            }
        }
        finally
        {
            ctx.Locals.ExitScope();
        }
    }

    /// <summary>
    /// Emits an inlined <c>Array.prototype.filter</c> call. Spec ref:
    /// ECMA-262 23.1.3.8. Holes are skipped (callback NOT invoked, no
    /// output for that index — filter densifies). When the body's value
    /// is truthy, the source <i>element</i> (not the body's value) is
    /// pushed to the result.
    /// </summary>
    /// <remarks>
    /// V1 always routes the body's value through <c>IsTruthy</c>. A raw-bool
    /// fast path looked tempting but bodies like <c>v =&gt; v % 2 === 0</c>
    /// can leave either a raw int32 or a boxed Boolean on the stack
    /// depending on operand typing — the static-method emitter's
    /// <c>EmitReturn</c> coercion is not reachable here. <c>IsTruthy</c>
    /// accepts <c>object</c> and handles all variants. Future work can
    /// reintroduce a typed fast path with proper stack-type coercion.
    /// Stack on entry: <c>[List&lt;object?&gt;]</c>. Stack on exit:
    /// <c>[List&lt;object?&gt;]</c>.
    /// </remarks>
    public static void EmitInlinedFilter(IEmitterContext emitter, Expr.ArrowFunction af)
    {
        var ctx = emitter.Context;
        var il = ctx.IL;
        var types = ctx.Types;
        var runtime = ctx.Runtime!;

        var listCountGetter = types.GetProperty(types.ListOfObject, "Count").GetGetMethod()!;
        var listIndexerGetter = types.GetProperty(types.ListOfObject, "Item").GetGetMethod()!;
        var listAdd = types.GetMethod(types.ListOfObject, "Add", types.Object);

        var listLocal = il.DeclareLocal(types.ListOfObject);
        il.Emit(OpCodes.Stloc, listLocal);

        var resultLocal = il.DeclareLocal(types.ListOfObject);
        il.Emit(OpCodes.Ldloc, listLocal);
        il.Emit(OpCodes.Callvirt, listCountGetter);
        il.Emit(OpCodes.Newobj, types.GetConstructor(types.ListOfObject, types.Int32));
        il.Emit(OpCodes.Stloc, resultLocal);

        var indexLocal = il.DeclareLocal(types.Int32);
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Stloc, indexLocal);

        var elementLocal = il.DeclareLocal(types.Object);

        var loopStart = il.DefineLabel();
        var loopEnd = il.DefineLabel();
        var advance = il.DefineLabel();

        il.MarkLabel(loopStart);
        il.Emit(OpCodes.Ldloc, indexLocal);
        il.Emit(OpCodes.Ldloc, listLocal);
        il.Emit(OpCodes.Callvirt, listCountGetter);
        il.Emit(OpCodes.Bge, loopEnd);

        // Load element; skip if hole (filter densifies — callback NOT
        // invoked, no element copied for that index).
        il.Emit(OpCodes.Ldloc, listLocal);
        il.Emit(OpCodes.Ldloc, indexLocal);
        il.Emit(OpCodes.Callvirt, listIndexerGetter);
        il.Emit(OpCodes.Stloc, elementLocal);
        il.Emit(OpCodes.Ldloc, elementLocal);
        il.Emit(OpCodes.Isinst, runtime.ArrayHoleType);
        il.Emit(OpCodes.Brtrue, advance);

        // Per-return handler for filter: emit value, IsTruthy, on truthy
        // push element to result; either way Br advance. A fresh
        // per-return label scopes the truthy gate so multiple `return`
        // statements in the body don't trip over each other.
        Action<Expr?> filterHandler = value =>
        {
            var skipPerReturn = il.DefineLabel();
            EmitValueOrUndefined(emitter, value, runtime);
            il.Emit(OpCodes.Call, runtime.IsTruthy);
            il.Emit(OpCodes.Brfalse, skipPerReturn);
            il.Emit(OpCodes.Ldloc, resultLocal);
            il.Emit(OpCodes.Ldloc, elementLocal);
            il.Emit(OpCodes.Callvirt, listAdd);
            il.MarkLabel(skipPerReturn);
            il.Emit(OpCodes.Br, advance);
        };

        EmitBodyViaHandler(emitter, af, elementLocal, accLocal: null, filterHandler);

        il.MarkLabel(advance);
        il.Emit(OpCodes.Ldloc, indexLocal);
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Add);
        il.Emit(OpCodes.Stloc, indexLocal);
        il.Emit(OpCodes.Br, loopStart);

        il.MarkLabel(loopEnd);
        il.Emit(OpCodes.Ldloc, resultLocal);
    }

    /// <summary>
    /// Discriminator for the short-circuit family of inliners
    /// (find/findIndex/findLast/findLastIndex/some/every). Each kind owns
    /// four properties: iteration direction, hole behavior, short-circuit
    /// trigger (truthy or falsy), and the per-helper return values.
    /// </summary>
    public enum ShortCircuitKind
    {
        /// <summary>find: forward, unhole-at-read, return element on truthy, default $Undefined.</summary>
        Find,
        /// <summary>findIndex: forward, unhole-at-read, return raw double i on truthy, default -1.0 raw.</summary>
        FindIndex,
        /// <summary>findLast: reverse, unhole-at-read, return element on truthy, default $Undefined.</summary>
        FindLast,
        /// <summary>findLastIndex: reverse, unhole-at-read, return raw double i on truthy, default -1.0 raw.</summary>
        FindLastIndex,
        /// <summary>some: forward, skip-holes, return boxed-true on truthy match, default boxed-false.</summary>
        Some,
        /// <summary>every: forward, skip-holes, return boxed-false on falsy match, default boxed-true.</summary>
        Every,
    }

    /// <summary>
    /// Emits an inlined call for the short-circuit family of iterator
    /// helpers. Body's value goes through <see cref="EmittedRuntime.IsTruthy"/>
    /// (matches the universal-truthiness rationale documented on
    /// <see cref="EmitInlinedFilter"/>).
    /// </summary>
    /// <remarks>
    /// Stack on entry: <c>[List&lt;object?&gt;]</c>. Stack on exit varies
    /// by kind: <see cref="ShortCircuitKind.Find"/>/<see cref="ShortCircuitKind.FindLast"/>/
    /// <see cref="ShortCircuitKind.Some"/>/<see cref="ShortCircuitKind.Every"/>
    /// leave a boxed object; <see cref="ShortCircuitKind.FindIndex"/>/
    /// <see cref="ShortCircuitKind.FindLastIndex"/> leave a raw
    /// <c>double</c> (the call site emits <c>Box ctx.Types.Double</c> after
    /// — matches the existing Direct contract).
    /// </remarks>
    public static void EmitInlinedShortCircuit(IEmitterContext emitter, Expr.ArrowFunction af, ShortCircuitKind kind)
    {
        var ctx = emitter.Context;
        var il = ctx.IL;
        var types = ctx.Types;
        var runtime = ctx.Runtime!;

        bool reverse = kind is ShortCircuitKind.FindLast or ShortCircuitKind.FindLastIndex;
        bool unholeAtRead = kind is ShortCircuitKind.Find or ShortCircuitKind.FindIndex
            or ShortCircuitKind.FindLast or ShortCircuitKind.FindLastIndex;
        bool returnsIndex = kind is ShortCircuitKind.FindIndex or ShortCircuitKind.FindLastIndex;
        bool returnsBool = kind is ShortCircuitKind.Some or ShortCircuitKind.Every;
        bool shortCircuitOnTruthy = kind is ShortCircuitKind.Find or ShortCircuitKind.FindIndex
            or ShortCircuitKind.FindLast or ShortCircuitKind.FindLastIndex
            or ShortCircuitKind.Some;

        var listCountGetter = types.GetProperty(types.ListOfObject, "Count").GetGetMethod()!;
        var listIndexerGetter = types.GetProperty(types.ListOfObject, "Item").GetGetMethod()!;

        var listLocal = il.DeclareLocal(types.ListOfObject);
        il.Emit(OpCodes.Stloc, listLocal);

        var indexLocal = il.DeclareLocal(types.Int32);
        if (reverse)
        {
            // i = list.Count - 1
            il.Emit(OpCodes.Ldloc, listLocal);
            il.Emit(OpCodes.Callvirt, listCountGetter);
            il.Emit(OpCodes.Ldc_I4_1);
            il.Emit(OpCodes.Sub);
            il.Emit(OpCodes.Stloc, indexLocal);
        }
        else
        {
            il.Emit(OpCodes.Ldc_I4_0);
            il.Emit(OpCodes.Stloc, indexLocal);
        }

        var elementLocal = il.DeclareLocal(types.Object);

        // Result staging: a local typed per-kind that holds the value we
        // leave on the stack when control exits the loop. Necessary because
        // we cannot use `Ret` here (we're inlined into the OUTER method;
        // `Ret` would terminate it). Match path stores result and `Br done`;
        // fall-off (loopEnd) stores the default and `Br done` (implicit
        // fall-through). At `done`, we Ldloc the result.
        var resultType = returnsIndex ? types.Double : types.Object;
        var resultLocal = il.DeclareLocal(resultType);

        var loopStart = il.DefineLabel();
        var loopEnd = il.DefineLabel();
        var advance = il.DefineLabel();
        var done = il.DefineLabel();

        il.MarkLabel(loopStart);
        if (reverse)
        {
            il.Emit(OpCodes.Ldloc, indexLocal);
            il.Emit(OpCodes.Ldc_I4_0);
            il.Emit(OpCodes.Blt, loopEnd);
        }
        else
        {
            il.Emit(OpCodes.Ldloc, indexLocal);
            il.Emit(OpCodes.Ldloc, listLocal);
            il.Emit(OpCodes.Callvirt, listCountGetter);
            il.Emit(OpCodes.Bge, loopEnd);
        }

        // Load element
        il.Emit(OpCodes.Ldloc, listLocal);
        il.Emit(OpCodes.Ldloc, indexLocal);
        il.Emit(OpCodes.Callvirt, listIndexerGetter);
        il.Emit(OpCodes.Stloc, elementLocal);

        // Hole handling
        il.Emit(OpCodes.Ldloc, elementLocal);
        il.Emit(OpCodes.Isinst, runtime.ArrayHoleType);
        if (unholeAtRead)
        {
            // find/findLast/findIndex/findLastIndex: callback IS invoked,
            // but with $Undefined.Instance substituted for the hole.
            var notHole = il.DefineLabel();
            il.Emit(OpCodes.Brfalse, notHole);
            il.Emit(OpCodes.Ldsfld, runtime.UndefinedInstance);
            il.Emit(OpCodes.Stloc, elementLocal);
            il.MarkLabel(notHole);
        }
        else
        {
            // some/every: skip holes (no callback invocation).
            il.Emit(OpCodes.Brtrue, advance);
        }

        // Per-return handler for the short-circuit family.
        // For each `return v` in the body (or implicit fall-off as
        // `return undefined`):
        //   1. evaluate v (or push $Undefined.Instance) and IsTruthy
        //   2. branch on the per-kind condition (truthy for find/some,
        //      falsy for every)
        //   3. on match: store the per-kind result + Br done
        //   4. on no-match: Br advance
        Action<Expr?> shortCircuitHandler = value =>
        {
            var noMatch = il.DefineLabel();
            EmitValueOrUndefined(emitter, value, runtime);
            il.Emit(OpCodes.Call, runtime.IsTruthy);
            il.Emit(shortCircuitOnTruthy ? OpCodes.Brfalse : OpCodes.Brtrue, noMatch);
            // Match path: store per-kind result, jump to done.
            if (returnsIndex)
            {
                il.Emit(OpCodes.Ldloc, indexLocal);
                il.Emit(OpCodes.Conv_R8);
                il.Emit(OpCodes.Stloc, resultLocal);
            }
            else if (returnsBool)
            {
                il.Emit(shortCircuitOnTruthy ? OpCodes.Ldc_I4_1 : OpCodes.Ldc_I4_0);
                il.Emit(OpCodes.Box, types.Boolean);
                il.Emit(OpCodes.Stloc, resultLocal);
            }
            else
            {
                il.Emit(OpCodes.Ldloc, elementLocal);
                il.Emit(OpCodes.Stloc, resultLocal);
            }
            il.Emit(OpCodes.Br, done);

            il.MarkLabel(noMatch);
            il.Emit(OpCodes.Br, advance);
        };

        EmitBodyViaHandler(emitter, af, elementLocal, accLocal: null, shortCircuitHandler);

        // Advance and loop.
        il.MarkLabel(advance);
        il.Emit(OpCodes.Ldloc, indexLocal);
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(reverse ? OpCodes.Sub : OpCodes.Add);
        il.Emit(OpCodes.Stloc, indexLocal);
        il.Emit(OpCodes.Br, loopStart);

        // Fall-off: store per-kind default, then fall through to done.
        il.MarkLabel(loopEnd);
        if (returnsIndex)
        {
            il.Emit(OpCodes.Ldc_R8, -1.0);
        }
        else if (returnsBool)
        {
            // some default: false. every default: true.
            il.Emit(shortCircuitOnTruthy ? OpCodes.Ldc_I4_0 : OpCodes.Ldc_I4_1);
            il.Emit(OpCodes.Box, types.Boolean);
        }
        else
        {
            il.Emit(OpCodes.Ldsfld, runtime.UndefinedInstance);
        }
        il.Emit(OpCodes.Stloc, resultLocal);

        il.MarkLabel(done);
        il.Emit(OpCodes.Ldloc, resultLocal);
    }

    /// <summary>
    /// Emits an inlined <c>Array.prototype.reduce</c> /
    /// <c>reduceRight</c> call with explicit initial value (V1 only handles
    /// the 2-arg form; no-initial-value short-scan is deferred). Spec ref:
    /// ECMA-262 23.1.3.24 (reduce) / 23.1.3.25 (reduceRight). Holes are
    /// skipped per spec — accumulator is preserved across hole indices.
    /// Body's value is stored back into the accumulator slot each iteration.
    /// </summary>
    /// <remarks>
    /// Stack on entry: <c>[List&lt;object?&gt;]</c>. Stack on exit:
    /// <c>[acc]</c> (the final accumulator value as a boxed object). The
    /// initial-value expression is emitted from the call site directly into
    /// this method, so the caller's
    /// <c>arguments[1]</c> is captured here rather than pre-pushed.
    /// </remarks>
    public static void EmitInlinedReduce(IEmitterContext emitter, Expr.ArrowFunction af, Expr initialValue, bool reverse)
    {
        var ctx = emitter.Context;
        var il = ctx.IL;
        var types = ctx.Types;
        var runtime = ctx.Runtime!;

        var listCountGetter = types.GetProperty(types.ListOfObject, "Count").GetGetMethod()!;
        var listIndexerGetter = types.GetProperty(types.ListOfObject, "Item").GetGetMethod()!;

        // Stash list (was on stack from the case dispatch).
        var listLocal = il.DeclareLocal(types.ListOfObject);
        il.Emit(OpCodes.Stloc, listLocal);

        // Initialize accLocal from the user's initial value. Box if needed
        // so the slot consistently holds an object reference; the body's
        // value-typed result will be EnsureBoxed before the per-iteration
        // store.
        var accLocal = il.DeclareLocal(types.Object);
        emitter.EmitExpression(initialValue);
        emitter.EmitBoxIfNeeded(initialValue);
        il.Emit(OpCodes.Stloc, accLocal);

        var indexLocal = il.DeclareLocal(types.Int32);
        if (reverse)
        {
            // i = list.Count - 1
            il.Emit(OpCodes.Ldloc, listLocal);
            il.Emit(OpCodes.Callvirt, listCountGetter);
            il.Emit(OpCodes.Ldc_I4_1);
            il.Emit(OpCodes.Sub);
            il.Emit(OpCodes.Stloc, indexLocal);
        }
        else
        {
            il.Emit(OpCodes.Ldc_I4_0);
            il.Emit(OpCodes.Stloc, indexLocal);
        }

        var elementLocal = il.DeclareLocal(types.Object);

        var loopStart = il.DefineLabel();
        var loopEnd = il.DefineLabel();
        var advance = il.DefineLabel();

        il.MarkLabel(loopStart);
        if (reverse)
        {
            il.Emit(OpCodes.Ldloc, indexLocal);
            il.Emit(OpCodes.Ldc_I4_0);
            il.Emit(OpCodes.Blt, loopEnd);
        }
        else
        {
            il.Emit(OpCodes.Ldloc, indexLocal);
            il.Emit(OpCodes.Ldloc, listLocal);
            il.Emit(OpCodes.Callvirt, listCountGetter);
            il.Emit(OpCodes.Bge, loopEnd);
        }

        // Load element; skip if hole (reduce/reduceRight do not invoke
        // callback on holes — accumulator passes through unchanged).
        il.Emit(OpCodes.Ldloc, listLocal);
        il.Emit(OpCodes.Ldloc, indexLocal);
        il.Emit(OpCodes.Callvirt, listIndexerGetter);
        il.Emit(OpCodes.Stloc, elementLocal);
        il.Emit(OpCodes.Ldloc, elementLocal);
        il.Emit(OpCodes.Isinst, runtime.ArrayHoleType);
        il.Emit(OpCodes.Brtrue, advance);

        // Per-return handler for reduce/reduceRight: emit value (or push
        // undefined), store as the new accumulator, branch to advance.
        Action<Expr?> reduceHandler = value =>
        {
            EmitValueOrUndefined(emitter, value, runtime);
            il.Emit(OpCodes.Stloc, accLocal);
            il.Emit(OpCodes.Br, advance);
        };

        EmitBodyViaHandler(emitter, af, elementLocal, accLocal, reduceHandler);

        il.MarkLabel(advance);
        il.Emit(OpCodes.Ldloc, indexLocal);
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(reverse ? OpCodes.Sub : OpCodes.Add);
        il.Emit(OpCodes.Stloc, indexLocal);
        il.Emit(OpCodes.Br, loopStart);

        il.MarkLabel(loopEnd);
        il.Emit(OpCodes.Ldloc, accLocal);
    }
}
