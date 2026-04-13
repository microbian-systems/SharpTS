using System.Reflection;
using System.Reflection.Emit;
using SharpTS.Compilation.Emitters;
using SharpTS.Diagnostics.Exceptions;
using SharpTS.Parsing;

namespace SharpTS.Compilation;

/// <summary>
/// Abstract base class for expression emission across different emitter types.
/// Provides unified dispatch logic and shared helper delegations.
/// </summary>
/// <remarks>
/// This base class centralizes the expression dispatch switch statement that was
/// duplicated across ILEmitter, AsyncMoveNextEmitter, GeneratorMoveNextEmitter,
/// AsyncArrowMoveNextEmitter, and AsyncGeneratorMoveNextEmitter.
///
/// All expression methods are abstract - subclasses must implement them.
/// EmitAwait and EmitYield are virtual and throw by default; async/generator
/// emitters override them with their implementations.
/// </remarks>
public abstract partial class ExpressionEmitterBase : IEmitterContext
{
    #region IEmitterContext Implementation

    CompilationContext IEmitterContext.Context => Ctx;
    ILGenerator IEmitterContext.IL => IL;
    void IEmitterContext.EmitExpression(Expr expr) => EmitExpression(expr);
    void IEmitterContext.EmitBoxIfNeeded(Expr expr) => EnsureBoxed();
    void IEmitterContext.EnsureBoxed() => EnsureBoxed();
    void IEmitterContext.EmitExpressionAsDouble(Expr expr) => EmitExpressionAsDouble(expr);
    void IEmitterContext.SetStackUnknown() => SetStackUnknown();
    void IEmitterContext.SetStackType(StackType type) => SetStackType(type);

    bool IEmitterContext.TryEmitConsoleMethod(Expr.Call call)
    {
        return _helpers.TryEmitConsoleMethod(
            call,
            arg => { EmitExpression(arg); EnsureBoxed(); },
            Ctx.Runtime!);
    }

    void IEmitterContext.EmitFetchCall(List<Expr> arguments) => EmitFetchCall(arguments);

    void IEmitterContext.EmitConversionForParameter(Expr expr, Type targetType) => EmitConversionForParameter(expr, targetType);

    void IEmitterContext.EmitDefaultForType(Type type) => EmitDefaultForType(type);

    #endregion

    protected readonly StateMachineEmitHelpers _helpers;

    protected abstract ILGenerator IL { get; }
    protected abstract CompilationContext Ctx { get; }
    protected abstract TypeProvider Types { get; }
    protected abstract IVariableResolver Resolver { get; }

    /// <summary>
    /// Gets the hoisted field for a variable name, or null if not hoisted.
    /// Override in state machine emitters to check the builder's variable fields.
    /// </summary>
    protected virtual FieldBuilder? GetHoistedVariableField(string name) => null;

    protected ExpressionEmitterBase(StateMachineEmitHelpers helpers)
    {
        _helpers = helpers;
    }

    #region Stack Type Delegation
    protected StackType StackType
    {
        get => _helpers.StackType;
        set => _helpers.StackType = value;
    }

    protected void SetStackUnknown() => _helpers.SetStackUnknown();
    protected void SetStackType(StackType type) => _helpers.SetStackType(type);
    #endregion

    #region Boxing and Type Conversion Delegations
    protected void EnsureBoxed() => _helpers.EnsureBoxed();
    protected void EnsureDouble() => _helpers.EnsureDouble();
    protected void EnsureBoolean() => _helpers.EnsureBoolean();
    protected void EnsureString() => _helpers.EnsureString();
    #endregion

    #region Constant Emission Delegations
    protected void EmitNullConstant() => _helpers.EmitNullConstant();
    protected void EmitUndefinedConstant() => _helpers.EmitUndefinedConstant();
    protected void EmitDoubleConstant(double value) => _helpers.EmitDoubleConstant(value);
    protected void EmitBoolConstant(bool value) => _helpers.EmitBoolConstant(value);
    protected void EmitStringConstant(string value) => _helpers.EmitStringConstant(value);
    #endregion

    #region Common Helper Wrappers - Delegated to StateMachineEmitHelpers

    // Boxing
    protected void EmitBoxedDoubleConstant(double value) => _helpers.EmitBoxedDoubleConstant(value);
    protected void EmitBoxedBoolConstant(bool value) => _helpers.EmitBoxedBoolConstant(value);
    protected void EmitBoxDouble() => _helpers.EmitBoxDouble();
    protected void EmitBoxBool() => _helpers.EmitBoxBool();

    // Arithmetic
    protected void EmitAdd_Double() => _helpers.EmitAdd_Double();
    protected void EmitSub_Double() => _helpers.EmitSub_Double();
    protected void EmitMul_Double() => _helpers.EmitMul_Double();
    protected void EmitDiv_Double() => _helpers.EmitDiv_Double();
    protected void EmitRem_Double() => _helpers.EmitRem_Double();
    protected void EmitNeg_Double() => _helpers.EmitNeg_Double();

    // Comparison
    protected void EmitClt_Boolean() => _helpers.EmitClt_Boolean();
    protected void EmitCgt_Boolean() => _helpers.EmitCgt_Boolean();
    protected void EmitCeq_Boolean() => _helpers.EmitCeq_Boolean();
    protected void EmitLessOrEqual_Boolean() => _helpers.EmitLessOrEqual_Boolean();
    protected void EmitGreaterOrEqual_Boolean() => _helpers.EmitGreaterOrEqual_Boolean();

    // Method calls
    protected void EmitCallUnknown(MethodInfo method) => _helpers.EmitCallUnknown(method);
    protected void EmitCallvirtUnknown(MethodInfo method) => _helpers.EmitCallvirtUnknown(method);
    protected void EmitCallString(MethodInfo method) => _helpers.EmitCallString(method);
    protected void EmitCallBoolean(MethodInfo method) => _helpers.EmitCallBoolean(method);
    protected void EmitCallDouble(MethodInfo method) => _helpers.EmitCallDouble(method);
    protected void EmitCallAndBoxDouble(MethodInfo method) => _helpers.EmitCallAndBoxDouble(method);
    protected void EmitCallAndBoxBool(MethodInfo method) => _helpers.EmitCallAndBoxBool(method);

    // Variable loads
    protected void EmitLdlocUnknown(LocalBuilder local) => _helpers.EmitLdlocUnknown(local);
    protected void EmitLdargUnknown(int argIndex) => _helpers.EmitLdargUnknown(argIndex);
    protected void EmitLdfldUnknown(FieldInfo field) => _helpers.EmitLdfldUnknown(field);

    // Specialized
    protected void EmitNewobjUnknown(ConstructorInfo ctor) => _helpers.EmitNewobjUnknown(ctor);
    protected void EmitConvertToDouble() => _helpers.EmitConvertToDouble();
    protected void EmitConvR8AndBox() => _helpers.EmitConvR8AndBox();
    protected void EmitObjectEqualsBoxed() => _helpers.EmitObjectEqualsBoxed();
    protected void EmitObjectNotEqualsBoxed() => _helpers.EmitObjectNotEqualsBoxed();

    #endregion

