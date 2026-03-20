using System.Reflection;
using System.Reflection.Emit;
using SharpTS.Parsing;

namespace SharpTS.Compilation;

public partial class GeneratorMoveNextEmitter
{
    protected override void EmitCall(Expr.Call c)
    {
        // Handle console.log specially (handles both Variable and Get patterns)
        if (_helpers.TryEmitConsoleLog(c,
            arg => { EmitExpression(arg); EnsureBoxed(); },
            _ctx!.Runtime!.ConsoleLog,
            _ctx!.Runtime!.ConsoleLogMultiple))
        {
            return;
        }

        // Direct function call
        if (c.Callee is Expr.Variable funcVar && _ctx!.Functions.TryGetValue(_ctx.ResolveFunctionName(funcVar.Name.Lexeme), out var funcMethod))
        {
            var parameters = funcMethod.GetParameters();
            for (int i = 0; i < parameters.Length; i++)
            {
                if (i < c.Arguments.Count)
                {
                    EmitExpression(c.Arguments[i]);
                    EnsureBoxed();
                }
                else
                {
                    _il.Emit(OpCodes.Ldnull);
                }
            }
            _il.Emit(OpCodes.Call, funcMethod);
            SetStackUnknown();
            return;
        }

        // Method call: obj.method(args)
        if (c.Callee is Expr.Get methodGet)
        {
            var methodName = methodGet.Name.Lexeme;
            var arguments = c.Arguments;

            // Check compile-time type to use optimized emitters
            var objType = _ctx!.TypeMap?.Get(methodGet.Object);

            // Handle Map methods
            if (objType is TypeSystem.TypeInfo.Map)
            {
                EmitMapMethodCall(methodGet.Object, methodName, arguments);
                return;
            }

            // Handle Set methods
            if (objType is TypeSystem.TypeInfo.Set)
            {
                EmitSetMethodCall(methodGet.Object, methodName, arguments);
                return;
            }

            // Fallback for Map/Set methods when type isn't known at compile time
            if (methodName is "get" or "set" or "has" or "delete" or "clear" or "keys" or "values" or "entries" or "forEach" or "add")
            {
                EmitMapSetMethodCall(methodGet.Object, methodName, arguments);
                return;
            }
        }

        // Generic call through runtime
        EmitExpression(c.Callee);
        EnsureBoxed();

        _il.Emit(OpCodes.Ldc_I4, c.Arguments.Count);
        _il.Emit(OpCodes.Newarr, typeof(object));
        for (int i = 0; i < c.Arguments.Count; i++)
        {
            _il.Emit(OpCodes.Dup);
            _il.Emit(OpCodes.Ldc_I4, i);
            EmitExpression(c.Arguments[i]);
            EnsureBoxed();
            _il.Emit(OpCodes.Stelem_Ref);
        }

        _il.Emit(OpCodes.Call, _ctx!.Runtime!.InvokeValue);
        SetStackUnknown();
    }

