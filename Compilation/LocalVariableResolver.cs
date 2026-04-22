using System.Reflection;
using System.Reflection.Emit;

namespace SharpTS.Compilation;

/// <summary>
/// Variable resolver for standard IL emission contexts (non-state-machine).
/// Handles locals, parameters, and captured variables from display classes.
/// </summary>
/// <remarks>
/// Resolution order:
/// 1. Parameters (via CompilationContext.TryGetParameter)
/// 2. Local variables (via CompilationContext.Locals)
/// 3. Captured fields (via CompilationContext.CapturedFields)
///
/// Does NOT handle: functions, namespaces, classes, Math (caller handles these as fallback).
/// </remarks>
public class LocalVariableResolver : IVariableResolver
{
    private readonly ILGenerator _il;
    private readonly CompilationContext _ctx;
    private readonly TypeProvider _types;

    /// <summary>
    /// Creates a new resolver for standard IL emission variable access.
    /// </summary>
    /// <param name="il">The IL generator for emitting instructions</param>
    /// <param name="ctx">The compilation context with locals, parameters, and captured fields</param>
    /// <param name="types">The type provider for type checking</param>
    public LocalVariableResolver(ILGenerator il, CompilationContext ctx, TypeProvider types)
    {
        _il = il;
        _ctx = ctx;
        _types = types;
    }