    #region Core Expression Dispatch
    /// <summary>
    /// Dispatches expression emission to the appropriate handler method.
    /// </summary>
    public virtual void EmitExpression(Expr expr)
    {
        switch (expr)
        {
            case Expr.Comma c:
                EmitComma(c);
                break;
            case Expr.Literal lit:
                EmitLiteral(lit);
                break;
            case Expr.Variable v:
                EmitVariable(v);
                break;
            case Expr.Assign a:
                EmitAssign(a);
                break;
            case Expr.Binary b:
                EmitBinary(b);
                break;
            case Expr.Logical l:
                EmitLogical(l);
                break;
            case Expr.Unary u:
                EmitUnary(u);
                break;
            case Expr.Delete del:
                EmitDelete(del);
                break;
            case Expr.Call c:
                if (c.Optional)
                    EmitOptionalCall(c);
                else
                    EmitCall(c);
                break;
            case Expr.Get g:
                EmitGet(g);
                break;
            case Expr.Set s:
                EmitSet(s);
                break;
            case Expr.GetPrivate gp:
                EmitGetPrivate(gp);
                break;
            case Expr.SetPrivate sp:
                EmitSetPrivate(sp);
                break;
            case Expr.CallPrivate cp:
                EmitCallPrivate(cp);
                break;
            case Expr.GetIndex gi:
                EmitGetIndex(gi);
                break;
            case Expr.SetIndex si:
                EmitSetIndex(si);
                break;
            case Expr.Ternary t:
                EmitTernary(t);
                break;
            case Expr.NullishCoalescing nc:
                EmitNullishCoalescing(nc);
                break;
            case Expr.TemplateLiteral tl:
                EmitTemplateLiteral(tl);
                break;
            case Expr.TaggedTemplateLiteral ttl:
                EmitTaggedTemplateLiteral(ttl);
                break;
            case Expr.ArrayLiteral al:
                EmitArrayLiteral(al);
                break;
            case Expr.ObjectLiteral ol:
                EmitObjectLiteral(ol);
                break;
            case Expr.New n:
                EmitNew(n);
                break;
            case Expr.This:
                EmitThis();
                break;
            case Expr.Super s:
                EmitSuper(s);
                break;
            case Expr.CompoundAssign ca:
                EmitCompoundAssign(ca);
                break;
            case Expr.CompoundSet cs:
                EmitCompoundSet(cs);
                break;
            case Expr.CompoundSetIndex csi:
                EmitCompoundSetIndex(csi);
                break;
            case Expr.LogicalAssign la:
                EmitLogicalAssign(la);
                break;
            case Expr.LogicalSet ls:
                EmitLogicalSet(ls);
                break;
            case Expr.LogicalSetIndex lsi:
                EmitLogicalSetIndex(lsi);
                break;
            case Expr.PrefixIncrement pi:
                EmitPrefixIncrement(pi);
                break;
            case Expr.PostfixIncrement poi:
                EmitPostfixIncrement(poi);
                break;
            case Expr.ArrowFunction af:
                EmitArrowFunction(af);
                break;
            case Expr.RegexLiteral re:
                EmitRegexLiteral(re);
                break;
            case Expr.DynamicImport di:
                EmitDynamicImport(di);
                break;
            case Expr.ImportMeta im:
                EmitImportMeta(im);
                break;
            case Expr.Grouping g:
                EmitGrouping(g);
                break;
            case Expr.TypeAssertion ta:
                EmitTypeAssertion(ta);
                break;
            case Expr.Satisfies sat:
                EmitSatisfies(sat);
                break;
            case Expr.NonNullAssertion nna:
                EmitNonNullAssertion(nna);
                break;
            case Expr.Spread sp:
                EmitSpread(sp);
                break;
            case Expr.Await aw:
                EmitAwait(aw);
                break;
            case Expr.Yield y:
                EmitYield(y);
                break;
            case Expr.ClassExpr ce:
                EmitClassExpression(ce);
                break;
            default:
                throw new CompileException($"Unhandled expression type in ILEmitter: {expr.GetType().Name}");
        }
    }
    #endregion

    #region Virtual Expression Methods with Default Implementations
    // These methods provide default implementations suitable for state machine emitters.
    // ILEmitter overrides most with stack-type-tracked and optimized versions.
    // AsyncArrowMoveNextEmitter overrides where capture indirection differs.

    /// <summary>
    /// Emits a literal value. Default uses helper methods (EmitNullConstant, EmitDoubleConstant, etc.).
    /// ILEmitter overrides for BigInteger/SharpTSUndefined support.
    /// AsyncArrowMoveNextEmitter overrides for eager boxing.
    /// </summary>
    protected virtual void EmitLiteral(Expr.Literal lit)
    {
        switch (lit.Value)
        {
            case null: EmitNullConstant(); break;
            case double d: EmitDoubleConstant(d); break;
            case bool b: EmitBoolConstant(b); break;
            case string s: EmitStringConstant(s); break;
            default: IL.Emit(OpCodes.Ldnull); SetStackUnknown(); break;
        }
    }

    /// <summary>
    /// Emits a binary operator expression. Default boxes both operands and delegates to TryEmitBinaryOperator.
    /// ILEmitter overrides with stack-type-tracked fast paths.
    /// </summary>
    protected virtual void EmitBinary(Expr.Binary b)
    {
        EmitExpression(b.Left);
        EnsureBoxed();
        EmitExpression(b.Right);
        EnsureBoxed();

        if (!_helpers.TryEmitBinaryOperator(b.Operator.Type, Ctx.Runtime!.Add, Ctx.Runtime!.Equals))
        {
            IL.Emit(OpCodes.Pop);
            IL.Emit(OpCodes.Ldnull);
            SetStackUnknown();
        }
    }

    /// <summary>
    /// Emits an index get expression (obj[index]). Default boxes both and calls Runtime.GetIndex.
    /// ILEmitter overrides with IndexTarget dispatch and stack type tracking.
    /// </summary>
    protected virtual void EmitGetIndex(Expr.GetIndex gi)
    {
        // globalThis[key] → GlobalThisGetProperty(key)
        if (gi.Object is Expr.Variable gtGetIdx && gtGetIdx.Name.Lexeme == "globalThis")
        {
            EmitExpression(gi.Index);
            EnsureBoxed();
            IL.Emit(OpCodes.Callvirt, Types.Object.GetMethod("ToString")!);
            IL.Emit(OpCodes.Call, Ctx.Runtime!.GlobalThisGetProperty);
            SetStackUnknown();
            return;
        }

        EmitExpression(gi.Object);
        EnsureBoxed();

        if (gi.Optional)
        {
            var nullishLabel = IL.DefineLabel();
            var endLabel = IL.DefineLabel();

            // Check for null
            IL.Emit(OpCodes.Dup);
            IL.Emit(OpCodes.Brfalse, nullishLabel);

            // Check for undefined
            IL.Emit(OpCodes.Dup);
            IL.Emit(OpCodes.Isinst, Ctx.Runtime!.UndefinedType);
            IL.Emit(OpCodes.Brtrue, nullishLabel);

            // Not nullish — proceed with index access
            EmitExpression(gi.Index);
            EnsureBoxed();
            IL.Emit(OpCodes.Call, Ctx.Runtime!.GetIndex);
            IL.Emit(OpCodes.Br, endLabel);

            IL.MarkLabel(nullishLabel);
            IL.Emit(OpCodes.Pop);
            IL.Emit(OpCodes.Ldsfld, Ctx.Runtime!.UndefinedInstance);

            IL.MarkLabel(endLabel);
            SetStackUnknown();
        }
        else
        {
            EmitExpression(gi.Index);
            EnsureBoxed();
            IL.Emit(OpCodes.Call, Ctx.Runtime!.GetIndex);
            SetStackUnknown();
        }
    }

    /// <summary>
    /// Emits an index set expression (obj[index] = value). Default saves value to local (SetIndex is void),
    /// emits obj + index, calls SetIndex, then pushes value back as expression result.
    /// ILEmitter overrides with IndexTarget dispatch.
    /// </summary>
    protected virtual void EmitSetIndex(Expr.SetIndex si)
    {
        // globalThis[key] = value → GlobalThisSetProperty(key, value)
        if (si.Object is Expr.Variable gtSetIdx && gtSetIdx.Name.Lexeme == "globalThis")
        {
            EmitExpression(si.Value);
            EnsureBoxed();
            var valueTemp = IL.DeclareLocal(typeof(object));
            IL.Emit(OpCodes.Stloc, valueTemp);
            EmitExpression(si.Index);
            EnsureBoxed();
            IL.Emit(OpCodes.Callvirt, Types.Object.GetMethod("ToString")!);
            IL.Emit(OpCodes.Ldloc, valueTemp);
            IL.Emit(OpCodes.Call, Ctx.Runtime!.GlobalThisSetProperty);
            IL.Emit(OpCodes.Ldloc, valueTemp);
            SetStackUnknown();
            return;
        }

        EmitExpression(si.Value);
        EnsureBoxed();
        var valueLocal = IL.DeclareLocal(typeof(object));
        IL.Emit(OpCodes.Stloc, valueLocal);

        EmitExpression(si.Object);
        EnsureBoxed();
        EmitExpression(si.Index);
        EnsureBoxed();
        IL.Emit(OpCodes.Ldloc, valueLocal);
        IL.Emit(OpCodes.Call, Ctx.Runtime!.SetIndex);

        IL.Emit(OpCodes.Ldloc, valueLocal);
        SetStackUnknown();
    }

    /// <summary>
    /// Emits 'this' expression. Default uses GetThisField() hook — loads from field if non-null, else null.
    /// ILEmitter overrides (Resolver.LoadThis). AsyncArrowMoveNextEmitter overrides (outer state machine capture).
    /// </summary>
    protected virtual void EmitThis()
    {
        var thisField = GetThisField();
        if (thisField != null)
        {
            IL.Emit(OpCodes.Ldarg_0);
            IL.Emit(OpCodes.Ldfld, thisField);
            SetStackUnknown();
        }
        else
        {
            IL.Emit(OpCodes.Ldnull);
            SetStackType(StackType.Null);
        }
    }

    /// <summary>
    /// Emits a super method access. Default loads 'this' via EmitThis(), then calls GetSuperMethod.
    /// ILEmitter and AsyncArrowMoveNextEmitter override with their own this-loading logic.
    /// </summary>
    protected virtual void EmitSuper(Expr.Super s)
    {
        var thisField = GetThisField();
        if (thisField != null)
        {
            IL.Emit(OpCodes.Ldarg_0);
            IL.Emit(OpCodes.Ldfld, thisField);
        }
        else
        {
            IL.Emit(OpCodes.Ldnull);
        }

        IL.Emit(OpCodes.Ldstr, s.Method?.Lexeme ?? "constructor");
        IL.Emit(OpCodes.Call, Ctx.Runtime!.GetSuperMethod);
        SetStackUnknown();
    }