    /// <summary>
    /// Emits a Map method call when the receiver is known to be a Map at compile time.
    /// </summary>
    private void EmitMapMethodCall(Expr receiver, string methodName, List<Expr> arguments)
    {
        EmitExpression(receiver);
        EnsureBoxed();

        switch (methodName)
        {
            case "set":
                if (arguments.Count >= 2)
                {
                    EmitExpression(arguments[0]);
                    EnsureBoxed();
                    EmitExpression(arguments[1]);
                    EnsureBoxed();
                }
                else if (arguments.Count == 1)
                {
                    EmitExpression(arguments[0]);
                    EnsureBoxed();
                    _il.Emit(OpCodes.Ldnull);
                }
                else
                {
                    _il.Emit(OpCodes.Ldnull);
                    _il.Emit(OpCodes.Ldnull);
                }
                _il.Emit(OpCodes.Call, _ctx!.Runtime!.MapSet);
                SetStackUnknown();
                break;

            case "get":
                if (arguments.Count >= 1)
                {
                    EmitExpression(arguments[0]);
                    EnsureBoxed();
                }
                else
                {
                    _il.Emit(OpCodes.Ldnull);
                }
                _il.Emit(OpCodes.Call, _ctx!.Runtime!.MapGet);
                SetStackUnknown();
                break;

            case "has":
                if (arguments.Count >= 1)
                {
                    EmitExpression(arguments[0]);
                    EnsureBoxed();
                }
                else
                {
                    _il.Emit(OpCodes.Ldnull);
                }
                _il.Emit(OpCodes.Call, _ctx!.Runtime!.MapHas);
                _il.Emit(OpCodes.Box, _types.Boolean);
                SetStackUnknown();
                break;

            case "delete":
                if (arguments.Count >= 1)
                {
                    EmitExpression(arguments[0]);
                    EnsureBoxed();
                }
                else
                {
                    _il.Emit(OpCodes.Ldnull);
                }
                _il.Emit(OpCodes.Call, _ctx!.Runtime!.MapDelete);
                _il.Emit(OpCodes.Box, _types.Boolean);
                SetStackUnknown();
                break;

            case "clear":
                _il.Emit(OpCodes.Call, _ctx!.Runtime!.MapClear);
                _il.Emit(OpCodes.Ldnull);
                SetStackUnknown();
                break;

            case "keys":
                _il.Emit(OpCodes.Call, _ctx!.Runtime!.MapKeys);
                SetStackUnknown();
                break;

            case "values":
                _il.Emit(OpCodes.Call, _ctx!.Runtime!.MapValues);
                SetStackUnknown();
                break;

            case "entries":
                _il.Emit(OpCodes.Call, _ctx!.Runtime!.MapEntries);
                SetStackUnknown();
                break;

            case "forEach":
                if (arguments.Count >= 1)
                {
                    EmitExpression(arguments[0]);
                    EnsureBoxed();
                }
                else
                {
                    _il.Emit(OpCodes.Ldnull);
                }
                _il.Emit(OpCodes.Call, _ctx!.Runtime!.MapForEach);
                _il.Emit(OpCodes.Ldnull);
                SetStackUnknown();
                break;

            default:
                // Unknown method - use generic call
                _il.Emit(OpCodes.Pop); // Pop receiver
                EmitGenericCall(receiver, methodName, arguments);
                break;
        }
    }

    /// <summary>
    /// Emits a Set method call when the receiver is known to be a Set at compile time.
    /// </summary>
    private void EmitSetMethodCall(Expr receiver, string methodName, List<Expr> arguments)
    {
        EmitExpression(receiver);
        EnsureBoxed();

        switch (methodName)
        {
            case "add":
                if (arguments.Count >= 1)
                {
                    EmitExpression(arguments[0]);
                    EnsureBoxed();
                }
                else
                {
                    _il.Emit(OpCodes.Ldnull);
                }
                _il.Emit(OpCodes.Call, _ctx!.Runtime!.SetAdd);
                SetStackUnknown();
                break;

            case "has":
                if (arguments.Count >= 1)
                {
                    EmitExpression(arguments[0]);
                    EnsureBoxed();
                }
                else
                {
                    _il.Emit(OpCodes.Ldnull);
                }
                _il.Emit(OpCodes.Call, _ctx!.Runtime!.SetHas);
                _il.Emit(OpCodes.Box, _types.Boolean);
                SetStackUnknown();
                break;

            case "delete":
                if (arguments.Count >= 1)
                {
                    EmitExpression(arguments[0]);
                    EnsureBoxed();
                }
                else
                {
                    _il.Emit(OpCodes.Ldnull);
                }
                _il.Emit(OpCodes.Call, _ctx!.Runtime!.SetDelete);
                _il.Emit(OpCodes.Box, _types.Boolean);
                SetStackUnknown();
                break;

            case "clear":
                _il.Emit(OpCodes.Call, _ctx!.Runtime!.SetClear);
                _il.Emit(OpCodes.Ldnull);
                SetStackUnknown();
                break;

            case "keys":
            case "values":
                _il.Emit(OpCodes.Call, _ctx!.Runtime!.SetValues);
                SetStackUnknown();
                break;

            case "entries":
                _il.Emit(OpCodes.Call, _ctx!.Runtime!.SetEntries);
                SetStackUnknown();
                break;

            case "forEach":
                if (arguments.Count >= 1)
                {
                    EmitExpression(arguments[0]);
                    EnsureBoxed();
                }
                else
                {
                    _il.Emit(OpCodes.Ldnull);
                }
                _il.Emit(OpCodes.Call, _ctx!.Runtime!.SetForEach);
                _il.Emit(OpCodes.Ldnull);
                SetStackUnknown();
                break;

            default:
                // Unknown method - use generic call
                _il.Emit(OpCodes.Pop); // Pop receiver
                EmitGenericCall(receiver, methodName, arguments);
                break;
        }
    }

