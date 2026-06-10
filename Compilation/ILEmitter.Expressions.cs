using System.IO;
using System.Reflection.Emit;
using SharpTS.Parsing;
using SharpTS.TypeSystem;

namespace SharpTS.Compilation;

/// <summary>
/// Basic expression emission methods for the IL emitter.
/// </summary>
public partial class ILEmitter
{
    protected override void EmitLiteral(Expr.Literal lit)
    {
        switch (lit.Value)
        {
            case double d:
                EmitDoubleConstant(d);
                break;
            case string s:
                EmitStringConstant(s);
                break;
            case bool b:
                EmitBoolConstant(b);
                break;
            case System.Numerics.BigInteger bi:
                if (bi >= long.MinValue && bi <= long.MaxValue)
                {
                    // Optimization: Use BigInteger(long) constructor for small values
                    IL.Emit(OpCodes.Ldc_I8, (long)bi);
                    IL.Emit(OpCodes.Newobj, _ctx.Types.GetConstructor(_ctx.Types.BigInteger, _ctx.Types.Int64));
                }
                else
                {
                    // Fallback: Parse from string for large values
                    IL.Emit(OpCodes.Ldstr, bi.ToString());
                    IL.Emit(OpCodes.Call, _ctx.Types.GetMethod(_ctx.Types.BigInteger, "Parse", _ctx.Types.String));
                }
                IL.Emit(OpCodes.Box, _ctx.Types.BigInteger);
                SetStackUnknown();
                break;
            case Runtime.Types.SharpTSUndefined:
                EmitUndefinedConstant();
                break;
            case null:
                EmitNullConstant();
                break;
            default:
                EmitNullConstant();
                break;
        }
    }