    /// <summary>
    /// Emits a variable load. Default uses resolver fallback chain:
    /// resolver → TopLevelStaticVars → Functions → NamespaceFields → CapturedTopLevelVars → null.
    /// ILEmitter overrides (pseudo-variables, class types, inner functions, ReferenceError).
    /// AsyncArrowMoveNextEmitter overrides (capture indirection).
    /// </summary>
    protected virtual void EmitVariable(Expr.Variable v)
    {
        string name = v.Name.Lexeme;

        var stackType = Resolver.TryLoadVariable(name);
        if (stackType != null)
        {
            SetStackType(stackType.Value);
            return;
        }

        // CommonJS: bare `exports` → ldsfld $exports of the current CJS module.
        if (TryEmitCjsVariable(name)) return;

        if (TryEmitGlobalVariable(name)) return;

        IL.Emit(OpCodes.Ldnull);
        SetStackUnknown();
    }

    /// <summary>
    /// Emits a variable assignment. Default emits value, dups, then tries global store or resolver.
    /// ILEmitter overrides (pseudo-variables, class types, inner functions, ReferenceError).
    /// AsyncArrowMoveNextEmitter overrides (capture indirection).
    /// </summary>
    protected virtual void EmitAssign(Expr.Assign a)
    {
        string name = a.Name.Lexeme;

        EmitExpression(a.Value);
        EnsureBoxed();
        IL.Emit(OpCodes.Dup);

        if (TryEmitGlobalStore(name)) return;

        Resolver.TryStoreVariable(name);
        SetStackUnknown();
    }

    /// <summary>
    /// Emits a property set (obj.prop = value). Default checks static fields, TypeEmitterRegistry,
    /// then falls back to dynamic SetProperty.
    /// ILEmitter overrides (property descriptor, additional patterns).
    /// </summary>
    protected virtual void EmitSet(Expr.Set s)
    {
        // CommonJS: `module.exports = X` → stsfld $exports.
        if (TryEmitCjsSet(s)) return;

        // Handle globalThis.x = value
        if (s.Object is Expr.Variable gtVar && gtVar.Name.Lexeme == "globalThis")
        {
            EmitExpression(s.Value);
            EnsureBoxed();
            var gtResultTemp = IL.DeclareLocal(typeof(object));
            IL.Emit(OpCodes.Stloc, gtResultTemp);
            IL.Emit(OpCodes.Ldstr, s.Name.Lexeme);
            IL.Emit(OpCodes.Ldloc, gtResultTemp);
            IL.Emit(OpCodes.Call, Ctx.Runtime!.GlobalThisSetProperty);
            IL.Emit(OpCodes.Ldloc, gtResultTemp);
            SetStackUnknown();
            return;
        }

        // Handle static field assignment: Class.field = value
        if (s.Object is Expr.Variable classVar &&
            Ctx.Classes.TryGetValue(Ctx.ResolveClassName(classVar.Name.Lexeme), out _))
        {
            string resolvedClassName = Ctx.ResolveClassName(classVar.Name.Lexeme);
            if (Ctx.ClassRegistry!.TryGetStaticField(resolvedClassName, s.Name.Lexeme, out var staticField))
            {
                EmitExpression(s.Value);
                EnsureBoxed();
                IL.Emit(OpCodes.Dup);
                IL.Emit(OpCodes.Stsfld, staticField!);
                SetStackUnknown();
                return;
            }
        }

        // Type-first dispatch: property setter via TypeEmitterRegistry
        if (TryEmitTypeRegistryPropertySet(s)) return;

        // Default: dynamic property assignment
        EmitExpression(s.Object);
        EnsureBoxed();
        IL.Emit(OpCodes.Ldstr, s.Name.Lexeme);
        EmitExpression(s.Value);
        EnsureBoxed();

        IL.Emit(OpCodes.Dup);
        var resultTemp = IL.DeclareLocal(typeof(object));
        IL.Emit(OpCodes.Stloc, resultTemp);

        IL.Emit(OpCodes.Call, Ctx.Runtime!.SetProperty);
        IL.Emit(OpCodes.Ldloc, resultTemp);
        SetStackUnknown();
    }

    /// <summary>
    /// Emits a private field get (#field). Default uses ConditionalWeakTable pattern.
    /// ILEmitter overrides with its own implementation.
    /// </summary>
    protected virtual void EmitGetPrivate(Expr.GetPrivate gp)
    {
        string fieldName = gp.Name.Lexeme;
        if (fieldName.StartsWith('#'))
            fieldName = fieldName[1..];

        string? className = Ctx.CurrentClassName;
        if (className == null)
        {
            IL.Emit(OpCodes.Ldstr, $"Cannot access private field '#{fieldName}' - class context not available");
            IL.Emit(OpCodes.Newobj, Types.InvalidOperationExceptionCtorString);
            IL.Emit(OpCodes.Throw);
            return;
        }

        if (gp.Object is Expr.Variable classVar &&
            classVar.Name.Lexeme == Ctx.CurrentClassShortName &&
            Ctx.ClassRegistry!.TryGetStaticPrivateField(className, fieldName, out var staticField))
        {
            IL.Emit(OpCodes.Ldsfld, staticField!);
            SetStackUnknown();
            return;
        }

        var storageField = Ctx.ClassRegistry!.GetPrivateFieldStorage(className);
        if (storageField != null)
        {
            var cwtType = typeof(System.Runtime.CompilerServices.ConditionalWeakTable<,>)
                .MakeGenericType(typeof(object), typeof(Dictionary<string, object?>));
            var dictType = typeof(Dictionary<string, object?>);
            var dictLocal = IL.DeclareLocal(dictType);

            IL.Emit(OpCodes.Ldsfld, storageField);
            EmitExpression(gp.Object);
            EnsureBoxed();
            IL.Emit(OpCodes.Ldloca, dictLocal);
            var tryGetValueMethod = cwtType.GetMethod("TryGetValue", [typeof(object), dictType.MakeByRefType()])!;
            IL.Emit(OpCodes.Callvirt, tryGetValueMethod);

            var successLabel = IL.DefineLabel();
            IL.Emit(OpCodes.Brtrue, successLabel);
            IL.Emit(OpCodes.Ldstr, $"TypeError: Cannot read private member #{fieldName} from an object whose class did not declare it");
            IL.Emit(OpCodes.Newobj, Types.ExceptionCtorString);
            IL.Emit(OpCodes.Throw);
            IL.MarkLabel(successLabel);

            IL.Emit(OpCodes.Ldloc, dictLocal);
            IL.Emit(OpCodes.Ldstr, fieldName);
            IL.Emit(OpCodes.Callvirt, dictType.GetMethod("get_Item", [typeof(string)])!);
            SetStackUnknown();
            return;
        }

        if (Ctx.ClassRegistry!.TryGetStaticPrivateField(className, fieldName, out var fallbackStaticField))
        {
            IL.Emit(OpCodes.Ldsfld, fallbackStaticField!);
            SetStackUnknown();
            return;
        }

        IL.Emit(OpCodes.Ldstr, $"Private field '#{fieldName}' not found in class '{className}'");
        IL.Emit(OpCodes.Newobj, Types.ExceptionCtorString);
        IL.Emit(OpCodes.Throw);
    }

