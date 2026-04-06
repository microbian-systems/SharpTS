using System.Reflection.Emit;
using SharpTS.Diagnostics.Exceptions;
using SharpTS.Parsing;
using SharpTS.Runtime.BuiltIns;

namespace SharpTS.Compilation;

/// <summary>
/// Shared built-in constructor emission for all emitters (ILEmitter + state machine emitters).
/// This centralizes constructor dispatch that was previously duplicated (and incomplete) across
/// AsyncMoveNextEmitter, AsyncArrowMoveNextEmitter, GeneratorMoveNextEmitter, and
/// AsyncGeneratorMoveNextEmitter.
/// </summary>
public abstract partial class ExpressionEmitterBase
{
    /// <summary>
    /// Extracts a qualified class name from a callee expression.
    /// Returns (namespaceParts, className) where namespaceParts is empty for simple names.
    /// </summary>
    protected static (List<string> namespaceParts, string className) ExtractQualifiedNameFromCallee(Expr callee)
    {
        List<string> parts = [];
        CollectGetChainParts(callee, parts);

        if (parts.Count == 0)
            return ([], "");

        var namespaceParts = parts.Count > 1 ? parts.Take(parts.Count - 1).ToList() : new List<string>();
        var className = parts[^1];
        return (namespaceParts, className);
    }

    private static void CollectGetChainParts(Expr expr, List<string> parts)
    {
        switch (expr)
        {
            case Expr.Variable v:
                parts.Add(v.Name.Lexeme);
                break;
            case Expr.Get g:
                CollectGetChainParts(g.Object, parts);
                parts.Add(g.Name.Lexeme);
                break;
        }
    }

    /// <summary>
    /// Emits an expression and ensures the result is a double on the stack.
    /// Optimizes literal doubles/ints by pushing directly.
    /// </summary>
    public virtual void EmitExpressionAsDouble(Expr expr)
    {
        if (expr is Expr.Literal lit && lit.Value is double d)
        {
            IL.Emit(OpCodes.Ldc_R8, d);
            SetStackType(StackType.Double);
        }
        else if (expr is Expr.Literal intLit && intLit.Value is int i)
        {
            IL.Emit(OpCodes.Ldc_R8, (double)i);
            SetStackType(StackType.Double);
        }
        else
        {
            EmitExpression(expr);
            EnsureDouble();
        }
    }

    /// <summary>
    /// Emits a boxed argument expression, or null if the argument index exceeds the count.
    /// </summary>
    private void EmitBoxedArgOrNull(List<Expr> arguments, int index)
    {
        if (index < arguments.Count)
        {
            EmitExpression(arguments[index]);
            EnsureBoxed();
        }
        else
        {
            IL.Emit(OpCodes.Ldnull);
        }
    }

    /// <summary>
    /// Emits a two-boxed-argument constructor pattern (arg0 or null, arg1 or null, then call/newobj).
    /// </summary>
    private void EmitTwoArgConstructor(List<Expr> arguments, System.Reflection.MethodInfo method, bool useNewobj = false)
    {
        EmitBoxedArgOrNull(arguments, 0);
        EmitBoxedArgOrNull(arguments, 1);
        IL.Emit(useNewobj ? OpCodes.Newobj : OpCodes.Call, method);
        SetStackUnknown();
    }

    /// <summary>
    /// Emits a two-boxed-argument constructor pattern using Newobj for ConstructorInfo.
    /// </summary>
    private void EmitTwoArgConstructor(List<Expr> arguments, System.Reflection.ConstructorInfo ctor)
    {
        EmitBoxedArgOrNull(arguments, 0);
        EmitBoxedArgOrNull(arguments, 1);
        IL.Emit(OpCodes.Newobj, ctor);
        SetStackUnknown();
    }

