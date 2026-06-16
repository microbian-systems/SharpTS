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

            // Load the captured variable using the capture-population loader
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
            // A capturing standalone nested arrow's stub takes its single captured value as a leading
            // argument. $TSFunction's "static method with target" mechanism prepends the target before
            // the call args, so we pass the capture AS the target, read from THIS (enclosing) arrow's
            // frame. Mirrors the module-level standalone path in ILEmitter.EmitAsyncArrowFunction.
            // Before #641 every capturing standalone nested arrow threw "not yet supported"; now the
            // single read-only capture (the #641 repros) is supported, and the two harder shapes below
            // fail loudly rather than miscompiling.
            var captureOrder = nestedBuilder.StandaloneCaptureFields.Keys
                .OrderBy(k => k, System.StringComparer.Ordinal).ToList();

            if (captureOrder.Count > 0)
            {
                // A standalone arrow captures BY VALUE (the values are copied into its own state
                // machine). A write to a capture therefore cannot propagate back to the enclosing
                // binding, so reject it instead of silently dropping the write (#684).
                var written = CapturedWriteAnalysis.CollectImmediateWrites(af);
                written.IntersectWith(captureOrder);
                if (written.Count > 0)
                {
                    throw new CompileException(
                        "A nested async arrow inside a top-level async arrow that WRITES a captured " +
                        $"variable ({string.Join(", ", written.OrderBy(n => n, System.StringComparer.Ordinal))}) " +
                        "is not yet supported in compiled mode (#684): the standalone arrow captures by " +
                        "value, so the write would be lost. Hoist it to a named async function, or run " +
                        "in interpreted mode.");
                }

                // Multiple captures would need to ride in $TSFunction's single prepended target slot
                // (an object[]), but the standalone stub expects each capture as a separate arg — a
                // mismatch also broken for module-level standalone async arrows (#684).
                if (captureOrder.Count > 1)
                {
                    throw new CompileException(
                        "A nested async arrow inside a top-level async arrow that captures more than " +
                        "one variable is not yet supported in compiled mode (#684); reduce it to a " +
                        "single capture, hoist it to a named async function, or run in interpreted mode.");
                }
            }

            if (captureOrder.Count == 1)
            {
                LoadVariableForCapture(captureOrder[0]);
                EnsureBoxed();
            }
            else
            {
                _il.Emit(OpCodes.Ldnull);
            }

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
