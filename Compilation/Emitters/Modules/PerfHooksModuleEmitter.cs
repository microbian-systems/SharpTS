using System.Reflection.Emit;
using SharpTS.Parsing;

namespace SharpTS.Compilation.Emitters.Modules;

/// <summary>
/// Emits IL code for the Node.js 'perf_hooks' module.
/// </summary>
/// <remarks>
/// Provides high-resolution timing APIs similar to the browser's Performance API,
/// including mark(), measure(), getEntries*(), clear*(), and PerformanceObserver.
/// </remarks>
public sealed class PerfHooksModuleEmitter : IBuiltInModuleEmitter
{
    public string ModuleName => "perf_hooks";

    private static readonly string[] _exportedMembers = ["performance", "PerformanceObserver"];

    public IReadOnlyList<string> GetExportedMembers() => _exportedMembers;

    public bool TryEmitMethodCall(IEmitterContext emitter, string methodName, List<Expr> arguments)
    {
        // performance is an object, not a method
        // Method calls like performance.now() are handled as property access + call
        return false;
    }

    public bool TryEmitPropertyGet(IEmitterContext emitter, string propertyName)
    {
        if (propertyName == "performance")
        {
            var ctx = emitter.Context;
            var il = ctx.IL;
            il.Emit(OpCodes.Call, ctx.Runtime!.PerfHooksGetPerformance);
            return true;
        }

        if (propertyName == "PerformanceObserver")
        {
            // Return a TSFunction wrapping PerfHooksCreateObserverWrapper
            var ctx = emitter.Context;
            var il = ctx.IL;
            il.Emit(OpCodes.Ldnull); // target
            il.Emit(OpCodes.Ldtoken, ctx.Runtime!.PerfHooksCreateObserver);
            il.Emit(OpCodes.Call, typeof(System.Reflection.MethodBase)
                .GetMethod("GetMethodFromHandle", [typeof(RuntimeMethodHandle)])!);
            il.Emit(OpCodes.Castclass, typeof(System.Reflection.MethodInfo));
            il.Emit(OpCodes.Newobj, ctx.Runtime.TSFunctionCtor);
            return true;
        }

        return false;
    }

    public bool IsExportedProperty(string memberName) => memberName is "performance" or "PerformanceObserver";
}
