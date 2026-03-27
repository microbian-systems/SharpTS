using System.Reflection.Emit;
using SharpTS.Diagnostics.Exceptions;
using SharpTS.Parsing;
using SharpTS.Runtime.BuiltIns;
using SharpTS.TypeSystem;

namespace SharpTS.Compilation;

/// <summary>
/// Core property access methods for the IL emitter (get, set, index, direct dispatch, special cases).
/// Literal emission is in ILEmitter.Properties.Literals.cs.
/// External/static member access is in ILEmitter.Properties.External.cs.
/// Private class elements are in ILEmitter.Properties.Private.cs.
/// Built-in type property access is in ILEmitter.Properties.BuiltIns.cs.
/// </summary>
public partial class ILEmitter
{
    protected override void EmitGet(Expr.Get g)
    {
        // Static type property dispatch via registry (Math.PI, Number.MAX_VALUE, Symbol.iterator, etc.)
        if (g.Object is Expr.Variable staticVar && _ctx.TypeEmitterRegistry != null)
        {
            var staticStrategy = _ctx.TypeEmitterRegistry.GetStaticStrategy(staticVar.Name.Lexeme);
            if (staticStrategy != null && staticStrategy.TryEmitStaticPropertyGet(this, g.Name.Lexeme))
            {
                SetStackUnknown();
                return;
            }
        }

        // Special case: process.stdin.isTTY, process.stdout.isTTY, process.stderr.isTTY
        if (TryEmitProcessStreamProperty(g))
        {
            return;
        }

        // Special case: globalThis.Math.PI, globalThis.JSON.parse, etc.
        if (TryEmitGlobalThisChainedProperty(g))
        {
            return;
        }

        // Built-in module property access (path.sep, path.delimiter, os.EOL, etc.)
        if (g.Object is Expr.Variable builtInVar &&
            _ctx.BuiltInModuleNamespaces != null &&
            _ctx.BuiltInModuleNamespaces.TryGetValue(builtInVar.Name.Lexeme, out var builtInModuleName) &&
            _ctx.BuiltInModuleEmitterRegistry?.GetEmitter(builtInModuleName) is { } builtInEmitter)
        {
            if (builtInEmitter.TryEmitPropertyGet(this, g.Name.Lexeme))
            {
                SetStackUnknown();
                return;
            }
        }

        // EventEmitter.defaultMaxListeners static property
        if (g.Object is Expr.Variable eeVar && g.Name.Lexeme == "defaultMaxListeners" &&
            _ctx.BuiltInModuleMethodBindings?.TryGetValue(eeVar.Name.Lexeme, out var eeBinding) == true &&
            eeBinding.ModuleName == "events" && eeBinding.MethodName == "EventEmitter" &&
            _ctx.Runtime?.TSEventEmitterDefaultMaxListeners != null)
        {
            IL.Emit(OpCodes.Ldsfld, _ctx.Runtime.TSEventEmitterDefaultMaxListeners);
            IL.Emit(OpCodes.Conv_R8);
            IL.Emit(OpCodes.Box, _ctx.Types.Double);
            SetStackUnknown();
            return;
        }

        // Enum forward mapping: Direction.Up -> 0 or Status.Success -> "SUCCESS"
        if (g.Object is Expr.Variable enumVar &&
            _ctx.EnumMembers?.TryGetValue(_ctx.ResolveEnumName(enumVar.Name.Lexeme), out var members) == true &&
            members.TryGetValue(g.Name.Lexeme, out var value))
        {
            if (value is double d)
            {
                IL.Emit(OpCodes.Ldc_R8, d);
                IL.Emit(OpCodes.Box, _ctx.Types.Double);
                SetStackUnknown();
            }
            else if (value is string s)
            {
                IL.Emit(OpCodes.Ldstr, s);
                SetStackType(StackType.String);
            }
            return;
        }

        // Handle static member access via 'this' in static context (static blocks, static methods)
        // In static blocks, 'this' refers to the class constructor, so this.property accesses static members
        if (g.Object is Expr.This && !_ctx.IsInstanceMethod && _ctx.CurrentClassBuilder != null)
        {
            // Use cached CurrentClassName instead of linear search
            string? currentClassName = _ctx.CurrentClassName;

            if (currentClassName != null)
            {
                // Emit as static field access on the current class
                if (EmitStaticMemberAccess(currentClassName, _ctx.CurrentClassBuilder, g.Name.Lexeme))
                {
                    return;
                }
            }
        }

        // Handle static member access via class name
        if (g.Object is Expr.Variable classVar)
        {
            string resolvedClassName = _ctx.ResolveClassName(classVar.Name.Lexeme);
            if (_ctx.Classes.TryGetValue(resolvedClassName, out var classBuilder))
            {
                // Try static getter first (for auto-accessors and explicit static accessors)
                if (_ctx.ClassRegistry!.TryGetStaticGetter(resolvedClassName, g.Name.Lexeme, out var staticGetter))
                {
                    IL.Emit(OpCodes.Call, staticGetter!);

                    // The getter returns the typed value (e.g., double for number).
                    // Track the stack type so EmitBoxIfNeeded can box only when necessary.
                    // This avoids unnecessary boxing in numeric contexts like `Counter.count + 1`.
                    string pascalPropName = NamingConventions.ToPascalCase(g.Name.Lexeme);
                    if (_ctx.PropertyTypes != null &&
                        _ctx.PropertyTypes.TryGetValue(resolvedClassName, out var propTypes) &&
                        propTypes.TryGetValue(pascalPropName, out var propType))
                    {
                        if (propType == _ctx.Types.Double)
                        {
                            SetStackType(StackType.Double);
                        }
                        else if (propType == _ctx.Types.Boolean)
                        {
                            SetStackType(StackType.Boolean);
                        }
                        else if (propType == _ctx.Types.String)
                        {
                            SetStackType(StackType.String);
                        }
                        else
                        {
                            // Other reference types
                            SetStackUnknown();
                        }
                    }
                    else
                    {
                        // Fallback: assume object return (legacy behavior)
                        SetStackUnknown();
                    }
                    return;
                }

                // Try to find static field using stored FieldBuilders
                // Use TryGetCallableStaticField to handle generic classes properly
                if (_ctx.ClassRegistry!.TryGetCallableStaticField(resolvedClassName, g.Name.Lexeme, classBuilder, out var callableStaticField))
                {
                    IL.Emit(OpCodes.Ldsfld, callableStaticField!);
                    SetStackUnknown();
                    return;
                }

                // Static methods are handled in EmitCall, so just fall through for now
                // If we get here for a method reference (not call), we'll use the generic path
            }
        }

        // Handle static member access via imported class alias (import X = require('./module') where module exports a class)
        if (g.Object is Expr.Variable importedClassVar &&
            _ctx.ImportedClassAliases?.TryGetValue(importedClassVar.Name.Lexeme, out var importedQualifiedClassName) == true &&
            _ctx.Classes.TryGetValue(importedQualifiedClassName, out var importedClassBuilder))
        {
            // Try static getter first
            if (_ctx.ClassRegistry!.TryGetStaticGetter(importedQualifiedClassName, g.Name.Lexeme, out var importedStaticGetter))
            {
                IL.Emit(OpCodes.Call, importedStaticGetter!);
                SetStackUnknown();
                return;
            }

            // Try static field
            if (_ctx.ClassRegistry!.TryGetCallableStaticField(importedQualifiedClassName, g.Name.Lexeme, importedClassBuilder, out var importedStaticField))
            {
                IL.Emit(OpCodes.Ldsfld, importedStaticField!);
                SetStackUnknown();
                return;
            }
        }

        // Handle static member access via class expression variable
        if (g.Object is Expr.Variable classExprVar &&
            _ctx.VarToClassExpr != null &&
            _ctx.VarToClassExpr.TryGetValue(classExprVar.Name.Lexeme, out var classExpr) &&
            _ctx.ClassExprStaticFields != null &&
            _ctx.ClassExprStaticFields.TryGetValue(classExpr, out var exprStaticFields) &&
            exprStaticFields.TryGetValue(g.Name.Lexeme, out var exprStaticField))
        {
            IL.Emit(OpCodes.Ldsfld, exprStaticField);
            SetStackUnknown();
            return;
        }

        // Handle static property access on external .NET types (@DotNetType)
        if (g.Object is Expr.Variable extVar && _ctx.TypeMapper.ExternalTypes.TryGetValue(extVar.Name.Lexeme, out var externalType))
        {
            if (TryEmitExternalStaticPropertyGet(externalType, g.Name.Lexeme))
                return;
        }

        // Try direct getter dispatch for known class instance types
        TypeInfo? objType = _ctx.TypeMap?.Get(g.Object);
        if (TryEmitDirectGetterCall(g.Object, objType, g.Name.Lexeme))
            return;

        // Type-first dispatch: Use TypeEmitterRegistry for property getters
        if (objType != null && _ctx.TypeEmitterRegistry != null)
        {
            var strategy = _ctx.TypeEmitterRegistry.GetStrategy(objType);
            if (strategy != null && strategy.TryEmitPropertyGet(this, g.Object, g.Name.Lexeme))
            {
                SetStackUnknown();
                return;
            }
        }

        // Category-based built-in type property dispatch
        if (objType != null && TryEmitBuiltInTypePropertyGet(g, objType))
            return;

        EmitExpression(g.Object);
        EmitBoxIfNeeded(g.Object);

        if (g.Optional)
        {
            var builder = _ctx.ILBuilder;
            var nullishLabel = builder.DefineLabel("optional_nullish");
            var endLabel = builder.DefineLabel("optional_end");

            // Check for null
            IL.Emit(OpCodes.Dup);
            builder.Emit_Brfalse(nullishLabel);

            // Check for undefined (non-null singleton $Undefined.Instance)
            IL.Emit(OpCodes.Dup);
            IL.Emit(OpCodes.Isinst, _ctx.Runtime!.UndefinedType);
            builder.Emit_Brtrue(nullishLabel);

            // Not nullish - proceed with property access
            IL.Emit(OpCodes.Ldstr, g.Name.Lexeme);
            IL.Emit(OpCodes.Call, _ctx.Runtime!.GetProperty);
            builder.Emit_Br(endLabel);

            builder.MarkLabel(nullishLabel);
            IL.Emit(OpCodes.Pop);
            // Optional chaining returns undefined (not null) when object is nullish
            IL.Emit(OpCodes.Ldsfld, _ctx.Runtime!.UndefinedInstance);

            builder.MarkLabel(endLabel);
        }
        else
        {
            IL.Emit(OpCodes.Ldstr, g.Name.Lexeme);
            IL.Emit(OpCodes.Call, _ctx.Runtime!.GetProperty);
        }
    }

