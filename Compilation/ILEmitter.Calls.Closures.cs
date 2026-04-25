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

    private void EmitCapturingArrowFunction(Expr.ArrowFunction af, MethodBuilder method, TypeBuilder displayClass)
    {
        // Get the pre-tracked constructor (we can't call GetConstructors() on TypeBuilder before CreateType)
        if (!_ctx.DisplayClassConstructors.TryGetValue(af, out var displayCtor))
        {
            // Fallback
            IL.Emit(OpCodes.Ldnull);
            return;
        }

        IL.Emit(OpCodes.Newobj, displayCtor);

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

        // Get captured variables for this arrow using the stored field mapping
        if (!_ctx.DisplayClassFields.TryGetValue(af, out var fieldMap))
        {
            // No field map found - this arrow might only have $functionDC or $entryPointDC fields
            // These were already populated above, so just create the TSFunction
            // Use two-argument GetMethodFromHandle for display class methods
            IL.Emit(OpCodes.Ldtoken, method);
            IL.Emit(OpCodes.Ldtoken, displayClass);
            IL.Emit(OpCodes.Call, _ctx.Types.GetMethod(_ctx.Types.MethodBase, "GetMethodFromHandle", _ctx.Types.RuntimeMethodHandle, _ctx.Types.RuntimeTypeHandle));
            IL.Emit(OpCodes.Castclass, _ctx.Types.MethodInfo);
            IL.Emit(OpCodes.Newobj, _ctx.Runtime!.TSFunctionCtor);
            return;
        }

        // If fieldMap is empty but we have $functionDC or other special fields, that's fine
        // The special fields were populated above

        // Determine if this is a named function expression with self-reference
        string? selfRefName = af.Name?.Lexeme;
        FieldBuilder? selfRefField = null;
        if (selfRefName != null && fieldMap.TryGetValue(selfRefName, out var srf))
        {
            selfRefField = srf;
        }

        // If we have a self-reference, save the display class instance for later use
        LocalBuilder? displayClassLocal = null;
        if (selfRefField != null)
        {
            displayClassLocal = IL.DeclareLocal(displayClass);
            IL.Emit(OpCodes.Dup);
            IL.Emit(OpCodes.Stloc, displayClassLocal);
        }

        // Populate captured fields (except self-reference which needs to be set after TSFunction creation)
        foreach (var (capturedVar, field) in fieldMap)
        {
            // Skip self-reference field - will be populated after TSFunction is created
            if (selfRefField != null && capturedVar == selfRefName) continue;

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

        // Create TSFunction: new TSFunction(displayInstance, method)
        // Stack has: displayInstance

        // Load method info - use two-argument GetMethodFromHandle for display class methods
        // This is required because the method's parameter types need the declaring type context to resolve
        IL.Emit(OpCodes.Ldtoken, method);
        IL.Emit(OpCodes.Ldtoken, displayClass);
        IL.Emit(OpCodes.Call, _ctx.Types.GetMethod(_ctx.Types.MethodBase, "GetMethodFromHandle", _ctx.Types.RuntimeMethodHandle, _ctx.Types.RuntimeTypeHandle));
        IL.Emit(OpCodes.Castclass, _ctx.Types.MethodInfo);

        // Call $TSFunction constructor
        IL.Emit(OpCodes.Newobj, _ctx.Runtime!.TSFunctionCtor);

        // For named function expressions, populate the self-reference field with the TSFunction
        // Stack now has: TSFunction
        if (selfRefField != null && displayClassLocal != null)
        {
            // Save TSFunction to local
            var tsFuncLocal = IL.DeclareLocal(_ctx.Runtime!.TSFunctionType);
            IL.Emit(OpCodes.Stloc, tsFuncLocal);

            // Load display class, load TSFunction, store in self-reference field
            IL.Emit(OpCodes.Ldloc, displayClassLocal);
            IL.Emit(OpCodes.Ldloc, tsFuncLocal);
            IL.Emit(OpCodes.Stfld, selfRefField);

            // Leave TSFunction on stack for the return value
            IL.Emit(OpCodes.Ldloc, tsFuncLocal);
        }
    }
}