    /// <inheritdoc />
    public StackType? TryLoadVariable(string name)
    {
        // 1. Parameters
        if (_ctx.TryGetParameter(name, out var argIndex))
        {
            _il.Emit(OpCodes.Ldarg, argIndex);
            // Check if we have type information for this parameter
            if (_ctx.TryGetParameterType(name, out var paramType) && paramType != null)
            {
                var stackType = MapTypeToStackType(paramType);
                // Box union types immediately so that StackType.Unknown correctly means "boxed object"
                if (stackType == StackType.Unknown && UnionTypeHelper.IsUnionType(paramType))
                {
                    _il.Emit(OpCodes.Box, paramType);
                }
                return stackType;
            }
            return StackType.Unknown; // Fallback for untyped parameters
        }

        // 2. Function display class fields (captured function-local vars)
        // Check this BEFORE regular locals to ensure we use the shared storage
        if (_ctx.CapturedFunctionLocals?.Contains(name) == true &&
            _ctx.FunctionDisplayClassFields?.TryGetValue(name, out var funcDCField) == true)
        {
            if (_ctx.FunctionDisplayClassLocal != null)
            {
                // Direct access from function body - use the local
                _il.Emit(OpCodes.Ldloc, _ctx.FunctionDisplayClassLocal);
                _il.Emit(OpCodes.Ldfld, funcDCField);
                return StackType.Unknown;
            }

            if (_ctx.CurrentArrowFunctionDCField != null)
            {
                // Access from arrow body - go through $functionDC field.
                // For arrows inside async arrows, $functionDC might be null at runtime
                // (not populated by the intermediary). Emit a null check and fall through
                // to CapturedFields if null.
                if (_ctx.CapturedFields?.ContainsKey(name) == true)
                {
                    // Emit: if (this.$functionDC != null) use DC, else use own field
                    var fallbackLabel = _il.DefineLabel();
                    var doneLabel = _il.DefineLabel();
                    _il.Emit(OpCodes.Ldarg_0);
                    _il.Emit(OpCodes.Ldfld, _ctx.CurrentArrowFunctionDCField);
                    _il.Emit(OpCodes.Dup);
                    _il.Emit(OpCodes.Brfalse, fallbackLabel);
                    // DC is non-null — load from it
                    _il.Emit(OpCodes.Ldfld, funcDCField);
                    _il.Emit(OpCodes.Br, doneLabel);
                    // Fallback — load from own captured field
                    _il.MarkLabel(fallbackLabel);
                    _il.Emit(OpCodes.Pop); // discard null DC
                    _il.Emit(OpCodes.Ldarg_0);
                    _il.Emit(OpCodes.Ldfld, _ctx.CapturedFields[name]);
                    _il.MarkLabel(doneLabel);
                }
                else
                {
                    // No fallback field — assume DC is populated
                    _il.Emit(OpCodes.Ldarg_0);
                    _il.Emit(OpCodes.Ldfld, _ctx.CurrentArrowFunctionDCField);
                    _il.Emit(OpCodes.Ldfld, funcDCField);
                }
                return StackType.Unknown;
            }

            // No DC access available — fall through to other checks
        }

        // 2b. Arrow scope display class fields — CURRENT arrow's own DC (direct
        // access via the arrow's DC local).
        if (_ctx.CapturedArrowLocals?.Contains(name) == true &&
            _ctx.ArrowScopeDisplayClassFields?.TryGetValue(name, out var arrowDCField) == true &&
            _ctx.ArrowScopeDisplayClassLocal != null)
        {
            _il.Emit(OpCodes.Ldloc, _ctx.ArrowScopeDisplayClassLocal);
            _il.Emit(OpCodes.Ldfld, arrowDCField);
            return StackType.Unknown;
        }

        // 2c. Arrow scope display class fields — PARENT arrow's DC, reachable
        // through the current arrow's `$arrowDC` field. Kept separate from the
        // own-DC slots above so an arrow that *both* owns a DC (for its own
        // locals captured by inner arrows) AND captures values from a parent
        // arrow's DC can dispatch each variable to the correct path.
        if (_ctx.ParentArrowCapturedLocals?.Contains(name) == true &&
            _ctx.ParentArrowScopeDisplayClassFields?.TryGetValue(name, out var parentArrowDCField) == true &&
            _ctx.CurrentArrowScopeDCField != null)
        {
            _il.Emit(OpCodes.Ldarg_0);                              // this
            _il.Emit(OpCodes.Ldfld, _ctx.CurrentArrowScopeDCField); // this.$arrowDC (parent's DC)
            _il.Emit(OpCodes.Ldfld, parentArrowDCField);            // parent.<name>
            return StackType.Unknown;
        }

        // (Removed: the old CapturedArrowLocals + $arrowDC chain path. With
        // ParentArrowCapturedLocals now the dedicated parent slot, the old
        // path was emitting broken Ldarg_0 sequences in non-display-class
        // method contexts (object-literal methods) where Ldarg_0 is `__this`
        // rather than a display class instance.)

        // 3. Locals (with type awareness)
        var local = _ctx.Locals.GetLocal(name);
        if (local != null)
        {
            var localType = _ctx.Locals.GetLocalType(name);
            _il.Emit(OpCodes.Ldloc, local);
            return MapTypeToStackType(localType);
        }

        // 4. Captured fields (closure)
        if (_ctx.CapturedFields?.TryGetValue(name, out var field) == true)
        {
            _il.Emit(OpCodes.Ldarg_0);
            _il.Emit(OpCodes.Ldfld, field);
            return MapTypeToStackType(field.FieldType);
        }

        // 5. Entry-point display class fields (captured top-level vars)
        if (_ctx.CapturedTopLevelVars?.Contains(name) == true &&
            _ctx.EntryPointDisplayClassFields?.TryGetValue(name, out var entryPointField) == true)
        {
            if (_ctx.EntryPointDisplayClassLocal != null)
            {
                // Direct access from entry point - use the local
                _il.Emit(OpCodes.Ldloc, _ctx.EntryPointDisplayClassLocal);
                _il.Emit(OpCodes.Ldfld, entryPointField);
            }
            else if (_ctx.CurrentArrowEntryPointDCField != null)
            {
                // Access from arrow body - go through $entryPointDC field
                _il.Emit(OpCodes.Ldarg_0); // Load display class instance
                _il.Emit(OpCodes.Ldfld, _ctx.CurrentArrowEntryPointDCField); // Load entry-point display class
                _il.Emit(OpCodes.Ldfld, entryPointField); // Load the variable field
            }
            else if (_ctx.EntryPointDisplayClassStaticField != null)
            {
                // Access from module init method - use static field
                _il.Emit(OpCodes.Ldsfld, _ctx.EntryPointDisplayClassStaticField);
                _il.Emit(OpCodes.Ldfld, entryPointField);
            }
            else
            {
                // Fallback - shouldn't happen
                return null;
            }
            return StackType.Unknown;
        }

        // 6. Top-level static vars (non-captured)
        if (_ctx.TopLevelStaticVars?.TryGetValue(name, out var topLevelField) == true)
        {
            _il.Emit(OpCodes.Ldsfld, topLevelField);
            return StackType.Unknown;
        }

        return null; // Caller handles fallback (Math, classes, functions, namespaces)
    }