    protected override void EmitSet(Expr.Set s)
    {
        // Handle process.exitCode assignment
        if (s.Object is Expr.Variable processVar && processVar.Name.Lexeme == "process" && s.Name.Lexeme == "exitCode")
        {
            EmitExpression(s.Value);
            EmitUnboxToDouble();
            IL.Emit(OpCodes.Conv_I4);
            IL.Emit(OpCodes.Dup); // Keep value for expression result
            IL.Emit(OpCodes.Call, _ctx.Types.GetPropertySetter(_ctx.Types.Environment, "ExitCode"));
            IL.Emit(OpCodes.Conv_R8); // Convert back to double for JS number
            IL.Emit(OpCodes.Box, _ctx.Types.Double);
            return;
        }

        // Handle static property assignment via 'this' in static context (static blocks, static methods)
        if (s.Object is Expr.This && !_ctx.IsInstanceMethod && _ctx.CurrentClassBuilder != null)
        {
            // First check for class expressions
            if (_ctx.CurrentClassExpr != null &&
                _ctx.ClassExprStaticFields != null &&
                _ctx.ClassExprStaticFields.TryGetValue(_ctx.CurrentClassExpr, out var classExprStaticFields) &&
                classExprStaticFields.TryGetValue(s.Name.Lexeme, out var classExprStaticField))
            {
                EmitExpression(s.Value);
                EmitBoxIfNeeded(s.Value);
                IL.Emit(OpCodes.Dup); // Keep value for expression result
                IL.Emit(OpCodes.Stsfld, classExprStaticField);
                return;
            }

            // Use cached CurrentClassName instead of linear search (class declarations)
            string? currentClassName = _ctx.CurrentClassName;

            if (currentClassName != null)
            {
                // Emit as static field assignment on the current class
                if (EmitStaticMemberSet(currentClassName, _ctx.CurrentClassBuilder, s.Name.Lexeme, s.Value))
                {
                    return;
                }
            }
        }

        // Handle static property assignment via class name
        if (s.Object is Expr.Variable classVar)
        {
            string resolvedClassName = _ctx.ResolveClassName(classVar.Name.Lexeme);
            if (_ctx.Classes.TryGetValue(resolvedClassName, out var classBuilder))
            {
                // Try static setter first (for auto-accessors and explicit static accessors)
                if (_ctx.ClassRegistry!.TryGetStaticSetter(resolvedClassName, s.Name.Lexeme, out var staticSetter))
                {
                    // Get the property type from PropertyTypes dictionary
                    Type propertyType = _ctx.Types.Object;
                    string pascalPropName = NamingConventions.ToPascalCase(s.Name.Lexeme);

                    // Try to get the type from PropertyTypes dictionary
                    if (_ctx.PropertyTypes != null &&
                        _ctx.PropertyTypes.TryGetValue(resolvedClassName, out var propTypes) &&
                        propTypes.TryGetValue(pascalPropName, out var propType))
                    {
                        propertyType = propType;
                    }
                    else
                    {
                        // Fallback: infer from value expression type via TypeMap
                        TypeInfo? valueType = _ctx.TypeMap?.Get(s.Value);
                        if (valueType is TypeInfo.Primitive prim)
                        {
                            propertyType = prim.Type switch
                            {
                                TokenType.TYPE_NUMBER => _ctx.Types.Double,
                                TokenType.TYPE_BOOLEAN => _ctx.Types.Boolean,
                                TokenType.TYPE_STRING => _ctx.Types.String,
                                _ => _ctx.Types.Object
                            };
                        }
                    }

                    EmitExpression(s.Value);
                    EmitBoxIfNeeded(s.Value);
                    IL.Emit(OpCodes.Dup); // Keep value for expression result
                    var staticSetterResultTemp = IL.DeclareLocal(_ctx.Types.Object);
                    IL.Emit(OpCodes.Stloc, staticSetterResultTemp);

                    // If setter expects a value type, unbox the value
                    if (propertyType.IsValueType)
                    {
                        IL.Emit(OpCodes.Unbox_Any, propertyType);
                    }
                    else if (!_ctx.Types.IsObject(propertyType))
                    {
                        IL.Emit(OpCodes.Castclass, propertyType);
                    }

                    IL.Emit(OpCodes.Call, staticSetter!);

                    // Restore result for expression value
                    IL.Emit(OpCodes.Ldloc, staticSetterResultTemp);
                    return;
                }

                if (_ctx.ClassRegistry!.TryGetStaticField(resolvedClassName, s.Name.Lexeme, out var staticField))
                {
                    EmitExpression(s.Value);
                    EmitBoxIfNeeded(s.Value);
                    IL.Emit(OpCodes.Dup); // Keep value for expression result
                    IL.Emit(OpCodes.Stsfld, staticField!);
                    return;
                }
            }
        }

        // Try direct setter dispatch for known class instance types
        TypeInfo? objType = _ctx.TypeMap?.Get(s.Object);
        if (TryEmitDirectSetterCall(s.Object, objType, s.Name.Lexeme, s.Value))
            return;

        // Special case: RegExp.lastIndex setter
        if (objType is TypeInfo.RegExp && s.Name.Lexeme == "lastIndex")
        {
            EmitExpression(s.Object);
            EmitBoxIfNeeded(s.Object);
            EmitExpression(s.Value);
            EmitUnboxToDouble();
            // Dup value for expression result
            IL.Emit(OpCodes.Dup);
            var valueTemp = IL.DeclareLocal(_ctx.Types.Double);
            IL.Emit(OpCodes.Stloc, valueTemp);
            IL.Emit(OpCodes.Call, _ctx.Runtime!.RegExpSetLastIndex);
            // Put value back on stack as boxed result
            IL.Emit(OpCodes.Ldloc, valueTemp);
            IL.Emit(OpCodes.Box, _ctx.Types.Double);
            return;
        }

        // Type-first dispatch: Use TypeEmitterRegistry for property setters
        if (objType != null && _ctx.TypeEmitterRegistry != null)
        {
            var strategy = _ctx.TypeEmitterRegistry.GetStrategy(objType);
            if (strategy != null && strategy.TryEmitPropertySet(this, s.Object, s.Name.Lexeme, s.Value))
            {
                SetStackUnknown();
                return;
            }
        }

        // Build stack for SetProperty(obj, name, value) or SetPropertyStrict(obj, name, value, strictMode)
        EmitExpression(s.Object);
        EmitBoxIfNeeded(s.Object);
        IL.Emit(OpCodes.Ldstr, s.Name.Lexeme);
        EmitExpression(s.Value);
        EmitBoxIfNeeded(s.Value);

        // Stack: [obj, name, value]
        // Dup value for expression result, then store it
        IL.Emit(OpCodes.Dup);
        var resultTemp = IL.DeclareLocal(_ctx.Types.Object);
        IL.Emit(OpCodes.Stloc, resultTemp);

        // Stack: [obj, name, value] - call SetProperty or SetPropertyStrict
        if (_ctx.IsStrictMode)
        {
            IL.Emit(OpCodes.Ldc_I4_1); // true for strict mode
            IL.Emit(OpCodes.Call, _ctx.Runtime!.SetPropertyStrict);
        }
        else
        {
            IL.Emit(OpCodes.Call, _ctx.Runtime!.SetProperty);
        }

        // Put result back on stack
        IL.Emit(OpCodes.Ldloc, resultTemp);
    }