    protected override void EmitVariable(Expr.Variable v)
    {
        var name = v.Name.Lexeme;

        // Try resolver first (user-defined variables: parameters, locals, captured)
        var stackType = _resolver.TryLoadVariable(name);
        if (stackType.HasValue)
        {
            SetStackType(stackType.Value);
            return;
        }

        // CommonJS: bare `exports` resolves to the current module's $exports static field.
        if (TryEmitCjsVariable(name)) return;

        // Fallback: pseudo-variables (Math, process, classes, functions, namespaces)
        if (name == "Math")
        {
            // Bare `Math` resolves to a shared Dictionary<string, object>
            // singleton so `Math.length = 1; Math[0] = 1` and iteration via
            // `Array.prototype.X.call(Math, cb)` work per ECMA-262 (Math is an
            // ordinary extensible object). `Math.PI`/`Math.floor`/etc. still
            // route through MathStaticEmitter's compile-time interception
            // *before* this bare-reference path, so static-member dispatch is
            // unaffected.
            IL.Emit(OpCodes.Ldsfld, _ctx.Runtime!.MathSingletonField);
            SetStackUnknown();
            return;
        }

        if (name == "JSON")
        {
            // Stage 4z3: bare `JSON` resolves to a singleton Dictionary so
            // `typeof JSON === "object"` per ECMA-262 24.5. Compile-time
            // static dispatch (JSON.parse / JSON.stringify) intercepts before
            // this bare-reference path so behavior is preserved.
            IL.Emit(OpCodes.Ldsfld, _ctx.Runtime!.JsonSingletonField);
            SetStackUnknown();
            return;
        }

        // AbortSignal / Intl value-position namespace singletons (#224) —
        // shared with the state-machine emitters via the base helper.
        if (TryEmitNamespaceSingleton(name)) return;

        if (name == "process")
        {
            EmitNullConstant(); // process is handled specially in property access
            return;
        }

        if (name == "globalThis")
        {
            EmitNullConstant(); // globalThis is handled specially in property access
            return;
        }

        // `global` is a Node.js alias for globalThis. Mirror globalThis resolution so CJS
        // packages (lodash) that sniff for `typeof global === 'object'` to find their
        // ambient scope don't crash.
        if (name == "global")
        {
            EmitNullConstant();
            return;
        }

        if (name == "Symbol")
        {
            // Bare `Symbol` resolves to the $TSSymbol Type token (#234) — the
            // same value-form pattern as Array/Number/String. typeof → "function",
            // aliased member access hits GetProperty's Type branch (well-known
            // symbols are public static fields with their JS names; for/keyFor
            // route through LookupBuiltInStaticMember), and the call form
            // dispatches via InvokeValue's Type-callee branch. Direct
            // `Symbol.iterator` / `Symbol(...)` sites still compile through
            // SymbolStaticEmitter / BuiltInConstructorHandler first.
            IL.Emit(OpCodes.Ldtoken, _ctx.Runtime!.TSSymbolType);
            IL.Emit(OpCodes.Call, _ctx.Types.GetMethod(_ctx.Types.Type, "GetTypeFromHandle", _ctx.Types.RuntimeTypeHandle));
            SetStackUnknown();
            return;
        }

        // JavaScript global constants
        if (name == "NaN")
        {
            EmitDoubleConstant(double.NaN);
            return;
        }

        if (name == "Infinity")
        {
            EmitDoubleConstant(double.PositiveInfinity);
            return;
        }

        if (name == "undefined")
        {
            EmitUndefinedConstant();
            return;
        }

        // Global fetch function - use cached TSFunction for reference equality with globalThis.fetch
        if (name == "fetch")
        {
            IL.Emit(OpCodes.Ldstr, "fetch");
            IL.Emit(OpCodes.Call, _ctx.Runtime!.GlobalThisGetProperty);
            SetStackUnknown();
            return;
        }

        // Other global functions routable through globalThis (they have fast-path call handlers
        // but must ALSO be addressable as values — lodash caches them: `var freeParseFloat = parseFloat`
        // then calls the alias later). Matches how `fetch` resolves the bare reference above.
        if (name is "parseFloat" or "parseInt" or "isNaN" or "isFinite"
            or "encodeURIComponent" or "decodeURIComponent"
            or "setTimeout" or "clearTimeout" or "setInterval" or "clearInterval"
            or "queueMicrotask" or "structuredClone")
        {
            IL.Emit(OpCodes.Ldstr, name);
            IL.Emit(OpCodes.Call, _ctx.Runtime!.GlobalThisGetProperty);
            SetStackUnknown();
            return;
        }

        // Check for Node.js module globals (__dirname, __filename)
        if (name == "__filename")
        {
            IL.Emit(OpCodes.Ldstr, _ctx.CurrentModulePath ?? "");
            SetStackUnknown();
            return;
        }

        if (name == "__dirname")
        {
            string dirname = string.IsNullOrEmpty(_ctx.CurrentModulePath)
                ? ""
                : Path.GetDirectoryName(_ctx.CurrentModulePath) ?? "";
            IL.Emit(OpCodes.Ldstr, dirname);
            SetStackUnknown();
            return;
        }

        // Check if it's an imported value (from another module) - must check BEFORE Functions
        // because cross-module function references need to go through the import field
        if (_ctx.TopLevelStaticVars?.TryGetValue(name, out var topLevelField) == true)
        {
            IL.Emit(OpCodes.Ldsfld, topLevelField);
            SetStackUnknown();
            return;
        }

        // Check if it's a class - load the Type object
        if (_ctx.Classes.TryGetValue(_ctx.ResolveClassName(name), out var classType))
        {
            IL.Emit(OpCodes.Ldtoken, classType);
            IL.Emit(OpCodes.Call, _ctx.Types.GetMethod(_ctx.Types.Type, "GetTypeFromHandle", _ctx.Types.RuntimeTypeHandle));
            SetStackUnknown();
            return;
        }

        // Check if it's a top-level function - wrap as TSFunction.
        // Stage 6r: route through GetOrCreate (MethodInfo-keyed instance cache)
        // so multiple references to the same function decl produce the SAME
        // $TSFunction wrapper. Without this, `e.constructor === ErrorClass`
        // and `instanceof ErrorClass` checks across separately-loaded
        // references fail with reference inequality even though the
        // underlying MethodInfo is the same.
        if (_ctx.Functions.TryGetValue(_ctx.ResolveFunctionName(name), out var methodBuilder))
        {
            IL.Emit(OpCodes.Ldtoken, methodBuilder);
            // Use two-parameter GetMethodFromHandle with declaring type for proper token resolution in persisted assemblies
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

            // Compute function arity at compile time. name/length are used only
            // on first create (subsequent cache hits return the existing wrapper
            // whose name/length are already set).
            int arity = 0;
            foreach (var param in methodBuilder.GetParameters())
            {
                if (param.IsOptional) continue;
                if (param.ParameterType == typeof(List<object>)) continue;
                if (param.Name?.StartsWith("__") == true) continue;
                arity++;
            }
            IL.Emit(OpCodes.Ldstr, name);  // function name
            IL.Emit(OpCodes.Ldc_I4, arity);  // function length
            IL.Emit(OpCodes.Call, _ctx.Runtime!.TSFunctionGetOrCreate);
            SetStackUnknown();
            return;
        }

        // Check if it's an inner function - wrap as TSFunction for value reference
        if (_ctx.InnerFunctionMethodsByName?.TryGetValue(name, out var innerFuncMethod) == true)
        {
            TypeBuilder? innerDC = null;
            bool isCapturing = _ctx.InnerFunctionDisplayClassesByName?.TryGetValue(name, out innerDC) == true;
            if (isCapturing)
            {
                // Capturing: TSFunction(this, invokeMethod) where this is the display class instance
                IL.Emit(OpCodes.Ldarg_0); // Load display class instance
                IL.Emit(OpCodes.Ldtoken, innerFuncMethod);
                IL.Emit(OpCodes.Ldtoken, innerDC!);
                IL.Emit(OpCodes.Call, _ctx.Types.GetMethod(_ctx.Types.MethodBase, "GetMethodFromHandle", _ctx.Types.RuntimeMethodHandle, _ctx.Types.RuntimeTypeHandle));
            }
            else
            {
                // Non-capturing: TSFunction(null, staticMethod)
                IL.Emit(OpCodes.Ldnull);
                IL.Emit(OpCodes.Ldtoken, innerFuncMethod);
                IL.Emit(OpCodes.Call, _ctx.Types.GetMethod(_ctx.Types.MethodBase, "GetMethodFromHandle", _ctx.Types.RuntimeMethodHandle));
            }
            IL.Emit(OpCodes.Castclass, _ctx.Types.MethodInfo);
            EmitNewobjUnknown(_ctx.Runtime!.TSFunctionCtor);
            return;
        }

        // Check if it's a namespace - load the static field
        if (_ctx.NamespaceFields?.TryGetValue(name, out var nsField) == true)
        {
            IL.Emit(OpCodes.Ldsfld, nsField);
            SetStackUnknown();
            return;
        }

        // Check if it's a built-in Error constructor — push the emitted Type object
        if (TryEmitErrorTypeToken(name))
            return;

        // Built-in classes referenced as values (e.g. `instanceof Date`,
        // `x === Map`, passing Date as an arg). Emit the .NET Type object for
        // the runtime class so InstanceOf's IsAssignableFrom check matches
        // instances produced by `new Date()` / `new Map()` / etc.
        if (TryEmitBuiltInClassType(name))
            return;

        // Last resort for JS globals (Object, globalThis, etc.): fall through to
        // globalThis.<name>. Positioned AFTER TryEmitBuiltInClassType so existing
        // IsAssignableFrom-based instanceof checks keep their Type-token emissions.
        // Runs for `Object` (coercion/identity — lodash `root.Object === Object`),
        // `Function`, and anything else the resolver/classes/functions paths didn't claim.
        if (name is "Object" or "Function" or "Number" or "String" or "Boolean")
        {
            // Bare reference to a built-in constructor. Number/String/Boolean
            // are added here (issue #62) so that patterns like
            // `var isInt = Number.isInteger` and `typeof Number === "function"`
            // don't throw ReferenceError — matches how Object and Function
            // already resolve via globalThis.
            IL.Emit(OpCodes.Ldstr, name);
            IL.Emit(OpCodes.Call, _ctx.Runtime!.GlobalThisGetProperty);
            SetStackUnknown();
            return;
        }

        // Unknown variable - throw ReferenceError at runtime
        IL.Emit(OpCodes.Ldstr, name);
        IL.Emit(OpCodes.Call, _ctx.Runtime!.ThrowUndefinedVariable);
        // Emit unreachable null to satisfy IL verification (method never returns but stack must balance)
        EmitNullConstant();
    }