    /// <summary>
    /// Emits a generic method call through the runtime.
    /// </summary>
    private void EmitGenericCall(Expr receiver, string methodName, List<Expr> arguments)
    {
        EmitExpression(receiver);
        EnsureBoxed();
        _il.Emit(OpCodes.Ldstr, methodName);
        _il.Emit(OpCodes.Ldc_I4, arguments.Count);
        _il.Emit(OpCodes.Newarr, typeof(object));
        for (int i = 0; i < arguments.Count; i++)
        {
            _il.Emit(OpCodes.Dup);
            _il.Emit(OpCodes.Ldc_I4, i);
            EmitExpression(arguments[i]);
            EnsureBoxed();
            _il.Emit(OpCodes.Stelem_Ref);
        }
        _il.Emit(OpCodes.Call, _ctx!.Runtime!.InvokeMethodValue);
        SetStackUnknown();
    }

    /// <summary>
    /// Emits a Map or Set method call using runtime dispatch.
    /// </summary>
    private void EmitMapSetMethodCall(Expr receiver, string methodName, List<Expr> arguments)
    {
        EmitExpression(receiver);
        EnsureBoxed();

        switch (methodName)
        {
            case "set":
                // Map.set(key, value)
                if (arguments.Count >= 2)
                {
                    EmitExpression(arguments[0]);
                    EnsureBoxed();
                    EmitExpression(arguments[1]);
                    EnsureBoxed();
                    _il.Emit(OpCodes.Call, _ctx!.Runtime!.MapSet);
                }
                else if (arguments.Count == 1)
                {
                    EmitExpression(arguments[0]);
                    EnsureBoxed();
                    _il.Emit(OpCodes.Ldnull);
                    _il.Emit(OpCodes.Call, _ctx!.Runtime!.MapSet);
                }
                else
                {
                    _il.Emit(OpCodes.Ldnull);
                    _il.Emit(OpCodes.Ldnull);
                    _il.Emit(OpCodes.Call, _ctx!.Runtime!.MapSet);
                }
                SetStackUnknown();
                break;

            case "get":
                // Map.get(key)
                if (arguments.Count >= 1)
                {
                    EmitExpression(arguments[0]);
                    EnsureBoxed();
                }
                else
                {
                    _il.Emit(OpCodes.Ldnull);
                }
                _il.Emit(OpCodes.Call, _ctx!.Runtime!.MapGet);
                SetStackUnknown();
                break;

            case "has":
                // Map/Set.has(key)
                if (arguments.Count >= 1)
                {
                    EmitExpression(arguments[0]);
                    EnsureBoxed();
                }
                else
                {
                    _il.Emit(OpCodes.Ldnull);
                }
                _il.Emit(OpCodes.Call, _ctx!.Runtime!.MapHas);
                _il.Emit(OpCodes.Box, _types.Boolean);
                SetStackUnknown();
                break;

            case "delete":
                // Map/Set.delete(key)
                if (arguments.Count >= 1)
                {
                    EmitExpression(arguments[0]);
                    EnsureBoxed();
                }
                else
                {
                    _il.Emit(OpCodes.Ldnull);
                }
                _il.Emit(OpCodes.Call, _ctx!.Runtime!.MapDelete);
                _il.Emit(OpCodes.Box, _types.Boolean);
                SetStackUnknown();
                break;

            case "clear":
                _il.Emit(OpCodes.Call, _ctx!.Runtime!.MapClear);
                _il.Emit(OpCodes.Ldnull);
                SetStackUnknown();
                break;

            case "keys":
                _il.Emit(OpCodes.Call, _ctx!.Runtime!.MapKeys);
                SetStackUnknown();
                break;

            case "values":
                _il.Emit(OpCodes.Call, _ctx!.Runtime!.MapValues);
                SetStackUnknown();
                break;

            case "entries":
                _il.Emit(OpCodes.Call, _ctx!.Runtime!.MapEntries);
                SetStackUnknown();
                break;

            case "forEach":
                if (arguments.Count >= 1)
                {
                    EmitExpression(arguments[0]);
                    EnsureBoxed();
                }
                else
                {
                    _il.Emit(OpCodes.Ldnull);
                }
                _il.Emit(OpCodes.Call, _ctx!.Runtime!.MapForEach);
                _il.Emit(OpCodes.Ldnull);
                SetStackUnknown();
                break;

            case "add":
                // Set.add(value)
                if (arguments.Count >= 1)
                {
                    EmitExpression(arguments[0]);
                    EnsureBoxed();
                }
                else
                {
                    _il.Emit(OpCodes.Ldnull);
                }
                _il.Emit(OpCodes.Call, _ctx!.Runtime!.SetAdd);
                SetStackUnknown();
                break;

            default:
                // Fallback to generic call
                _il.Emit(OpCodes.Ldstr, methodName);
                _il.Emit(OpCodes.Ldc_I4, arguments.Count);
                _il.Emit(OpCodes.Newarr, typeof(object));
                for (int i = 0; i < arguments.Count; i++)
                {
                    _il.Emit(OpCodes.Dup);
                    _il.Emit(OpCodes.Ldc_I4, i);
                    EmitExpression(arguments[i]);
                    EnsureBoxed();
                    _il.Emit(OpCodes.Stelem_Ref);
                }
                _il.Emit(OpCodes.Call, _ctx!.Runtime!.InvokeMethodValue);
                SetStackUnknown();
                break;
        }
    }