    protected override void EmitGetIndex(Expr.GetIndex gi)
    {
        // Enum reverse mapping: Direction[0] -> "Up"
        if (gi.Object is Expr.Variable enumVar &&
            _ctx.EnumReverse?.TryGetValue(_ctx.ResolveEnumName(enumVar.Name.Lexeme), out var reverse) == true)
        {
            // Check if index is a literal we can resolve at compile time
            if (gi.Index is Expr.Literal lit && lit.Value is double d && reverse.TryGetValue(d, out var memberName))
            {
                IL.Emit(OpCodes.Ldstr, memberName);
                SetStackType(StackType.String);
                return;
            }

            // Runtime lookup using cached helper
            var keys = reverse.Keys.ToArray();
            var values = reverse.Values.ToArray();
            IL.Emit(OpCodes.Ldstr, enumVar.Name.Lexeme);
            EmitExpression(gi.Index);
            EmitUnboxToDouble();
            IL.Emit(OpCodes.Ldc_I4, keys.Length);
            IL.Emit(OpCodes.Newarr, _ctx.Types.Double);
            for (int i = 0; i < keys.Length; i++)
            {
                IL.Emit(OpCodes.Dup);
                IL.Emit(OpCodes.Ldc_I4, i);
                IL.Emit(OpCodes.Ldc_R8, keys[i]);
                IL.Emit(OpCodes.Stelem_R8);
            }
            IL.Emit(OpCodes.Ldc_I4, values.Length);
            IL.Emit(OpCodes.Newarr, _ctx.Types.String);
            for (int i = 0; i < values.Length; i++)
            {
                IL.Emit(OpCodes.Dup);
                IL.Emit(OpCodes.Ldc_I4, i);
                IL.Emit(OpCodes.Ldstr, values[i]);
                IL.Emit(OpCodes.Stelem_Ref);
            }
            IL.Emit(OpCodes.Call, _ctx.Runtime!.GetEnumMemberName);
            SetStackType(StackType.String);
            return;
        }

        // Descriptor-driven fast path: when receiver is statically known to be an array,
        // emit direct List<T> access — skips runtime type dispatch,
        // index boxing, and Convert.ToInt32(object) overhead.
        var desc = ArrayElements.Resolve(_ctx.TypeMap?.Get(gi.Object));
        if (desc != null)
        {
            var fallbackLabel = IL.DefineLabel();
            var endLabel = IL.DefineLabel();

            EmitExpression(gi.Object);
            EnsureBoxed();

            var objLocal = IL.DeclareLocal(_ctx.Types.Object);
            IL.Emit(OpCodes.Dup);
            IL.Emit(OpCodes.Stloc, objLocal);

            // Typed fast path: isinst List<T> → direct get_Item with native type on stack
            if (desc.Kind != ArrayElementsKind.Object)
            {
                var listType = desc.GetListType(_ctx.Types);
                var notTypedLabel = IL.DefineLabel();
                IL.Emit(OpCodes.Isinst, listType);
                IL.Emit(OpCodes.Brfalse, notTypedLabel);

                IL.Emit(OpCodes.Ldloc, objLocal);
                IL.Emit(OpCodes.Castclass, listType);
                EmitExpressionAsDouble(gi.Index);
                IL.Emit(OpCodes.Conv_I4);
                IL.Emit(OpCodes.Callvirt, _ctx.Types.GetMethod(listType, "get_Item", _ctx.Types.Int32));
                SetStackType(desc.StackType);
                IL.Emit(OpCodes.Br, endLabel);

                IL.MarkLabel(notTypedLabel);
                IL.Emit(OpCodes.Ldloc, objLocal);
            }

            // List<object?> / $Array path
            IL.Emit(OpCodes.Isinst, _ctx.Types.ListOfObject);
            var isListLabel = IL.DefineLabel();
            IL.Emit(OpCodes.Brtrue, isListLabel);
            IL.Emit(OpCodes.Ldloc, objLocal);
            IL.Emit(OpCodes.Isinst, _ctx.Runtime!.TSArrayType);
            IL.Emit(OpCodes.Brfalse, fallbackLabel);

            // $Array path: extract Elements list
            IL.Emit(OpCodes.Ldloc, objLocal);
            IL.Emit(OpCodes.Castclass, _ctx.Runtime!.TSArrayType);
            IL.Emit(OpCodes.Callvirt, _ctx.Runtime!.TSArrayElementsGetter);
            var doGetLabel = IL.DefineLabel();
            IL.Emit(OpCodes.Br, doGetLabel);

            // List path: cast directly
            IL.MarkLabel(isListLabel);
            IL.Emit(OpCodes.Ldloc, objLocal);
            IL.Emit(OpCodes.Castclass, _ctx.Types.ListOfObject);

            // Common: list[index]
            IL.MarkLabel(doGetLabel);
            EmitExpressionAsDouble(gi.Index);
            IL.Emit(OpCodes.Conv_I4);
            IL.Emit(OpCodes.Callvirt, _ctx.Types.GetMethod(_ctx.Types.ListOfObject, "get_Item", _ctx.Types.Int32));
            SetStackUnknown();
            IL.Emit(OpCodes.Br, endLabel);

            // Fallback: generic dispatch
            IL.MarkLabel(fallbackLabel);
            IL.Emit(OpCodes.Ldloc, objLocal);
            EmitExpression(gi.Index);
            EmitBoxIfNeeded(gi.Index);
            IL.Emit(OpCodes.Call, _ctx.Runtime!.GetIndex);
            SetStackUnknown();

            IL.MarkLabel(endLabel);
            return;
        }

        EmitExpression(gi.Object);
        EmitBoxIfNeeded(gi.Object);
        EmitExpression(gi.Index);
        EmitBoxIfNeeded(gi.Index);
        IL.Emit(OpCodes.Call, _ctx.Runtime!.GetIndex);
    }

