using SharpTS.Parsing;

namespace SharpTS.Compilation.CallHandlers;

/// <summary>
/// Handles built-in module method calls: path.join, fs.readFileSync, os.platform, etc.
/// Also handles nested calls like util.types.isArray().
/// Delegates to BuiltInModuleEmitterRegistry for module-specific emission.
/// </summary>
public class BuiltInModuleHandler : ICallHandler
{
    public int Priority => 40;

    public bool TryHandle(ILEmitter emitter, Expr.Call call)
    {
        var ctx = emitter.Context;
        if (ctx.BuiltInModuleNamespaces == null || ctx.BuiltInModuleEmitterRegistry == null)
            return false;

        // Try direct module method call: module.method()
        if (call.Callee is Expr.Get builtInGet &&
            builtInGet.Object is Expr.Variable builtInVar &&
            ctx.BuiltInModuleNamespaces.TryGetValue(builtInVar.Name.Lexeme, out var builtInModuleName))
        {
            var builtInEmitter = ctx.BuiltInModuleEmitterRegistry.GetEmitter(builtInModuleName);
            if (builtInEmitter != null &&
                builtInEmitter.TryEmitMethodCall(emitter, builtInGet.Name.Lexeme, call.Arguments))
            {
                emitter.ResetStackType();
                return true;
            }
        }

        // Try nested module method call: module.namespace.method() (e.g., util.types.isArray)
        if (call.Callee is Expr.Get nestedGet &&
            nestedGet.Object is Expr.Get parentGet &&
            parentGet.Object is Expr.Variable moduleVar &&
            ctx.BuiltInModuleNamespaces.TryGetValue(moduleVar.Name.Lexeme, out var nestedModuleName))
        {
            var nestedEmitter = ctx.BuiltInModuleEmitterRegistry.GetEmitter(nestedModuleName);
            if (nestedEmitter != null)
            {
                // Try with combined name: "types.isArray"
                var combinedName = $"{parentGet.Name.Lexeme}.{nestedGet.Name.Lexeme}";
                if (nestedEmitter.TryEmitMethodCall(emitter, combinedName, call.Arguments))
                {
                    emitter.ResetStackType();
                    return true;
                }
            }
        }

        // Try named import static method call: Readable.from(), EventEmitter.once(), etc.
        if (call.Callee is Expr.Get namedGet &&
            namedGet.Object is Expr.Variable namedVar &&
            ctx.BuiltInModuleMethodBindings?.TryGetValue(namedVar.Name.Lexeme, out var namedBinding) == true)
        {
            var namedEmitter = ctx.BuiltInModuleEmitterRegistry.GetEmitter(namedBinding.ModuleName);
            if (namedEmitter != null)
            {
                var combinedKey = $"{namedBinding.MethodName}.{namedGet.Name.Lexeme}";
                if (namedEmitter.TryEmitMethodCall(emitter, combinedKey, call.Arguments))
                {
                    emitter.ResetStackType();
                    return true;
                }
            }
        }

        return false;
    }
}
