using System.Reflection;
using System.Reflection.Emit;
using SharpTS.Parsing;
using SharpTS.TypeSystem;

namespace SharpTS.Compilation;

/// <summary>
/// Instance method routing and dispatch for the IL emitter.
/// </summary>
public partial class ILEmitter
{
    private void EmitMethodCall(Expr.Get methodGet, List<Expr> arguments)
    {
        string methodName = methodGet.Name.Lexeme;

        // Try direct dispatch for known class instance methods
        TypeSystem.TypeInfo? objType = _ctx.TypeMap?.Get(methodGet.Object);
        if (TryEmitDirectMethodCall(methodGet.Object, objType, methodName, arguments))
            return;

        // Timeout instance methods: ref(), unref()
        if (objType is TypeSystem.TypeInfo.Timeout && methodName is "ref" or "unref")
        {
            EmitExpression(methodGet.Object);
            EmitBoxIfNeeded(methodGet.Object);
            IL.Emit(OpCodes.Castclass, _ctx.Runtime!.TSTimeoutType);
            IL.Emit(OpCodes.Callvirt, methodName == "ref" ? _ctx.Runtime!.TSTimeoutRef : _ctx.Runtime!.TSTimeoutUnref);
            SetStackUnknown();
            return;
        }

        // Promise instance methods: then(), catch(), finally()
        // These work on Task<object?> returned by async functions
        if (methodName is "then" or "catch" or "finally")
        {
            // Check if we know it's a Promise at compile time
            if (objType is TypeSystem.TypeInfo.Promise)
            {
                EmitPromiseInstanceMethodCall(methodGet.Object, methodName, arguments);
                return;
            }
        }

        // Type-first dispatch: Use TypeEmitterRegistry if we have type information
        if (objType != null && _ctx.TypeEmitterRegistry != null)
        {
            var strategy = _ctx.TypeEmitterRegistry.GetStrategy(objType);
            if (strategy != null && strategy.TryEmitMethodCall(this, methodGet.Object, methodName, arguments))
                return;

            // Handle union types - try emitters for member types
            // BUT for methods that exist on multiple types, use runtime dispatch instead
            if (objType is TypeSystem.TypeInfo.Union union)
            {
                bool hasBufferMember = union.Types.Any(t => t is TypeSystem.TypeInfo.Buffer);
                bool hasStringMember = union.Types.Any(t => t is TypeSystem.TypeInfo.String or TypeSystem.TypeInfo.StringLiteral);
                bool hasArrayMember = union.Types.Any(t => t is TypeSystem.TypeInfo.Array);

                // Count how many types in the union could have this method
                // Methods like includes, indexOf, slice exist on multiple types
                bool isAmbiguousMethod = methodName is "slice" or "concat" or "includes" or "indexOf"
                    or "toString" or "valueOf";
                int typesWithMethod = 0;
                if (hasBufferMember && isAmbiguousMethod) typesWithMethod++;
                if (hasStringMember && isAmbiguousMethod) typesWithMethod++;
                if (hasArrayMember && isAmbiguousMethod) typesWithMethod++;

                // If multiple types could have this method, skip type-specific emitters
                // and let runtime dispatch handle it below
                if (typesWithMethod <= 1)
                {
                    // Try buffer emitter if union contains buffer
                    if (hasBufferMember)
                    {
                        var bufferStrategy = _ctx.TypeEmitterRegistry.GetStrategy(new TypeSystem.TypeInfo.Buffer());
                        if (bufferStrategy != null && bufferStrategy.TryEmitMethodCall(this, methodGet.Object, methodName, arguments))
                            return;
                    }

                    // Try string emitter if union contains string
                    if (hasStringMember)
                    {
                        var stringStrategy = _ctx.TypeEmitterRegistry.GetStrategy(new TypeSystem.TypeInfo.String());
                        if (stringStrategy != null && stringStrategy.TryEmitMethodCall(this, methodGet.Object, methodName, arguments))
                            return;
                    }

                    // Try array emitter if union contains array
                    if (hasArrayMember)
                    {
                        var arrayStrategy = _ctx.TypeEmitterRegistry.GetStrategy(new TypeSystem.TypeInfo.Array(new TypeSystem.TypeInfo.Any()));
                        if (arrayStrategy != null && arrayStrategy.TryEmitMethodCall(this, methodGet.Object, methodName, arguments))
                            return;
                    }
                }
            }
        }

        // Methods that exist on both strings and arrays - runtime dispatch for any/unknown/union types
        // Note: String and Array types are handled by TypeEmitterRegistry above
        // startsWith/endsWith are string-only but included here for Any-typed values
        // substring/charAt are string-only but need runtime dispatch for union types (string | undefined)
        if (methodName is "slice" or "concat" or "includes" or "indexOf" or "startsWith" or "endsWith"
            or "substring" or "charAt")
        {
            EmitAmbiguousMethodCall(methodGet.Object, methodName, arguments);
            return;
        }

        // Number instance methods - runtime dispatch for any/unknown types
        if (methodName is "toFixed" or "toPrecision" or "toExponential" or "valueOf" or "toString")
        {
            // Check if we know it's a number at compile time
            if (objType is TypeSystem.TypeInfo.Primitive { Type: Parsing.TokenType.TYPE_NUMBER } or TypeSystem.TypeInfo.NumberLiteral)
            {
                EmitNumberMethodCallDirect(methodGet.Object, methodName, arguments);
                return;
            }
            // For unknown types, use runtime dispatch
            EmitNumberMethodCall(methodGet.Object, methodName, arguments);
            return;
        }

        // Note: Date, Map, Set, WeakMap, WeakSet, RegExp methods are handled by TypeEmitterRegistry above
        // For Any types, we need runtime dispatch for Map methods since TypeEmitterRegistry won't catch them
        if (methodName is "get" or "set" or "has" or "delete" or "clear" or "keys" or "values" or "entries" or "forEach")
        {
            EmitMapMethodCall(methodGet.Object, methodName, arguments);
            return;
        }

        // String-only methods - runtime dispatch for any/unknown types
        // When type is known as string, TypeEmitterRegistry's StringEmitter handles these
        // For Any types, we need to check at runtime if the receiver is a string
        if (methodName is "padEnd" or "padStart" or "trim" or "trimStart" or "trimEnd"
            or "toUpperCase" or "toLowerCase" or "replace" or "replaceAll" or "split"
            or "match" or "search" or "repeat" or "charCodeAt" or "at" or "lastIndexOf"
            or "normalize" or "localeCompare")
        {
            EmitStringOnlyMethodCall(methodGet.Object, methodName, arguments);
            return;
        }

        // For object method calls, we need to pass the receiver as 'this'
        // Stack order: receiver, function, args

        // Emit receiver object once and store in a local to avoid double evaluation
        EmitExpression(methodGet.Object);
        EmitBoxIfNeeded(methodGet.Object);
        var receiverLocal = IL.DeclareLocal(_ctx.Types.Object);
        IL.Emit(OpCodes.Stloc, receiverLocal);

        // Load receiver for InvokeMethodValue's first argument
        IL.Emit(OpCodes.Ldloc, receiverLocal);

        // Get the method/function value from the object using same receiver
        IL.Emit(OpCodes.Ldloc, receiverLocal);
        IL.Emit(OpCodes.Ldstr, methodName);
        IL.Emit(OpCodes.Call, _ctx.Runtime!.GetProperty);

        // Create args array
        IL.Emit(OpCodes.Ldc_I4, arguments.Count);
        IL.Emit(OpCodes.Newarr, _ctx.Types.Object);

        for (int i = 0; i < arguments.Count; i++)
        {
            IL.Emit(OpCodes.Dup);
            IL.Emit(OpCodes.Ldc_I4, i);
            EmitExpression(arguments[i]);
            EmitBoxIfNeeded(arguments[i]);
            IL.Emit(OpCodes.Stelem_Ref);
        }

        // Call InvokeMethodValue(receiver, function, args) to bind 'this'
        IL.Emit(OpCodes.Call, _ctx.Runtime!.InvokeMethodValue);
    }