    /// <summary>
    /// Attempts to emit IL for a built-in type constructor.
    /// Returns true if the constructor was handled, false to fall through to user-class resolution.
    /// </summary>
    protected virtual bool TryEmitBuiltInConstructor(string className, List<Expr> arguments)
    {
        switch (className)
        {
            // --- No-arg constructors ---
            case "WeakMap":
                IL.Emit(OpCodes.Call, Ctx.Runtime!.CreateWeakMap);
                SetStackUnknown();
                return true;

            case "WeakSet":
                IL.Emit(OpCodes.Call, Ctx.Runtime!.CreateWeakSet);
                SetStackUnknown();
                return true;

            case "EventEmitter":
                IL.Emit(OpCodes.Newobj, Ctx.Runtime!.TSEventEmitterCtor);
                SetStackUnknown();
                return true;

            case "AsyncLocalStorage":
                IL.Emit(OpCodes.Newobj, Ctx.Runtime!.TSAsyncLocalStorageCtor);
                SetStackUnknown();
                return true;

            case "Resolver":
                IL.Emit(OpCodes.Call, Ctx.Runtime!.DnsResolverFactory);
                SetStackUnknown();
                return true;

            case "AbortController":
                IL.Emit(OpCodes.Call, Ctx.Runtime!.CreateAbortController);
                SetStackUnknown();
                return true;

            case "TextEncoder":
                IL.Emit(OpCodes.Newobj, Ctx.Runtime!.TSTextEncoderCtor);
                SetStackUnknown();
                return true;

            case "PassThrough":
                IL.Emit(OpCodes.Newobj, Ctx.Runtime!.TSPassThroughCtor);
                SetStackUnknown();
                return true;

            case "MessageChannel":
                IL.Emit(OpCodes.Call, Ctx.Runtime!.TSMessageChannelCtor);
                SetStackUnknown();
                return true;

            // --- Single boxed arg (null if missing) ---
            case "WeakRef":
                EmitBoxedArgOrNull(arguments, 0);
                IL.Emit(OpCodes.Call, Ctx.Runtime!.CreateWeakRef);
                SetStackUnknown();
                return true;

            case "FinalizationRegistry":
                EmitBoxedArgOrNull(arguments, 0);
                IL.Emit(OpCodes.Call, Ctx.Runtime!.CreateFinalizationRegistry);
                SetStackUnknown();
                return true;

            case "Headers":
                EmitBoxedArgOrNull(arguments, 0);
                IL.Emit(OpCodes.Newobj, Ctx.Runtime!.TSHeadersCtor);
                SetStackUnknown();
                return true;

            case "URLSearchParams":
                EmitBoxedArgOrNull(arguments, 0);
                IL.Emit(OpCodes.Newobj, Ctx.Runtime!.TSUrlSearchParamsCtor);
                SetStackUnknown();
                return true;

            // --- Two boxed args (null if missing) ---
            case "Proxy":
                EmitTwoArgConstructor(arguments, Ctx.Runtime!.CreateProxy);
                return true;

            case "URL":
                EmitTwoArgConstructor(arguments, Ctx.Runtime!.TSUrlCtor);
                return true;

            case "Request":
                EmitTwoArgConstructor(arguments, Ctx.Runtime!.TSRequestCtor);
                return true;

            case "Response":
                EmitTwoArgConstructor(arguments, Ctx.Runtime!.TSResponseCtor);
                return true;

            // --- Date (multi-arg) ---
            case "Date":
                EmitNewDateConstructor(arguments);
                return true;

            // --- Map/Set with optional entries ---
            case "Map":
                if (arguments.Count == 0)
                    IL.Emit(OpCodes.Call, Ctx.Runtime!.CreateMap);
                else
                {
                    EmitExpression(arguments[0]);
                    EnsureBoxed();
                    IL.Emit(OpCodes.Call, Ctx.Runtime!.CreateMapFromEntries);
                }
                SetStackUnknown();
                return true;

            case "Set":
                if (arguments.Count == 0)
                    IL.Emit(OpCodes.Call, Ctx.Runtime!.CreateSet);
                else
                {
                    EmitExpression(arguments[0]);
                    EnsureBoxed();
                    IL.Emit(OpCodes.Call, Ctx.Runtime!.CreateSetFromArray);
                }
                SetStackUnknown();
                return true;

            // --- RegExp ---
            case "RegExp":
                EmitNewRegExpConstructor(arguments);
                return true;

            // --- Promise ---
            case "Promise":
                if (arguments.Count != 1)
                    throw new CompileException("Promise constructor requires exactly 1 argument (executor function).");
                EmitExpression(arguments[0]);
                EnsureBoxed();
                IL.Emit(OpCodes.Call, Ctx.Runtime!.PromiseFromExecutor);
                SetStackUnknown();
                return true;

            // --- TextDecoder ---
            case "TextDecoder":
                if (arguments.Count > 0)
                {
                    EmitExpression(arguments[0]);
                    EnsureBoxed();
                    IL.Emit(OpCodes.Call, Ctx.Runtime!.Stringify);
                }
                else
                {
                    IL.Emit(OpCodes.Ldnull);
                }
                IL.Emit(OpCodes.Ldc_I4_0); // fatal: false
                IL.Emit(OpCodes.Ldc_I4_0); // ignoreBOM: false
                IL.Emit(OpCodes.Newobj, Ctx.Runtime!.TSTextDecoderCtor);
                SetStackUnknown();
                return true;

            // --- StringDecoder ---
            case "StringDecoder":
                if (arguments.Count > 0)
                {
                    EmitExpression(arguments[0]);
                    EnsureBoxed();
                    IL.Emit(OpCodes.Call, Ctx.Runtime!.Stringify);
                }
                else
                {
                    IL.Emit(OpCodes.Ldstr, "utf8");
                }
                IL.Emit(OpCodes.Newobj, Ctx.Runtime!.TSStringDecoderCtor);
                SetStackUnknown();
                return true;

            // --- PerformanceObserver ---
            case "PerformanceObserver":
                IL.Emit(OpCodes.Ldc_I4_1);
                IL.Emit(OpCodes.Newarr, typeof(object));
                if (arguments.Count > 0)
                {
                    IL.Emit(OpCodes.Dup);
                    IL.Emit(OpCodes.Ldc_I4_0);
                    EmitExpression(arguments[0]);
                    EnsureBoxed();
                    IL.Emit(OpCodes.Stelem_Ref);
                }
                IL.Emit(OpCodes.Call, Ctx.Runtime!.PerfHooksCreateObserver);
                SetStackUnknown();
                return true;

            // --- SharedArrayBuffer / ArrayBuffer ---
            case "SharedArrayBuffer":
                if (arguments.Count == 0)
                    IL.Emit(OpCodes.Ldc_R8, 0.0);
                else
                    EmitExpressionAsDouble(arguments[0]);
                IL.Emit(OpCodes.Call, Ctx.Runtime!.TSSharedArrayBufferCtor);
                SetStackUnknown();
                return true;

            case "ArrayBuffer":
                if (arguments.Count == 0)
                    IL.Emit(OpCodes.Ldc_R8, 0.0);
                else
                    EmitExpressionAsDouble(arguments[0]);
                IL.Emit(OpCodes.Call, Ctx.Runtime!.TSArrayBufferCtor);
                SetStackUnknown();
                return true;

            // --- DataView ---
            case "DataView":
                if (arguments.Count == 0)
                    throw new CompileException("DataView constructor requires at least 1 argument (buffer).");
                EmitExpression(arguments[0]);
                EnsureBoxed();
                if (arguments.Count > 1)
                    EmitExpressionAsDouble(arguments[1]);
                else
                    IL.Emit(OpCodes.Ldc_R8, 0.0);
                EmitBoxedArgOrNull(arguments, 2);
                IL.Emit(OpCodes.Call, Ctx.Runtime!.TSDataViewCtor);
                SetStackUnknown();
                return true;

            // --- Worker ---
            case "Worker":
                if (arguments.Count == 0)
                    throw new CompileException("Worker constructor requires at least 1 argument (filename).");
                EmitExpression(arguments[0]);
                EnsureBoxed();
                IL.Emit(OpCodes.Call, Ctx.Runtime!.Stringify);
                EmitBoxedArgOrNull(arguments, 1);
                IL.Emit(OpCodes.Ldnull); // parentInterpreter (null in compiled code)
                IL.Emit(OpCodes.Call, Ctx.Runtime!.TSWorkerCtor);
                SetStackUnknown();
                return true;

            // --- http.Agent ---
            case "Agent":
                EmitBoxedArgOrNull(arguments, 0);
                IL.Emit(OpCodes.Call, Ctx.Runtime!.HttpAgentFactory);
                SetStackUnknown();
                return true;

            // --- vm.Script ---
            case "Script":
                EmitBoxedArgOrNull(arguments, 0);
                EmitBoxedArgOrNull(arguments, 1);
                IL.Emit(OpCodes.Call, Ctx.Runtime!.VmNewScript);
                SetStackUnknown();
                return true;

            // --- Stream constructors ---
            case "Readable":
                EmitNewReadableConstructor(arguments);
                return true;

            case "Writable":
                EmitNewWritableConstructor(arguments);
                return true;

            case "Duplex":
                EmitNewDuplexConstructor(arguments);
                return true;

            case "Transform":
                EmitNewTransformConstructor(arguments);
                return true;

            default:
                // Check for error types (Error, TypeError, RangeError, etc.)
                if (BuiltInNames.IsErrorTypeName(className))
                {
                    EmitNewErrorConstructor(className, arguments);
                    return true;
                }

                // Check for TypedArray types
                if (BuiltInNames.IsTypedArrayName(className))
                {
                    EmitNewTypedArrayConstructor(className, arguments);
                    return true;
                }

                return false;
        }
    }

