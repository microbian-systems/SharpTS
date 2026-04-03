using System.Reflection;
using System.Reflection.Emit;
using SharpTS.Parsing;
using SharpTS.TypeSystem;

namespace SharpTS.Compilation;

/// <summary>
/// Async function and Promise emission for the IL emitter.
/// </summary>
public partial class ILEmitter
{
    private void EmitPromiseStaticCall(string methodName, List<Expr> arguments)
    {
        switch (methodName)
        {
            case "resolve":
                // Promise.resolve(value?) - returns Task<object?> directly
                // The caller (async context) will await if needed
                if (arguments.Count > 0)
                {
                    EmitExpression(arguments[0]);
                    EmitBoxIfNeeded(arguments[0]);
                }
                else
                {
                    IL.Emit(OpCodes.Ldnull);
                }
                IL.Emit(OpCodes.Call, _ctx.Runtime!.PromiseResolve);
                // Don't await here - return the Task directly
                // Sync functions return Task, async functions will await via proper machinery
                return;

            case "reject":
                // Promise.reject(reason) - returns Task<object?> directly
                if (arguments.Count > 0)
                {
                    EmitExpression(arguments[0]);
                    EmitBoxIfNeeded(arguments[0]);
                }
                else
                {
                    IL.Emit(OpCodes.Ldnull);
                }
                IL.Emit(OpCodes.Call, _ctx.Runtime!.PromiseReject);
                // Don't await here - return the Task directly
                return;

            case "all":
                // Promise.all(iterable) - returns Task<object?> directly
                if (arguments.Count > 0)
                {
                    EmitExpression(arguments[0]);
                    EmitBoxIfNeeded(arguments[0]);
                }
                else
                {
                    IL.Emit(OpCodes.Ldnull);
                }
                IL.Emit(OpCodes.Call, _ctx.Runtime!.PromiseAll);
                // Don't await here - return the Task directly
                return;

            case "race":
                // Promise.race(iterable) - returns Task<object?> directly
                if (arguments.Count > 0)
                {
                    EmitExpression(arguments[0]);
                    EmitBoxIfNeeded(arguments[0]);
                }
                else
                {
                    IL.Emit(OpCodes.Ldnull);
                }
                IL.Emit(OpCodes.Call, _ctx.Runtime!.PromiseRace);
                // Don't await here - return the Task directly
                return;

            case "allSettled":
                // Promise.allSettled(iterable) - returns Task<object?> directly
                if (arguments.Count > 0)
                {
                    EmitExpression(arguments[0]);
                    EmitBoxIfNeeded(arguments[0]);
                }
                else
                {
                    IL.Emit(OpCodes.Ldnull);
                }
                IL.Emit(OpCodes.Call, _ctx.Runtime!.PromiseAllSettled);
                // Don't await here - return the Task directly
                return;

            case "any":
                // Promise.any(iterable) - returns Task<object?> directly
                if (arguments.Count > 0)
                {
                    EmitExpression(arguments[0]);
                    EmitBoxIfNeeded(arguments[0]);
                }
                else
                {
                    IL.Emit(OpCodes.Ldnull);
                }
                IL.Emit(OpCodes.Call, _ctx.Runtime!.PromiseAny);
                // Don't await here - return the Task directly
                return;

            case "withResolvers":
                // Promise.withResolvers() - returns Task<object?> wrapping {promise, resolve, reject}
                IL.Emit(OpCodes.Call, _ctx.Runtime!.PromiseWithResolvers);
                // Don't await here - return the Task directly
                return;

            default:
                IL.Emit(OpCodes.Ldnull);
                return;
        }
    }

    /// <summary>
    /// Emits code to await a Task<object?> and get its result.
    /// </summary>
    private void EmitAwaitTask()
    {
        // Store task in local
        var taskOfObject = _ctx.Types.TaskOfObject;
        var taskLocal = IL.DeclareLocal(taskOfObject);
        IL.Emit(OpCodes.Stloc, taskLocal);
        IL.Emit(OpCodes.Ldloca, taskLocal);

        // Call GetAwaiter()
        var getAwaiter = _ctx.Types.GetMethod(taskOfObject, "GetAwaiter");
        IL.Emit(OpCodes.Call, getAwaiter);

        // Store awaiter and call GetResult()
        var awaiterType = _ctx.Types.TaskAwaiterOfObject;
        var awaiterLocal = IL.DeclareLocal(awaiterType);
        IL.Emit(OpCodes.Stloc, awaiterLocal);
        IL.Emit(OpCodes.Ldloca, awaiterLocal);
        var getResult = _ctx.Types.GetMethod(awaiterType, "GetResult");
        IL.Emit(OpCodes.Call, getResult);
    }
}
