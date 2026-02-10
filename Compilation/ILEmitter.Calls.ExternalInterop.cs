using System.Reflection;
using System.Reflection.Emit;
using SharpTS.Diagnostics.Exceptions;
using SharpTS.Parsing;

namespace SharpTS.Compilation;

/// <summary>
/// External .NET type interop methods for the IL emitter.
/// </summary>
public partial class ILEmitter
{
    /// <summary>
    /// Emits an instance method call on an external .NET type (via @DotNetType).
    /// </summary>
    private void EmitExternalInstanceMethodCall(Expr receiver, Type externalType, string methodName, List<Expr> arguments)
    {
        // Try to find the instance method - first with original name, then with PascalCase
        string pascalMethodName = NamingConventions.ToPascalCase(methodName);
        var methods = externalType.GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Where(m => m.Name == methodName || m.Name == pascalMethodName)
            .ToArray();

        if (methods.Length == 0)
        {
            throw new CompileException($"Instance method '{methodName}' (or '{pascalMethodName}') not found on external type {externalType.FullName}");
        }

        // Use type-aware overload resolution
        var resolver = new ExternalMethodResolver(_ctx.TypeMap, _ctx.Types);
        var candidate = resolver.ResolveMethod(methods, arguments);
        var method = (MethodInfo)candidate.Method;

        // Emit receiver and prepare for member access
        EmitExpression(receiver);
        EmitBoxIfNeeded(receiver);
        bool isValueType = PrepareReceiverForMemberAccess(externalType);

        // Emit arguments with type conversion (handles params arrays)
        EmitExternalCallArguments(arguments, method, candidate);

        // Emit the call - use Call for value types (with address), Callvirt for reference types
        IL.Emit(isValueType ? OpCodes.Call : OpCodes.Callvirt, method);

        // Handle return value
        if (method.ReturnType == typeof(void))
        {
            IL.Emit(OpCodes.Ldnull); // void returns undefined
        }
        else
        {
            BoxResultIfValueType(method.ReturnType);
        }
        SetStackUnknown();
    }

    /// <summary>
    /// Emits construction of an external .NET type (via @DotNetType).
    /// </summary>
    private void EmitExternalTypeConstruction(Type externalType, List<Expr> arguments)
    {
        // Find a constructor matching the argument count
        var ctors = externalType.GetConstructors(BindingFlags.Public | BindingFlags.Instance);

        if (ctors.Length == 0)
        {
            throw new CompileException($"No public constructors found on external type {externalType.FullName}");
        }

        // Use type-aware overload resolution
        var resolver = new ExternalMethodResolver(_ctx.TypeMap, _ctx.Types);
        var candidate = resolver.ResolveConstructor(ctors, arguments);
        var ctor = (ConstructorInfo)candidate.Method;

        // Emit arguments with type conversion (handles params arrays)
        EmitExternalCallArguments(arguments, ctor, candidate);

        // Emit newobj instruction
        IL.Emit(OpCodes.Newobj, ctor);
        SetStackUnknown();
    }

    /// <summary>
    /// Emits a static method call on an external .NET type (via @DotNetType).
    /// </summary>
    private void EmitExternalStaticMethodCall(Type externalType, string methodName, List<Expr> arguments)
    {
        // Try to find the static method - first with original name, then with PascalCase
        string pascalMethodName = NamingConventions.ToPascalCase(methodName);
        var methods = externalType.GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Where(m => m.Name == methodName || m.Name == pascalMethodName)
            .ToArray();

        if (methods.Length == 0)
        {
            throw new CompileException($"Static method '{methodName}' (or '{pascalMethodName}') not found on external type {externalType.FullName}");
        }

        // Use type-aware overload resolution
        var resolver = new ExternalMethodResolver(_ctx.TypeMap, _ctx.Types);
        var candidate = resolver.ResolveMethod(methods, arguments);
        var method = (MethodInfo)candidate.Method;

        // Emit arguments with type conversion (handles params arrays)
        EmitExternalCallArguments(arguments, method, candidate);

        // Emit the static call
        IL.Emit(OpCodes.Call, method);

        // Handle return value
        if (method.ReturnType == typeof(void))
        {
            IL.Emit(OpCodes.Ldnull); // void returns undefined
        }
        else if (method.ReturnType.IsValueType)
        {
            IL.Emit(OpCodes.Box, method.ReturnType);
        }
        SetStackUnknown();
    }