    /// <summary>
    /// Attempts to emit an Intl namespace constructor.
    /// Returns true if handled.
    /// </summary>
    protected bool TryEmitIntlConstructor(List<string> namespaceParts, string className, List<Expr> arguments)
    {
        if (namespaceParts is not ["Intl"])
            return false;

        var runtime = Ctx.Runtime!;
        System.Reflection.MethodInfo? method = className switch
        {
            "NumberFormat" => runtime.CreateIntlNumberFormat,
            "DateTimeFormat" => runtime.CreateIntlDateTimeFormat,
            "Collator" => runtime.CreateIntlCollator,
            "PluralRules" => runtime.CreateIntlPluralRules,
            "RelativeTimeFormat" => runtime.CreateIntlRelativeTimeFormat,
            "ListFormat" => runtime.CreateIntlListFormat,
            "Segmenter" => runtime.CreateIntlSegmenter,
            "DisplayNames" => runtime.CreateIntlDisplayNames,
            _ => null
        };

        if (method == null)
            return false;

        EmitTwoArgConstructor(arguments, method);
        return true;
    }

    /// <summary>
    /// Attempts to emit a module-qualified constructor (e.g., new util.TextEncoder()).
    /// Returns true if handled.
    /// </summary>
    protected bool TryEmitModuleQualifiedConstructor(List<string> namespaceParts, string className, List<Expr> arguments)
    {
        if (namespaceParts.Count != 1)
            return false;

        return className switch
        {
            "TextEncoder" => TryEmitBuiltInConstructor("TextEncoder", arguments),
            "TextDecoder" => TryEmitBuiltInConstructor("TextDecoder", arguments),
            "StringDecoder" => TryEmitBuiltInConstructor("StringDecoder", arguments),
            "PerformanceObserver" => TryEmitBuiltInConstructor("PerformanceObserver", arguments),
            "Agent" => TryEmitAgentConstructor(arguments),
            "Resolver" => TryEmitResolverConstructor(),
            _ => false
        };
    }