    protected override void EmitSetIndex(Expr.SetIndex si)
    {
        // Descriptor-driven fast path: when receiver is statically known to be an array,
        // emit direct List<T> access with auto-extension — skips runtime type dispatch,
        // index boxing, and Convert.ToInt32(object) overhead.
        var desc = ArrayElements.Resolve(_ctx.TypeMap?.Get(si.Object));
        if (desc != null)
        {
            var fallbackLabel = IL.DefineLabel();
            var endLabel = IL.DefineLabel();

            // Emit and coerce value based on descriptor
            EmitExpression(si.Value);
            if (desc.Kind == ArrayElementsKind.Double) EnsureDouble();
            else if (desc.Kind == ArrayElementsKind.Bool) EnsureBoolean();
            else EmitBoxIfNeeded(si.Value);

            var typedValueLocal = IL.DeclareLocal(desc.GetElementType(_ctx.Types));
            IL.Emit(OpCodes.Stloc, typedValueLocal);

            EmitExpression(si.Object);
            EnsureBoxed();

            var objLocal = IL.DeclareLocal(_ctx.Types.Object);
            IL.Emit(OpCodes.Dup);
            IL.Emit(OpCodes.Stloc, objLocal);

            // Typed fast path: isinst List<T> → direct SetArrayElement{Kind}
            if (desc.Kind != ArrayElementsKind.Object)
            {
                var listType = desc.GetListType(_ctx.Types);
                var notTypedLabel = IL.DefineLabel();
                IL.Emit(OpCodes.Isinst, listType);
                IL.Emit(OpCodes.Brfalse, notTypedLabel);

                IL.Emit(OpCodes.Ldloc, objLocal);
                IL.Emit(OpCodes.Castclass, listType);
                EmitExpressionAsDouble(si.Index);
                IL.Emit(OpCodes.Conv_I4);
                IL.Emit(OpCodes.Ldloc, typedValueLocal);
                IL.Emit(OpCodes.Call, desc.GetSetArrayElementMethod(_ctx.Runtime!));
                IL.Emit(OpCodes.Ldloc, typedValueLocal);
                SetStackType(desc.StackType);
                IL.Emit(OpCodes.Br, endLabel);

                // Not typed list: box value and fall through to List<object?> path
                IL.MarkLabel(notTypedLabel);
                IL.Emit(OpCodes.Ldloc, objLocal);
                IL.Emit(OpCodes.Ldloc, typedValueLocal);
                IL.Emit(OpCodes.Box, desc.GetElementType(_ctx.Types));
                var boxedValueLocal = IL.DeclareLocal(_ctx.Types.Object);
                IL.Emit(OpCodes.Stloc, boxedValueLocal);

                EmitSetIndexListObjectPath(si, objLocal, boxedValueLocal, fallbackLabel, endLabel);
                return;
            }

            // Object descriptor: go straight to List<object?> path
            EmitSetIndexListObjectPath(si, objLocal, typedValueLocal, fallbackLabel, endLabel);
            return;
        }

        // No static type info: full generic dispatch
        EmitExpression(si.Value);
        EmitBoxIfNeeded(si.Value);
        var valueLocalGeneric = IL.DeclareLocal(_ctx.Types.Object);
        IL.Emit(OpCodes.Stloc, valueLocalGeneric);

        EmitExpression(si.Object);
        EmitBoxIfNeeded(si.Object);
        EmitExpression(si.Index);
        EmitBoxIfNeeded(si.Index);
        IL.Emit(OpCodes.Ldloc, valueLocalGeneric);

        if (_ctx.IsStrictMode)
        {
            IL.Emit(OpCodes.Ldc_I4_1);
            IL.Emit(OpCodes.Call, _ctx.Runtime!.SetIndexStrict);
        }
        else
        {
            IL.Emit(OpCodes.Call, _ctx.Runtime!.SetIndex);
        }

        IL.Emit(OpCodes.Ldloc, valueLocalGeneric);
    }

