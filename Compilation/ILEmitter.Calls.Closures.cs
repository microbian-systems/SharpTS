using System.Reflection;
using System.Reflection.Emit;
using SharpTS.Parsing;
using SharpTS.TypeSystem;

namespace SharpTS.Compilation;

/// <summary>
/// Arrow function and closure emission for the IL emitter.
/// </summary>
public partial class ILEmitter
{
    /// <summary>
    /// Iterator-helper fast path: emit <paramref name="af"/> as a typed
    /// delegate of <paramref name="delegateType"/> (Func&lt;,&gt; / Func&lt;,,&gt;
    /// etc.) without going through <c>$TSFunction</c>. Handles both
    /// non-capturing arrows (<c>ldftn</c> on the static method) and
    /// capturing arrows (allocate + populate display class, then <c>ldftn</c>
    /// on the instance Invoke method). Returns false on unsupported shape:
    /// named function expression with self-ref, async, generator, missing
    /// arrow method, missing display ctor.
    /// </summary>
    protected override bool TryEmitArrowAsDelegate(Expr.ArrowFunction af, Type delegateType)
    {
        if (af.IsAsync || af.IsGenerator) return false;
        // Self-reference (named function expression) needs the wrapper to
        // be the captured value, not the bare display instance. Skip.
        if (af.Name != null) return false;
        if (!_ctx.ArrowMethods.TryGetValue(af, out var method)) return false;

        var delegateCtor = delegateType.GetConstructor([typeof(object), typeof(IntPtr)]);
        if (delegateCtor == null) return false;

        if (_ctx.DisplayClasses.TryGetValue(af, out var displayClass))
        {
            // Capturing path: build display instance, then bind delegate to
            // instance + Invoke method.
            if (!EmitCapturingArrowDisplayInstance(af, displayClass))
                return false;
            // Stack: [displayInstance]
            IL.Emit(OpCodes.Ldftn, method);
            IL.Emit(OpCodes.Newobj, delegateCtor);
            SetStackUnknown();
            return true;
        }

        // Non-capturing path: target = null, ldftn on the static method.
        IL.Emit(OpCodes.Ldnull);
        IL.Emit(OpCodes.Ldftn, method);
        IL.Emit(OpCodes.Newobj, delegateCtor);
        SetStackUnknown();
        return true;
    }

    protected override void EmitArrowFunction(Expr.ArrowFunction af)
    {
        // Check if this is an async arrow function with a state machine
        if (af.IsAsync && _ctx.AsyncArrowBuilders?.TryGetValue(af, out var arrowBuilder) == true)
        {
            // Async arrow with its own state machine - emit a callable for the stub method
            EmitAsyncArrowFunction(af, arrowBuilder);
            SetStackUnknown();
            return;
        }

        // Get the method for this arrow function
        if (!_ctx.ArrowMethods.TryGetValue(af, out var method))
        {
            // Fallback if not found (shouldn't happen with proper collection)
            IL.Emit(OpCodes.Ldnull);
            SetStackUnknown();
            return;
        }

        // Check if this is a capturing arrow (has display class)
        if (_ctx.DisplayClasses.TryGetValue(af, out var displayClass))
        {
            // Capturing arrow: create display class instance and populate fields
            EmitCapturingArrowFunction(af, method, displayClass);
        }
        else
        {
            // Non-capturing arrow: create TSFunction wrapping static method
            EmitNonCapturingArrowFunction(af, method);
        }

        // $TSFunction is a reference type, mark stack as unknown (not a value type)
        SetStackUnknown();
    }