    // TryEmitBuiltInClassType and TryEmitErrorTypeToken are inherited from
    // ExpressionEmitterBase so state-machine emitters resolve built-in
    // constructor identifiers identically (#232).

    // IsKnownVariable is inherited from ExpressionEmitterBase

    protected override void EmitAssign(Expr.Assign a)
    {
        // CommonJS: `exports = X` → stsfld $exports (mirrors TryEmitCjsSet for module.exports).
        // Must run before EmitExpression(a.Value) so we can own the box+dup+stsfld sequence
        // cleanly — otherwise the generic "Unknown target" fallback at the bottom of this method
        // emits Box+Dup and leaves a dangling value on the stack, which propagates through the
        // rest of the module body and ultimately trips PathStackDepth at the final ret.
        if (TryEmitCjsAssign(a)) return;

        EmitExpression(a.Value);

        // 1. Function display class fields (captured function-local vars)
        // Check this BEFORE regular locals to ensure we use the shared storage
        if (_ctx.CapturedFunctionLocals?.Contains(a.Name.Lexeme) == true &&
            _ctx.FunctionDisplayClassFields?.TryGetValue(a.Name.Lexeme, out var funcDCField) == true)
        {
            EmitBoxIfNeeded(a.Value);
            IL.Emit(OpCodes.Dup);
            // Store to field: need temp since value is on top of stack
            var temp = IL.DeclareLocal(_ctx.Types.Object);
            IL.Emit(OpCodes.Stloc, temp);

            if (_ctx.FunctionDisplayClassLocal != null)
            {
                // Direct access from function body
                IL.Emit(OpCodes.Ldloc, _ctx.FunctionDisplayClassLocal);
            }
            else if (_ctx.CurrentArrowFunctionDCField != null)
            {
                // Access from arrow body - go through $functionDC field
                IL.Emit(OpCodes.Ldarg_0);
                IL.Emit(OpCodes.Ldfld, _ctx.CurrentArrowFunctionDCField);
            }
            else
            {
                // Fallback - just discard the temp and leave value on stack
                IL.Emit(OpCodes.Pop);
                SetStackUnknown();
                return;
            }

            IL.Emit(OpCodes.Ldloc, temp);
            IL.Emit(OpCodes.Stfld, funcDCField);
            SetStackUnknown();
            return;
        }

        // 1b. Arrow scope display class fields (captured arrow-local vars)
        if (_ctx.CapturedArrowLocals?.Contains(a.Name.Lexeme) == true &&
            _ctx.ArrowScopeDisplayClassFields?.TryGetValue(a.Name.Lexeme, out var arrowDCField) == true)
        {
            EmitBoxIfNeeded(a.Value);
            IL.Emit(OpCodes.Dup);
            var temp = IL.DeclareLocal(_ctx.Types.Object);
            IL.Emit(OpCodes.Stloc, temp);

            if (_ctx.ArrowScopeDisplayClassLocal != null)
            {
                // Direct access from arrow body
                IL.Emit(OpCodes.Ldloc, _ctx.ArrowScopeDisplayClassLocal);
            }
            else if (_ctx.CurrentArrowScopeDCField != null)
            {
                // Access from nested arrow body - go through $arrowDC field
                IL.Emit(OpCodes.Ldarg_0);
                IL.Emit(OpCodes.Ldfld, _ctx.CurrentArrowScopeDCField);
            }
            else
            {
                IL.Emit(OpCodes.Pop);
                SetStackUnknown();
                return;
            }

            IL.Emit(OpCodes.Ldloc, temp);
            IL.Emit(OpCodes.Stfld, arrowDCField);
            SetStackUnknown();
            return;
        }

        var local = _ctx.Locals.GetLocal(a.Name.Lexeme);
        if (local != null)
        {
            var localType = _ctx.Locals.GetLocalType(a.Name.Lexeme);
            if (localType != null && _ctx.Types.IsDouble(localType))
            {
                // Typed local - ensure unboxed double
                EnsureDouble();
                IL.Emit(OpCodes.Dup);
                IL.Emit(OpCodes.Stloc, local);
                SetStackType(StackType.Double);
            }
            else
            {
                // Object local - ensure boxed
                EmitBoxIfNeeded(a.Value);
                IL.Emit(OpCodes.Dup);
                IL.Emit(OpCodes.Stloc, local);
                SetStackUnknown();
            }
        }
        else if (_ctx.TryGetParameter(a.Name.Lexeme, out var argIndex))
        {
            // Parameters are always object type
            EmitBoxIfNeeded(a.Value);
            IL.Emit(OpCodes.Dup);
            IL.Emit(OpCodes.Starg, argIndex);
            SetStackUnknown();
        }
        else if (_ctx.CapturedFields?.TryGetValue(a.Name.Lexeme, out var field) == true)
        {
            // Captured field in display class (closure)
            EmitBoxIfNeeded(a.Value);
            IL.Emit(OpCodes.Dup);
            // Store to field: need temp since value is on top of stack
            var temp = IL.DeclareLocal(_ctx.Types.Object);
            IL.Emit(OpCodes.Stloc, temp);
            IL.Emit(OpCodes.Ldarg_0);  // Load display class instance
            IL.Emit(OpCodes.Ldloc, temp);
            IL.Emit(OpCodes.Stfld, field);
            SetStackUnknown();
        }
        else if (_ctx.CapturedTopLevelVars?.Contains(a.Name.Lexeme) == true &&
                 _ctx.EntryPointDisplayClassFields?.TryGetValue(a.Name.Lexeme, out var entryPointField) == true)
        {
            // Captured top-level variable in entry-point display class
            EmitBoxIfNeeded(a.Value);
            IL.Emit(OpCodes.Dup);
            // Store to field: need temp since value is on top of stack
            var temp = IL.DeclareLocal(_ctx.Types.Object);
            IL.Emit(OpCodes.Stloc, temp);

            if (_ctx.EntryPointDisplayClassLocal != null)
            {
                // Direct access from entry point
                IL.Emit(OpCodes.Ldloc, _ctx.EntryPointDisplayClassLocal);
            }
            else if (_ctx.CurrentArrowEntryPointDCField != null)
            {
                // Access from arrow body - go through $entryPointDC field
                IL.Emit(OpCodes.Ldarg_0);
                IL.Emit(OpCodes.Ldfld, _ctx.CurrentArrowEntryPointDCField);
            }
            else if (_ctx.EntryPointDisplayClassStaticField != null)
            {
                // Access from module init method - use static field
                IL.Emit(OpCodes.Ldsfld, _ctx.EntryPointDisplayClassStaticField);
            }
            else
            {
                // Fallback - just discard the temp and leave value on stack
                IL.Emit(OpCodes.Pop);
                SetStackUnknown();
                return;
            }

            IL.Emit(OpCodes.Ldloc, temp);
            IL.Emit(OpCodes.Stfld, entryPointField);
            SetStackUnknown();
        }
        else if (_ctx.TopLevelStaticVars?.TryGetValue(a.Name.Lexeme, out var topLevelField) == true)
        {
            // Top-level static variable
            EmitBoxIfNeeded(a.Value);
            IL.Emit(OpCodes.Dup);
            IL.Emit(OpCodes.Stsfld, topLevelField);
            SetStackUnknown();
        }
        else
        {
            // Unknown target - box for safety
            EmitBoxIfNeeded(a.Value);
            IL.Emit(OpCodes.Dup);
            SetStackUnknown();
        }
    }

