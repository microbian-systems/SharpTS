using System.Reflection.Emit;
using SharpTS.Parsing;
using SharpTS.Runtime.BuiltIns.Modules;

namespace SharpTS.Compilation;

/// <summary>
/// CommonJS lowering helpers shared by all expression emitters (ILEmitter and the four state
/// machine emitters: AsyncMoveNextEmitter, AsyncArrowMoveNextEmitter, GeneratorMoveNextEmitter,
/// AsyncGeneratorMoveNextEmitter).
/// </summary>
/// <remarks>
/// These helpers are placed on <see cref="ExpressionEmitterBase"/> so that <c>require()</c>,
/// <c>module.exports</c>, and bare <c>exports</c> all work inside async/generator function bodies
/// nested in a CJS module — without each state machine emitter needing its own copy.
///
/// The helpers use the public <see cref="ExpressionEmitterBase.Ctx"/> property and the protected
/// <see cref="ExpressionEmitterBase.IL"/> property, so they're accessible to derived classes
/// regardless of which subclass is currently emitting.
/// </remarks>
public abstract partial class ExpressionEmitterBase
{
    /// <summary>True if the current emission context is inside a CJS module body.</summary>
    protected bool InCjsContext => Ctx.CurrentCjsExportsField != null;

    /// <summary>
    /// Tries to load a CJS-special variable (currently just <c>exports</c>). Returns true if handled.
    /// </summary>
    protected bool TryEmitCjsVariable(string name)
    {
        if (!InCjsContext) return false;

        if (name == "exports")
        {
            IL.Emit(OpCodes.Ldsfld, Ctx.CurrentCjsExportsField!);
            SetStackUnknown();
            return true;
        }

        return false;
    }

    /// <summary>
    /// Tries to handle a Get expression matching <c>module.exports</c>. Returns true if handled.
    /// </summary>
    protected bool TryEmitCjsGet(Expr.Get get)
    {
        if (!InCjsContext) return false;

        if (get.Object is Expr.Variable v &&
            v.Name.Lexeme == "module" &&
            get.Name.Lexeme == "exports")
        {
            IL.Emit(OpCodes.Ldsfld, Ctx.CurrentCjsExportsField!);
            SetStackUnknown();
            return true;
        }

        return false;
    }

    /// <summary>
    /// Tries to handle a Set expression matching <c>module.exports = X</c>. Returns true if handled.
    /// Leaves the assigned value on the stack as the expression result (matching JS semantics).
    /// </summary>
    protected bool TryEmitCjsSet(Expr.Set set)
    {
        if (!InCjsContext) return false;

        if (set.Object is Expr.Variable v &&
            v.Name.Lexeme == "module" &&
            set.Name.Lexeme == "exports")
        {
            EmitExpression(set.Value);
            EnsureBoxed();
            IL.Emit(OpCodes.Dup);
            IL.Emit(OpCodes.Stsfld, Ctx.CurrentCjsExportsField!);
            SetStackUnknown();
            return true;
        }

        return false;
    }