    private bool TryEmitAgentConstructor(List<Expr> arguments)
    {
        if (arguments.Count > 0)
        {
            EmitExpression(arguments[0]);
            EnsureBoxed();
        }
        else
        {
            IL.Emit(OpCodes.Ldnull);
        }
        IL.Emit(OpCodes.Call, Ctx.Runtime!.HttpAgentFactory);
        _helpers.SetStackUnknown();
        return true;
    }

    private bool TryEmitResolverConstructor()
    {
        IL.Emit(OpCodes.Call, Ctx.Runtime!.DnsResolverFactory);
        _helpers.SetStackUnknown();
        return true;
    }

    #region Private constructor helpers

    private void EmitNewDateConstructor(List<Expr> arguments)
    {
        switch (arguments.Count)
        {
            case 0:
                IL.Emit(OpCodes.Call, Ctx.Runtime!.CreateDateNoArgs);
                break;
            case 1:
                EmitExpression(arguments[0]);
                EnsureBoxed();
                IL.Emit(OpCodes.Call, Ctx.Runtime!.CreateDateFromValue);
                break;
            default:
                // new Date(year, month, day?, hours?, minutes?, seconds?, ms?)
                for (int i = 0; i < 7; i++)
                {
                    if (i < arguments.Count)
                        EmitExpressionAsDouble(arguments[i]);
                    else
                        IL.Emit(OpCodes.Ldc_R8, i == 2 ? 1.0 : 0.0);
                }
                IL.Emit(OpCodes.Call, Ctx.Runtime!.CreateDateFromComponents);
                break;
        }
        SetStackUnknown();
    }

    private void EmitNewRegExpConstructor(List<Expr> arguments)
    {
        switch (arguments.Count)
        {
            case 0:
                IL.Emit(OpCodes.Ldstr, "");
                IL.Emit(OpCodes.Ldstr, "");
                IL.Emit(OpCodes.Call, Ctx.Runtime!.CreateRegExpWithFlags);
                break;
            case 1:
                EmitExpression(arguments[0]);
                EnsureBoxed();
                IL.Emit(OpCodes.Call, Ctx.Runtime!.Stringify);
                IL.Emit(OpCodes.Call, Ctx.Runtime!.CreateRegExp);
                break;
            default:
                EmitExpression(arguments[0]);
                EnsureBoxed();
                IL.Emit(OpCodes.Call, Ctx.Runtime!.Stringify);
                EmitExpression(arguments[1]);
                EnsureBoxed();
                IL.Emit(OpCodes.Call, Ctx.Runtime!.Stringify);
                IL.Emit(OpCodes.Call, Ctx.Runtime!.CreateRegExpWithFlags);
                break;
        }
        SetStackUnknown();
    }

    private void EmitNewErrorConstructor(string errorTypeName, List<Expr> arguments)
    {
        IL.Emit(OpCodes.Ldstr, errorTypeName);
        IL.Emit(OpCodes.Ldc_I4, arguments.Count);
        IL.Emit(OpCodes.Newarr, typeof(object));
        for (int i = 0; i < arguments.Count; i++)
        {
            IL.Emit(OpCodes.Dup);
            IL.Emit(OpCodes.Ldc_I4, i);
            EmitExpression(arguments[i]);
            EnsureBoxed();
            IL.Emit(OpCodes.Stelem_Ref);
        }
        IL.Emit(OpCodes.Call, Ctx.Runtime!.CreateError);
        SetStackUnknown();
    }