    protected override void EmitThis()
    {
        _resolver.LoadThis();
        SetStackUnknown();
    }

    protected override void EmitSuper(Expr.Super s)
    {
        // Load this and prepare for base method call
        // Note: super() constructor calls are handled in EmitCall, not here
        IL.Emit(OpCodes.Ldarg_0);
        IL.Emit(OpCodes.Ldstr, s.Method?.Lexeme ?? "constructor");
        EmitCallUnknown(_ctx.Runtime!.GetSuperMethod);
    }

    protected override void EmitTernary(Expr.Ternary t)
    {
        var builder = _ctx.ILBuilder;
        var elseLabel = builder.DefineLabel("ternary_else");
        var endLabel = builder.DefineLabel("ternary_end");

        EmitExpression(t.Condition);
        // Handle condition based on what's actually on the stack
        if (_stackType == StackType.Boolean)
        {
            // Already have unboxed boolean - ready for branch
        }
        else if (_stackType == StackType.Unknown && IsComparisonExpr(t.Condition))
        {
            // Boxed boolean from comparison - unbox it
            IL.Emit(OpCodes.Unbox_Any, _ctx.Types.Boolean);
        }
        else
        {
            // For other expressions (including Expr.Logical which returns boxed object),
            // apply truthy check to convert to int for Brfalse
            EnsureBoxed();
            EmitTruthyCheck();
        }
        builder.Emit_Brfalse(elseLabel);

        EmitExpression(t.ThenBranch);
        EmitBoxIfNeeded(t.ThenBranch);
        builder.Emit_Br(endLabel);

        builder.MarkLabel(elseLabel);
        EmitExpression(t.ElseBranch);
        EmitBoxIfNeeded(t.ElseBranch);

        builder.MarkLabel(endLabel);
        // Both branches box, so result is Unknown (boxed object)
        SetStackUnknown();
    }

