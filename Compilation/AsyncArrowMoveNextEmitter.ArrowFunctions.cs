using System.Reflection;
using System.Reflection.Emit;
using SharpTS.Diagnostics.Exceptions;
using SharpTS.Parsing;

namespace SharpTS.Compilation;

public partial class AsyncArrowMoveNextEmitter
{
    protected override void EmitArrowFunction(Expr.ArrowFunction af)
    {
        // Check if it's an async arrow (nested async arrow)
        if (af.IsAsync)
        {
            EmitNestedAsyncArrow(af);
            return;
        }

        // Get the method for this arrow function (pre-compiled)
        if (_ctx?.ArrowMethods == null || !_ctx.ArrowMethods.TryGetValue(af, out var method))
        {
            // Fallback if not found
            _il.Emit(OpCodes.Ldnull);
            SetStackUnknown();
            return;
        }

        // Check if this is a capturing arrow (has display class)
        if (_ctx.DisplayClasses != null && _ctx.DisplayClasses.TryGetValue(af, out var displayClass))
        {
            EmitCapturingArrowInAsyncArrow(af, method, displayClass);
        }
        else
        {
            EmitNonCapturingArrowInAsyncArrow(af, method);
        }
    }

    private void EmitCapturingArrowInAsyncArrow(Expr.ArrowFunction af, MethodBuilder method, TypeBuilder displayClass)
    {
        // Get the pre-tracked constructor
        if (_ctx!.DisplayClassConstructors == null || !_ctx.DisplayClassConstructors.TryGetValue(af, out var displayCtor))
        {
            _il.Emit(OpCodes.Ldnull);
            SetStackUnknown();
            return;
        }

        // Create display class instance
        _il.Emit(OpCodes.Newobj, displayCtor);

        // Get captured variables field mapping
        if (_ctx.DisplayClassFields == null || !_ctx.DisplayClassFields.TryGetValue(af, out var fieldMap))
        {
            // No fields to populate, just create TSFunction
            _il.Emit(OpCodes.Ldtoken, method);
            _il.Emit(OpCodes.Call, Types.MethodBaseGetMethodFromHandle);
            _il.Emit(OpCodes.Castclass, typeof(MethodInfo));
            _il.Emit(OpCodes.Newobj, _ctx.Runtime!.TSFunctionCtor);
            SetStackUnknown();
            return;
        }

        // Populate captured fields from async arrow state machine context
        foreach (var (capturedVar, field) in fieldMap)
        {
            _il.Emit(OpCodes.Dup); // Keep display class instance on stack

            // Load the captured variable using the same logic as LoadVariable
            LoadVariableForCapture(capturedVar);

            _il.Emit(OpCodes.Stfld, field);
        }

        // Create TSFunction: new TSFunction(displayInstance, method)
        // Stack has: displayInstance
        _il.Emit(OpCodes.Ldtoken, method);
        _il.Emit(OpCodes.Call, Types.MethodBaseGetMethodFromHandle);
        _il.Emit(OpCodes.Castclass, typeof(MethodInfo));
        _il.Emit(OpCodes.Newobj, _ctx.Runtime!.TSFunctionCtor);
        SetStackUnknown();
    }

    private void EmitNonCapturingArrowInAsyncArrow(Expr.ArrowFunction af, MethodBuilder method)
    {
        // Create TSFunction for static method: new TSFunction(null, method)
        _il.Emit(OpCodes.Ldnull);
        _il.Emit(OpCodes.Ldtoken, method);
        _il.Emit(OpCodes.Call, Types.MethodBaseGetMethodFromHandle);
        _il.Emit(OpCodes.Castclass, typeof(MethodInfo));
        _il.Emit(OpCodes.Newobj, _ctx!.Runtime!.TSFunctionCtor);
        SetStackUnknown();
    }

    private void EmitNestedAsyncArrow(Expr.ArrowFunction af)
    {
        // Get the nested async arrow's state machine builder
        if (_ctx?.AsyncArrowBuilders == null ||
            !_ctx.AsyncArrowBuilders.TryGetValue(af, out var nestedBuilder))
        {
            throw new CompileException(
                "Nested async arrow function not registered with state machine builder.");
        }

        // A *standalone* nested async arrow — one whose enclosing scope is itself a top-level async
        // arrow rather than an async function — has no outer state machine to thread. Its stub takes
        // only its own (captures…, params…); there is no self-boxed instance to share. Emit it as a
        // plain TSFunction over its stub with a null target, exactly like a non-capturing arrow value.
        // This is the path the #615 repro hits: a top-level async arrow whose nested async arrows are
        // all registered standalone, so without this branch they fell through to the SelfBoxedField
        // guard below and the compile failed. (#615)
        if (nestedBuilder.IsStandalone)
        {
            if (nestedBuilder.StandaloneCaptureFields.Count > 0)
            {
                // A standalone nested async arrow that closes over the enclosing arrow's locals needs
                // those captured values threaded into its stub args; that wiring isn't in place yet.
                // Fail with a clear message instead of silently passing the wrong target. (#641)
                throw new CompileException(
                    "Compiled mode does not yet support a capturing async arrow nested directly inside " +
                    "a top-level async arrow. Move the inner arrow to a named binding or a regular async " +
                    "function as a workaround.");
            }

            _il.Emit(OpCodes.Ldnull);
            _il.Emit(OpCodes.Ldtoken, nestedBuilder.StubMethod);
            _il.Emit(OpCodes.Call, Types.MethodBaseGetMethodFromHandle);
            _il.Emit(OpCodes.Castclass, typeof(MethodInfo));
            _il.Emit(OpCodes.Newobj, _ctx!.Runtime!.TSFunctionCtor);
            SetStackUnknown();
            return;
        }

        // Non-standalone (nested inside an async function): the nested arrow's stub expects the
        // enclosing scope's boxed state machine as its outer reference (for by-reference captures).
        if (_builder.SelfBoxedField != null)
        {
            _il.Emit(OpCodes.Ldarg_0);
            _il.Emit(OpCodes.Ldfld, _builder.SelfBoxedField);
        }
        else
        {
            // Fallback: this shouldn't happen if hasNestedAsyncArrows was set correctly
            throw new CompileException(
                "Async arrow with nested arrows does not have SelfBoxedField set.");
        }

        // Load the stub method for the nested arrow
        _il.Emit(OpCodes.Ldtoken, nestedBuilder.StubMethod);
        _il.Emit(OpCodes.Call, Types.MethodBaseGetMethodFromHandle);
        _il.Emit(OpCodes.Castclass, typeof(MethodInfo));

        // Create TSFunction(target: self boxed, method: stub)
        _il.Emit(OpCodes.Newobj, _ctx!.Runtime!.TSFunctionCtor);

        SetStackUnknown();
    }
}