    private void EmitNewTypedArrayConstructor(string typeName, List<Expr> arguments)
    {
        if (arguments.Count == 0)
        {
            if (Ctx.Runtime!.TypedArrayFromObjectHelpers.TryGetValue(typeName, out var helper))
            {
                IL.Emit(OpCodes.Ldnull);
                IL.Emit(OpCodes.Call, helper);
            }
            else
                throw new CompileException($"Missing TypedArray helper for: {typeName}");
        }
        else if (arguments.Count == 1)
        {
            EmitExpression(arguments[0]);
            EnsureBoxed();
            if (Ctx.Runtime!.TypedArrayFromObjectHelpers.TryGetValue(typeName, out var helper))
                IL.Emit(OpCodes.Call, helper);
            else
                throw new CompileException($"Missing TypedArray helper for: {typeName}");
        }
        else
        {
            if (Ctx.Runtime!.TypedArrayFromBufferHelpers.TryGetValue(typeName, out var bufferHelper))
            {
                EmitExpression(arguments[0]);
                EnsureBoxed();
                if (arguments.Count > 1)
                    EmitExpressionAsDouble(arguments[1]);
                else
                    IL.Emit(OpCodes.Ldc_R8, 0.0);
                EmitBoxedArgOrNull(arguments, 2);
                IL.Emit(OpCodes.Call, bufferHelper);
            }
            else
                throw new CompileException($"Missing TypedArray buffer helper for: {typeName}");
        }
        SetStackUnknown();
    }

    private void EmitNewReadableConstructor(List<Expr> arguments)
    {
        IL.Emit(OpCodes.Newobj, Ctx.Runtime!.TSReadableCtor);
        if (arguments.Count > 0)
        {
            var instanceLocal = IL.DeclareLocal(Ctx.Runtime!.TSReadableType);
            IL.Emit(OpCodes.Stloc, instanceLocal);
            EmitExpression(arguments[0]);
            EnsureBoxed();
            var optionsLocal = IL.DeclareLocal(typeof(object));
            IL.Emit(OpCodes.Stloc, optionsLocal);
            var endLabel = IL.DefineLabel();
            IL.Emit(OpCodes.Ldloc, optionsLocal);
            IL.Emit(OpCodes.Brfalse, endLabel);

            // Extract 'objectMode' property
            var skipObjectModeLabel = IL.DefineLabel();
            var afterObjectModeLabel = IL.DefineLabel();
            IL.Emit(OpCodes.Ldloc, optionsLocal);
            IL.Emit(OpCodes.Ldstr, "objectMode");
            IL.Emit(OpCodes.Call, Ctx.Runtime!.GetProperty);
            IL.Emit(OpCodes.Dup);
            IL.Emit(OpCodes.Brfalse, skipObjectModeLabel);
            IL.Emit(OpCodes.Pop);
            IL.Emit(OpCodes.Ldloc, instanceLocal);
            IL.Emit(OpCodes.Ldc_I4_1);
            IL.Emit(OpCodes.Call, Ctx.Runtime!.TSReadableSetObjectMode);
            IL.Emit(OpCodes.Br, afterObjectModeLabel);
            IL.MarkLabel(skipObjectModeLabel);
            IL.Emit(OpCodes.Pop);
            IL.MarkLabel(afterObjectModeLabel);

            // Extract 'highWaterMark' property and call SetHighWaterMark
            {
                var skipHwmLabel = IL.DefineLabel();
                var hwmValLocal = IL.DeclareLocal(typeof(object));
                IL.Emit(OpCodes.Ldloc, optionsLocal);
                IL.Emit(OpCodes.Ldstr, "highWaterMark");
                IL.Emit(OpCodes.Call, Ctx.Runtime!.GetProperty);
                IL.Emit(OpCodes.Stloc, hwmValLocal);
                // Skip if null
                IL.Emit(OpCodes.Ldloc, hwmValLocal);
                IL.Emit(OpCodes.Brfalse, skipHwmLabel);
                // Skip if $Undefined
                IL.Emit(OpCodes.Ldloc, hwmValLocal);
                IL.Emit(OpCodes.Isinst, Ctx.Runtime!.UndefinedType);
                IL.Emit(OpCodes.Brtrue, skipHwmLabel);
                // Convert to int via Convert.ToInt32
                IL.Emit(OpCodes.Ldloc, instanceLocal);
                IL.Emit(OpCodes.Ldloc, hwmValLocal);
                IL.Emit(OpCodes.Call, typeof(Convert).GetMethod("ToInt32", [typeof(object)])!);
                IL.Emit(OpCodes.Call, Ctx.Runtime!.TSReadableSetHighWaterMark!);
                IL.MarkLabel(skipHwmLabel);
            }

            IL.MarkLabel(endLabel);
            IL.Emit(OpCodes.Ldloc, instanceLocal);
        }
        SetStackUnknown();
    }