    /// <inheritdoc />
    public bool HasVariable(string name)
    {
        if (_ctx.TryGetParameter(name, out _)) return true;
        if (_ctx.CapturedFunctionLocals?.Contains(name) == true &&
            _ctx.FunctionDisplayClassFields?.ContainsKey(name) == true) return true;
        if (_ctx.CapturedArrowLocals?.Contains(name) == true &&
            _ctx.ArrowScopeDisplayClassFields?.ContainsKey(name) == true) return true;
        if (_ctx.ParentArrowCapturedLocals?.Contains(name) == true &&
            _ctx.ParentArrowScopeDisplayClassFields?.ContainsKey(name) == true) return true;
        if (_ctx.Locals.HasLocal(name)) return true;
        if (_ctx.CapturedFields?.ContainsKey(name) == true) return true;
        if (_ctx.CapturedTopLevelVars?.Contains(name) == true &&
            _ctx.EntryPointDisplayClassFields?.ContainsKey(name) == true) return true;
        if (_ctx.TopLevelStaticVars?.ContainsKey(name) == true) return true;
        return false;
    }

    /// <inheritdoc />
    public bool TryStoreVariable(string name)
    {
        // 1. Function display class fields (captured function-local vars)
        // Check this BEFORE regular locals to ensure we use the shared storage
        if (_ctx.CapturedFunctionLocals?.Contains(name) == true &&
            _ctx.FunctionDisplayClassFields?.TryGetValue(name, out var funcDCField) == true)
        {
            var temp = _il.DeclareLocal(_types.Object);
            _il.Emit(OpCodes.Stloc, temp);

            if (_ctx.FunctionDisplayClassLocal != null)
            {
                // Direct access from function body - use the local
                _il.Emit(OpCodes.Ldloc, _ctx.FunctionDisplayClassLocal);
                _il.Emit(OpCodes.Ldloc, temp);
                _il.Emit(OpCodes.Stfld, funcDCField);
                return true;
            }

            if (_ctx.CurrentArrowFunctionDCField != null)
            {
                if (_ctx.CapturedFields?.ContainsKey(name) == true)
                {
                    // Emit: if (this.$functionDC != null) store to DC, else store to own field
                    var fallbackLabel = _il.DefineLabel();
                    var doneLabel = _il.DefineLabel();
                    _il.Emit(OpCodes.Ldarg_0);
                    _il.Emit(OpCodes.Ldfld, _ctx.CurrentArrowFunctionDCField);
                    _il.Emit(OpCodes.Dup);
                    _il.Emit(OpCodes.Brfalse, fallbackLabel);
                    // DC is non-null — store to it
                    _il.Emit(OpCodes.Ldloc, temp);
                    _il.Emit(OpCodes.Stfld, funcDCField);
                    _il.Emit(OpCodes.Br, doneLabel);
                    // Fallback — store to own captured field
                    _il.MarkLabel(fallbackLabel);
                    _il.Emit(OpCodes.Pop); // discard null DC
                    _il.Emit(OpCodes.Ldarg_0);
                    _il.Emit(OpCodes.Ldloc, temp);
                    _il.Emit(OpCodes.Stfld, _ctx.CapturedFields[name]);
                    _il.MarkLabel(doneLabel);
                }
                else
                {
                    // No fallback field — assume DC is populated
                    _il.Emit(OpCodes.Ldarg_0);
                    _il.Emit(OpCodes.Ldfld, _ctx.CurrentArrowFunctionDCField);
                    _il.Emit(OpCodes.Ldloc, temp);
                    _il.Emit(OpCodes.Stfld, funcDCField);
                }
                return true;
            }

            // No DC access available — restore value to stack and fall through
            _il.Emit(OpCodes.Ldloc, temp);
        }

        // 1b. Arrow scope display class fields (captured arrow-local vars) —
        // CURRENT arrow's own DC (direct access via the arrow's DC local).
        if (_ctx.CapturedArrowLocals?.Contains(name) == true &&
            _ctx.ArrowScopeDisplayClassFields?.TryGetValue(name, out var arrowDCFieldStore) == true &&
            _ctx.ArrowScopeDisplayClassLocal != null)
        {
            var tempArrow = _il.DeclareLocal(_types.Object);
            _il.Emit(OpCodes.Stloc, tempArrow);
            _il.Emit(OpCodes.Ldloc, _ctx.ArrowScopeDisplayClassLocal);
            _il.Emit(OpCodes.Ldloc, tempArrow);
            _il.Emit(OpCodes.Stfld, arrowDCFieldStore);
            return true;
        }

        // 1c. Arrow scope display class fields — PARENT arrow's DC, reachable
        // through `$arrowDC`. Mirror of read path 2c. Also covers the old
        // "no own DC, chain through parent" case where the field was tracked
        // in ArrowScopeDisplayClassFields before the parent/own slot split.
        {
            FieldBuilder? storeField = null;
            if (_ctx.ParentArrowCapturedLocals?.Contains(name) == true &&
                _ctx.ParentArrowScopeDisplayClassFields?.TryGetValue(name, out var parentField) == true)
            {
                storeField = parentField;
            }
            else if (_ctx.CapturedArrowLocals?.Contains(name) == true &&
                     _ctx.ArrowScopeDisplayClassFields?.TryGetValue(name, out var ownChainField) == true &&
                     _ctx.ArrowScopeDisplayClassLocal == null)
            {
                storeField = ownChainField;
            }
            if (storeField != null && _ctx.CurrentArrowScopeDCField != null)
            {
                var tempArrow = _il.DeclareLocal(_types.Object);
                _il.Emit(OpCodes.Stloc, tempArrow);
                _il.Emit(OpCodes.Ldarg_0);
                _il.Emit(OpCodes.Ldfld, _ctx.CurrentArrowScopeDCField);
                _il.Emit(OpCodes.Ldloc, tempArrow);
                _il.Emit(OpCodes.Stfld, storeField);
                return true;
            }
        }

        // 2. Locals
        if (_ctx.Locals.TryGetLocal(name, out var local))
        {
            _il.Emit(OpCodes.Stloc, local);
            return true;
        }

        // 3. Parameters
        if (_ctx.TryGetParameter(name, out var argIndex))
        {
            _il.Emit(OpCodes.Starg, argIndex);
            return true;
        }

        // 4. Captured fields (auto-detect value/reference type)
        if (_ctx.CapturedFields?.TryGetValue(name, out var field) == true)
        {
            // Use temp local pattern for storing to fields
            // This works for both value and reference type display classes
            var temp = _il.DeclareLocal(_types.Object);
            _il.Emit(OpCodes.Stloc, temp);
            _il.Emit(OpCodes.Ldarg_0);
            _il.Emit(OpCodes.Ldloc, temp);
            _il.Emit(OpCodes.Stfld, field);
            return true;
        }

        // 5. Entry-point display class fields (captured top-level vars)
        if (_ctx.CapturedTopLevelVars?.Contains(name) == true &&
            _ctx.EntryPointDisplayClassFields?.TryGetValue(name, out var entryPointField) == true)
        {
            // Use temp local pattern for storing to fields
            var temp = _il.DeclareLocal(_types.Object);
            _il.Emit(OpCodes.Stloc, temp);

            if (_ctx.EntryPointDisplayClassLocal != null)
            {
                // Direct access from entry point - use the local
                _il.Emit(OpCodes.Ldloc, _ctx.EntryPointDisplayClassLocal);
            }
            else if (_ctx.CurrentArrowEntryPointDCField != null)
            {
                // Access from arrow body - go through $entryPointDC field
                _il.Emit(OpCodes.Ldarg_0); // Load display class instance
                _il.Emit(OpCodes.Ldfld, _ctx.CurrentArrowEntryPointDCField); // Load entry-point display class
            }
            else if (_ctx.EntryPointDisplayClassStaticField != null)
            {
                // Access from module init method - use static field
                _il.Emit(OpCodes.Ldsfld, _ctx.EntryPointDisplayClassStaticField);
            }
            else
            {
                // Fallback - shouldn't happen
                return false;
            }

            _il.Emit(OpCodes.Ldloc, temp);
            _il.Emit(OpCodes.Stfld, entryPointField);
            return true;
        }

        // 6. Top-level static vars (non-captured)
        if (_ctx.TopLevelStaticVars?.TryGetValue(name, out var topLevelField) == true)
        {
            _il.Emit(OpCodes.Stsfld, topLevelField);
            return true;
        }

        return false;
    }