    /// <summary>
    /// Emits arguments for an external method call, handling params arrays if present.
    /// </summary>
    private void EmitExternalCallArguments(List<Expr> arguments, MethodBase method, MethodCandidate candidate)
    {
        var parameters = method.GetParameters();

        if (candidate.ParamsStartIndex < 0)
        {
            // No params array - emit arguments normally
            for (int i = 0; i < arguments.Count; i++)
            {
                EmitExpression(arguments[i]);
                EmitExternalTypeConversion(parameters[i].ParameterType);
            }
        }
        else
        {
            // Emit regular (non-params) arguments first
            for (int i = 0; i < candidate.ParamsStartIndex; i++)
            {
                EmitExpression(arguments[i]);
                EmitExternalTypeConversion(parameters[i].ParameterType);
            }

            // Create and fill the params array
            var paramsParam = parameters[candidate.ParamsStartIndex];
            var elementType = paramsParam.ParameterType.GetElementType()!;
            int paramsCount = arguments.Count - candidate.ParamsStartIndex;

            // Emit array creation: new T[paramsCount]
            IL.Emit(OpCodes.Ldc_I4, paramsCount);
            IL.Emit(OpCodes.Newarr, elementType);

            // Fill array elements
            bool isObjectArray = elementType == _ctx.Types.Object || elementType == typeof(object);
            for (int i = 0; i < paramsCount; i++)
            {
                IL.Emit(OpCodes.Dup);                    // Duplicate array reference
                IL.Emit(OpCodes.Ldc_I4, i);              // Push index
                EmitExpression(arguments[candidate.ParamsStartIndex + i]);

                // For object[], box value types but leave reference types as-is
                if (isObjectArray)
                {
                    // Box unboxed value types on the stack (numbers, booleans)
                    if (_stackType == StackType.Double)
                    {
                        IL.Emit(OpCodes.Box, _ctx.Types.Double);
                    }
                    else if (_stackType == StackType.Boolean)
                    {
                        IL.Emit(OpCodes.Box, _ctx.Types.Boolean);
                    }
                    // Reference types (strings, objects) are already boxed, no action needed
                }
                else
                {
                    EmitExternalTypeConversion(elementType);
                    if (elementType.IsValueType)
                        IL.Emit(OpCodes.Box, elementType);
                }

                IL.Emit(OpCodes.Stelem_Ref);             // Store in array
            }
            SetStackUnknown();
        }
    }