    /// <summary>
    /// Emits a private field set (#field = value). Default uses ConditionalWeakTable pattern.
    /// ILEmitter overrides with its own implementation.
    /// </summary>
    protected virtual void EmitSetPrivate(Expr.SetPrivate sp)
    {
        string fieldName = sp.Name.Lexeme;
        if (fieldName.StartsWith('#'))
            fieldName = fieldName[1..];

        string? className = Ctx.CurrentClassName;
        if (className == null)
        {
            IL.Emit(OpCodes.Ldstr, $"Cannot write private field '#{fieldName}' - class context not available");
            IL.Emit(OpCodes.Newobj, Types.InvalidOperationExceptionCtorString);
            IL.Emit(OpCodes.Throw);
            return;
        }

        if (sp.Object is Expr.Variable classVar &&
            classVar.Name.Lexeme == Ctx.CurrentClassShortName &&
            Ctx.ClassRegistry!.TryGetStaticPrivateField(className, fieldName, out var staticField))
        {
            EmitExpression(sp.Value);
            EnsureBoxed();
            IL.Emit(OpCodes.Dup);
            IL.Emit(OpCodes.Stsfld, staticField!);
            SetStackUnknown();
            return;
        }

        var storageField = Ctx.ClassRegistry!.GetPrivateFieldStorage(className);
        if (storageField != null)
        {
            var cwtType = typeof(System.Runtime.CompilerServices.ConditionalWeakTable<,>)
                .MakeGenericType(typeof(object), typeof(Dictionary<string, object?>));
            var dictType = typeof(Dictionary<string, object?>);
            var dictLocal = IL.DeclareLocal(dictType);
            var valueLocal = IL.DeclareLocal(typeof(object));

            IL.Emit(OpCodes.Ldsfld, storageField);
            EmitExpression(sp.Object);
            EnsureBoxed();
            IL.Emit(OpCodes.Ldloca, dictLocal);
            var tryGetValueMethod = cwtType.GetMethod("TryGetValue", [typeof(object), dictType.MakeByRefType()])!;
            IL.Emit(OpCodes.Callvirt, tryGetValueMethod);

            var successLabel = IL.DefineLabel();
            IL.Emit(OpCodes.Brtrue, successLabel);
            IL.Emit(OpCodes.Ldstr, $"TypeError: Cannot write private member #{fieldName} to an object whose class did not declare it");
            IL.Emit(OpCodes.Newobj, Types.ExceptionCtorString);
            IL.Emit(OpCodes.Throw);
            IL.MarkLabel(successLabel);

            EmitExpression(sp.Value);
            EnsureBoxed();
            IL.Emit(OpCodes.Stloc, valueLocal);
            IL.Emit(OpCodes.Ldloc, dictLocal);
            IL.Emit(OpCodes.Ldstr, fieldName);
            IL.Emit(OpCodes.Ldloc, valueLocal);
            IL.Emit(OpCodes.Callvirt, dictType.GetMethod("set_Item", [typeof(string), typeof(object)])!);
            IL.Emit(OpCodes.Ldloc, valueLocal);
            SetStackUnknown();
            return;
        }

        if (Ctx.ClassRegistry!.TryGetStaticPrivateField(className, fieldName, out var fallbackStaticField))
        {
            EmitExpression(sp.Value);
            EnsureBoxed();
            IL.Emit(OpCodes.Dup);
            IL.Emit(OpCodes.Stsfld, fallbackStaticField!);
            SetStackUnknown();
            return;
        }

        IL.Emit(OpCodes.Ldstr, $"Private field '#{fieldName}' not found in class '{className}'");
        IL.Emit(OpCodes.Newobj, Types.ExceptionCtorString);
        IL.Emit(OpCodes.Throw);
    }

    /// <summary>
    /// Emits a private method call (#method(args)). Default uses ConditionalWeakTable pattern.
    /// ILEmitter overrides with its own implementation.
    /// </summary>
    protected virtual void EmitCallPrivate(Expr.CallPrivate cp)
    {
        string methodName = cp.Name.Lexeme;
        if (methodName.StartsWith('#'))
            methodName = methodName[1..];

        string? className = Ctx.CurrentClassName;
        if (className == null)
        {
            IL.Emit(OpCodes.Ldstr, $"Cannot call private method '#{methodName}' - class context not available");
            IL.Emit(OpCodes.Newobj, Types.InvalidOperationExceptionCtorString);
            IL.Emit(OpCodes.Throw);
            return;
        }

        if (cp.Object is Expr.Variable classVar &&
            classVar.Name.Lexeme == Ctx.CurrentClassShortName &&
            Ctx.ClassRegistry!.TryGetStaticPrivateMethod(className, methodName, out var staticMethod))
        {
            foreach (var arg in cp.Arguments)
            {
                EmitExpression(arg);
                EnsureBoxed();
            }
            IL.Emit(OpCodes.Call, staticMethod!);
            SetStackUnknown();
            return;
        }

        if (Ctx.ClassRegistry!.TryGetPrivateMethod(className, methodName, out var instanceMethod))
        {
            var storageField = Ctx.ClassRegistry!.GetPrivateFieldStorage(className);
            if (storageField != null)
            {
                var cwtType = typeof(System.Runtime.CompilerServices.ConditionalWeakTable<,>)
                    .MakeGenericType(typeof(object), typeof(Dictionary<string, object?>));
                var dictType = typeof(Dictionary<string, object?>);
                var objLocal = IL.DeclareLocal(typeof(object));
                var dictLocal = IL.DeclareLocal(dictType);

                EmitExpression(cp.Object);
                EnsureBoxed();
                IL.Emit(OpCodes.Stloc, objLocal);

                IL.Emit(OpCodes.Ldsfld, storageField);
                IL.Emit(OpCodes.Ldloc, objLocal);
                IL.Emit(OpCodes.Ldloca, dictLocal);
                var tryGetValueMethod = cwtType.GetMethod("TryGetValue", [typeof(object), dictType.MakeByRefType()])!;
                IL.Emit(OpCodes.Callvirt, tryGetValueMethod);

                var validLabel = IL.DefineLabel();
                IL.Emit(OpCodes.Brtrue, validLabel);
                IL.Emit(OpCodes.Ldstr, $"TypeError: Cannot call private method #{methodName} on an object whose class did not declare it");
                IL.Emit(OpCodes.Newobj, Types.ExceptionCtorString);
                IL.Emit(OpCodes.Throw);
                IL.MarkLabel(validLabel);

                IL.Emit(OpCodes.Ldloc, objLocal);
                if (Ctx.CurrentClassBuilder != null)
                    IL.Emit(OpCodes.Castclass, Ctx.CurrentClassBuilder);

                foreach (var arg in cp.Arguments)
                {
                    EmitExpression(arg);
                    EnsureBoxed();
                }

                IL.Emit(OpCodes.Callvirt, instanceMethod!);
                SetStackUnknown();
                return;
            }
            else
            {
                // No private field storage — skip brand check
                EmitExpression(cp.Object);
                EnsureBoxed();
                if (Ctx.CurrentClassBuilder != null)
                    IL.Emit(OpCodes.Castclass, Ctx.CurrentClassBuilder);

                foreach (var arg in cp.Arguments)
                {
                    EmitExpression(arg);
                    EnsureBoxed();
                }

                IL.Emit(OpCodes.Callvirt, instanceMethod!);
                SetStackUnknown();
                return;
            }
        }

        IL.Emit(OpCodes.Ldstr, $"Private method '#{methodName}' not found in class '{className}'");
        IL.Emit(OpCodes.Newobj, Types.ExceptionCtorString);
        IL.Emit(OpCodes.Throw);
    }

    /// <summary>
    /// Emits a template literal. Default uses two-phase pattern (eval to temps, then build string)
    /// which is safe across await/yield boundaries.
    /// ILEmitter overrides with array-based ConcatTemplate approach.
    /// </summary>
    protected virtual void EmitTemplateLiteral(Expr.TemplateLiteral tl)
    {
        if (tl.Strings.Count == 0 && tl.Expressions.Count == 0)
        {
            IL.Emit(OpCodes.Ldstr, "");
            SetStackType(StackType.String);
            return;
        }

        // Phase 1: Evaluate all expressions to temps (awaits/yields happen here)
        var exprTemps = new List<LocalBuilder>();
        for (int i = 0; i < tl.Expressions.Count; i++)
        {
            EmitExpression(tl.Expressions[i]);
            EnsureBoxed();
            var temp = IL.DeclareLocal(typeof(object));
            IL.Emit(OpCodes.Stloc, temp);
            exprTemps.Add(temp);
        }

        // Phase 2: Build string from temps (no awaits, stack safe)
        IL.Emit(OpCodes.Ldstr, tl.Strings[0]);

        for (int i = 0; i < exprTemps.Count; i++)
        {
            IL.Emit(OpCodes.Ldloc, exprTemps[i]);
            IL.Emit(OpCodes.Call, Ctx.Runtime!.Stringify);
            IL.Emit(OpCodes.Call, Types.StringConcat2);

            if (i + 1 < tl.Strings.Count)
            {
                IL.Emit(OpCodes.Ldstr, tl.Strings[i + 1]);
                IL.Emit(OpCodes.Call, Types.StringConcat2);
            }
        }
        SetStackType(StackType.String);
    }