    /// <summary>
    /// Emits the common List&lt;object?&gt; / $Array set path with frozen checks and fallback.
    /// Shared by all descriptor-driven SetIndex paths (typed miss fallthrough and object direct).
    /// Stack: obj is on the stack (from the isinst result). objLocal and valueLocal are populated.
    /// </summary>
    private void EmitSetIndexListObjectPath(
        Expr.SetIndex si, LocalBuilder objLocal, LocalBuilder valueLocal,
        Label fallbackLabel, Label endLabel)
    {
        // Check List<object?>
        IL.Emit(OpCodes.Isinst, _ctx.Types.ListOfObject);
        var isListLabel = IL.DefineLabel();
        IL.Emit(OpCodes.Brtrue, isListLabel);
        IL.Emit(OpCodes.Ldloc, objLocal);
        IL.Emit(OpCodes.Isinst, _ctx.Runtime!.TSArrayType);
        IL.Emit(OpCodes.Brfalse, fallbackLabel);

        // $Array path: check frozen, then extract Elements list
        IL.Emit(OpCodes.Ldloc, objLocal);
        IL.Emit(OpCodes.Castclass, _ctx.Runtime!.TSArrayType);
        var tsArrayLocal = IL.DeclareLocal(_ctx.Runtime!.TSArrayType);
        IL.Emit(OpCodes.Stloc, tsArrayLocal);
        IL.Emit(OpCodes.Ldloc, tsArrayLocal);
        IL.Emit(OpCodes.Callvirt, _ctx.Runtime!.TSArrayIsFrozenGetter);
        IL.Emit(OpCodes.Brtrue, fallbackLabel);
        IL.Emit(OpCodes.Ldloc, tsArrayLocal);
        IL.Emit(OpCodes.Callvirt, _ctx.Runtime!.TSArrayElementsGetter);
        var doSetLabel = IL.DefineLabel();
        IL.Emit(OpCodes.Br, doSetLabel);

        // List path: check frozen, then cast
        IL.MarkLabel(isListLabel);
        var frozenCheckLocal = IL.DeclareLocal(_ctx.Types.Object);
        IL.Emit(OpCodes.Ldsfld, _ctx.Runtime!.FrozenObjectsField);
        IL.Emit(OpCodes.Ldloc, objLocal);
        IL.Emit(OpCodes.Ldloca, frozenCheckLocal);
        IL.Emit(OpCodes.Callvirt, _ctx.Types.GetMethod(
            _ctx.Types.ConditionalWeakTable, "TryGetValue",
            _ctx.Types.Object, _ctx.Types.Object.MakeByRefType()));
        IL.Emit(OpCodes.Brtrue, fallbackLabel);
        IL.Emit(OpCodes.Ldloc, objLocal);
        IL.Emit(OpCodes.Castclass, _ctx.Types.ListOfObject);

        // Common: SetArrayElement(list, index, value)
        IL.MarkLabel(doSetLabel);
        EmitExpressionAsDouble(si.Index);
        IL.Emit(OpCodes.Conv_I4);
        IL.Emit(OpCodes.Ldloc, valueLocal);
        IL.Emit(OpCodes.Call, _ctx.Runtime!.SetArrayElement);
        IL.Emit(OpCodes.Ldloc, valueLocal);
        IL.Emit(OpCodes.Br, endLabel);

        // Fallback: generic dispatch
        IL.MarkLabel(fallbackLabel);
        IL.Emit(OpCodes.Ldloc, objLocal);
        EmitExpression(si.Index);
        EmitBoxIfNeeded(si.Index);
        IL.Emit(OpCodes.Ldloc, valueLocal);
        if (_ctx.IsStrictMode)
        {
            IL.Emit(OpCodes.Ldc_I4_1);
            IL.Emit(OpCodes.Call, _ctx.Runtime!.SetIndexStrict);
        }
        else
        {
            IL.Emit(OpCodes.Call, _ctx.Runtime!.SetIndex);
        }
        IL.Emit(OpCodes.Ldloc, valueLocal);

        IL.MarkLabel(endLabel);
    }

