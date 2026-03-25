using System.Reflection;
using System.Reflection.Emit;
using SharpTS.Diagnostics.Exceptions;
using SharpTS.Parsing;

namespace SharpTS.Compilation;

public partial class AsyncArrowMoveNextEmitter
{
    // EmitExpression dispatch is inherited from ExpressionEmitterBase

    protected override void EmitLiteral(Expr.Literal lit)
    {
        if (lit.Value == null)
        {
            _il.Emit(OpCodes.Ldnull);
            SetStackType(StackType.Null);
        }
        else if (lit.Value is double d)
        {
            _il.Emit(OpCodes.Ldc_R8, d);
            _il.Emit(OpCodes.Box, typeof(double));
            SetStackUnknown();
        }
        else if (lit.Value is string s)
        {
            _il.Emit(OpCodes.Ldstr, s);
            SetStackType(StackType.String);
        }
        else if (lit.Value is bool b)
        {
            _il.Emit(b ? OpCodes.Ldc_I4_1 : OpCodes.Ldc_I4_0);
            _il.Emit(OpCodes.Box, typeof(bool));
            SetStackUnknown();
        }
        else
        {
            _il.Emit(OpCodes.Ldnull);
            SetStackType(StackType.Null);
        }
    }

    protected override void EmitThis()
    {
        // Load 'this' from outer state machine if captured
        if (_builder.Captures.Contains("this"))
        {
            // Get outer state machine's ThisField (non-standalone path)
            if (_ctx?.AsyncArrowOuterBuilders?.TryGetValue(_builder.Arrow, out var outerBuilder) == true &&
                outerBuilder.ThisField != null)
            {
                _il.Emit(OpCodes.Ldarg_0);
                _il.Emit(OpCodes.Ldfld, _builder.OuterStateMachineField!);
                _il.Emit(OpCodes.Unbox, _builder.OuterStateMachineType!);
                _il.Emit(OpCodes.Ldfld, outerBuilder.ThisField);
                SetStackUnknown();
                return;
            }

            // Standalone async arrow: 'this' captured as a standalone field in this state machine
            if (_builder.IsStandalone && _builder.StandaloneCaptureFields.TryGetValue("this", out var thisField))
            {
                _il.Emit(OpCodes.Ldarg_0);
                _il.Emit(OpCodes.Ldfld, thisField);
                SetStackUnknown();
                return;
            }
        }

        _il.Emit(OpCodes.Ldnull);
        SetStackType(StackType.Null);
    }

    protected override void EmitArrayLiteral(Expr.ArrayLiteral a)
    {
        // Check if any element is a spread
        bool hasSpreads = a.Elements.Any(e => e is Expr.Spread);

        if (!hasSpreads)
        {
            // Simple case: no spreads, just create array directly
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
        }
        else
        {
            // Complex case: has spreads, use ConcatArrays
            _il.Emit(OpCodes.Ldc_I4, a.Elements.Count);
            _il.Emit(OpCodes.Newarr, typeof(object));

            for (int i = 0; i < a.Elements.Count; i++)
            {
                _il.Emit(OpCodes.Dup);
                _il.Emit(OpCodes.Ldc_I4, i);

                if (a.Elements[i] is Expr.Spread spread)
                {
                    EmitExpression(spread.Expression);
                    EnsureBoxed();
                }
                else
                {
                    _il.Emit(OpCodes.Ldc_I4, 1);
                    _il.Emit(OpCodes.Newarr, typeof(object));
                    _il.Emit(OpCodes.Dup);
                    _il.Emit(OpCodes.Ldc_I4_0);
                    EmitExpression(a.Elements[i]);
                    EnsureBoxed();
                    _il.Emit(OpCodes.Stelem_Ref);
                    _il.Emit(OpCodes.Call, _ctx!.Runtime!.CreateArray);
                }

                _il.Emit(OpCodes.Stelem_Ref);
            }

            // Pass Symbol.iterator and runtimeType for iterator protocol support
            _il.Emit(OpCodes.Ldsfld, _ctx!.Runtime!.SymbolIterator);
            _il.Emit(OpCodes.Ldtoken, _ctx!.Runtime!.RuntimeType);
            _il.Emit(OpCodes.Call, Types.TypeGetTypeFromHandle);
            _il.Emit(OpCodes.Call, _ctx!.Runtime!.ConcatArrays);
        }
        SetStackUnknown();
    }