    protected override void EmitSet(Expr.Set s)
    {
        EmitExpression(s.Object);
        EnsureBoxed();
        _il.Emit(OpCodes.Ldstr, s.Name.Lexeme);
        EmitExpression(s.Value);
        EnsureBoxed();

        _il.Emit(OpCodes.Dup);
        var resultTemp = _il.DeclareLocal(typeof(object));
        _il.Emit(OpCodes.Stloc, resultTemp);

        _il.Emit(OpCodes.Call, _ctx!.Runtime!.SetProperty);
        _il.Emit(OpCodes.Ldloc, resultTemp);
        SetStackUnknown();
    }

    protected override void EmitGetPrivate(Expr.GetPrivate gp)
    {
        // Get the field name without the # prefix
        string fieldName = gp.Name.Lexeme;
        if (fieldName.StartsWith('#'))
            fieldName = fieldName[1..];

        string? className = _ctx?.CurrentClassName;
        if (className == null)
        {
            _il.Emit(OpCodes.Ldstr, $"Cannot access private field '#{fieldName}' - class context not available");
            _il.Emit(OpCodes.Newobj, Types.InvalidOperationExceptionCtorString);
            _il.Emit(OpCodes.Throw);
            return;
        }

        if (gp.Object is Expr.Variable classVar &&
            classVar.Name.Lexeme == _ctx!.CurrentClassName!.Split('.').Last().Split('_').Last() &&
            _ctx!.ClassRegistry!.TryGetStaticPrivateField(className, fieldName, out var staticField))
        {
            _il.Emit(OpCodes.Ldsfld, staticField!);
            SetStackUnknown();
            return;
        }

        var storageField = _ctx!.ClassRegistry!.GetPrivateFieldStorage(className);
        if (storageField != null)
        {
            var cwtType = typeof(System.Runtime.CompilerServices.ConditionalWeakTable<,>)
                .MakeGenericType(typeof(object), typeof(Dictionary<string, object?>));
            var dictType = typeof(Dictionary<string, object?>);
            var dictLocal = _il.DeclareLocal(dictType);

            _il.Emit(OpCodes.Ldsfld, storageField);
            EmitExpression(gp.Object);
            EnsureBoxed();
            _il.Emit(OpCodes.Ldloca, dictLocal);
            var tryGetValueMethod = cwtType.GetMethod("TryGetValue", [typeof(object), dictType.MakeByRefType()])!;
            _il.Emit(OpCodes.Callvirt, tryGetValueMethod);

            var successLabel = _il.DefineLabel();
            _il.Emit(OpCodes.Brtrue, successLabel);
            _il.Emit(OpCodes.Ldstr, $"TypeError: Cannot read private member #{fieldName} from an object whose class did not declare it");
            _il.Emit(OpCodes.Newobj, Types.ExceptionCtorString);
            _il.Emit(OpCodes.Throw);
            _il.MarkLabel(successLabel);

            _il.Emit(OpCodes.Ldloc, dictLocal);
            _il.Emit(OpCodes.Ldstr, fieldName);
            _il.Emit(OpCodes.Callvirt, dictType.GetMethod("get_Item", [typeof(string)])!);
            SetStackUnknown();
            return;
        }

        if (_ctx!.ClassRegistry!.TryGetStaticPrivateField(className, fieldName, out var fallbackStaticField))
        {
            _il.Emit(OpCodes.Ldsfld, fallbackStaticField!);
            SetStackUnknown();
            return;
        }

        _il.Emit(OpCodes.Ldstr, $"Private field '#{fieldName}' not found in class '{className}'");
        _il.Emit(OpCodes.Newobj, Types.ExceptionCtorString);
        _il.Emit(OpCodes.Throw);
    }