    /// <summary>
    /// Emits a tagged template literal. Default uses two-phase pattern.
    /// ILEmitter overrides with its own implementation.
    /// </summary>
    protected virtual void EmitTaggedTemplateLiteral(Expr.TaggedTemplateLiteral ttl)
    {
        // Phase 1: Evaluate tag and all expressions to temps
        EmitExpression(ttl.Tag);
        EnsureBoxed();
        var tagTemp = IL.DeclareLocal(typeof(object));
        IL.Emit(OpCodes.Stloc, tagTemp);

        var exprTemps = new List<LocalBuilder>();
        for (int i = 0; i < ttl.Expressions.Count; i++)
        {
            EmitExpression(ttl.Expressions[i]);
            EnsureBoxed();
            var temp = IL.DeclareLocal(typeof(object));
            IL.Emit(OpCodes.Stloc, temp);
            exprTemps.Add(temp);
        }

        // Phase 2: Build arrays and call from temps
        IL.Emit(OpCodes.Ldloc, tagTemp);

        IL.Emit(OpCodes.Ldc_I4, ttl.CookedStrings.Count);
        IL.Emit(OpCodes.Newarr, typeof(object));
        for (int i = 0; i < ttl.CookedStrings.Count; i++)
        {
            IL.Emit(OpCodes.Dup);
            IL.Emit(OpCodes.Ldc_I4, i);
            if (ttl.CookedStrings[i] != null)
                IL.Emit(OpCodes.Ldstr, ttl.CookedStrings[i]!);
            else
                IL.Emit(OpCodes.Ldnull);
            IL.Emit(OpCodes.Stelem_Ref);
        }

        IL.Emit(OpCodes.Ldc_I4, ttl.RawStrings.Count);
        IL.Emit(OpCodes.Newarr, typeof(string));
        for (int i = 0; i < ttl.RawStrings.Count; i++)
        {
            IL.Emit(OpCodes.Dup);
            IL.Emit(OpCodes.Ldc_I4, i);
            IL.Emit(OpCodes.Ldstr, ttl.RawStrings[i]);
            IL.Emit(OpCodes.Stelem_Ref);
        }

        IL.Emit(OpCodes.Ldc_I4, ttl.Expressions.Count);
        IL.Emit(OpCodes.Newarr, typeof(object));
        for (int i = 0; i < exprTemps.Count; i++)
        {
            IL.Emit(OpCodes.Dup);
            IL.Emit(OpCodes.Ldc_I4, i);
            IL.Emit(OpCodes.Ldloc, exprTemps[i]);
            IL.Emit(OpCodes.Stelem_Ref);
        }

        IL.Emit(OpCodes.Call, Ctx.Runtime!.InvokeTaggedTemplate);
        SetStackUnknown();
    }

    /// <summary>
    /// Emits an array literal with spread support. Default handles simple arrays and spread-concatenation.
    /// ILEmitter overrides with stack-type-tracked array/concat APIs.
    /// </summary>
    protected virtual void EmitArrayLiteral(Expr.ArrayLiteral a)
    {
        bool hasSpreads = a.Elements.Any(e => e is Expr.Spread);

        if (!hasSpreads)
        {
            IL.Emit(OpCodes.Ldc_I4, a.Elements.Count);
            IL.Emit(OpCodes.Newarr, typeof(object));

            for (int i = 0; i < a.Elements.Count; i++)
            {
                IL.Emit(OpCodes.Dup);
                IL.Emit(OpCodes.Ldc_I4, i);
                EmitExpression(a.Elements[i]);
                EnsureBoxed();
                IL.Emit(OpCodes.Stelem_Ref);
            }

            IL.Emit(OpCodes.Call, Ctx.Runtime!.CreateArray);
        }
        else
        {
            IL.Emit(OpCodes.Ldc_I4, a.Elements.Count);
            IL.Emit(OpCodes.Newarr, typeof(object));

            for (int i = 0; i < a.Elements.Count; i++)
            {
                IL.Emit(OpCodes.Dup);
                IL.Emit(OpCodes.Ldc_I4, i);

                if (a.Elements[i] is Expr.Spread spread)
                {
                    EmitExpression(spread.Expression);
                    EnsureBoxed();
                }
                else
                {
                    IL.Emit(OpCodes.Ldc_I4, 1);
                    IL.Emit(OpCodes.Newarr, typeof(object));
                    IL.Emit(OpCodes.Dup);
                    IL.Emit(OpCodes.Ldc_I4_0);
                    EmitExpression(a.Elements[i]);
                    EnsureBoxed();
                    IL.Emit(OpCodes.Stelem_Ref);
                    IL.Emit(OpCodes.Call, Ctx.Runtime!.CreateArray);
                }

                IL.Emit(OpCodes.Stelem_Ref);
            }

            IL.Emit(OpCodes.Ldsfld, Ctx.Runtime!.SymbolIterator);
            IL.Emit(OpCodes.Ldtoken, Ctx.Runtime!.RuntimeType);
            IL.Emit(OpCodes.Call, Types.TypeGetTypeFromHandle);
            IL.Emit(OpCodes.Call, Ctx.Runtime!.ConcatArrays);
        }
        SetStackUnknown();
    }

    /// <summary>
    /// Emits an object literal with support for spreads, computed keys, and accessors.
    /// ILEmitter overrides with stack-type-tracked implementation.
    /// </summary>
    protected virtual void EmitObjectLiteral(Expr.ObjectLiteral o)
    {
        bool hasSpreads = o.Properties.Any(p => p.IsSpread);
        bool hasComputedKeys = o.Properties.Any(p => p.Key is Expr.ComputedKey);
        bool hasAccessors = o.Properties.Any(p => p.Kind is Expr.ObjectPropertyKind.Getter or Expr.ObjectPropertyKind.Setter);

        if (hasAccessors)
        {
            EmitObjectLiteralWithAccessors(o);
        }
        else if (!hasSpreads && !hasComputedKeys)
        {
            IL.Emit(OpCodes.Newobj, Types.DictionaryStringObjectCtor);

            foreach (var prop in o.Properties)
            {
                IL.Emit(OpCodes.Dup);
                EmitStaticPropertyKey(prop.Key!);
                EmitExpression(prop.Value);
                EnsureBoxed();
                IL.Emit(OpCodes.Callvirt, Types.DictionaryStringObjectSetItem);
            }

            IL.Emit(OpCodes.Call, Ctx.Runtime!.CreateObject);
        }
        else
        {
            IL.Emit(OpCodes.Newobj, Types.DictionaryStringObjectNullableCtor);

            foreach (var prop in o.Properties)
            {
                IL.Emit(OpCodes.Dup);

                if (prop.IsSpread)
                {
                    EmitExpression(prop.Value);
                    EnsureBoxed();
                    IL.Emit(OpCodes.Call, Ctx.Runtime!.MergeIntoObject);
                }
                else if (prop.Key is Expr.ComputedKey ck)
                {
                    EmitExpression(ck.Expression);
                    EnsureBoxed();
                    EmitExpression(prop.Value);
                    EnsureBoxed();
                    IL.Emit(OpCodes.Call, Ctx.Runtime!.SetIndex);
                }
                else
                {
                    EmitStaticPropertyKey(prop.Key!);
                    EmitExpression(prop.Value);
                    EnsureBoxed();
                    IL.Emit(OpCodes.Callvirt, Types.DictionaryStringObjectNullableSetItem);
                }
            }

            IL.Emit(OpCodes.Call, Ctx.Runtime!.CreateObject);
        }
        SetStackUnknown();
    }

    /// <summary>
    /// Emits an object literal with getter/setter accessors using the $Object type.
    /// </summary>
    protected virtual void EmitObjectLiteralWithAccessors(Expr.ObjectLiteral o)
    {
        IL.Emit(OpCodes.Newobj, Types.DictionaryStringObjectNullableCtor);
        IL.Emit(OpCodes.Newobj, Ctx.Runtime!.TSObjectCtor);

        var objLocal = IL.DeclareLocal(Ctx.Runtime!.TSObjectType);
        IL.Emit(OpCodes.Stloc, objLocal);

        foreach (var prop in o.Properties)
        {
            if (prop.IsSpread)
            {
                IL.Emit(OpCodes.Ldloc, objLocal);
                EmitExpression(prop.Value);
                EnsureBoxed();
                IL.Emit(OpCodes.Call, Ctx.Runtime!.MergeIntoTSObject);
                continue;
            }

            string propKey = GetPropertyKeyString(prop.Key!);

            switch (prop.Kind)
            {
                case Expr.ObjectPropertyKind.Getter:
                    IL.Emit(OpCodes.Ldloc, objLocal);
                    IL.Emit(OpCodes.Ldstr, propKey);
                    EmitExpression(prop.Value);
                    EnsureBoxed();
                    IL.Emit(OpCodes.Callvirt, Ctx.Runtime!.TSObjectDefineGetter);
                    break;

                case Expr.ObjectPropertyKind.Setter:
                    IL.Emit(OpCodes.Ldloc, objLocal);
                    IL.Emit(OpCodes.Ldstr, propKey);
                    EmitExpression(prop.Value);
                    EnsureBoxed();
                    IL.Emit(OpCodes.Callvirt, Ctx.Runtime!.TSObjectDefineSetter);
                    break;

                default:
                    IL.Emit(OpCodes.Ldloc, objLocal);
                    IL.Emit(OpCodes.Ldstr, propKey);
                    EmitExpression(prop.Value);
                    EnsureBoxed();
                    IL.Emit(OpCodes.Callvirt, Ctx.Runtime!.TSObjectSetProperty);
                    break;
            }
        }

        IL.Emit(OpCodes.Ldloc, objLocal);
        SetStackUnknown();
    }