    protected override void EmitObjectLiteral(Expr.ObjectLiteral o)
    {
        // Check if any property is a spread or computed key
        bool hasSpreads = o.Properties.Any(p => p.IsSpread);
        bool hasComputedKeys = o.Properties.Any(p => p.Key is Expr.ComputedKey);

        if (!hasSpreads && !hasComputedKeys)
        {
            // Simple case: no spreads, no computed keys
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
        }
        else
        {
            // Complex case: has spreads or computed keys, use SetIndex
            _il.Emit(OpCodes.Newobj, Types.DictionaryStringObjectNullableCtor);

            foreach (var prop in o.Properties)
            {
                _il.Emit(OpCodes.Dup);

                if (prop.IsSpread)
                {
                    EmitExpression(prop.Value);
                    EnsureBoxed();
                    _il.Emit(OpCodes.Call, _ctx!.Runtime!.MergeIntoObject);
                }
                else if (prop.Key is Expr.ComputedKey ck)
                {
                    EmitExpression(ck.Expression);
                    EnsureBoxed();
                    EmitExpression(prop.Value);
                    EnsureBoxed();
                    _il.Emit(OpCodes.Call, _ctx!.Runtime!.SetIndex);
                }
                else
                {
                    EmitStaticPropertyKey(prop.Key!);
                    EmitExpression(prop.Value);
                    EnsureBoxed();
                    _il.Emit(OpCodes.Callvirt, Types.DictionaryStringObjectNullableSetItem);
                }
            }

            _il.Emit(OpCodes.Call, _ctx!.Runtime!.CreateObject);
        }
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
                throw new CompileException($"Unexpected static property key type: {key.GetType().Name}");
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
        // Save value to local first (SetIndex is void, but the expression evaluates to the assigned value)
        EmitExpression(si.Value);
        EnsureBoxed();
        var valueLocal = _il.DeclareLocal(typeof(object));
        _il.Emit(OpCodes.Stloc, valueLocal);

        EmitExpression(si.Object);
        EnsureBoxed();
        EmitExpression(si.Index);
        EnsureBoxed();
        _il.Emit(OpCodes.Ldloc, valueLocal);
        _il.Emit(OpCodes.Call, _ctx!.Runtime!.SetIndex);

        // Push value back as the expression result (arr[i] = v evaluates to v)
        _il.Emit(OpCodes.Ldloc, valueLocal);
        SetStackUnknown();
    }