    /// <inheritdoc />
    public void LoadThis()
    {
        // 1. Captured this (closure)
        if (_ctx.CapturedFields?.TryGetValue("this", out var thisField) == true)
        {
            _il.Emit(OpCodes.Ldarg_0);
            _il.Emit(OpCodes.Ldfld, thisField);
            return;
        }

        // 2. __this parameter (object method shorthand)
        if (_ctx.TryGetParameter("__this", out var thisArgIndex))
        {
            _il.Emit(OpCodes.Ldarg, thisArgIndex);
            return;
        }

        // 3. Instance method — but only when arg0 IS the JS `this`. For inner function
        //    declarations emitted onto a display class, arg0 is the display-class self,
        //    not the user's `this`; fall through to the thread-local path in that case.
        if (_ctx.IsInstanceMethod && !_ctx.IsInnerFunctionOnDisplayClass)
        {
            _il.Emit(OpCodes.Ldarg_0);
            return;
        }

        // 4. Static constructor context - 'this' is the class type
        if (_ctx.IsStaticConstructorContext && _ctx.CurrentClassBuilder != null)
        {
            // Load the Type object for the current class
            _il.Emit(OpCodes.Ldtoken, _ctx.CurrentClassBuilder);
            _il.Emit(OpCodes.Call, _types.GetMethod(_types.Type, "GetTypeFromHandle", _types.RuntimeTypeHandle));
            return;
        }

        // 5. Thread-local `this` set by $Runtime.NewOnFunction (or other call paths
        //    that prep a thisArg for a method whose signature has no __this param).
        //    Falls back to null when no such this is active — matches the JS sloppy-
        //    mode default (undefined-ish) for bare function calls.
        if (_ctx.Runtime?.CurrentFunctionThisField != null)
        {
            _il.Emit(OpCodes.Ldsfld, _ctx.Runtime.CurrentFunctionThisField);
            return;
        }

        // 6. Static context without an emitted runtime (reference-assembly mode etc.)
        _il.Emit(OpCodes.Ldnull);
    }

    private StackType MapTypeToStackType(Type? type)
    {
        if (type == null) return StackType.Unknown;
        if (_types.IsDouble(type)) return StackType.Double;
        if (_types.IsBoolean(type)) return StackType.Boolean;
        if (_types.IsString(type)) return StackType.String;
        return StackType.Unknown;
    }
}