    /// <summary>
    /// Tries to lower a <c>require('./literal')</c> call to a direct call to the target module's
    /// <c>$GetExports</c> method. Returns true if handled.
    /// </summary>
    /// <remarks>
    /// Available in BOTH CJS and ESM contexts (mirrors interpreter mode where <c>require</c> is
    /// global). Specifier MUST be a string literal — non-literal specifiers throw a compile error
    /// (SHARPTS_CJS001) per the strict-AOT design.
    /// </remarks>
    protected bool TryEmitCjsRequireCall(Expr.Call call)
    {
        if (call.Callee is not Expr.Variable callee || callee.Name.Lexeme != "require")
            return false;

        if (call.Arguments.Count != 1)
        {
            throw new InvalidOperationException(
                "SHARPTS_CJS001: require() requires exactly one argument.");
        }

        if (call.Arguments[0] is not Expr.Literal lit || lit.Value is not string specifier)
        {
            throw new InvalidOperationException(
                "SHARPTS_CJS001: require() specifier must be a string literal in compiled mode.");
        }

        if (Ctx.ModuleResolver == null || Ctx.CurrentModulePath == null ||
            Ctx.CommonJsGetExportsMethods == null)
        {
            // Insufficient context to lower require() — most commonly because we're inside a
            // state machine emitter (async/generator) whose ctx isn't seeded with module info.
            // V1 limitation: hoist require() calls to the top of the CJS module body.
            // Returning false lets the call fall through to the normal "undefined function" path
            // which produces a clearer runtime error than aborting compilation here.
            return false;
        }

        string resolvedPath;
        try
        {
            resolvedPath = Ctx.ModuleResolver.ResolveModulePath(specifier, Ctx.CurrentModulePath);
        }
        catch (Exception ex)
        {
            // Module didn't resolve at compile time — emit a runtime throw so try/catch can catch it.
            EmitRuntimeModuleNotFound(specifier, ex.Message);
            return true;
        }

        // Handle built-in modules (e.g., builtin:tty, builtin:path)
        var builtInName = BuiltInModuleRegistry.GetModuleName(resolvedPath);
        if (builtInName != null)
        {
            EmitCjsBuiltInModuleObject(builtInName);
            return true;
        }

        if (!Ctx.CommonJsGetExportsMethods.TryGetValue(resolvedPath, out var getExportsMethod))
        {
            EmitRuntimeModuleNotFound(specifier,
                $"resolved to '{resolvedPath}' which is not a CJS module in this assembly");
            return true;
        }

        IL.Emit(OpCodes.Call, getExportsMethod);
        SetStackUnknown();
        return true;
    }

    /// <summary>
    /// Emits a built-in module namespace object for CJS require() calls.
    /// Creates a Dictionary&lt;string, object?&gt; wrapped in a SharpTSObject with all exported members.
    /// </summary>
    private void EmitCjsBuiltInModuleObject(string moduleName)
    {
        var emitter = Ctx.BuiltInModuleEmitterRegistry?.GetEmitter(moduleName);
        if (emitter == null)
        {
            IL.Emit(OpCodes.Ldnull);
            SetStackUnknown();
            return;
        }

        var dictType = Ctx.Types.DictionaryStringObject;
        var dictCtor = Ctx.Types.GetDefaultConstructor(dictType);
        var addMethod = Ctx.Types.GetMethod(dictType, "Add", Ctx.Types.String, Ctx.Types.Object);

        IL.Emit(OpCodes.Newobj, dictCtor);

        foreach (var memberName in emitter.GetExportedMembers())
        {
            IL.Emit(OpCodes.Dup);
            IL.Emit(OpCodes.Ldstr, memberName);

            if (!emitter.TryEmitPropertyGet(this, memberName))
            {
                // For methods, create a TSFunction wrapper
                var helperMethod = Ctx.Runtime?.GetBuiltInModuleMethod(moduleName, memberName);
                if (helperMethod != null)
                {
                    IL.Emit(OpCodes.Ldnull);
                    IL.Emit(OpCodes.Ldtoken, helperMethod);
                    var runtimeMethodHandle = Ctx.Types.Resolve("System.RuntimeMethodHandle");
                    var methodBase = Ctx.Types.Resolve("System.Reflection.MethodBase");
                    IL.Emit(OpCodes.Call, Ctx.Types.GetMethod(methodBase, "GetMethodFromHandle", runtimeMethodHandle));
                    IL.Emit(OpCodes.Castclass, Ctx.Types.MethodInfo);
                    IL.Emit(OpCodes.Newobj, Ctx.Runtime!.TSFunctionCtor);
                }
                else
                {
                    IL.Emit(OpCodes.Ldnull);
                }
            }

            EnsureBoxed();
            IL.Emit(OpCodes.Callvirt, addMethod);
        }

        IL.Emit(OpCodes.Call, Ctx.Runtime!.CreateObject);
        SetStackUnknown();
    }

    /// <summary>
    /// Emits IL that throws a Node-style MODULE_NOT_FOUND-flavored error at runtime.
    /// </summary>
    private void EmitRuntimeModuleNotFound(string specifier, string detail)
    {
        IL.Emit(OpCodes.Ldstr, $"Cannot find module '{specifier}': {detail}");
        var exCtor = typeof(Exception).GetConstructor([typeof(string)])!;
        IL.Emit(OpCodes.Newobj, exCtor);
        IL.Emit(OpCodes.Throw);
        SetStackUnknown();
    }
}