    /// <summary>
    /// Try to emit a direct getter call for known class instance types.
    /// Returns true if direct dispatch was emitted, false to fall back to runtime dispatch.
    /// </summary>
    private bool TryEmitDirectGetterCall(Expr receiver, TypeInfo? receiverType, string propertyName)
    {
        // Resolve TypeParameter constraints (e.g., T extends Animal → Instance(Animal))
        if (receiverType is TypeInfo.TypeParameter { Constraint: TypeInfo.Instance } tp)
            receiverType = tp.Constraint;

        // Only handle Instance types (e.g., let p: Person = ...)
        if (receiverType is not TypeInfo.Instance instance)
            return false;

        // Extract the class name from the instance's class type
        string? simpleClassName = instance.ClassType switch
        {
            TypeInfo.Class c => c.Name,
            _ => null
        };
        if (simpleClassName == null)
            return false;

        // Check if this is an external .NET type (@DotNetType)
        if (_ctx.TypeMapper.ExternalTypes.TryGetValue(simpleClassName, out var externalType))
        {
            EmitExternalPropertyGet(receiver, externalType, propertyName);
            return true;
        }

        // Resolve to qualified name for multi-module compilation
        string className = _ctx.ResolveClassName(simpleClassName);

        // Also check if the qualified name is an external type
        if (_ctx.TypeMapper.ExternalTypes.TryGetValue(className, out externalType))
        {
            EmitExternalPropertyGet(receiver, externalType, propertyName);
            return true;
        }

        // Convert TypeScript camelCase property name to .NET PascalCase for lookup
        string pascalPropertyName = NamingConventions.ToPascalCase(propertyName);

        // Look up the getter in the class hierarchy
        var getterBuilder = _ctx.ResolveInstanceGetter(className, pascalPropertyName);
        if (getterBuilder == null)
            return false;

        // Get the class type builder to cast the receiver
        if (!_ctx.Classes.TryGetValue(className, out var classType))
            return false;

        // Emit: ((ClassName)receiver).get_PropertyName()
        EmitExpression(receiver);
        EmitBoxIfNeeded(receiver);
        IL.Emit(OpCodes.Castclass, classType);
        IL.Emit(OpCodes.Callvirt, getterBuilder);

        // Check the actual return type of the getter method
        // Field properties have typed getters, but explicit accessors return object
        var getterReturnType = getterBuilder.ReturnType;

        if (getterReturnType.IsValueType)
        {
            // Getter returns a native value type - box it for internal code that expects object
            IL.Emit(OpCodes.Box, getterReturnType);
            SetStackUnknown();
        }
        else if (_ctx.Types.IsString(getterReturnType))
        {
            SetStackType(StackType.String);
        }
        else
        {
            // Reference types (including object) don't need boxing
            SetStackUnknown();
        }

        return true;
    }