    protected override void EmitNullishCoalescing(Expr.NullishCoalescing nc)
    {
        var builder = _ctx.ILBuilder;
        var endLabel = builder.DefineLabel("nullish_end");
        var useRightLabel = builder.DefineLabel("nullish_use_right");

        EmitExpression(nc.Left);
        EmitBoxIfNeeded(nc.Left);
        IL.Emit(OpCodes.Dup);

        // If left is null, use right
        builder.Emit_Brfalse(useRightLabel);

        // If left is undefined, use right
        IL.Emit(OpCodes.Dup);
        IL.Emit(OpCodes.Isinst, _ctx.Runtime!.UndefinedType);
        builder.Emit_Brtrue(useRightLabel);

        // Left is neither null nor undefined - use it
        builder.Emit_Br(endLabel);

        builder.MarkLabel(useRightLabel);
        IL.Emit(OpCodes.Pop);
        EmitExpression(nc.Right);
        EmitBoxIfNeeded(nc.Right);

        builder.MarkLabel(endLabel);
        // Both branches box, so result is Unknown (boxed object)
        SetStackUnknown();
    }

    protected override void EmitTemplateLiteral(Expr.TemplateLiteral tl)
    {
        // Build array of parts
        var totalParts = tl.Strings.Count + tl.Expressions.Count;
        IL.Emit(OpCodes.Ldc_I4, totalParts);
        IL.Emit(OpCodes.Newarr, _ctx.Types.Object);

        int partIndex = 0;
        for (int i = 0; i < tl.Strings.Count; i++)
        {
            IL.Emit(OpCodes.Dup);
            IL.Emit(OpCodes.Ldc_I4, partIndex++);
            IL.Emit(OpCodes.Ldstr, tl.Strings[i]);
            IL.Emit(OpCodes.Stelem_Ref);

            if (i < tl.Expressions.Count)
            {
                IL.Emit(OpCodes.Dup);
                IL.Emit(OpCodes.Ldc_I4, partIndex++);
                EmitExpression(tl.Expressions[i]);
                EmitBoxIfNeeded(tl.Expressions[i]);
                IL.Emit(OpCodes.Stelem_Ref);
            }
        }

        EmitCallString(_ctx.Runtime!.ConcatTemplate);
    }