    /// <summary>
    /// Extracts the string key from a property key expression.
    /// </summary>
    protected static string GetPropertyKeyString(Expr.PropertyKey key)
    {
        return key switch
        {
            Expr.IdentifierKey ik => ik.Name.Lexeme,
            Expr.LiteralKey lk when lk.Literal.Type == TokenType.STRING => (string)lk.Literal.Literal!,
            Expr.LiteralKey lk when lk.Literal.Type == TokenType.NUMBER => lk.Literal.Literal!.ToString()!,
            Expr.ComputedKey => throw new Diagnostics.Exceptions.CompileException("Computed keys not supported in accessor context"),
            _ => throw new Diagnostics.Exceptions.CompileException($"Unexpected property key type: {key.GetType().Name}")
        };
    }

    /// <summary>
    /// Emits a static property key (identifier, string literal, or number literal) as a string.
    /// </summary>
    protected virtual void EmitStaticPropertyKey(Expr.PropertyKey key)
    {
        switch (key)
        {
            case Expr.IdentifierKey ik:
                IL.Emit(OpCodes.Ldstr, ik.Name.Lexeme);
                break;
            case Expr.LiteralKey lk when lk.Literal.Type == TokenType.STRING:
                IL.Emit(OpCodes.Ldstr, (string)lk.Literal.Literal!);
                break;
            case Expr.LiteralKey lk when lk.Literal.Type == TokenType.NUMBER:
                IL.Emit(OpCodes.Ldstr, lk.Literal.Literal!.ToString()!);
                break;
            default:
                throw new Diagnostics.Exceptions.CompileException($"Unexpected static property key type: {key.GetType().Name}");
        }
    }

    /// <summary>
    /// Emits a new expression (constructor call). Default handles built-in, Intl, module-qualified,
    /// and user-class constructors with generic type support and Activator.CreateInstance fallback.
    /// ILEmitter overrides (60+ additional built-in cases, inner class constructors, enum constructors).
    /// </summary>
    protected virtual void EmitNew(Expr.New n)
    {
        var (namespaceParts, className) = ExtractQualifiedNameFromCallee(n.Callee);

        if (namespaceParts.Count == 0 && n.Callee is Expr.Variable && TryEmitBuiltInConstructor(className, n.Arguments))
            return;

        if (TryEmitIntlConstructor(namespaceParts, className, n.Arguments))
            return;

        if (TryEmitModuleQualifiedConstructor(namespaceParts, className, n.Arguments))
            return;

        EmitUserClassNew(namespaceParts, className, n);
    }

    /// <summary>
    /// Emits a user-class constructor call with generic type support, argument temps (safe across
    /// await/yield boundaries), and Activator.CreateInstance fallback.
    /// </summary>
    protected virtual void EmitUserClassNew(List<string> namespaceParts, string className, Expr.New n)
    {
        string resolvedClassName = ResolveClassNameForNew(namespaceParts, className);

        // Check class expression constructors (e.g., const C = class { }; new C())
        if (Ctx.VarToClassExpr != null &&
            Ctx.VarToClassExpr.TryGetValue(className, out var classExpr) &&
            Ctx.ClassExprConstructors != null &&
            Ctx.ClassExprConstructors.TryGetValue(classExpr, out var classExprCtor))
        {
            EmitClassExprConstruction(classExprCtor, n);
            return;
        }

        var ctorBuilder = Ctx.ClassRegistry?.GetConstructorByQualifiedName(resolvedClassName);
        if (Ctx.Classes.TryGetValue(resolvedClassName, out var typeBuilder) && ctorBuilder != null)
        {
            Type targetType = typeBuilder;
            ConstructorInfo targetCtor = ctorBuilder;

            // Handle generic class instantiation
            if (n.TypeArgs != null && n.TypeArgs.Count > 0 &&
                Ctx.ClassRegistry!.GetGenericParams(resolvedClassName) != null)
            {
                Type[] typeArgs = n.TypeArgs.Select(ResolveTypeArg).ToArray();
                targetType = typeBuilder.MakeGenericType(typeArgs);
                targetCtor = TypeBuilder.GetConstructor(targetType, ctorBuilder);
            }

            var ctorParams = ctorBuilder.GetParameters();
            int expectedParamCount = ctorParams.Length;

            // Pre-evaluate arguments to temps (safe across await/yield boundaries)
            List<LocalBuilder> argTemps = [];
            foreach (var arg in n.Arguments)
            {
                EmitExpression(arg);
                EnsureBoxed();
                var temp = IL.DeclareLocal(typeof(object));
                IL.Emit(OpCodes.Stloc, temp);
                argTemps.Add(temp);
            }

            // Load arguments with proper type conversions
            for (int i = 0; i < argTemps.Count; i++)
            {
                IL.Emit(OpCodes.Ldloc, argTemps[i]);
                if (i < ctorParams.Length)
                {
                    var targetParamType = ctorParams[i].ParameterType;
                    if (targetParamType.IsValueType && targetParamType != typeof(object))
                    {
                        IL.Emit(OpCodes.Unbox_Any, targetParamType);
                    }
                }
            }

            // Pad missing optional arguments
            for (int i = n.Arguments.Count; i < expectedParamCount; i++)
            {
                EmitDefaultForType(ctorParams[i].ParameterType);
            }

            IL.Emit(OpCodes.Newobj, targetCtor);
            SetStackUnknown();
        }
        else
        {
            // Fallback: try to load via resolver
            var stackType = Resolver.TryLoadVariable(className);
            if (stackType != null)
            {
                var typeTemp = IL.DeclareLocal(typeof(object));
                IL.Emit(OpCodes.Stloc, typeTemp);

                List<LocalBuilder> argTemps = [];
                foreach (var arg in n.Arguments)
                {
                    EmitExpression(arg);
                    EnsureBoxed();
                    var temp = IL.DeclareLocal(typeof(object));
                    IL.Emit(OpCodes.Stloc, temp);
                    argTemps.Add(temp);
                }

                IL.Emit(OpCodes.Ldloc, typeTemp);
                IL.Emit(OpCodes.Ldc_I4, n.Arguments.Count);
                IL.Emit(OpCodes.Newarr, Ctx.Types.Object);

                for (int i = 0; i < argTemps.Count; i++)
                {
                    IL.Emit(OpCodes.Dup);
                    IL.Emit(OpCodes.Ldc_I4, i);
                    IL.Emit(OpCodes.Ldloc, argTemps[i]);
                    IL.Emit(OpCodes.Stelem_Ref);
                }

                var createInstanceMethod = Ctx.Types.GetMethod(Ctx.Types.Activator, "CreateInstance", Ctx.Types.Type, Ctx.Types.ObjectArray);
                IL.Emit(OpCodes.Call, createInstanceMethod!);
                SetStackUnknown();
            }
            else
            {
                IL.Emit(OpCodes.Ldnull);
                SetStackType(StackType.Null);
            }
        }
    }

    /// <summary>
    /// Resolves a TypeScript type argument name to a CLR type (stub — all map to object).
    /// </summary>
    protected virtual Type ResolveTypeArg(string typeArg) => typeof(object);

    /// <summary>
    /// Resolves class name considering namespace imports and imported class aliases.
    /// Overridden by ILEmitter for additional resolution paths (external types, etc.).
    /// </summary>
    protected virtual string ResolveClassNameForNew(List<string> namespaceParts, string className)
    {
        if (namespaceParts.Count > 0)
        {
            string nsAlias = namespaceParts[0];
            if (Ctx.NamespaceImports?.TryGetValue(nsAlias, out var modulePath) == true)
            {
                if (Ctx.ExportedClasses?.TryGetValue(modulePath, out var exportedClasses) == true &&
                    exportedClasses.TryGetValue(className, out var qualifiedName))
                    return qualifiedName;
            }

            string nsPath = string.Join("_", namespaceParts);
            return $"{nsPath}_{className}";
        }

        if (Ctx.ImportedClassAliases?.TryGetValue(className, out var importedClassName) == true)
            return importedClassName;

        return Ctx.ResolveClassName(className);
    }

    /// <summary>
    /// Emits class expression construction with argument temps (safe across await/yield).
    /// </summary>
    private void EmitClassExprConstruction(ConstructorBuilder classExprCtor, Expr.New n)
    {
        var ctorParams = classExprCtor.GetParameters();
        int expectedParamCount = ctorParams.Length;

        // Pre-evaluate arguments to temps (safe across await/yield boundaries)
        List<LocalBuilder> argTemps = [];
        foreach (var arg in n.Arguments)
        {
            EmitExpression(arg);
            EnsureBoxed();
            var temp = IL.DeclareLocal(typeof(object));
            IL.Emit(OpCodes.Stloc, temp);
            argTemps.Add(temp);
        }

        for (int i = 0; i < argTemps.Count; i++)
        {
            IL.Emit(OpCodes.Ldloc, argTemps[i]);
            if (i < ctorParams.Length)
            {
                var targetParamType = ctorParams[i].ParameterType;
                if (targetParamType.IsValueType && targetParamType != typeof(object))
                {
                    IL.Emit(OpCodes.Unbox_Any, targetParamType);
                }
            }
        }

        for (int i = n.Arguments.Count; i < expectedParamCount; i++)
            EmitDefaultForType(ctorParams[i].ParameterType);

        IL.Emit(OpCodes.Newobj, classExprCtor);
        SetStackUnknown();
    }