    /// <summary>
    /// Try to emit a direct setter call for known class instance types.
    /// Returns true if direct dispatch was emitted, false to fall back to runtime dispatch.
    /// </summary>
    private bool TryEmitDirectSetterCall(Expr receiver, TypeInfo? receiverType, string propertyName, Expr value)
    {
        // Only handle Instance types (e.g., let p: Person = ...)
        if (receiverType is not TypeInfo.Instance instance)
            return false;

        // Extract the class name from the instance's class type
        string? simpleClassName = instance.ClassType switch
        {
            TypeInfo.Class c => c.Name,
            _ => null
        };
        if (simpleClassName == null)
            return false;

        // Check if this is an external .NET type (@DotNetType)
        if (_ctx.TypeMapper.ExternalTypes.TryGetValue(simpleClassName, out var externalType))
        {
            EmitExternalPropertySet(receiver, externalType, propertyName, value);
            return true;
        }

        // Resolve to qualified name for multi-module compilation
        string className = _ctx.ResolveClassName(simpleClassName);

        // Also check if the qualified name is an external type
        if (_ctx.TypeMapper.ExternalTypes.TryGetValue(className, out externalType))
        {
            EmitExternalPropertySet(receiver, externalType, propertyName, value);
            return true;
        }

        // Convert TypeScript camelCase property name to .NET PascalCase for lookup
        string pascalPropertyName = NamingConventions.ToPascalCase(propertyName);

        // Look up the setter in the class hierarchy
        var setterBuilder = _ctx.ResolveInstanceSetter(className, pascalPropertyName);
        if (setterBuilder == null)
            return false;

        // Get the class type builder to cast the receiver
        if (!_ctx.Classes.TryGetValue(className, out var classType))
            return false;

        // Get the actual parameter type of the setter method
        // Field properties have typed setters, but explicit accessors take object
        var setterParams = setterBuilder.GetParameters();
        var setterParamType = setterParams.Length > 0 ? setterParams[0].ParameterType : _ctx.Types.Object;

        // Emit: ((ClassName)receiver).set_PropertyName(value)
        // Also need to keep the value on the stack as the expression result
        // But first check if object is frozen - if so, skip setter call

        // Emit receiver and save for freeze check and potential setter call
        EmitExpression(receiver);
        EmitBoxIfNeeded(receiver);
        var receiverTemp = IL.DeclareLocal(_ctx.Types.Object);
        IL.Emit(OpCodes.Stloc, receiverTemp);

        // Check if frozen: _frozenObjects.TryGetValue(obj, out _)
        var notFrozenLabel = IL.DefineLabel();
        var endLabel = IL.DefineLabel();
        var frozenCheckLocal = IL.DeclareLocal(_ctx.Types.Object);
        IL.Emit(OpCodes.Ldsfld, _ctx.Runtime!.FrozenObjectsField);
        IL.Emit(OpCodes.Ldloc, receiverTemp);
        IL.Emit(OpCodes.Ldloca, frozenCheckLocal);
        IL.Emit(OpCodes.Callvirt, _ctx.Types.GetMethod(_ctx.Types.ConditionalWeakTable, "TryGetValue", _ctx.Types.Object, _ctx.Types.Object.MakeByRefType()));
        IL.Emit(OpCodes.Brfalse, notFrozenLabel);

        // Object is frozen - emit value but skip setter call
        // Just return the value as the expression result
        EmitExpression(value);
        EmitBoxIfNeeded(value);
        IL.Emit(OpCodes.Br, endLabel);

        // Not frozen - proceed with normal setter call
        IL.MarkLabel(notFrozenLabel);

        // Load receiver and cast to class type
        IL.Emit(OpCodes.Ldloc, receiverTemp);
        IL.Emit(OpCodes.Castclass, classType);

        // Emit value and convert to setter parameter type
        EmitExpression(value);

        // Check if setter returns void (field properties) or object (explicit accessors)
        var setterReturnsVoid = _ctx.Types.IsVoid(setterBuilder.ReturnType);

        // Save a copy for expression result (need to box if value type for consistent handling)
        if (setterParamType.IsValueType)
        {
            // For value types: box first, then dup, then unbox for setter
            EmitBoxIfNeeded(value);
            IL.Emit(OpCodes.Dup);
            var resultTemp = IL.DeclareLocal(_ctx.Types.Object);
            IL.Emit(OpCodes.Stloc, resultTemp);
            IL.Emit(OpCodes.Unbox_Any, setterParamType);
            IL.Emit(OpCodes.Callvirt, setterBuilder);
            // Pop setter return value if not void (explicit accessors return object)
            if (!setterReturnsVoid)
            {
                IL.Emit(OpCodes.Pop);
            }
            IL.Emit(OpCodes.Ldloc, resultTemp);
        }
        else
        {
            // For reference types (including object): dup, optionally cast, call setter
            EmitBoxIfNeeded(value);
            IL.Emit(OpCodes.Dup);
            var resultTemp = IL.DeclareLocal(_ctx.Types.Object);
            IL.Emit(OpCodes.Stloc, resultTemp);
            if (!_ctx.Types.IsObject(setterParamType))
            {
                IL.Emit(OpCodes.Castclass, setterParamType);
            }
            IL.Emit(OpCodes.Callvirt, setterBuilder);
            // Pop setter return value if not void (explicit accessors return object)
            if (!setterReturnsVoid)
            {
                IL.Emit(OpCodes.Pop);
            }
            IL.Emit(OpCodes.Ldloc, resultTemp);
        }

        IL.MarkLabel(endLabel);
        SetStackUnknown();  // Result is boxed object
        return true;
    }