    protected override void EmitSetPrivate(Expr.SetPrivate sp)
    {
        // Get the field name without the # prefix
        string fieldName = sp.Name.Lexeme;
        if (fieldName.StartsWith('#'))
            fieldName = fieldName[1..];

        string? className = _ctx?.CurrentClassName;
        if (className == null)
        {
            _il.Emit(OpCodes.Ldstr, $"Cannot write private field '#{fieldName}' - class context not available");
            _il.Emit(OpCodes.Newobj, Types.InvalidOperationExceptionCtorString);
            _il.Emit(OpCodes.Throw);
            return;
        }

        if (sp.Object is Expr.Variable classVar &&
            classVar.Name.Lexeme == _ctx!.CurrentClassName!.Split('.').Last().Split('_').Last() &&
            _ctx!.ClassRegistry!.TryGetStaticPrivateField(className, fieldName, out var staticField))
        {
            EmitExpression(sp.Value);
            EnsureBoxed();
            _il.Emit(OpCodes.Dup);
            _il.Emit(OpCodes.Stsfld, staticField!);
            SetStackUnknown();
            return;
        }

        var storageField = _ctx!.ClassRegistry!.GetPrivateFieldStorage(className);
        if (storageField != null)
        {
            var cwtType = typeof(System.Runtime.CompilerServices.ConditionalWeakTable<,>)
                .MakeGenericType(typeof(object), typeof(Dictionary<string, object?>));
            var dictType = typeof(Dictionary<string, object?>);
            var dictLocal = _il.DeclareLocal(dictType);
            var valueLocal = _il.DeclareLocal(typeof(object));

            _il.Emit(OpCodes.Ldsfld, storageField);
            EmitExpression(sp.Object);
            EnsureBoxed();
            _il.Emit(OpCodes.Ldloca, dictLocal);
            var tryGetValueMethod = cwtType.GetMethod("TryGetValue", [typeof(object), dictType.MakeByRefType()])!;
            _il.Emit(OpCodes.Callvirt, tryGetValueMethod);

            var successLabel = _il.DefineLabel();
            _il.Emit(OpCodes.Brtrue, successLabel);
            _il.Emit(OpCodes.Ldstr, $"TypeError: Cannot write private member #{fieldName} to an object whose class did not declare it");
            _il.Emit(OpCodes.Newobj, Types.ExceptionCtorString);
            _il.Emit(OpCodes.Throw);
            _il.MarkLabel(successLabel);

            EmitExpression(sp.Value);
            EnsureBoxed();
            _il.Emit(OpCodes.Stloc, valueLocal);
            _il.Emit(OpCodes.Ldloc, dictLocal);
            _il.Emit(OpCodes.Ldstr, fieldName);
            _il.Emit(OpCodes.Ldloc, valueLocal);
            _il.Emit(OpCodes.Callvirt, dictType.GetMethod("set_Item", [typeof(string), typeof(object)])!);
            _il.Emit(OpCodes.Ldloc, valueLocal);
            SetStackUnknown();
            return;
        }

        if (_ctx!.ClassRegistry!.TryGetStaticPrivateField(className, fieldName, out var fallbackStaticField))
        {
            EmitExpression(sp.Value);
            EnsureBoxed();
            _il.Emit(OpCodes.Dup);
            _il.Emit(OpCodes.Stsfld, fallbackStaticField!);
            SetStackUnknown();
            return;
        }

        _il.Emit(OpCodes.Ldstr, $"Private field '#{fieldName}' not found in class '{className}'");
        _il.Emit(OpCodes.Newobj, Types.ExceptionCtorString);
        _il.Emit(OpCodes.Throw);
    }