    /// <summary>
    /// Emits an arrow function. Default looks up in ArrowMethods and creates TSFunction.
    /// ILEmitter overrides (display class, non-capturing optimization).
    /// AsyncMoveNextEmitter overrides (async arrow dispatch, display class capture).
    /// AsyncArrowMoveNextEmitter overrides (nested async arrows).
    /// </summary>
    protected virtual void EmitArrowFunction(Expr.ArrowFunction af)
    {
        if (Ctx.ArrowMethods == null || !Ctx.ArrowMethods.TryGetValue(af, out var method))
        {
            IL.Emit(OpCodes.Ldnull);
            SetStackUnknown();
            return;
        }

        IL.Emit(OpCodes.Ldnull);
        IL.Emit(OpCodes.Ldtoken, method);
        IL.Emit(OpCodes.Call, Types.MethodBaseGetMethodFromHandle);
        IL.Emit(OpCodes.Newobj, Ctx.Runtime!.TSFunctionCtor);
        SetStackUnknown();
    }

    // EmitCall is virtual with a default implementation in ExpressionEmitterBase.CallHelpers.cs
    // EmitCompoundAssign, EmitLogicalAssign, EmitPrefixIncrement, EmitPostfixIncrement
    // are implemented in ExpressionEmitterBase.Operators.cs as virtual methods.
    // ILEmitter and AsyncArrowMoveNextEmitter override with their own implementations.
    protected abstract void EmitClassExpression(Expr.ClassExpr ce);
    protected abstract void EmitDelete(Expr.Delete del);
    #endregion

    #region Global Variable Resolution Helpers

    /// <summary>
    /// Tries to emit a global variable load (after resolver fails).
    /// Checks TopLevelStaticVars → Functions → NamespaceFields → CapturedTopLevelVars.
    /// </summary>
    protected virtual bool TryEmitGlobalVariable(string name)
    {
        // JavaScript global constants — must be checked before user-defined variables
        // ILEmitter.EmitVariable handles these too, but state machine emitters
        // (Async/Generator/AsyncGenerator MoveNextEmitter, AsyncArrowMoveNextEmitter)
        // inherit from this base class and need them here.
        if (name == "NaN") { EmitDoubleConstant(double.NaN); return true; }
        if (name == "Infinity") { EmitDoubleConstant(double.PositiveInfinity); return true; }
        if (name == "undefined") { EmitUndefinedConstant(); return true; }

        if (Ctx.TopLevelStaticVars?.TryGetValue(name, out var topLevelField) == true)
        {
            IL.Emit(OpCodes.Ldsfld, topLevelField);
            SetStackUnknown();
            return true;
        }

        if (Ctx.Functions.TryGetValue(Ctx.ResolveFunctionName(name), out var funcMethod))
        {
            IL.Emit(OpCodes.Ldnull);
            IL.Emit(OpCodes.Ldtoken, funcMethod);
            IL.Emit(OpCodes.Call, Types.MethodBaseGetMethodFromHandle);
            IL.Emit(OpCodes.Newobj, Ctx.Runtime!.TSFunctionCtor);
            SetStackUnknown();
            return true;
        }

        if (Ctx.NamespaceFields?.TryGetValue(name, out var nsField) == true)
        {
            IL.Emit(OpCodes.Ldsfld, nsField);
            SetStackUnknown();
            return true;
        }

        if (Ctx.CapturedTopLevelVars?.Contains(name) == true &&
            Ctx.EntryPointDisplayClassFields?.TryGetValue(name, out var entryPointField) == true &&
            Ctx.EntryPointDisplayClassStaticField != null)
        {
            IL.Emit(OpCodes.Ldsfld, Ctx.EntryPointDisplayClassStaticField);
            IL.Emit(OpCodes.Ldfld, entryPointField);
            SetStackUnknown();
            return true;
        }

        return false;
    }

    /// <summary>
    /// Tries to emit a global variable store.
    /// Checks CapturedTopLevelVars → TopLevelStaticVars.
    /// Returns true if handled (value consumed from stack).
    /// </summary>
    protected virtual bool TryEmitGlobalStore(string name)
    {
        if (Ctx.CapturedTopLevelVars?.Contains(name) == true &&
            Ctx.EntryPointDisplayClassFields?.TryGetValue(name, out var entryPointField) == true &&
            Ctx.EntryPointDisplayClassStaticField != null)
        {
            var temp = IL.DeclareLocal(Types.Object);
            IL.Emit(OpCodes.Stloc, temp);
            IL.Emit(OpCodes.Ldsfld, Ctx.EntryPointDisplayClassStaticField);
            IL.Emit(OpCodes.Ldloc, temp);
            IL.Emit(OpCodes.Stfld, entryPointField);
            SetStackUnknown();
            return true;
        }

        if (Ctx.TopLevelStaticVars?.TryGetValue(name, out var topLevelField) == true)
        {
            IL.Emit(OpCodes.Stsfld, topLevelField);
            SetStackUnknown();
            return true;
        }

        return false;
    }

    #endregion

    #region Static Field Access

    /// <summary>
    /// Tries to emit static field access for Class.field patterns.
    /// Default checks ClassRegistry for static fields.
    /// </summary>
    protected virtual bool TryEmitStaticFieldAccess(Expr.Get g)
    {
        if (g.Object is Expr.Variable classVar &&
            Ctx.Classes.TryGetValue(Ctx.ResolveClassName(classVar.Name.Lexeme), out _))
        {
            string resolvedClassName = Ctx.ResolveClassName(classVar.Name.Lexeme);
            if (Ctx.ClassRegistry!.TryGetStaticField(resolvedClassName, g.Name.Lexeme, out var staticField))
            {
                IL.Emit(OpCodes.Ldsfld, staticField!);
                SetStackUnknown();
                return true;
            }
        }
        return false;
    }

    #endregion

    #region Virtual Methods - Comma operator
    /// <summary>
    /// Emits a comma (sequence) expression: evaluates left for side effects, discards its value, returns right.
    /// </summary>
    protected virtual void EmitComma(Expr.Comma c)
    {
        EmitExpression(c.Left);
        EnsureBoxed();
        IL.Emit(OpCodes.Pop);
        EmitExpression(c.Right);
        EnsureBoxed();
    }
    #endregion

    #region Virtual Methods - Pass-through expressions
    /// <summary>
    /// Emits a grouping expression by evaluating its inner expression.
    /// </summary>
    protected virtual void EmitGrouping(Expr.Grouping g) => EmitExpression(g.Expression);

    /// <summary>
    /// Emits a type assertion. Type assertions are compile-time only, just emit the inner expression.
    /// </summary>
    protected virtual void EmitTypeAssertion(Expr.TypeAssertion ta) => EmitExpression(ta.Expression);

    /// <summary>
    /// Emits a satisfies expression. Satisfies is compile-time only, just emit the inner expression.
    /// </summary>
    protected virtual void EmitSatisfies(Expr.Satisfies sat) => EmitExpression(sat.Expression);

    /// <summary>
    /// Emits a non-null assertion. These are compile-time only, just emit the inner expression.
    /// </summary>
    protected virtual void EmitNonNullAssertion(Expr.NonNullAssertion nna) => EmitExpression(nna.Expression);

    /// <summary>
    /// Emits a spread expression. Spread is handled contextually in array/object literals.
    /// When encountered standalone, just emit the expression.
    /// </summary>
    protected virtual void EmitSpread(Expr.Spread sp) => EmitExpression(sp.Expression);

    /// <summary>
    /// Emits a regex literal. Default implementation pushes null - override in ILEmitter for actual regex creation.
    /// </summary>
    protected virtual void EmitRegexLiteral(Expr.RegexLiteral re)
    {
        IL.Emit(OpCodes.Ldnull);
        SetStackUnknown();
    }
    #endregion

    #region Virtual Methods - Helper delegations
    /// <summary>
    /// Emits a logical AND/OR expression with short-circuit evaluation.
    /// </summary>
    protected virtual void EmitLogical(Expr.Logical l)
    {
        bool isAnd = l.Operator.Type == TokenType.AND_AND;
        _helpers.EmitLogical(
            isAnd,
            () => { EmitExpression(l.Left); EnsureBoxed(); },
            () => { EmitExpression(l.Right); EnsureBoxed(); },
            Ctx.Runtime!.IsTruthy);
    }