    /// <summary>
    /// Try to emit a direct method call for known class instance types.
    /// Returns true if direct dispatch was emitted, false to fall back to runtime dispatch.
    /// </summary>
    private bool TryEmitDirectMethodCall(Expr receiver, TypeSystem.TypeInfo? receiverType,
        string methodName, List<Expr> arguments)
    {
        // Only handle Instance types (e.g., let p: Person = ...)
        if (receiverType is not TypeSystem.TypeInfo.Instance instance)
            return false;

        // Extract the class name from the instance's class type
        string? simpleClassName = instance.ClassType switch
        {
            TypeSystem.TypeInfo.Class c => c.Name,
            _ => null
        };
        if (simpleClassName == null)
            return false;

        // Check if this is an external .NET type (@DotNetType)
        if (_ctx.TypeMapper.ExternalTypes.TryGetValue(simpleClassName, out var externalType))
        {
            EmitExternalInstanceMethodCall(receiver, externalType, methodName, arguments);
            return true;
        }

        // Resolve to qualified name for multi-module compilation
        string className = _ctx.ResolveClassName(simpleClassName);

        // Also check if the qualified name is an external type
        if (_ctx.TypeMapper.ExternalTypes.TryGetValue(className, out externalType))
        {
            EmitExternalInstanceMethodCall(receiver, externalType, methodName, arguments);
            return true;
        }

        // Look up the method in the class hierarchy
        var methodBuilder = _ctx.ResolveInstanceMethod(className, methodName);
        if (methodBuilder == null)
            return false;

        // Get the class type builder to cast the receiver
        if (!_ctx.Classes.TryGetValue(className, out var classType))
            return false;

        // Get target parameter types for proper conversion
        var targetParams = methodBuilder.GetParameters();
        int expectedParamCount = targetParams.Length;

        // Emit: ((ClassName)receiver).method(args)
        EmitExpression(receiver);
        EmitBoxIfNeeded(receiver);
        IL.Emit(OpCodes.Castclass, classType);

        // Emit arguments with proper type conversions
        for (int i = 0; i < arguments.Count; i++)
        {
            var arg = arguments[i];
            EmitExpression(arg);
            // Convert to target parameter type
            if (i < targetParams.Length)
            {
                EmitConversionForParameter(arg, targetParams[i].ParameterType);
            }
            else
            {
                EmitBoxIfNeeded(arg);
            }
        }

        // Pad missing optional arguments with appropriate default values
        for (int i = arguments.Count; i < expectedParamCount; i++)
        {
            var paramType = targetParams[i].ParameterType;
            EmitDefaultForType(paramType);
        }

        // Emit the virtual call
        IL.Emit(OpCodes.Callvirt, methodBuilder);
        SetStackUnknown();
        return true;
    }

