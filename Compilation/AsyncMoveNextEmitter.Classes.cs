using System.Reflection;
using System.Reflection.Emit;
using SharpTS.Diagnostics.Exceptions;
using SharpTS.Parsing;

namespace SharpTS.Compilation;

public partial class AsyncMoveNextEmitter
{
    protected override void EmitNew(Expr.New n)
    {
        // Extract qualified name from callee expression
        var (namespaceParts, className) = ExtractQualifiedNameFromCallee(n.Callee);

        // Handle built-in type constructors (simple name)
        if (namespaceParts.Count == 0 && n.Callee is Expr.Variable && TryEmitBuiltInConstructor(className, n.Arguments))
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
            Type targetType = typeBuilder;
            ConstructorInfo targetCtor = ctorBuilder;

            // Handle generic class instantiation (e.g., new Box<number>(42))
            if (n.TypeArgs != null && n.TypeArgs.Count > 0 &&
                _ctx.ClassRegistry!.GetGenericParams(resolvedClassName) != null)
            {
                // Resolve type arguments
                Type[] typeArgs = n.TypeArgs.Select(ResolveTypeArg).ToArray();

                // Create the constructed generic type
                targetType = typeBuilder.MakeGenericType(typeArgs);

                // Get the constructor on the constructed type
                targetCtor = TypeBuilder.GetConstructor(targetType, ctorBuilder);
            }

            // Get constructor parameters for typed emission
            var ctorParams = ctorBuilder.GetParameters();
            int expectedParamCount = ctorParams.Length;

            // IMPORTANT: In async, await can happen in arguments
            // Emit all arguments first and store to temps
            List<LocalBuilder> argTemps = [];
            foreach (var arg in n.Arguments)
            {
                EmitExpression(arg);
                EnsureBoxed();
                var temp = _il.DeclareLocal(typeof(object));
                _il.Emit(OpCodes.Stloc, temp);
                argTemps.Add(temp);
            }

            // Now load all arguments onto stack with proper type conversions
            for (int i = 0; i < argTemps.Count; i++)
            {
                _il.Emit(OpCodes.Ldloc, argTemps[i]);
                if (i < ctorParams.Length)
                {
                    var targetParamType = ctorParams[i].ParameterType;
                    if (targetParamType.IsValueType && targetParamType != typeof(object))
                    {
                        _il.Emit(OpCodes.Unbox_Any, targetParamType);
                    }
                }
            }

            // Pad missing optional arguments with appropriate default values
            for (int i = n.Arguments.Count; i < expectedParamCount; i++)
            {
                EmitDefaultForType(ctorParams[i].ParameterType);
            }

            // Call the constructor directly using newobj
            _il.Emit(OpCodes.Newobj, targetCtor);
            SetStackUnknown();
        }
        else
        {
            // Fallback: try to load via resolver (handles parameters, hoisted variables, etc.)
            var stackType = _resolver?.TryLoadVariable(className);
            if (stackType != null)
            {
                // Variable loaded - save Type to temp (safe across await boundaries)
                var typeTemp = _il.DeclareLocal(typeof(object));
                _il.Emit(OpCodes.Stloc, typeTemp);

                // Pre-evaluate arguments to temps (may contain await expressions)
                List<LocalBuilder> argTemps = [];
                foreach (var arg in n.Arguments)
                {
                    EmitExpression(arg);
                    EnsureBoxed();
                    var temp = _il.DeclareLocal(typeof(object));
                    _il.Emit(OpCodes.Stloc, temp);
                    argTemps.Add(temp);
                }

                // Load Type and build args array from temps
                _il.Emit(OpCodes.Ldloc, typeTemp);
                _il.Emit(OpCodes.Ldc_I4, n.Arguments.Count);
                _il.Emit(OpCodes.Newarr, _ctx!.Types.Object);

                for (int i = 0; i < argTemps.Count; i++)
                {
                    _il.Emit(OpCodes.Dup);
                    _il.Emit(OpCodes.Ldc_I4, i);
                    _il.Emit(OpCodes.Ldloc, argTemps[i]);
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

    private Type ResolveTypeArg(string typeArg)
    {
        // Simple type argument resolution - similar to ILEmitter
        return typeArg switch
        {
            "number" => typeof(object),
            "string" => typeof(object),
            "boolean" => typeof(object),
            "any" => typeof(object),
            _ => typeof(object)
        };
    }

    protected override void EmitThis()
    {
        // 'this' in async methods - load from hoisted field if available
        if (_builder.ThisField != null)
        {
            _il.Emit(OpCodes.Ldarg_0);  // Load state machine ref
            _il.Emit(OpCodes.Ldfld, _builder.ThisField);
            SetStackUnknown();
        }
        else
        {
            // Not an instance method or 'this' not hoisted - emit null
            _il.Emit(OpCodes.Ldnull);
            SetStackType(StackType.Null);
        }
    }
}