    private void EmitAsyncArrowFunction(Expr.ArrowFunction af, AsyncArrowStateMachineBuilder arrowBuilder)
    {
        var stubMethod = arrowBuilder.StubMethod;

        // For standalone async arrows with captures, TSFunction's "static method with target"
        // mechanism prepends the target to the args, so captures get passed as leading args.
        if (arrowBuilder.IsStandalone && arrowBuilder.StandaloneCaptureFields.Count > 0)
        {
            var captureOrder = arrowBuilder.StandaloneCaptureFields.Keys.OrderBy(k => k).ToList();

            if (captureOrder.Count == 1)
            {
                // Single capture: use TSFunction target prepend mechanism.
                // TSFunction.Invoke detects static method + non-null target and prepends target as arg0.
                var captureName = captureOrder[0];
                if (captureName == "this")
                {
                    // Load 'this' (arg0 in instance methods)
                    IL.Emit(OpCodes.Ldarg_0);
                }
                else
                {
                    // Load the captured variable
                    EmitVariable(new Expr.Variable(new Parsing.Token(Parsing.TokenType.IDENTIFIER, captureName, null, 0)));
                    EnsureBoxed();
                }
            }
            else
            {
                // Multiple captures: pack into an object[] and use as target.
                // The stub's paramOffset handles unpacking from arg0.
                IL.Emit(OpCodes.Ldc_I4, captureOrder.Count);
                IL.Emit(OpCodes.Newarr, _ctx.Types.Object);
                for (int i = 0; i < captureOrder.Count; i++)
                {
                    IL.Emit(OpCodes.Dup);
                    IL.Emit(OpCodes.Ldc_I4, i);
                    var captureName = captureOrder[i];
                    if (captureName == "this")
                    {
                        IL.Emit(OpCodes.Ldarg_0);
                    }
                    else
                    {
                        EmitVariable(new Expr.Variable(new Parsing.Token(Parsing.TokenType.IDENTIFIER, captureName, null, 0)));
                        EnsureBoxed();
                    }
                    IL.Emit(OpCodes.Stelem_Ref);
                }
            }
        }
        else
        {
            // No captures: new TSFunction(null, stubMethod)
            IL.Emit(OpCodes.Ldnull);
        }

        // Load method info for the stub method
        IL.Emit(OpCodes.Ldtoken, stubMethod);
        IL.Emit(OpCodes.Call, _ctx.Types.GetMethod(_ctx.Types.MethodBase, "GetMethodFromHandle", _ctx.Types.RuntimeMethodHandle));
        IL.Emit(OpCodes.Castclass, _ctx.Types.MethodInfo);

        // Create TSFunction
        IL.Emit(OpCodes.Newobj, _ctx.Runtime!.TSFunctionCtor);
    }

    private void EmitNonCapturingArrowFunction(Expr.ArrowFunction af, MethodBuilder method)
    {
        // Create TSFunction for static method:
        // new TSFunction(null, method)

        // Push null (no target)
        IL.Emit(OpCodes.Ldnull);

        // Get MethodInfo from the method builder using reflection
        // We need to load the method as a MethodInfo at runtime
        // Use Type.GetMethod or RuntimeMethodHandle

        // Load the method as a runtime handle and convert to MethodInfo
        IL.Emit(OpCodes.Ldtoken, method);

        // For static methods on a non-generic type:
        IL.Emit(OpCodes.Call, _ctx.Types.GetMethod(_ctx.Types.MethodBase, "GetMethodFromHandle", _ctx.Types.RuntimeMethodHandle));
        IL.Emit(OpCodes.Castclass, _ctx.Types.MethodInfo);

        // Call $TSFunction constructor
        IL.Emit(OpCodes.Newobj, _ctx.Runtime!.TSFunctionCtor);
    }

    /// <summary>
    /// Emits IL that allocates the display class instance for a capturing
    /// arrow and populates its fields (special DC chains + ordinary captures).
    /// On exit, the display-instance reference is the only thing left on the
    /// stack — caller decides what to do with it next ($TSFunction wrap, or
    /// for the iterator-helper fast path, an <c>ldftn</c> + Func ctor).
    ///
    /// Self-reference handling (named function expressions where the arrow's
    /// own name is captured into a field) is intentionally NOT done here —
    /// that field is populated AFTER the wrapping callable is created so it
    /// can hold the wrapper reference, not the bare display instance. Caller
    /// is responsible for that follow-up if needed; the iterator-helper fast
    /// path declines arrows where this matters.
    /// </summary>
    /// <returns>true if the display instance was emitted; false on
    /// unsupported configuration (the only current case is the constructor
    /// not being tracked, which is internally inconsistent — slow-path caller
    /// emits Ldnull and bails).</returns>
    private bool EmitCapturingArrowDisplayInstance(Expr.ArrowFunction af, TypeBuilder displayClass)
    {
        if (!_ctx.DisplayClassConstructors.TryGetValue(af, out var displayCtor))
            return false;

        IL.Emit(OpCodes.Newobj, displayCtor);
        EmitDisplayInstanceFieldPopulation(af, displayClass);
        return true;
    }

