using System.Reflection;
using System.Reflection.Emit;

namespace SharpTS.Compilation;

/// <summary>
/// Emits per-arrow "boxed adapter" static methods that let an arrow with
/// <b>annotated</b> parameters be dispatched by the array HOF direct-delegate
/// fast path (#861).
/// </summary>
/// <remarks>
/// <para>
/// An annotated callback like <c>(x: number): number =&gt; x*2</c> compiles to a
/// static method with the typed CLR signature <c>double(double)</c>, which cannot
/// bind to the <c>Func&lt;object,object&gt;</c> that the emitted
/// <c>Array*Direct</c> helpers expect. Without a bridge, such callbacks fall back
/// to the reflective <c>$TSFunction</c>/<c>MethodInvoker</c> path (per-element
/// dispatch + an <c>object[]</c> allocation), which is exactly the cost #861
/// targets.
/// </para>
/// <para>
/// The adapter is a static method <c>object Adapter(object[, object])</c> on
/// <c>$Program</c> whose body unboxes/casts each boxed element into the arrow's
/// typed parameter slot, <c>call</c>s the typed arrow method, then reboxes the
/// result. It binds to <c>Func&lt;object,object&gt;</c> /
/// <c>Func&lt;object,object,object&gt;</c> via <c>ldnull</c>+<c>ldftn</c>, so the
/// unchanged <c>Array*Direct</c> helpers drive it with a direct delegate call.
/// </para>
/// <para>
/// The unbox/box marshalling is shared with <see cref="DelegateAdapterEmitter"/>
/// (<see cref="DelegateAdapterEmitter.EmitUnboxForReturn"/> coerces
/// <c>object</c>→typed slot; <see cref="DelegateAdapterEmitter.EmitBoxForTS"/>
/// reboxes the typed result), so the conversion matches the reflective
/// <c>MethodInvoker</c> semantics for the no-arg-conversion regime (concrete
/// <c>double</c>/<c>bool</c>/<c>string</c> params — unions/nullable already widen
/// to <c>object</c> in <c>ParameterTypeResolver</c>, and the call site gates on
/// that). Only emits a static adapter, so it carries no <c>SharpTS.dll</c>
/// reference (standalone-DLL constraint preserved).
/// </para>
/// </remarks>
internal sealed class ArrowBoxedAdapterEmitter
{
    // Keyed by (typed arrow method, adapter arity). Arity is the delegate's
    // parameter count (1 for map/filter/forEach/find/…, 2 for reduce); the arrow
    // itself may declare fewer params, in which case the extra adapter args are
    // ignored. A given arrow node is emitted by exactly one CompilationContext (the
    // one containing its call site), so this per-context cache never double-defines;
    // the adapter NAME is derived from the arrow's globally-unique method name so it
    // stays collision-free across contexts that share the same $Program type.
    private readonly Dictionary<(MethodBuilder, int), MethodBuilder> _cache = [];

    /// <summary>
    /// Returns the boxed adapter for <paramref name="typedArrow"/> bound to a
    /// delegate of <paramref name="funcArity"/> object parameters, emitting it on
    /// <paramref name="programType"/> (the arrow's declaring <c>$Program</c> type)
    /// on first request.
    /// </summary>
    public MethodBuilder GetOrEmit(TypeBuilder programType, MethodBuilder typedArrow, int funcArity)
    {
        var key = (typedArrow, funcArity);
        if (_cache.TryGetValue(key, out var existing)) return existing;

        var objectType = typeof(object);
        var adapterParams = new Type[funcArity];
        for (int i = 0; i < funcArity; i++) adapterParams[i] = objectType;

        // Assembly-visible static, matching the arrow methods on $Program it calls
        // into. Name keyed off the arrow's unique method name (e.g. <>Arrow_5) so
        // two contexts emitting adapters onto the same $Program never collide.
        var adapter = programType.DefineMethod(
            $"{typedArrow.Name}$box{funcArity}",
            MethodAttributes.Assembly | MethodAttributes.Static,
            objectType,
            adapterParams);

        var il = adapter.GetILGenerator();
        var arrowParams = typedArrow.GetParameters();

        // Load only as many args as the arrow actually declares, coercing each
        // boxed object into its typed parameter slot. A 0-/1-param arrow under a
        // 2-arg delegate (or 1-arg delegate) simply ignores the surplus args.
        for (int i = 0; i < arrowParams.Length; i++)
        {
            EmitLdarg(il, i);
            DelegateAdapterEmitter.EmitUnboxForReturn(il, arrowParams[i].ParameterType);
        }

        il.Emit(OpCodes.Call, typedArrow);

        // Rebox the typed result back to object for the Func<object,…> contract.
        DelegateAdapterEmitter.EmitBoxForTS(il, typedArrow.ReturnType);
        il.Emit(OpCodes.Ret);

        _cache[key] = adapter;
        return adapter;
    }

    private static void EmitLdarg(ILGenerator il, int index)
    {
        switch (index)
        {
            case 0: il.Emit(OpCodes.Ldarg_0); break;
            case 1: il.Emit(OpCodes.Ldarg_1); break;
            case 2: il.Emit(OpCodes.Ldarg_2); break;
            case 3: il.Emit(OpCodes.Ldarg_3); break;
            default:
                if (index <= 255) il.Emit(OpCodes.Ldarg_S, (byte)index);
                else il.Emit(OpCodes.Ldarg, index);
                break;
        }
    }
}