    /// <summary>
    /// Emits type conversion for passing arguments to external .NET methods.
    /// </summary>
    private void EmitExternalTypeConversion(Type targetType)
    {
        if (targetType == _ctx.Types.Double || targetType == typeof(double))
        {
            // If we already have a native double on the stack, no conversion needed
            if (_stackType == StackType.Double)
                return;
            EmitUnboxToDouble();
        }
        else if (targetType == _ctx.Types.Boolean || targetType == typeof(bool))
        {
            // If we already have a native boolean on the stack, no conversion needed
            if (_stackType == StackType.Boolean)
                return;
            IL.Emit(OpCodes.Unbox_Any, _ctx.Types.Boolean);
        }
        else if (targetType == _ctx.Types.Int32 || targetType == typeof(int))
        {
            // If we already have a native double, just convert to int
            if (_stackType == StackType.Double)
            {
                IL.Emit(OpCodes.Conv_I4);
                return;
            }
            EmitUnboxToDouble();
            IL.Emit(OpCodes.Conv_I4);
        }
        else if (targetType == _ctx.Types.Int64 || targetType == typeof(long))
        {
            // If we already have a native double, just convert to long
            if (_stackType == StackType.Double)
            {
                IL.Emit(OpCodes.Conv_I8);
                return;
            }
            EmitUnboxToDouble();
            IL.Emit(OpCodes.Conv_I8);
        }
        else if (targetType == _ctx.Types.Single || targetType == typeof(float))
        {
            // Float (single precision)
            if (_stackType == StackType.Double)
            {
                IL.Emit(OpCodes.Conv_R4);
                return;
            }
            EmitUnboxToDouble();
            IL.Emit(OpCodes.Conv_R4);
        }
        else if (targetType == _ctx.Types.Int16 || targetType == typeof(short))
        {
            // Short (16-bit signed)
            if (_stackType == StackType.Double)
            {
                IL.Emit(OpCodes.Conv_I4);
                IL.Emit(OpCodes.Conv_I2);
                return;
            }
            EmitUnboxToDouble();
            IL.Emit(OpCodes.Conv_I4);
            IL.Emit(OpCodes.Conv_I2);
        }
        else if (targetType == _ctx.Types.Byte || targetType == typeof(byte))
        {
            // Byte (8-bit unsigned)
            if (_stackType == StackType.Double)
            {
                IL.Emit(OpCodes.Conv_I4);
                IL.Emit(OpCodes.Conv_U1);
                return;
            }
            EmitUnboxToDouble();
            IL.Emit(OpCodes.Conv_I4);
            IL.Emit(OpCodes.Conv_U1);
        }
        else if (targetType == _ctx.Types.SByte || targetType == typeof(sbyte))
        {
            // SByte (8-bit signed)
            if (_stackType == StackType.Double)
            {
                IL.Emit(OpCodes.Conv_I4);
                IL.Emit(OpCodes.Conv_I1);
                return;
            }
            EmitUnboxToDouble();
            IL.Emit(OpCodes.Conv_I4);
            IL.Emit(OpCodes.Conv_I1);
        }
        else if (targetType == _ctx.Types.UInt16 || targetType == typeof(ushort))
        {
            // UInt16 (16-bit unsigned)
            if (_stackType == StackType.Double)
            {
                IL.Emit(OpCodes.Conv_I4);
                IL.Emit(OpCodes.Conv_U2);
                return;
            }
            EmitUnboxToDouble();
            IL.Emit(OpCodes.Conv_I4);
            IL.Emit(OpCodes.Conv_U2);
        }
        else if (targetType == _ctx.Types.UInt32 || targetType == typeof(uint))
        {
            // UInt32 (32-bit unsigned)
            if (_stackType == StackType.Double)
            {
                IL.Emit(OpCodes.Conv_U4);
                return;
            }
            EmitUnboxToDouble();
            IL.Emit(OpCodes.Conv_U4);
        }
        else if (targetType == _ctx.Types.UInt64 || targetType == typeof(ulong))
        {
            // UInt64 (64-bit unsigned)
            if (_stackType == StackType.Double)
            {
                IL.Emit(OpCodes.Conv_U8);
                return;
            }
            EmitUnboxToDouble();
            IL.Emit(OpCodes.Conv_U8);
        }
        else if (targetType == _ctx.Types.Char || targetType == typeof(char))
        {
            // Char (16-bit Unicode character, treated as unsigned)
            if (_stackType == StackType.Double)
            {
                IL.Emit(OpCodes.Conv_I4);
                IL.Emit(OpCodes.Conv_U2);
                return;
            }
            EmitUnboxToDouble();
            IL.Emit(OpCodes.Conv_I4);
            IL.Emit(OpCodes.Conv_U2);
        }
        else if (targetType == _ctx.Types.Decimal || targetType == typeof(decimal))
        {
            // Decimal requires calling the explicit conversion operator
            if (_stackType != StackType.Double)
                EmitUnboxToDouble();
            var opExplicit = _ctx.Types.Decimal.GetMethod("op_Explicit",
                BindingFlags.Public | BindingFlags.Static, [_ctx.Types.Double]);
            IL.Emit(OpCodes.Call, opExplicit!);
        }
        else if (targetType == _ctx.Types.String || targetType == typeof(string))
        {
            // If we already have a string on the stack, no conversion needed
            if (_stackType == StackType.String)
                return;
            IL.Emit(OpCodes.Castclass, _ctx.Types.String);
        }
        else if (targetType.IsValueType)
        {
            IL.Emit(OpCodes.Unbox_Any, targetType);
        }
        else if (!_ctx.Types.IsObject(targetType))
        {
            IL.Emit(OpCodes.Castclass, targetType);
        }
        else
        {
            // For object type, box unboxed value types
            if (_stackType == StackType.Double)
            {
                IL.Emit(OpCodes.Box, _ctx.Types.Double);
                SetStackUnknown();
            }
            else if (_stackType == StackType.Boolean)
            {
                IL.Emit(OpCodes.Box, _ctx.Types.Boolean);
                SetStackUnknown();
            }
            // Reference types are already objects, no conversion needed
        }
    }
}