    /// <summary>
    /// Populates the display-class instance currently on top of the stack
    /// with all special-field DC chains and ordinary captured variables.
    /// Stack on entry/exit: <c>[displayInstance]</c>.
    /// </summary>
    private void EmitDisplayInstanceFieldPopulation(Expr.ArrowFunction af, TypeBuilder displayClass)
    {

        // Populate $entryPointDC field if this arrow captures top-level variables
        if (_ctx.ArrowEntryPointDCFields?.TryGetValue(af, out var entryPointDCField) == true)
        {
            if (_ctx.EntryPointDisplayClassLocal != null)
            {
                // In entry point method - use local variable
                IL.Emit(OpCodes.Dup); // Keep display class on stack
                IL.Emit(OpCodes.Ldloc, _ctx.EntryPointDisplayClassLocal);
                IL.Emit(OpCodes.Stfld, entryPointDCField);
            }
            else if (_ctx.CurrentArrowEntryPointDCField != null)
            {
                // In arrow body - get from parent arrow's $entryPointDC field
                IL.Emit(OpCodes.Dup); // Keep display class on stack
                IL.Emit(OpCodes.Ldarg_0); // Load parent display class
                IL.Emit(OpCodes.Ldfld, _ctx.CurrentArrowEntryPointDCField);
                IL.Emit(OpCodes.Stfld, entryPointDCField);
            }
            else if (_ctx.EntryPointDisplayClassStaticField != null)
            {
                // Fallback to static field
                IL.Emit(OpCodes.Dup); // Keep display class on stack
                IL.Emit(OpCodes.Ldsfld, _ctx.EntryPointDisplayClassStaticField);
                IL.Emit(OpCodes.Stfld, entryPointDCField);
            }
        }

        // Populate $functionDC field if this arrow captures function-level variables
        if (_ctx.ArrowFunctionDCFields?.TryGetValue(af, out var functionDCField) == true)
        {
            if (_ctx.FunctionDisplayClassLocal != null)
            {
                // In function body - use local variable
                IL.Emit(OpCodes.Dup); // Keep display class on stack
                IL.Emit(OpCodes.Ldloc, _ctx.FunctionDisplayClassLocal);
                IL.Emit(OpCodes.Stfld, functionDCField);
            }
            else if (_ctx.CurrentArrowFunctionDCField != null)
            {
                // In arrow body - get from parent arrow's $functionDC field
                IL.Emit(OpCodes.Dup); // Keep display class on stack
                IL.Emit(OpCodes.Ldarg_0); // Load parent display class
                IL.Emit(OpCodes.Ldfld, _ctx.CurrentArrowFunctionDCField);
                IL.Emit(OpCodes.Stfld, functionDCField);
            }
        }

        // Populate $arrowDC field if this arrow captures arrow-scope variables
        if (_ctx.ArrowScopeDCFields?.TryGetValue(af, out var arrowScopeDCField) == true)
        {
            if (_ctx.ArrowScopeDisplayClassLocal != null)
            {
                // In parent arrow body - use local variable
                IL.Emit(OpCodes.Dup);
                IL.Emit(OpCodes.Ldloc, _ctx.ArrowScopeDisplayClassLocal);
                IL.Emit(OpCodes.Stfld, arrowScopeDCField);
            }
            else if (_ctx.CurrentArrowScopeDCField != null)
            {
                // In nested arrow body - chain through parent's $arrowDC field
                IL.Emit(OpCodes.Dup);
                IL.Emit(OpCodes.Ldarg_0);
                IL.Emit(OpCodes.Ldfld, _ctx.CurrentArrowScopeDCField);
                IL.Emit(OpCodes.Stfld, arrowScopeDCField);
            }
        }

        // No captured-variable field map: special DC fields above are the
        // only state on the instance. Caller's wrapping step still needs to
        // run; just return with [instance] on the stack.
        if (!_ctx.DisplayClassFields.TryGetValue(af, out var fieldMap))
            return;

        // Self-reference field (named function expression) is left empty
        // here — it must hold the wrapping callable, not the bare display
        // instance. The caller (EmitCapturingArrowFunction) populates it
        // after building the $TSFunction.
        string? selfRefSkipName = af.Name?.Lexeme;

        foreach (var (capturedVar, field) in fieldMap)
        {
            if (selfRefSkipName != null && capturedVar == selfRefSkipName) continue;

            IL.Emit(OpCodes.Dup); // Keep display class on stack

            // Load the captured variable's current value
            if (capturedVar == "this")
            {
                // Arrow-spec semantics: `this` is lexically captured from the enclosing
                // non-arrow scope. When THIS arrow's own display class already has a
                // captured `this` field (i.e. we're inside a parent arrow's body and the
                // parent captured the class's `this`), propagate it from there. Don't use
                // bare Ldarg_0 in that case — inside an arrow-body the arg0 slot is the
                // display class instance itself, not the enclosing class's `this`, and
                // passing the DC into a nested arrow caused InvalidCastException /
                // NullReferenceException at use sites (minimatch's
                // `.map(s => s.map(ss => this.parse(ss)))` hit this).
                if (_ctx.CapturedFields != null && _ctx.CapturedFields.TryGetValue("this", out var outerThisField))
                {
                    IL.Emit(OpCodes.Ldarg_0);
                    IL.Emit(OpCodes.Ldfld, outerThisField);
                }
                else if (_ctx.IsInstanceMethod)
                {
                    // Direct class-method body: Ldarg_0 IS the enclosing instance.
                    IL.Emit(OpCodes.Ldarg_0);
                }
                else
                {
                    IL.Emit(OpCodes.Ldnull);
                }
            }
            else if (_ctx.TryGetParameter(capturedVar, out var argIndex))
            {
                IL.Emit(OpCodes.Ldarg, argIndex);
                // If parameter is typed (value type), box it for object field storage
                if (_ctx.TryGetParameterType(capturedVar, out var paramType) && paramType != null && paramType.IsValueType)
                {
                    IL.Emit(OpCodes.Box, paramType);
                }
            }
            else if (_ctx.CapturedFields != null && _ctx.CapturedFields.TryGetValue(capturedVar, out var capturedField))
            {
                // Variable is captured from outer closure
                IL.Emit(OpCodes.Ldarg_0); // this (display class)
                IL.Emit(OpCodes.Ldfld, capturedField);
            }
            else if (_ctx.CapturedTopLevelVars?.Contains(capturedVar) == true &&
                     _ctx.EntryPointDisplayClassFields?.TryGetValue(capturedVar, out var entryPointField) == true)
            {
                // Variable is a captured top-level var in entry-point display class
                if (_ctx.EntryPointDisplayClassLocal != null)
                {
                    IL.Emit(OpCodes.Ldloc, _ctx.EntryPointDisplayClassLocal);
                }
                else if (_ctx.EntryPointDisplayClassStaticField != null)
                {
                    IL.Emit(OpCodes.Ldsfld, _ctx.EntryPointDisplayClassStaticField);
                }
                else
                {
                    IL.Emit(OpCodes.Ldnull);
                }
                IL.Emit(OpCodes.Ldfld, entryPointField);
            }
            else if (_ctx.TopLevelStaticVars != null && _ctx.TopLevelStaticVars.TryGetValue(capturedVar, out var topLevelField))
            {
                // Variable is a top-level static var
                IL.Emit(OpCodes.Ldsfld, topLevelField);
            }
            else if (_ctx.Functions.TryGetValue(_ctx.ResolveFunctionName(capturedVar), out var capturedFuncMethod))
            {
                // Variable references an outer function declaration — wrap as $TSFunction
                // so the captured value matches what `callbackfn` resolves to in the
                // enclosing scope. Without this, capturing falls through to Ldnull and
                // the inner body sees `null` (typeof === "object") instead of a function.
                IL.Emit(OpCodes.Ldnull);
                IL.Emit(OpCodes.Ldtoken, capturedFuncMethod);
                if (_ctx.ProgramType != null)
                {
                    IL.Emit(OpCodes.Ldtoken, _ctx.ProgramType);
                    IL.Emit(OpCodes.Call, _ctx.Types.GetMethod(_ctx.Types.MethodBase, "GetMethodFromHandle", _ctx.Types.RuntimeMethodHandle, _ctx.Types.RuntimeTypeHandle));
                }
                else
                {
                    IL.Emit(OpCodes.Call, _ctx.Types.GetMethod(_ctx.Types.MethodBase, "GetMethodFromHandle", _ctx.Types.RuntimeMethodHandle));
                }
                IL.Emit(OpCodes.Castclass, _ctx.Types.MethodInfo);
                IL.Emit(OpCodes.Newobj, _ctx.Runtime!.TSFunctionCtor);
            }
            else
            {
                var local = _ctx.Locals.GetLocal(capturedVar);
                if (local != null)
                {
                    IL.Emit(OpCodes.Ldloc, local);
                }
                else
                {
                    IL.Emit(OpCodes.Ldnull); // Variable not found
                }
            }

            IL.Emit(OpCodes.Stfld, field);
        }
    }