    /// <summary>
    /// Tries to emit IL for process.stdin.isTTY, process.stdout.isTTY, process.stderr.isTTY property access.
    /// Returns true if the property was handled.
    /// </summary>
    private bool TryEmitProcessStreamProperty(Expr.Get g)
    {
        // Pattern: process.stdin.X, process.stdout.X, process.stderr.X
        // g.Object is Expr.Get { Object: Expr.Variable("process"), Name: "stdin/stdout/stderr" }

        if (g.Object is not Expr.Get streamGet)
            return false;

        if (streamGet.Object is not Expr.Variable processVar || processVar.Name.Lexeme != "process")
            return false;

        string streamName = streamGet.Name.Lexeme;
        string propertyName = g.Name.Lexeme;

        // Handle isTTY for all streams
        if (propertyName == "isTTY")
        {
            switch (streamName)
            {
                case "stdin":
                    IL.Emit(OpCodes.Call, _ctx.Runtime!.StdinIsTTY);
                    SetStackUnknown();
                    return true;
                case "stdout":
                    IL.Emit(OpCodes.Call, _ctx.Runtime!.StdoutIsTTY);
                    SetStackUnknown();
                    return true;
                case "stderr":
                    IL.Emit(OpCodes.Call, _ctx.Runtime!.StderrIsTTY);
                    SetStackUnknown();
                    return true;
            }
        }

        // Handle writable stream properties for stdout/stderr
        if (streamName is "stdout" or "stderr")
        {
            switch (propertyName)
            {
                case "writable":
                    IL.Emit(OpCodes.Ldc_I4_1);
                    IL.Emit(OpCodes.Box, _ctx.Types.Boolean);
                    SetStackUnknown();
                    return true;
                case "writableEnded":
                case "writableFinished":
                case "destroyed":
                    IL.Emit(OpCodes.Ldc_I4_0);
                    IL.Emit(OpCodes.Box, _ctx.Types.Boolean);
                    SetStackUnknown();
                    return true;
            }
        }

        // Handle readable stream properties for stdin
        if (streamName == "stdin")
        {
            switch (propertyName)
            {
                case "readable":
                    IL.Emit(OpCodes.Ldc_I4_1);
                    IL.Emit(OpCodes.Box, _ctx.Types.Boolean);
                    SetStackUnknown();
                    return true;
                case "readableEnded":
                case "destroyed":
                    IL.Emit(OpCodes.Ldc_I4_0);
                    IL.Emit(OpCodes.Box, _ctx.Types.Boolean);
                    SetStackUnknown();
                    return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Tries to emit IL for globalThis chained property access like globalThis.Math.PI, globalThis.console.log, etc.
    /// Returns true if the property was handled.
    /// </summary>
    private bool TryEmitGlobalThisChainedProperty(Expr.Get g)
    {
        // Pattern: globalThis.Math.PI, globalThis.console.log, etc.
        // g.Object is Expr.Get { Object: Expr.Variable("globalThis"), Name: "Math/JSON/console/etc" }
        // g.Name.Lexeme is "PI/parse/log/etc"

        if (g.Object is not Expr.Get innerGet)
            return false;

        if (innerGet.Object is not Expr.Variable globalThisVar || globalThisVar.Name.Lexeme != "globalThis")
            return false;

        string namespaceName = innerGet.Name.Lexeme;
        string propertyName = g.Name.Lexeme;

        // Try to use the static emitter for the inner namespace
        var staticStrategy = _ctx.TypeEmitterRegistry?.GetStaticStrategy(namespaceName);
        if (staticStrategy != null && staticStrategy.TryEmitStaticPropertyGet(this, propertyName))
        {
            SetStackUnknown();
            return true;
        }

        // Handle globalThis.globalThis.X case (self-reference)
        if (namespaceName == "globalThis")
        {
            // Treat globalThis.globalThis as just globalThis
            var selfStrategy = _ctx.TypeEmitterRegistry?.GetStaticStrategy("globalThis");
            if (selfStrategy != null && selfStrategy.TryEmitStaticPropertyGet(this, propertyName))
            {
                SetStackUnknown();
                return true;
            }
        }

        return false;
    }
}