    /// <summary>
    /// Emits a unary expression (-, !, typeof, ~).
    /// </summary>
    protected virtual void EmitUnary(Expr.Unary u)
    {
        switch (u.Operator.Type)
        {
            case TokenType.MINUS:
                _helpers.EmitUnaryMinus(() => EmitExpression(u.Right));
                break;
            case TokenType.BANG:
                _helpers.EmitUnaryNot(() => EmitExpression(u.Right), Ctx.Runtime!.IsTruthy);
                break;
            case TokenType.TYPEOF:
                // typeof never throws on undeclared variables - returns "undefined"
                if (u.Right is Expr.Variable tv && !IsKnownVariable(tv.Name.Lexeme))
                {
                    _helpers.IL.Emit(OpCodes.Ldstr, "undefined");
                    _helpers.SetStackUnknown();
                }
                // Error constructors are functions
                else if (u.Right is Expr.Variable ev
                    && Runtime.BuiltIns.BuiltInNames.IsErrorTypeName(ev.Name.Lexeme))
                {
                    _helpers.IL.Emit(OpCodes.Ldstr, "function");
                    _helpers.SetStackUnknown();
                }
                // process.stdin/stdout/stderr are marker strings in compiled mode but should return "object"
                else if (u.Right is Expr.Get tg && tg.Object is Expr.Variable tgv
                    && tgv.Name.Lexeme == "process" && tg.Name.Lexeme is "stdin" or "stdout" or "stderr")
                {
                    _helpers.IL.Emit(OpCodes.Ldstr, "object");
                    _helpers.SetStackUnknown();
                }
                else
                {
                    _helpers.EmitUnaryTypeOf(() => EmitExpression(u.Right), Ctx.Runtime!.TypeOf);
                }
                break;
            case TokenType.TILDE:
                _helpers.EmitUnaryBitwiseNot(() => EmitExpression(u.Right));
                break;
            default:
                throw new NotImplementedException($"Unary operator {u.Operator.Type} not implemented");
        }
    }

    /// <summary>
    /// Checks whether a variable name is known at compile time (without emitting IL).
    /// Used by typeof to return "undefined" for unknown variables instead of throwing.
    /// Checks context-level lookups (pseudo-variables, globals, classes, functions, etc).
    /// Subclasses should override to also check resolver-level variables.
    /// </summary>
    protected virtual bool IsKnownVariable(string name)
    {
        // Check resolver (parameters, locals, captured variables, state machine fields)
        if (Resolver.HasVariable(name)) return true;
        // Check pseudo-variables and global constants
        if (name is "Math" or "process" or "globalThis" or "Symbol" or "NaN" or "Infinity"
            or "undefined" or "fetch" or "__filename" or "__dirname") return true;
        if (Ctx.TopLevelStaticVars?.ContainsKey(name) == true) return true;
        if (Ctx.Classes.ContainsKey(Ctx.ResolveClassName(name))) return true;
        if (Ctx.Functions.ContainsKey(Ctx.ResolveFunctionName(name))) return true;
        if (Ctx.InnerFunctionMethodsByName?.ContainsKey(name) == true) return true;
        if (Ctx.NamespaceFields?.ContainsKey(name) == true) return true;
        if (Runtime.BuiltIns.BuiltInNames.IsErrorTypeName(name)) return true;
        return false;
    }

    /// <summary>
    /// Emits a ternary conditional expression (condition ? thenBranch : elseBranch).
    /// </summary>
    protected virtual void EmitTernary(Expr.Ternary t)
    {
        _helpers.EmitTernary(
            () => { EmitExpression(t.Condition); EnsureBoxed(); },
            () => { EmitExpression(t.ThenBranch); EnsureBoxed(); },
            () => { EmitExpression(t.ElseBranch); EnsureBoxed(); },
            Ctx.Runtime!.IsTruthy);
    }

    /// <summary>
    /// Emits a nullish coalescing expression (left ?? right).
    /// </summary>
    protected virtual void EmitNullishCoalescing(Expr.NullishCoalescing nc)
    {
        _helpers.EmitNullishCoalescing(
            () => { EmitExpression(nc.Left); EnsureBoxed(); },
            () => { EmitExpression(nc.Right); EnsureBoxed(); });
    }
    #endregion

    #region Virtual Methods - Property Access (EmitGet)

    /// <summary>
    /// Tries to emit a Symbol well-known property access (e.g., Symbol.iterator).
    /// Returns true if the expression was handled.
    /// </summary>
    protected bool TryEmitSymbolWellKnown(Expr.Get g)
    {
        if (g.Object is not Expr.Variable { Name.Lexeme: "Symbol" }) return false;
        switch (g.Name.Lexeme)
        {
            case "iterator":
                IL.Emit(OpCodes.Ldsfld, Ctx.Runtime!.SymbolIterator);
                break;
            case "asyncIterator":
                IL.Emit(OpCodes.Ldsfld, Ctx.Runtime!.SymbolAsyncIterator);
                break;
            case "toStringTag":
                IL.Emit(OpCodes.Ldsfld, Ctx.Runtime!.SymbolToStringTag);
                break;
            case "hasInstance":
                IL.Emit(OpCodes.Ldsfld, Ctx.Runtime!.SymbolHasInstance);
                break;
            case "isConcatSpreadable":
                IL.Emit(OpCodes.Ldsfld, Ctx.Runtime!.SymbolIsConcatSpreadable);
                break;
            case "toPrimitive":
                IL.Emit(OpCodes.Ldsfld, Ctx.Runtime!.SymbolToPrimitive);
                break;
            case "species":
                IL.Emit(OpCodes.Ldsfld, Ctx.Runtime!.SymbolSpecies);
                break;
            case "unscopables":
                IL.Emit(OpCodes.Ldsfld, Ctx.Runtime!.SymbolUnscopables);
                break;
            default:
                return false;
        }
        SetStackUnknown();
        return true;
    }

    /// <summary>
    /// Attempts to emit a property set via TypeEmitterRegistry strategy dispatch.
    /// Call from EmitSet before falling through to dynamic SetProperty.
    /// </summary>
    protected bool TryEmitTypeRegistryPropertySet(Expr.Set s)
    {
        var objType = Ctx.TypeMap?.Get(s.Object);
        if (objType != null && Ctx.TypeEmitterRegistry != null)
        {
            var ctx = (IEmitterContext)this;
            var strategy = Ctx.TypeEmitterRegistry.GetStrategy(objType);
            if (strategy != null && strategy.TryEmitPropertySet(ctx, s.Object, s.Name.Lexeme, s.Value))
            {
                SetStackUnknown();
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Emits a property access expression (obj.prop).
    /// Default implementation handles Symbol well-known properties, static field access,
    /// and falls back to dynamic property access via GetProperty.
    /// </summary>
    protected virtual void EmitGet(Expr.Get g)
    {
        // CommonJS: `module.exports` reads → ldsfld $exports.
        if (TryEmitCjsGet(g)) return;

        if (TryEmitSymbolWellKnown(g)) return;
        if (TryEmitStaticFieldAccess(g)) return;

        // Static type property dispatch via registry (Math.PI, Number.MAX_VALUE, Symbol.iterator, etc.)
        var ctx = (IEmitterContext)this;
        if (g.Object is Expr.Variable staticVar && Ctx.TypeEmitterRegistry != null)
        {
            var staticStrategy = Ctx.TypeEmitterRegistry.GetStaticStrategy(staticVar.Name.Lexeme);
            if (staticStrategy != null && staticStrategy.TryEmitStaticPropertyGet(ctx, g.Name.Lexeme))
            {
                SetStackUnknown();
                return;
            }
        }

        // Type-first dispatch: Use TypeEmitterRegistry for property getters (AbortController.signal, etc.)
        var objType = Ctx.TypeMap?.Get(g.Object);
        if (objType != null && Ctx.TypeEmitterRegistry != null)
        {
            var strategy = Ctx.TypeEmitterRegistry.GetStrategy(objType);
            if (strategy != null && strategy.TryEmitPropertyGet(ctx, g.Object, g.Name.Lexeme))
            {
                SetStackUnknown();
                return;
            }
        }

        // Dynamic property access fallback
        EmitExpression(g.Object);
        EnsureBoxed();
        IL.Emit(OpCodes.Ldstr, g.Name.Lexeme);
        IL.Emit(OpCodes.Call, Ctx.Runtime!.GetProperty);
        SetStackUnknown();
    }

    #endregion

    #region Virtual Methods (override in async/generator emitters)
    /// <summary>
    /// Emits an await expression. Override in async emitters.
    /// </summary>
    protected virtual void EmitAwait(Expr.Await aw)
    {
        throw new CompileException("Await not supported in this context");
    }

    /// <summary>
    /// Emits a yield expression. Override in generator emitters.
    /// </summary>
    protected virtual void EmitYield(Expr.Yield y)
    {
        throw new CompileException("Yield not supported in this context");
    }
    #endregion
}