    protected override void EmitTaggedTemplateLiteral(Expr.TaggedTemplateLiteral ttl)
    {
        // Check for String.raw special case
        if (IsStringRawTag(ttl.Tag))
        {
            EmitStringRawTaggedTemplate(ttl);
            return;
        }

        // Detect property access tag (obj.method`...`) for this binding
        bool hasThisBinding = ttl.Tag is Expr.Get;
        LocalBuilder? receiverLocal = null;

        // 1. Emit the tag function reference (and receiver for property access tags)
        if (hasThisBinding)
        {
            var g = (Expr.Get)ttl.Tag;
            // Emit and save the receiver object
            EmitExpression(g.Object);
            EnsureBoxed();
            receiverLocal = _ctx.ILBuilder.DeclareLocal(_ctx.Types.Object);
            IL.Emit(OpCodes.Stloc, receiverLocal);
            // Get the method: GetProperty(obj, name) — handles all object types including dictionaries
            IL.Emit(OpCodes.Ldloc, receiverLocal);
            IL.Emit(OpCodes.Ldstr, g.Name.Lexeme);
            IL.Emit(OpCodes.Call, _ctx.Runtime!.GetProperty);
            // Push thisArg (receiver) for WithThis call
            IL.Emit(OpCodes.Ldloc, receiverLocal);
        }
        else
        {
            EmitExpression(ttl.Tag);
            EmitBoxIfNeeded(ttl.Tag);
        }

        // 2. Create cooked strings array (object?[] to allow null for invalid escapes)
        IL.Emit(OpCodes.Ldc_I4, ttl.CookedStrings.Count);
        IL.Emit(OpCodes.Newarr, _ctx.Types.Object);
        for (int i = 0; i < ttl.CookedStrings.Count; i++)
        {
            IL.Emit(OpCodes.Dup);
            IL.Emit(OpCodes.Ldc_I4, i);
            if (ttl.CookedStrings[i] != null)
            {
                IL.Emit(OpCodes.Ldstr, ttl.CookedStrings[i]!);
            }
            else
            {
                IL.Emit(OpCodes.Ldnull); // null for invalid escape sequences
            }
            IL.Emit(OpCodes.Stelem_Ref);
        }

        // 3. Create raw strings array
        IL.Emit(OpCodes.Ldc_I4, ttl.RawStrings.Count);
        IL.Emit(OpCodes.Newarr, _ctx.Types.String);
        for (int i = 0; i < ttl.RawStrings.Count; i++)
        {
            IL.Emit(OpCodes.Dup);
            IL.Emit(OpCodes.Ldc_I4, i);
            IL.Emit(OpCodes.Ldstr, ttl.RawStrings[i]);
            IL.Emit(OpCodes.Stelem_Ref);
        }

        // 4. Create expressions array
        IL.Emit(OpCodes.Ldc_I4, ttl.Expressions.Count);
        IL.Emit(OpCodes.Newarr, _ctx.Types.Object);
        for (int i = 0; i < ttl.Expressions.Count; i++)
        {
            IL.Emit(OpCodes.Dup);
            IL.Emit(OpCodes.Ldc_I4, i);
            EmitExpression(ttl.Expressions[i]);
            EmitBoxIfNeeded(ttl.Expressions[i]);
            IL.Emit(OpCodes.Stelem_Ref);
        }

        // 5. Call appropriate runtime helper
        if (hasThisBinding)
        {
            // Stack: tag, thisArg, cooked, raw, exprs
            IL.Emit(OpCodes.Call, _ctx.Runtime!.InvokeTaggedTemplateWithThis);
        }
        else
        {
            // Stack: tag, cooked, raw, exprs
            IL.Emit(OpCodes.Call, _ctx.Runtime!.InvokeTaggedTemplate);
        }
        SetStackUnknown();
    }

