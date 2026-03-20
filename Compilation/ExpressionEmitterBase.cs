using System.Reflection;
using System.Reflection.Emit;
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
public abstract partial class ExpressionEmitterBase
{
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

    #region Abstract Expression Methods
    protected abstract void EmitLiteral(Expr.Literal lit);
    protected abstract void EmitVariable(Expr.Variable v);
    protected abstract void EmitAssign(Expr.Assign a);
    protected abstract void EmitBinary(Expr.Binary b);
    protected abstract void EmitCall(Expr.Call c);
    protected abstract void EmitSet(Expr.Set s);
    protected abstract void EmitGetPrivate(Expr.GetPrivate gp);
    protected abstract void EmitSetPrivate(Expr.SetPrivate sp);
    protected abstract void EmitCallPrivate(Expr.CallPrivate cp);
    protected abstract void EmitGetIndex(Expr.GetIndex gi);
    protected abstract void EmitSetIndex(Expr.SetIndex si);
    protected abstract void EmitTemplateLiteral(Expr.TemplateLiteral tl);
    protected abstract void EmitTaggedTemplateLiteral(Expr.TaggedTemplateLiteral ttl);
    protected abstract void EmitArrayLiteral(Expr.ArrayLiteral al);
    protected abstract void EmitObjectLiteral(Expr.ObjectLiteral ol);
    protected abstract void EmitNew(Expr.New n);
    protected abstract void EmitThis();
    protected abstract void EmitSuper(Expr.Super s);
    // EmitCompoundAssign, EmitLogicalAssign, EmitPrefixIncrement, EmitPostfixIncrement
    // are implemented in ExpressionEmitterBase.Operators.cs as virtual methods.
    // ILEmitter and AsyncArrowMoveNextEmitter override with their own implementations.
    protected abstract void EmitArrowFunction(Expr.ArrowFunction af);
    protected abstract void EmitClassExpression(Expr.ClassExpr ce);
    protected abstract void EmitDelete(Expr.Delete del);
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
    /// Tries to emit static field access for Class.field patterns.
    /// Override in emitters that have access to class registry information.
    /// Returns true if the expression was handled.
    /// </summary>
    protected virtual bool TryEmitStaticFieldAccess(Expr.Get g) => false;

    /// <summary>
    /// Emits a property access expression (obj.prop).
    /// Default implementation handles Symbol well-known properties, static field access,
    /// and falls back to dynamic property access via GetProperty.
    /// </summary>
    protected virtual void EmitGet(Expr.Get g)
    {
        if (TryEmitSymbolWellKnown(g)) return;
        if (TryEmitStaticFieldAccess(g)) return;

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