    protected override void EmitCallPrivate(Expr.CallPrivate cp)
    {
        // Get the method name without the # prefix
        string methodName = cp.Name.Lexeme;
        if (methodName.StartsWith('#'))
            methodName = methodName[1..];

        string? className = _ctx?.CurrentClassName;
        if (className == null)
        {
            _il.Emit(OpCodes.Ldstr, $"Cannot call private method '#{methodName}' - class context not available");
            _il.Emit(OpCodes.Newobj, Types.InvalidOperationExceptionCtorString);
            _il.Emit(OpCodes.Throw);
            return;
        }

        if (cp.Object is Expr.Variable classVar &&
            classVar.Name.Lexeme == _ctx!.CurrentClassName!.Split('.').Last().Split('_').Last() &&
            _ctx!.ClassRegistry!.TryGetStaticPrivateMethod(className, methodName, out var staticMethod))
        {
            foreach (var arg in cp.Arguments)
            {
                EmitExpression(arg);
                EnsureBoxed();
            }
            _il.Emit(OpCodes.Call, staticMethod!);
            SetStackUnknown();
            return;
        }

        if (_ctx!.ClassRegistry!.TryGetPrivateMethod(className, methodName, out var instanceMethod))
        {
            var storageField = _ctx!.ClassRegistry!.GetPrivateFieldStorage(className);
            if (storageField != null)
            {
                var cwtType = typeof(System.Runtime.CompilerServices.ConditionalWeakTable<,>)
                    .MakeGenericType(typeof(object), typeof(Dictionary<string, object?>));
                var dictType = typeof(Dictionary<string, object?>);
                var objLocal = _il.DeclareLocal(typeof(object));
                var dictLocal = _il.DeclareLocal(dictType);

                EmitExpression(cp.Object);
                EnsureBoxed();
                _il.Emit(OpCodes.Stloc, objLocal);

                _il.Emit(OpCodes.Ldsfld, storageField);
                _il.Emit(OpCodes.Ldloc, objLocal);
                _il.Emit(OpCodes.Ldloca, dictLocal);
                var tryGetValueMethod = cwtType.GetMethod("TryGetValue", [typeof(object), dictType.MakeByRefType()])!;
                _il.Emit(OpCodes.Callvirt, tryGetValueMethod);

                var validLabel = _il.DefineLabel();
                _il.Emit(OpCodes.Brtrue, validLabel);
                _il.Emit(OpCodes.Ldstr, $"TypeError: Cannot call private method #{methodName} on an object whose class did not declare it");
                _il.Emit(OpCodes.Newobj, Types.ExceptionCtorString);
                _il.Emit(OpCodes.Throw);
                _il.MarkLabel(validLabel);

                _il.Emit(OpCodes.Ldloc, objLocal);
                if (_ctx.CurrentClassBuilder != null)
                    _il.Emit(OpCodes.Castclass, _ctx.CurrentClassBuilder);

                foreach (var arg in cp.Arguments)
                {
                    EmitExpression(arg);
                    EnsureBoxed();
                }

                _il.Emit(OpCodes.Callvirt, instanceMethod!);
                SetStackUnknown();
                return;
            }
            else
            {
                // No private field storage (class has private methods but no private fields)
                // Skip brand check - just emit the call directly
                EmitExpression(cp.Object);
                EnsureBoxed();
                if (_ctx.CurrentClassBuilder != null)
                    _il.Emit(OpCodes.Castclass, _ctx.CurrentClassBuilder);

                foreach (var arg in cp.Arguments)
                {
                    EmitExpression(arg);
                    EnsureBoxed();
                }

                _il.Emit(OpCodes.Callvirt, instanceMethod!);
                SetStackUnknown();
                return;
            }
        }

        _il.Emit(OpCodes.Ldstr, $"Private method '#{methodName}' not found in class '{className}'");
        _il.Emit(OpCodes.Newobj, Types.ExceptionCtorString);
        _il.Emit(OpCodes.Throw);
    }

    /// <summary>
    /// Emits the typeof() for the declaring class containing the private member.
    /// </summary>
    private void EmitDeclaringClassType()
    {
        if (_ctx?.CurrentClassBuilder != null)
        {
            _il.Emit(OpCodes.Ldtoken, _ctx.CurrentClassBuilder);
            _il.Emit(OpCodes.Call, Types.TypeGetTypeFromHandle);
        }
        else
        {
            // Should not happen if called correctly - throw at runtime
            _il.Emit(OpCodes.Ldstr, "Cannot access private members outside of class context");
            _il.Emit(OpCodes.Newobj, Types.InvalidOperationExceptionCtorString);
            _il.Emit(OpCodes.Throw);
        }
    }

    /// <summary>
    /// Emits an object[] array from argument expressions.
    /// </summary>
    private void EmitArgumentArray(List<Expr> arguments)
    {
        _il.Emit(OpCodes.Ldc_I4, arguments.Count);
        _il.Emit(OpCodes.Newarr, typeof(object));
        for (int i = 0; i < arguments.Count; i++)
        {
            _il.Emit(OpCodes.Dup);
            _il.Emit(OpCodes.Ldc_I4, i);
            EmitExpression(arguments[i]);
            EnsureBoxed();
            _il.Emit(OpCodes.Stelem_Ref);
        }
    }