    /// <summary>
    /// Checks if the tag expression is String.raw.
    /// </summary>
    private static bool IsStringRawTag(Expr tag)
    {
        return tag is Expr.Get get
            && get.Name.Lexeme == "raw"
            && get.Object is Expr.Variable v
            && v.Name.Lexeme == "String";
    }

    /// <summary>
    /// Emits optimized code for String.raw tagged template literals.
    /// Calls the emitted $Runtime.StringRaw method directly.
    /// </summary>
    private void EmitStringRawTaggedTemplate(Expr.TaggedTemplateLiteral ttl)
    {
        // 1. Create raw strings array
        IL.Emit(OpCodes.Ldc_I4, ttl.RawStrings.Count);
        IL.Emit(OpCodes.Newarr, _ctx.Types.String);
        for (int i = 0; i < ttl.RawStrings.Count; i++)
        {
            IL.Emit(OpCodes.Dup);
            IL.Emit(OpCodes.Ldc_I4, i);
            IL.Emit(OpCodes.Ldstr, ttl.RawStrings[i]);
            IL.Emit(OpCodes.Stelem_Ref);
        }

        // 2. Build expressions list. StringRaw's second param is List<object>
        // (rest-param shape), so we need a List rather than a raw object[].
        var listLocal = IL.DeclareLocal(_ctx.Types.ListOfObject);
        IL.Emit(OpCodes.Newobj, _ctx.Types.GetDefaultConstructor(_ctx.Types.ListOfObject));
        IL.Emit(OpCodes.Stloc, listLocal);
        for (int i = 0; i < ttl.Expressions.Count; i++)
        {
            IL.Emit(OpCodes.Ldloc, listLocal);
            EmitExpression(ttl.Expressions[i]);
            EmitBoxIfNeeded(ttl.Expressions[i]);
            IL.Emit(OpCodes.Callvirt, _ctx.Types.ListOfObject.GetMethod("Add", [_ctx.Types.Object])!);
        }
        IL.Emit(OpCodes.Ldloc, listLocal);

        // 3. Call $Runtime.StringRaw(rawStrings, expressions)
        IL.Emit(OpCodes.Call, _ctx.Runtime!.StringRaw);
        SetStackType(StackType.String);
    }

    protected override void EmitRegexLiteral(Expr.RegexLiteral re)
    {
        IL.Emit(OpCodes.Ldstr, re.Pattern);
        IL.Emit(OpCodes.Ldstr, re.Flags);
        EmitCallUnknown(_ctx.Runtime!.CreateRegExpWithFlags);
    }

