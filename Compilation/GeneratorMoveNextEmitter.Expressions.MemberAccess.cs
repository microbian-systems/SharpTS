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
}