    private void EmitNewWritableConstructor(List<Expr> arguments)
    {
        IL.Emit(OpCodes.Newobj, Ctx.Runtime!.TSWritableCtor);
        if (arguments.Count > 0)
        {
            var optionsNullLabel = IL.DefineLabel();
            var endLabel = IL.DefineLabel();
            var instanceLocal = IL.DeclareLocal(Ctx.Runtime!.TSWritableType);
            IL.Emit(OpCodes.Stloc, instanceLocal);
            EmitExpression(arguments[0]);
            EnsureBoxed();
            var optionsLocal = IL.DeclareLocal(typeof(object));
            IL.Emit(OpCodes.Dup);
            IL.Emit(OpCodes.Stloc, optionsLocal);
            IL.Emit(OpCodes.Brfalse, optionsNullLabel);

            // Extract 'write' callback
            var skipWriteLabel = IL.DefineLabel();
            IL.Emit(OpCodes.Ldloc, optionsLocal);
            IL.Emit(OpCodes.Ldstr, "write");
            IL.Emit(OpCodes.Call, Ctx.Runtime!.GetProperty);
            var writeCallbackLocal = IL.DeclareLocal(typeof(object));
            IL.Emit(OpCodes.Stloc, writeCallbackLocal);
            IL.Emit(OpCodes.Ldloc, writeCallbackLocal);
            IL.Emit(OpCodes.Brfalse, skipWriteLabel);
            IL.Emit(OpCodes.Ldloc, instanceLocal);
            IL.Emit(OpCodes.Ldloc, writeCallbackLocal);
            IL.Emit(OpCodes.Callvirt, Ctx.Runtime!.TSWritableType.GetMethod("SetWriteCallback")!);
            IL.MarkLabel(skipWriteLabel);

            // Extract 'final' callback
            var skipFinalLabel = IL.DefineLabel();
            IL.Emit(OpCodes.Ldloc, optionsLocal);
            IL.Emit(OpCodes.Ldstr, "final");
            IL.Emit(OpCodes.Call, Ctx.Runtime!.GetProperty);
            var finalCallbackLocal = IL.DeclareLocal(typeof(object));
            IL.Emit(OpCodes.Stloc, finalCallbackLocal);
            IL.Emit(OpCodes.Ldloc, finalCallbackLocal);
            IL.Emit(OpCodes.Brfalse, skipFinalLabel);
            IL.Emit(OpCodes.Ldloc, instanceLocal);
            IL.Emit(OpCodes.Ldloc, finalCallbackLocal);
            IL.Emit(OpCodes.Callvirt, Ctx.Runtime!.TSWritableType.GetMethod("SetFinalCallback")!);
            IL.MarkLabel(skipFinalLabel);

            // Extract 'objectMode' — check if value is boxed true (not just non-null)
            {
                var skipObjectModeLabel = IL.DefineLabel();
                var objectModeValLocal = IL.DeclareLocal(typeof(object));
                IL.Emit(OpCodes.Ldloc, optionsLocal);
                IL.Emit(OpCodes.Ldstr, "objectMode");
                IL.Emit(OpCodes.Call, Ctx.Runtime!.GetProperty);
                IL.Emit(OpCodes.Stloc, objectModeValLocal);
                // Check if value is boxed Boolean true
                IL.Emit(OpCodes.Ldloc, objectModeValLocal);
                IL.Emit(OpCodes.Isinst, typeof(bool));
                IL.Emit(OpCodes.Brfalse, skipObjectModeLabel);
                IL.Emit(OpCodes.Ldloc, objectModeValLocal);
                IL.Emit(OpCodes.Unbox_Any, typeof(bool));
                IL.Emit(OpCodes.Brfalse, skipObjectModeLabel);
                IL.Emit(OpCodes.Ldloc, instanceLocal);
                IL.Emit(OpCodes.Ldc_I4_1);
                IL.Emit(OpCodes.Callvirt, Ctx.Runtime!.TSWritableType.GetMethod("SetObjectMode")!);
                IL.MarkLabel(skipObjectModeLabel);
            }

            // Extract 'autoDestroy' — check if value is boxed true
            {
                var skipAutoDestroyLabel = IL.DefineLabel();
                var autoDestroyValLocal = IL.DeclareLocal(typeof(object));
                IL.Emit(OpCodes.Ldloc, optionsLocal);
                IL.Emit(OpCodes.Ldstr, "autoDestroy");
                IL.Emit(OpCodes.Call, Ctx.Runtime!.GetProperty);
                IL.Emit(OpCodes.Stloc, autoDestroyValLocal);
                IL.Emit(OpCodes.Ldloc, autoDestroyValLocal);
                IL.Emit(OpCodes.Isinst, typeof(bool));
                IL.Emit(OpCodes.Brfalse, skipAutoDestroyLabel);
                IL.Emit(OpCodes.Ldloc, autoDestroyValLocal);
                IL.Emit(OpCodes.Unbox_Any, typeof(bool));
                IL.Emit(OpCodes.Brfalse, skipAutoDestroyLabel);
                IL.Emit(OpCodes.Ldloc, instanceLocal);
                IL.Emit(OpCodes.Ldc_I4_1);
                IL.Emit(OpCodes.Callvirt, Ctx.Runtime!.TSWritableType.GetMethod("SetAutoDestroy")!);
                IL.MarkLabel(skipAutoDestroyLabel);
            }

            // Extract 'highWaterMark'
            {
                var skipHwmLabel = IL.DefineLabel();
                var hwmValLocal = IL.DeclareLocal(typeof(object));
                IL.Emit(OpCodes.Ldloc, optionsLocal);
                IL.Emit(OpCodes.Ldstr, "highWaterMark");
                IL.Emit(OpCodes.Call, Ctx.Runtime!.GetProperty);
                IL.Emit(OpCodes.Stloc, hwmValLocal);
                IL.Emit(OpCodes.Ldloc, hwmValLocal);
                IL.Emit(OpCodes.Brfalse, skipHwmLabel);
                IL.Emit(OpCodes.Ldloc, hwmValLocal);
                IL.Emit(OpCodes.Isinst, Ctx.Runtime!.UndefinedType);
                IL.Emit(OpCodes.Brtrue, skipHwmLabel);
                IL.Emit(OpCodes.Ldloc, instanceLocal);
                IL.Emit(OpCodes.Ldloc, hwmValLocal);
                IL.Emit(OpCodes.Call, typeof(Convert).GetMethod("ToInt32", [typeof(object)])!);
                IL.Emit(OpCodes.Callvirt, Ctx.Runtime!.TSWritableType.GetMethod("SetHighWaterMark")!);
                IL.MarkLabel(skipHwmLabel);
            }

            IL.Emit(OpCodes.Br, endLabel);
            IL.MarkLabel(optionsNullLabel);
            IL.MarkLabel(endLabel);
            IL.Emit(OpCodes.Ldloc, instanceLocal);
        }
        SetStackUnknown();
    }