    protected override void EmitClassExpression(Expr.ClassExpr ce)
    {
        // Class expressions evaluate to the Type object at runtime.
        // The type has been pre-defined during collection phase.
        if (_ctx.ClassExprBuilders != null && _ctx.ClassExprBuilders.TryGetValue(ce, out var typeBuilder))
        {
            // If the class has static initializers, trigger the static constructor
            // JavaScript/TypeScript static blocks run when the class is defined, not lazily
            if (ce.StaticInitializers?.Count > 0 || ce.Fields.Any(f => f.IsStatic && f.Initializer != null))
            {
                // RuntimeHelpers.RunClassConstructor(typeof(ClassName).TypeHandle)
                IL.Emit(OpCodes.Ldtoken, typeBuilder);
                IL.Emit(OpCodes.Call, Types.TypeGetTypeFromHandle);
                IL.Emit(OpCodes.Callvirt, typeof(Type).GetProperty("TypeHandle")!.GetGetMethod()!);
                IL.Emit(OpCodes.Call, Types.RuntimeHelpersRunClassConstructor);
            }

            // Load the Type object using ldtoken + GetTypeFromHandle
            IL.Emit(OpCodes.Ldtoken, typeBuilder);
            IL.Emit(OpCodes.Call, Types.TypeGetTypeFromHandle);
            SetStackUnknown();
        }
        else
        {
            // Fallback: push null (should not happen if collection worked)
            IL.Emit(OpCodes.Ldnull);
            SetStackUnknown();
        }
    }

    protected override void EmitDelete(Expr.Delete del)
    {
        // delete operator: returns boolean
        // - delete obj.prop: removes property, returns true (or throws TypeError if frozen/sealed in strict mode)
        // - delete obj[key]: removes computed property, returns true (or throws TypeError if frozen/sealed in strict mode)
        // - delete variable: throws SyntaxError in strict mode, returns false in sloppy mode
        switch (del.Operand)
        {
            case Expr.Get get:
                // delete obj.prop - use static runtime helper with strict mode
                EmitExpression(get.Object);
                EmitBoxIfNeeded(get.Object);
                IL.Emit(OpCodes.Ldstr, get.Name.Lexeme);
                if (_ctx.IsStrictMode)
                {
                    IL.Emit(OpCodes.Ldc_I4_1); // true for strict mode
                    EmitCallUnknown(_ctx.Runtime!.DeletePropertyStrict);
                }
                else
                {
                    EmitCallUnknown(_ctx.Runtime!.DeleteProperty);
                }
                SetStackType(StackType.Boolean);
                break;

            case Expr.GetIndex getIndex:
                // delete obj[key] - use DeleteIndex with strict mode
                EmitExpression(getIndex.Object);
                EmitBoxIfNeeded(getIndex.Object);
                EmitExpression(getIndex.Index);
                EmitBoxIfNeeded(getIndex.Index);
                if (_ctx.IsStrictMode)
                {
                    IL.Emit(OpCodes.Ldc_I4_1); // true for strict mode
                    EmitCallUnknown(_ctx.Runtime!.DeleteIndexStrict);
                }
                else
                {
                    EmitCallUnknown(_ctx.Runtime!.DeleteIndex);
                }
                SetStackType(StackType.Boolean);
                break;

            case Expr.Variable v:
                if (_ctx.IsStrictMode)
                {
                    // Strict mode: throw SyntaxError
                    IL.Emit(OpCodes.Ldstr, $"Delete of unqualified identifier '{v.Name.Lexeme}' in strict mode");
                    EmitCallUnknown(_ctx.Runtime!.ThrowStrictSyntaxError);
                    // ThrowStrictSyntaxError throws, but we need a value on stack for IL verification
                    EmitBoolConstant(false);
                }
                else
                {
                    // Sloppy mode: warn and return false
                    IL.Emit(OpCodes.Ldstr, v.Name.Lexeme);
                    EmitCallUnknown(_ctx.Runtime!.WarnSloppyDeleteVariable);
                }
                SetStackType(StackType.Boolean);
                break;

            default:
                // delete on other expressions: returns true but does nothing
                // Still need to evaluate for side effects
                EmitExpression(del.Operand);
                IL.Emit(OpCodes.Pop);
                EmitBoolConstant(true);
                break;
        }
    }
}