    protected override void EmitNew(Expr.New n)
    {
        // Extract qualified name from callee expression
        var (namespaceParts, className) = ExtractQualifiedNameFromCallee(n.Callee);

        // Handle built-in types first (Set, Map, Date, Error, etc.)
        if (namespaceParts.Count == 0 && TryEmitBuiltInConstructor(className, n.Arguments))
            return;

        // Handle Intl.* constructors
        if (TryEmitIntlConstructor(namespaceParts, className, n.Arguments))
            return;

        // Handle module-qualified constructors (e.g., new util.TextEncoder())
        if (TryEmitModuleQualifiedConstructor(namespaceParts, className, n.Arguments))
            return;

        // Resolve class name (may be qualified for namespace classes or multi-module compilation)
        string resolvedClassName;
        if (namespaceParts.Count > 0)
        {
            // Build qualified name for namespace classes: Namespace_SubNs_ClassName
            string nsPath = string.Join("_", namespaceParts);
            resolvedClassName = $"{nsPath}_{className}";
        }
        else
        {
            resolvedClassName = _ctx!.ResolveClassName(className);
        }

        var ctorBuilder = _ctx!.ClassRegistry?.GetConstructorByQualifiedName(resolvedClassName);
        if (_ctx.Classes.TryGetValue(resolvedClassName, out var typeBuilder) && ctorBuilder != null)
        {
            int expectedParamCount = ctorBuilder.GetParameters().Length;

            foreach (var arg in n.Arguments)
            {
                EmitExpression(arg);
                EnsureBoxed();
            }

            for (int i = n.Arguments.Count; i < expectedParamCount; i++)
            {
                _il.Emit(OpCodes.Ldnull);
            }

            _il.Emit(OpCodes.Newobj, ctorBuilder);
            SetStackUnknown();
        }
        else
        {
            // Fallback: try to load via resolver (handles parameters, hoisted variables, etc.)
            var stackType = _resolver?.TryLoadVariable(className);
            if (stackType != null)
            {
                // Variable loaded - build args array for Activator.CreateInstance
                _il.Emit(OpCodes.Ldc_I4, n.Arguments.Count);
                _il.Emit(OpCodes.Newarr, _ctx!.Types.Object);

                for (int i = 0; i < n.Arguments.Count; i++)
                {
                    _il.Emit(OpCodes.Dup);
                    _il.Emit(OpCodes.Ldc_I4, i);
                    EmitExpression(n.Arguments[i]);
                    EnsureBoxed();
                    _il.Emit(OpCodes.Stelem_Ref);
                }

                var createInstanceMethod = _ctx.Types.GetMethod(_ctx.Types.Activator, "CreateInstance", _ctx.Types.Type, _ctx.Types.ObjectArray);
                _il.Emit(OpCodes.Call, createInstanceMethod!);
                SetStackUnknown();
            }
            else
            {
                _il.Emit(OpCodes.Ldnull);
                SetStackType(StackType.Null);
            }
        }
    }

    protected override void EmitThis()
    {
        if (_builder.ThisField != null)
        {
            _il.Emit(OpCodes.Ldarg_0);
            _il.Emit(OpCodes.Ldfld, _builder.ThisField);
            SetStackUnknown();
        }
        else
        {
            _il.Emit(OpCodes.Ldnull);
            SetStackType(StackType.Null);
        }
    }

    protected override void EmitArrayLiteral(Expr.ArrayLiteral a)
    {
        _il.Emit(OpCodes.Ldc_I4, a.Elements.Count);
        _il.Emit(OpCodes.Newarr, typeof(object));

        for (int i = 0; i < a.Elements.Count; i++)
        {
            _il.Emit(OpCodes.Dup);
            _il.Emit(OpCodes.Ldc_I4, i);
            EmitExpression(a.Elements[i]);
            EnsureBoxed();
            _il.Emit(OpCodes.Stelem_Ref);
        }

        _il.Emit(OpCodes.Call, _ctx!.Runtime!.CreateArray);
        SetStackUnknown();
    }

    protected override void EmitObjectLiteral(Expr.ObjectLiteral o)
    {
        _il.Emit(OpCodes.Newobj, Types.DictionaryStringObjectCtor);

        foreach (var prop in o.Properties)
        {
            _il.Emit(OpCodes.Dup);
            EmitStaticPropertyKey(prop.Key!);
            EmitExpression(prop.Value);
            EnsureBoxed();
            _il.Emit(OpCodes.Callvirt, Types.DictionaryStringObjectSetItem);
        }

        _il.Emit(OpCodes.Call, _ctx!.Runtime!.CreateObject);
        SetStackUnknown();
    }