    /// <summary>
    /// Emits a non-virtual call to a base class method for super.method() expressions.
    /// Uses OpCodes.Call instead of Callvirt to bypass virtual dispatch and invoke the
    /// base class implementation directly, preventing infinite recursion.
    /// </summary>
    private bool TryEmitSuperMethodCall(string methodName, List<Expr> arguments)
    {
        // Resolve the superclass name - try multiple sources:
        // 1. CurrentSuperclassName (set in constructor context)
        // 2. ClassRegistry superclass lookup (works in method body context)
        // 3. Class expression superclass mapping
        string? superclassName = _ctx.CurrentSuperclassName;

        if (superclassName == null && _ctx.CurrentClassName != null)
        {
            // Try qualified name first (includes namespace), then simple name
            superclassName = _ctx.ClassRegistry?.GetSuperclass(
                _ctx.CurrentClassBuilder?.FullName ?? _ctx.CurrentClassName)
                ?? _ctx.ClassRegistry?.GetSuperclass(_ctx.CurrentClassName);
        }

        if (superclassName == null && _ctx.CurrentClassExpr != null)
        {
            _ctx.ClassExprSuperclass?.TryGetValue(_ctx.CurrentClassExpr, out superclassName);
        }

        if (superclassName == null)
            return false;

        // superclassName from ClassRegistry is already qualified; from CurrentSuperclassName it's simple.
        // ResolveClassName is idempotent for already-qualified names, so always safe to call.
        string resolvedSuperName = _ctx.ResolveClassName(superclassName);

        // Look up the method directly on the superclass (not walking up — that's what
        // ResolveInstanceMethod does, but we specifically want the super's version)
        var methodBuilder = _ctx.ResolveInstanceMethod(resolvedSuperName, methodName);
        if (methodBuilder == null)
            return false;

        var methodParams = methodBuilder.GetParameters();

        // Emit: this.Base::method(args) via non-virtual Call
        IL.Emit(OpCodes.Ldarg_0);  // load 'this'

        for (int i = 0; i < arguments.Count; i++)
        {
            EmitExpression(arguments[i]);
            if (i < methodParams.Length)
            {
                EmitConversionForParameter(arguments[i], methodParams[i].ParameterType);
            }
            else
            {
                EmitBoxIfNeeded(arguments[i]);
            }
        }

        // Pad missing optional arguments
        for (int i = arguments.Count; i < methodParams.Length; i++)
        {
            EmitDefaultForType(methodParams[i].ParameterType);
        }

        // Use Call (NOT Callvirt) to bypass virtual dispatch
        IL.Emit(OpCodes.Call, methodBuilder);
        SetStackUnknown();
        return true;
    }

