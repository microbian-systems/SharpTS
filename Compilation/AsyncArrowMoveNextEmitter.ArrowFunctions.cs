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

        // A nested async-arrow expression inside a top-level (standalone) async arrow — e.g. an
        // immediately-invoked `(async () => 9)()` — is registered as its own standalone builder
        // whose stub takes only its own parameters (and captures): there is no enclosing state
        // machine to thread. Emit it as a plain TSFunction over that stub with a NULL target,
        // exactly like a non-capturing arrow value. Threading the enclosing arrow's boxed state
        // machine here instead (the nested-in-async-function mechanism) would prepend it as the
        // stub's arg0, clobbering the first real parameter. (#615)
        if (nestedBuilder.IsStandalone)
        {
            // A capturing standalone nested arrow would need its capture fields populated from the
            // enclosing frame, which this path does not wire up — fail loudly rather than read
            // uninitialized captures (it did not compile at all before #615; tracked in #641).
            if (nestedBuilder.StandaloneCaptureFields.Count > 0)
            {
                throw new CompileException(
                    "A nested async arrow that captures variables inside a top-level async arrow is " +
                    "not yet supported in compiled mode; hoist it to a named async arrow or async " +
                    "function, or run in interpreted mode.");
            }

            _il.Emit(OpCodes.Ldnull);
            _il.Emit(OpCodes.Ldtoken, nestedBuilder.StubMethod);
            _il.Emit(OpCodes.Call, Types.MethodBaseGetMethodFromHandle);
            _il.Emit(OpCodes.Castclass, typeof(MethodInfo));
            _il.Emit(OpCodes.Newobj, _ctx!.Runtime!.TSFunctionCtor);
            SetStackUnknown();
            return;
        }

        // Non-standalone nested arrow (nested inside an async function): the nested arrow's stub
        // expects (outer state machine boxed, params...), so thread the enclosing arrow's boxed
        // self-reference as the "outer" target.
        if (_builder.SelfBoxedField != null)
        {
            _il.Emit(OpCodes.Ldarg_0);
            _il.Emit(OpCodes.Ldfld, _builder.SelfBoxedField);
        }
        else
        {
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