    private void EmitStaticPropertyKey(Expr.PropertyKey key)
    {
        switch (key)
        {
            case Expr.IdentifierKey ik:
                _il.Emit(OpCodes.Ldstr, ik.Name.Lexeme);
                break;
            case Expr.LiteralKey lk when lk.Literal.Type == TokenType.STRING:
                _il.Emit(OpCodes.Ldstr, (string)lk.Literal.Literal!);
                break;
            case Expr.LiteralKey lk when lk.Literal.Type == TokenType.NUMBER:
                _il.Emit(OpCodes.Ldstr, lk.Literal.Literal!.ToString()!);
                break;
            default:
                _il.Emit(OpCodes.Ldstr, "");
                break;
        }
    }

    protected override void EmitGetIndex(Expr.GetIndex gi)
    {
        EmitExpression(gi.Object);
        EnsureBoxed();
        EmitExpression(gi.Index);
        EnsureBoxed();
        _il.Emit(OpCodes.Call, _ctx!.Runtime!.GetIndex);
        SetStackUnknown();
    }

    protected override void EmitSetIndex(Expr.SetIndex si)
    {
        EmitExpression(si.Object);
        EnsureBoxed();
        EmitExpression(si.Index);
        EnsureBoxed();
        EmitExpression(si.Value);
        EnsureBoxed();
        _il.Emit(OpCodes.Call, _ctx!.Runtime!.SetIndex);
        SetStackUnknown();
    }

    protected override void EmitTemplateLiteral(Expr.TemplateLiteral tl)
    {
        if (tl.Strings.Count == 0 && tl.Expressions.Count == 0)
        {
            _il.Emit(OpCodes.Ldstr, "");
            SetStackType(StackType.String);
            return;
        }

        _il.Emit(OpCodes.Ldstr, tl.Strings[0]);

        for (int i = 0; i < tl.Expressions.Count; i++)
        {
            EmitExpression(tl.Expressions[i]);
            EnsureBoxed();
            _il.Emit(OpCodes.Call, _ctx!.Runtime!.Stringify);
            _il.Emit(OpCodes.Call, Types.StringConcat2);

            if (i + 1 < tl.Strings.Count)
            {
                _il.Emit(OpCodes.Ldstr, tl.Strings[i + 1]);
                _il.Emit(OpCodes.Call, Types.StringConcat2);
            }
        }
        SetStackType(StackType.String);
    }

    protected override void EmitTaggedTemplateLiteral(Expr.TaggedTemplateLiteral ttl)
    {
        EmitExpression(ttl.Tag);
        EnsureBoxed();

        _il.Emit(OpCodes.Ldc_I4, ttl.CookedStrings.Count);
        _il.Emit(OpCodes.Newarr, typeof(object));
        for (int i = 0; i < ttl.CookedStrings.Count; i++)
        {
            _il.Emit(OpCodes.Dup);
            _il.Emit(OpCodes.Ldc_I4, i);
            if (ttl.CookedStrings[i] != null)
                _il.Emit(OpCodes.Ldstr, ttl.CookedStrings[i]!);
            else
                _il.Emit(OpCodes.Ldnull);
            _il.Emit(OpCodes.Stelem_Ref);
        }

        _il.Emit(OpCodes.Ldc_I4, ttl.RawStrings.Count);
        _il.Emit(OpCodes.Newarr, typeof(string));
        for (int i = 0; i < ttl.RawStrings.Count; i++)
        {
            _il.Emit(OpCodes.Dup);
            _il.Emit(OpCodes.Ldc_I4, i);
            _il.Emit(OpCodes.Ldstr, ttl.RawStrings[i]);
            _il.Emit(OpCodes.Stelem_Ref);
        }

        _il.Emit(OpCodes.Ldc_I4, ttl.Expressions.Count);
        _il.Emit(OpCodes.Newarr, typeof(object));
        for (int i = 0; i < ttl.Expressions.Count; i++)
        {
            _il.Emit(OpCodes.Dup);
            _il.Emit(OpCodes.Ldc_I4, i);
            EmitExpression(ttl.Expressions[i]);
            EnsureBoxed();
            _il.Emit(OpCodes.Stelem_Ref);
        }

        _il.Emit(OpCodes.Call, _ctx!.Runtime!.InvokeTaggedTemplate);
        SetStackUnknown();
    }

}