    /// <summary>
    /// Emits a super() constructor call with proper argument handling and generic type support.
    /// </summary>
    /// <param name="parentCtor">The parent class constructor to call.</param>
    /// <param name="arguments">The arguments to pass to the constructor.</param>
    private void EmitSuperConstructorCall(ConstructorBuilder parentCtor, List<Expr> arguments)
    {
        // Load this
        IL.Emit(OpCodes.Ldarg_0);

        // Load arguments with proper type conversions
        var ctorParams = parentCtor.GetParameters();
        for (int i = 0; i < arguments.Count; i++)
        {
            EmitExpression(arguments[i]);
            if (i < ctorParams.Length)
            {
                EmitConversionForParameter(arguments[i], ctorParams[i].ParameterType);
            }
            else
            {
                EmitBoxIfNeeded(arguments[i]);
            }
        }

        // Pad missing optional arguments with appropriate default values
        for (int i = arguments.Count; i < ctorParams.Length; i++)
        {
            EmitDefaultForType(ctorParams[i].ParameterType);
        }

        // Call parent constructor
        // Handle generic superclass with type arguments (e.g., extends Box<string>)
        ConstructorInfo ctorToCall = parentCtor;
        Type? baseType = _ctx.CurrentClassBuilder?.BaseType;
        if (baseType != null && baseType.IsGenericType && baseType.IsConstructedGenericType)
        {
            // Get the constructor for the closed generic type
            ctorToCall = TypeBuilder.GetConstructor(baseType, parentCtor);
        }
        IL.Emit(OpCodes.Call, ctorToCall);
        IL.Emit(OpCodes.Ldnull); // constructor call returns undefined
        SetStackUnknown();
    }

