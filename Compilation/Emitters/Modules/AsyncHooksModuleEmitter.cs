using System.Reflection.Emit;
using SharpTS.Parsing;

namespace SharpTS.Compilation.Emitters.Modules;

/// <summary>
/// Emits IL code for the Node.js 'async_hooks' module.
/// The main export is the AsyncLocalStorage constructor class.
/// </summary>
public sealed class AsyncHooksModuleEmitter : IBuiltInModuleEmitter
{
    public string ModuleName => "async_hooks";

    private static readonly string[] _exportedMembers = ["AsyncLocalStorage"];

    public IReadOnlyList<string> GetExportedMembers() => _exportedMembers;

    public bool TryEmitMethodCall(IEmitterContext emitter, string methodName, List<Expr> arguments)
    {
        // The async_hooks module doesn't have direct method calls - methods are on AsyncLocalStorage instances
        return false;
    }

    public bool TryEmitPropertyGet(IEmitterContext emitter, string propertyName)
    {
        if (propertyName != "AsyncLocalStorage")
            return false;

        var ctx = emitter.Context;
        var il = ctx.IL;

        // Emit a placeholder value for AsyncLocalStorage.
        // Actual instantiation (new AsyncLocalStorage()) happens via EmitNew.
        il.Emit(OpCodes.Ldstr, "[AsyncLocalStorage]");
        return true;
    }
}