    /// <summary>
    /// Slow-path emission for a capturing arrow: build display instance,
    /// then wrap as <c>$TSFunction</c> for the legacy callable dispatch.
    /// Iterator-helper fast paths bypass this and go through
    /// <see cref="EmitCapturingArrowDisplayInstance"/> directly to build a
    /// typed delegate instead.
    /// </summary>
    private void EmitCapturingArrowFunction(Expr.ArrowFunction af, MethodBuilder method, TypeBuilder displayClass)
    {
        if (!EmitCapturingArrowDisplayInstance(af, displayClass))
        {
            IL.Emit(OpCodes.Ldnull);
            return;
        }

        // For named function expressions, the self-reference field needs the
        // wrapping $TSFunction (not the bare display instance), so stash a
        // ref to the instance for use after the wrap.
        string? selfRefName = af.Name?.Lexeme;
        FieldBuilder? selfRefField = null;
        LocalBuilder? displayClassLocal = null;
        if (selfRefName != null
            && _ctx.DisplayClassFields.TryGetValue(af, out var fieldMap)
            && fieldMap.TryGetValue(selfRefName, out var srf))
        {
            selfRefField = srf;
            displayClassLocal = IL.DeclareLocal(displayClass);
            IL.Emit(OpCodes.Dup);
            IL.Emit(OpCodes.Stloc, displayClassLocal);
        }

        // Wrap: new $TSFunction(displayInstance, method)
        IL.Emit(OpCodes.Ldtoken, method);
        IL.Emit(OpCodes.Ldtoken, displayClass);
        IL.Emit(OpCodes.Call, _ctx.Types.GetMethod(_ctx.Types.MethodBase, "GetMethodFromHandle", _ctx.Types.RuntimeMethodHandle, _ctx.Types.RuntimeTypeHandle));
        IL.Emit(OpCodes.Castclass, _ctx.Types.MethodInfo);
        IL.Emit(OpCodes.Newobj, _ctx.Runtime!.TSFunctionCtor);

        if (selfRefField != null && displayClassLocal != null)
        {
            var tsFuncLocal = IL.DeclareLocal(_ctx.Runtime!.TSFunctionType);
            IL.Emit(OpCodes.Stloc, tsFuncLocal);
            IL.Emit(OpCodes.Ldloc, displayClassLocal);
            IL.Emit(OpCodes.Ldloc, tsFuncLocal);
            IL.Emit(OpCodes.Stfld, selfRefField);
            IL.Emit(OpCodes.Ldloc, tsFuncLocal);
        }
    }
}