    /// <summary>
    /// Emits a Promise instance method call (.then, .catch, .finally).
    /// These methods take callbacks and return a new Promise (Task).
    /// </summary>
    private void EmitPromiseInstanceMethodCall(Expr promise, string methodName, List<Expr> arguments)
    {
        // Emit the promise (should be Task<object?>)
        EmitExpression(promise);
        EmitBoxIfNeeded(promise);

        // Cast to Task<object?> if needed
        IL.Emit(OpCodes.Castclass, typeof(Task<object?>));

        switch (methodName)
        {
            case "then":
                // promise.then(onFulfilled?, onRejected?)
                // PromiseThen(Task<object?> promise, object? onFulfilled, object? onRejected)

                // onFulfilled callback (optional)
                if (arguments.Count > 0)
                {
                    EmitExpression(arguments[0]);
                    EmitBoxIfNeeded(arguments[0]);
                }
                else
                {
                    IL.Emit(OpCodes.Ldnull);
                }

                // onRejected callback (optional)
                if (arguments.Count > 1)
                {
                    EmitExpression(arguments[1]);
                    EmitBoxIfNeeded(arguments[1]);
                }
                else
                {
                    IL.Emit(OpCodes.Ldnull);
                }

                IL.Emit(OpCodes.Call, _ctx.Runtime!.PromiseThen);
                break;

            case "catch":
                // promise.catch(onRejected)
                // PromiseCatch(Task<object?> promise, object? onRejected)

                if (arguments.Count > 0)
                {
                    EmitExpression(arguments[0]);
                    EmitBoxIfNeeded(arguments[0]);
                }
                else
                {
                    IL.Emit(OpCodes.Ldnull);
                }

                IL.Emit(OpCodes.Call, _ctx.Runtime!.PromiseCatch);
                break;

            case "finally":
                // promise.finally(onFinally)
                // PromiseFinally(Task<object?> promise, object? onFinally)

                if (arguments.Count > 0)
                {
                    EmitExpression(arguments[0]);
                    EmitBoxIfNeeded(arguments[0]);
                }
                else
                {
                    IL.Emit(OpCodes.Ldnull);
                }

                IL.Emit(OpCodes.Call, _ctx.Runtime!.PromiseFinally);
                break;

            default:
                // Unknown method - just return the promise unchanged
                break;
        }

        SetStackUnknown();
    }

    /// <summary>
    /// Tries to emit IL for process.stdin.read(), process.stdout.write(), process.stderr.write() calls.
    /// Returns true if the call was handled.
    /// </summary>
    private bool TryEmitProcessStreamCall(Expr.Call c)
    {
        // Pattern: process.stdin.read(), process.stdout.write("data"), process.stderr.write("data")
        // c.Callee is Expr.Get { Object: Expr.Get { Object: Expr.Variable("process"), Name: "stdin/stdout/stderr" }, Name: "read/write" }

        if (c.Callee is not Expr.Get methodGet)
            return false;

        if (methodGet.Object is not Expr.Get streamGet)
            return false;

        if (streamGet.Object is not Expr.Variable processVar || processVar.Name.Lexeme != "process")
            return false;

        string streamName = streamGet.Name.Lexeme;
        string methodName = methodGet.Name.Lexeme;

        switch (streamName)
        {
            case "stdin" when methodName == "read":
                IL.Emit(OpCodes.Call, _ctx.Runtime!.StdinRead);
                SetStackUnknown();
                return true;

            case "stdout" when methodName == "write":
                if (c.Arguments.Count > 0)
                {
                    EmitExpression(c.Arguments[0]);
                    EmitBoxIfNeeded(c.Arguments[0]);
                }
                else
                {
                    IL.Emit(OpCodes.Ldstr, "");
                }
                IL.Emit(OpCodes.Call, _ctx.Runtime!.StdoutWrite);
                SetStackUnknown();
                return true;

            case "stderr" when methodName == "write":
                if (c.Arguments.Count > 0)
                {
                    EmitExpression(c.Arguments[0]);
                    EmitBoxIfNeeded(c.Arguments[0]);
                }
                else
                {
                    IL.Emit(OpCodes.Ldstr, "");
                }
                IL.Emit(OpCodes.Call, _ctx.Runtime!.StderrWrite);
                SetStackUnknown();
                return true;

            default:
                return false;
        }
    }
}