    private void EmitNewDuplexConstructor(List<Expr> arguments)
    {
        IL.Emit(OpCodes.Newobj, Ctx.Runtime!.TSDuplexCtor);
        if (arguments.Count > 0)
        {
            var optionsLabel = IL.DefineLabel();
            var endLabel = IL.DefineLabel();
            var instanceLocal = IL.DeclareLocal(Ctx.Runtime!.TSDuplexType);
            IL.Emit(OpCodes.Stloc, instanceLocal);
            EmitExpression(arguments[0]);
            EnsureBoxed();
            var optionsLocal = IL.DeclareLocal(typeof(object));
            IL.Emit(OpCodes.Dup);
            IL.Emit(OpCodes.Stloc, optionsLocal);
            IL.Emit(OpCodes.Brfalse, optionsLabel);

            // Extract 'write' callback
            var skipWriteLabel = IL.DefineLabel();
            IL.Emit(OpCodes.Ldloc, optionsLocal);
            IL.Emit(OpCodes.Ldstr, "write");
            IL.Emit(OpCodes.Call, Ctx.Runtime!.GetProperty);
            var writeCallbackLocal = IL.DeclareLocal(typeof(object));
            IL.Emit(OpCodes.Stloc, writeCallbackLocal);
            IL.Emit(OpCodes.Ldloc, writeCallbackLocal);
            IL.Emit(OpCodes.Brfalse, skipWriteLabel);
            IL.Emit(OpCodes.Ldloc, instanceLocal);
            IL.Emit(OpCodes.Ldloc, writeCallbackLocal);
            IL.Emit(OpCodes.Callvirt, Ctx.Runtime!.TSDuplexType.GetMethod("SetWriteCallback")!);
            IL.MarkLabel(skipWriteLabel);

            // Extract 'objectMode'
            var skipObjectModeLabel = IL.DefineLabel();
            IL.Emit(OpCodes.Ldloc, optionsLocal);
            IL.Emit(OpCodes.Ldstr, "objectMode");
            IL.Emit(OpCodes.Call, Ctx.Runtime!.GetProperty);
            IL.Emit(OpCodes.Dup);
            IL.Emit(OpCodes.Brfalse, skipObjectModeLabel);
            IL.Emit(OpCodes.Pop);
            IL.Emit(OpCodes.Ldloc, instanceLocal);
            IL.Emit(OpCodes.Ldc_I4_1);
            IL.Emit(OpCodes.Callvirt, Ctx.Runtime!.TSDuplexType.GetMethod("SetObjectMode")!);
            IL.Emit(OpCodes.Br, endLabel);
            IL.MarkLabel(skipObjectModeLabel);
            IL.Emit(OpCodes.Pop);
            IL.Emit(OpCodes.Br, endLabel);
            IL.MarkLabel(optionsLabel);
            IL.MarkLabel(endLabel);
            IL.Emit(OpCodes.Ldloc, instanceLocal);
        }
        SetStackUnknown();
    }