    protected override void EmitNew(Expr.New n)
    {
        var (namespaceParts, className) = ExtractQualifiedNameFromCallee(n.Callee);

        if (namespaceParts.Count == 0 && n.Callee is Expr.Variable && TryEmitBuiltInConstructor(className, n.Arguments))
            return;

        if (TryEmitIntlConstructor(namespaceParts, className, n.Arguments))
            return;

        if (TryEmitModuleQualifiedConstructor(namespaceParts, className, n.Arguments))
            return;

        // Resolve class name
        string resolvedClassName;
        if (namespaceParts.Count > 0)
        {
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

            // Handle generic class instantiation
            if (n.TypeArgs != null && n.TypeArgs.Count > 0 &&
                _ctx.ClassRegistry!.GetGenericParams(resolvedClassName) != null)
            {
                Type[] typeArgs = n.TypeArgs.Select(ResolveTypeArg).ToArray();
                targetType = typeBuilder.MakeGenericType(typeArgs);
                targetCtor = TypeBuilder.GetConstructor(targetType, ctorBuilder);
            }

            int expectedParamCount = ctorBuilder.GetParameters().Length;

            // Emit arguments
            foreach (var arg in n.Arguments)
            {
                EmitExpression(arg);
                EnsureBoxed();
            }

            // Pad missing arguments with null
            for (int i = n.Arguments.Count; i < expectedParamCount; i++)
            {
                _il.Emit(OpCodes.Ldnull);
            }

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
        return typeArg switch
        {
            "number" => typeof(object),
            "string" => typeof(object),
            "boolean" => typeof(object),
            "any" => typeof(object),
            _ => typeof(object)
        };
    }

    protected override void EmitTemplateLiteral(Expr.TemplateLiteral tl)
    {
        // TemplateLiteral has Strings (literal parts) and Expressions (interpolated parts)
        // Structure: strings[0] + expressions[0] + strings[1] + expressions[1] + ... + strings[n]
        if (tl.Strings.Count == 0 && tl.Expressions.Count == 0)
        {
            _il.Emit(OpCodes.Ldstr, "");
            SetStackType(StackType.String);
            return;
        }

        // In async context, expressions may contain await which clears the stack.
        // Phase 1: Evaluate all expressions to temps first (awaits happen here)
        var exprTemps = new List<LocalBuilder>();
        for (int i = 0; i < tl.Expressions.Count; i++)
        {
            EmitExpression(tl.Expressions[i]);
            EnsureBoxed();
            var temp = _il.DeclareLocal(typeof(object));
            _il.Emit(OpCodes.Stloc, temp);
            exprTemps.Add(temp);
        }

        // Phase 2: Build string from temps (no awaits, stack safe)
        _il.Emit(OpCodes.Ldstr, tl.Strings[0]);

        for (int i = 0; i < exprTemps.Count; i++)
        {
            // Load expression value from temp and convert to string
            _il.Emit(OpCodes.Ldloc, exprTemps[i]);
            _il.Emit(OpCodes.Call, _ctx!.Runtime!.Stringify);
            _il.Emit(OpCodes.Call, Types.StringConcat2);

            // Emit next string part
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
        // In async context, expressions may contain await which clears the stack.
        // Phase 1: Evaluate tag and all expressions to temps first (awaits happen here)
        EmitExpression(ttl.Tag);
        EnsureBoxed();
        var tagTemp = _il.DeclareLocal(typeof(object));
        _il.Emit(OpCodes.Stloc, tagTemp);

        var exprTemps = new List<LocalBuilder>();
        for (int i = 0; i < ttl.Expressions.Count; i++)
        {
            EmitExpression(ttl.Expressions[i]);
            EnsureBoxed();
            var temp = _il.DeclareLocal(typeof(object));
            _il.Emit(OpCodes.Stloc, temp);
            exprTemps.Add(temp);
        }

        // Phase 2: Build arrays and make call (no awaits, stack safe)
        // Load tag function
        _il.Emit(OpCodes.Ldloc, tagTemp);

        // Create cooked strings array
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

        // Create raw strings array
        _il.Emit(OpCodes.Ldc_I4, ttl.RawStrings.Count);
        _il.Emit(OpCodes.Newarr, typeof(string));
        for (int i = 0; i < ttl.RawStrings.Count; i++)
        {
            _il.Emit(OpCodes.Dup);
            _il.Emit(OpCodes.Ldc_I4, i);
            _il.Emit(OpCodes.Ldstr, ttl.RawStrings[i]);
            _il.Emit(OpCodes.Stelem_Ref);
        }

        // Create expressions array from temps
        _il.Emit(OpCodes.Ldc_I4, ttl.Expressions.Count);
        _il.Emit(OpCodes.Newarr, typeof(object));
        for (int i = 0; i < exprTemps.Count; i++)
        {
            _il.Emit(OpCodes.Dup);
            _il.Emit(OpCodes.Ldc_I4, i);
            _il.Emit(OpCodes.Ldloc, exprTemps[i]);
            _il.Emit(OpCodes.Stelem_Ref);
        }

        // Call runtime helper
        _il.Emit(OpCodes.Call, _ctx!.Runtime!.InvokeTaggedTemplate);
        SetStackUnknown();
    }

    protected override void EmitSet(Expr.Set s)
    {
        // Handle static field assignment
        if (s.Object is Expr.Variable classVar &&
            _ctx!.Classes.TryGetValue(_ctx.ResolveClassName(classVar.Name.Lexeme), out var classBuilder))
        {
            string resolvedClassName = _ctx.ResolveClassName(classVar.Name.Lexeme);
            if (_ctx.ClassRegistry!.TryGetStaticField(resolvedClassName, s.Name.Lexeme, out var staticField))
            {
                EmitExpression(s.Value);
                EnsureBoxed();
                _il.Emit(OpCodes.Dup);
                _il.Emit(OpCodes.Stsfld, staticField!);
                SetStackUnknown();
                return;
            }
        }

        // Type-first dispatch: property setter via TypeEmitterRegistry
        if (TryEmitTypeRegistryPropertySet(s)) return;

        // Default: dynamic property assignment
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
        if (className != null)
        {
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
            return;
        }

        _il.Emit(OpCodes.Ldstr, $"Cannot access private field '#{fieldName}' - class context not available");
        _il.Emit(OpCodes.Newobj, Types.InvalidOperationExceptionCtorString);
        _il.Emit(OpCodes.Throw);
    }

    protected override void EmitSetPrivate(Expr.SetPrivate sp)
    {
        // Get the field name without the # prefix
        string fieldName = sp.Name.Lexeme;
        if (fieldName.StartsWith('#'))
            fieldName = fieldName[1..];

        string? className = _ctx?.CurrentClassName;
        if (className != null)
        {
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
            return;
        }

        _il.Emit(OpCodes.Ldstr, $"Cannot write private field '#{fieldName}' - class context not available");
        _il.Emit(OpCodes.Newobj, Types.InvalidOperationExceptionCtorString);
        _il.Emit(OpCodes.Throw);
    }

    protected override void EmitCallPrivate(Expr.CallPrivate cp)
    {
        // Get the method name without the # prefix
        string methodName = cp.Name.Lexeme;
        if (methodName.StartsWith('#'))
            methodName = methodName[1..];

        string? className = _ctx?.CurrentClassName;
        if (className != null)
        {
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
            return;
        }

        _il.Emit(OpCodes.Ldstr, $"Cannot call private method '#{methodName}' - class context not available");
        _il.Emit(OpCodes.Newobj, Types.InvalidOperationExceptionCtorString);
        _il.Emit(OpCodes.Throw);
    }

    /// <summary>
    /// Emits the typeof() for the declaring class containing the private member.
    /// Used for static private member access where we know the class at compile time.
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
    /// Emits the declaring class type for instance private member access.
    /// When CurrentClassBuilder is available, uses typeof(). Otherwise, calls GetType()
    /// on the instance that's already on the stack (duplicates it first).
    /// Stack before: [instance]
    /// Stack after: [instance, Type]
    /// </summary>
    private void EmitDeclaringClassTypeOrGetFromObject()
    {
        if (_ctx?.CurrentClassBuilder != null)
        {
            // Known class at compile time - use typeof
            _il.Emit(OpCodes.Ldtoken, _ctx.CurrentClassBuilder);
            _il.Emit(OpCodes.Call, Types.TypeGetTypeFromHandle);
        }
        else
        {
            // Unknown class at compile time (async arrow in regular method)
            // Get the type from the instance that's on the stack
            // Stack: [instance] -> [instance, instance] -> [instance, Type]
            _il.Emit(OpCodes.Dup);
            _il.Emit(OpCodes.Callvirt, typeof(object).GetMethod("GetType")!);
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

    protected override void EmitSuper(Expr.Super s)
    {
        // Load hoisted 'this' from outer state machine if captured
        if (_builder.Captures.Contains("this"))
        {
            if (_ctx?.AsyncArrowOuterBuilders?.TryGetValue(_builder.Arrow, out var outerBuilder) == true &&
                outerBuilder.ThisField != null)
            {
                _il.Emit(OpCodes.Ldarg_0);
                _il.Emit(OpCodes.Ldfld, _builder.OuterStateMachineField!);
                _il.Emit(OpCodes.Unbox, _builder.OuterStateMachineType!);
                _il.Emit(OpCodes.Ldfld, outerBuilder.ThisField);
            }
            else
            {
                _il.Emit(OpCodes.Ldnull);
            }
        }
        else
        {
            _il.Emit(OpCodes.Ldnull);
        }

        _il.Emit(OpCodes.Ldstr, s.Method?.Lexeme ?? "constructor");
        _il.Emit(OpCodes.Call, _ctx!.Runtime!.GetSuperMethod);
        SetStackUnknown();
    }
}