    private void EmitNewTransformConstructor(List<Expr> arguments)
    {
        IL.Emit(OpCodes.Newobj, Ctx.Runtime!.TSTransformCtor);
        if (arguments.Count > 0)
        {
            var optionsLabel = IL.DefineLabel();
            var endLabel = IL.DefineLabel();
            var instanceLocal = IL.DeclareLocal(Ctx.Runtime!.TSTransformType);
            IL.Emit(OpCodes.Stloc, instanceLocal);
            EmitExpression(arguments[0]);
            EnsureBoxed();
            var optionsLocal = IL.DeclareLocal(typeof(object));
            IL.Emit(OpCodes.Dup);
            IL.Emit(OpCodes.Stloc, optionsLocal);
            IL.Emit(OpCodes.Brfalse, optionsLabel);

            // Get 'transform' callback
            var afterTransformLabel = IL.DefineLabel();
            IL.Emit(OpCodes.Ldloc, optionsLocal);
            IL.Emit(OpCodes.Ldstr, "transform");
            IL.Emit(OpCodes.Call, Ctx.Runtime!.GetProperty);
            var transformCallbackLocal = IL.DeclareLocal(typeof(object));
            IL.Emit(OpCodes.Stloc, transformCallbackLocal);
            IL.Emit(OpCodes.Ldloc, transformCallbackLocal);
            IL.Emit(OpCodes.Brfalse, afterTransformLabel);
            IL.Emit(OpCodes.Ldloc, instanceLocal);
            IL.Emit(OpCodes.Ldloc, transformCallbackLocal);
            IL.Emit(OpCodes.Callvirt, Ctx.Runtime!.TSTransformType.GetMethod("SetTransformCallback")!);
            IL.MarkLabel(afterTransformLabel);

            // Get 'flush' callback
            var afterFlushLabel = IL.DefineLabel();
            IL.Emit(OpCodes.Ldloc, optionsLocal);
            IL.Emit(OpCodes.Ldstr, "flush");
            IL.Emit(OpCodes.Call, Ctx.Runtime!.GetProperty);
            var flushCallbackLocal = IL.DeclareLocal(typeof(object));
            IL.Emit(OpCodes.Stloc, flushCallbackLocal);
            IL.Emit(OpCodes.Ldloc, flushCallbackLocal);
            IL.Emit(OpCodes.Brfalse, afterFlushLabel);
            IL.Emit(OpCodes.Ldloc, instanceLocal);
            IL.Emit(OpCodes.Ldloc, flushCallbackLocal);
            IL.Emit(OpCodes.Callvirt, Ctx.Runtime!.TSTransformType.GetMethod("SetFlushCallback")!);
            IL.MarkLabel(afterFlushLabel);

            // Extract 'objectMode' (Transform extends Duplex)
            var skipObjectModeLabel = IL.DefineLabel();
            IL.Emit(OpCodes.Ldloc, optionsLocal);
            IL.Emit(OpCodes.Ldstr, "objectMode");
            IL.Emit(OpCodes.Call, Ctx.Runtime!.GetProperty);
            IL.Emit(OpCodes.Dup);
            IL.Emit(OpCodes.Brfalse, skipObjectModeLabel);
            IL.Emit(OpCodes.Pop);
            IL.Emit(OpCodes.Ldloc, instanceLocal);
            IL.Emit(OpCodes.Ldc_I4_1);
            IL.Emit(OpCodes.Callvirt, Ctx.Runtime!.TSDuplexType.GetMethod("SetObjectMode")!);
            IL.Emit(OpCodes.Br, endLabel);
            IL.MarkLabel(skipObjectModeLabel);
            IL.Emit(OpCodes.Pop);
            IL.Emit(OpCodes.Br, endLabel);
            IL.MarkLabel(optionsLabel);
            IL.MarkLabel(endLabel);
            IL.Emit(OpCodes.Ldloc, instanceLocal);
        }
        SetStackUnknown();
    }

    #endregion
}
