using System.Reflection;
using System.Reflection.Emit;
using SharpTS.Compilation.Emitters;
using SharpTS.Parsing;

namespace SharpTS.Compilation;

/// <summary>
/// Emits the MoveNext body for an async arrow function's state machine.
/// Similar to AsyncMoveNextEmitter but handles captured variable access
/// through the outer state machine reference.
/// </summary>
public partial class AsyncArrowMoveNextEmitter : StatementEmitterBase, IEmitterContext
{
    private readonly AsyncArrowStateMachineBuilder _builder;
    private readonly AsyncStateAnalyzer.AsyncFunctionAnalysis _analysis;
    private readonly TypeProvider _types;
    private readonly ILGenerator _il;

    // Abstract property implementations for ExpressionEmitterBase
    protected override ILGenerator IL => _il;
    protected override CompilationContext Ctx => _ctx!;
    protected override TypeProvider Types => _types;
    protected override IVariableResolver Resolver => _resolver!;

    private CompilationContext? _ctx;
    private int _currentState = 0;
    private readonly List<Label> _stateLabels = [];
    private Label _exitLabel;
    private LocalBuilder? _resultLocal;
    private LocalBuilder? _exceptionLocal;

    // Stack type tracking via shared helpers (use base class _helpers)
    private StackType _stackType
    {
        get => _helpers.StackType;
        set => _helpers.StackType = value;
    }

    // Non-hoisted local variables (live within a single MoveNext invocation)
    private readonly Dictionary<string, LocalBuilder> _locals = [];

    // Variable resolver for hoisted fields, locals, and captured variables
    private IVariableResolver? _resolver;

    #region IEmitterContext Implementation

    /// <summary>
    /// Provides access to the compilation context for type emitter strategies.
    /// </summary>
    public CompilationContext Context => _ctx!;

    /// <summary>
    /// Provides access to the IL generator via IEmitterContext.
    /// </summary>
    ILGenerator IEmitterContext.IL => _il;

    /// <summary>
    /// Boxes the value on the stack if needed.
    /// In the async arrow context, uses EnsureBoxed from helpers.
    /// </summary>
    public void EmitBoxIfNeeded(Expr expr) => EnsureBoxed();

    /// <summary>
    /// Emits an expression and ensures the result is an unboxed double on the stack.
    /// </summary>
    public override void EmitExpressionAsDouble(Expr expr)
    {
        if (expr is Expr.Literal lit && lit.Value is double d)
        {
            _il.Emit(OpCodes.Ldc_R8, d);
            return;
        }

        EmitExpression(expr);
        EnsureBoxed();

        // Try to unbox if it's a boxed double
        var skipUnbox = _il.DefineLabel();
        var endLabel = _il.DefineLabel();

        _il.Emit(OpCodes.Dup);
        _il.Emit(OpCodes.Isinst, typeof(double));
        _il.Emit(OpCodes.Brfalse, skipUnbox);

        // It's a boxed double - unbox it
        _il.Emit(OpCodes.Unbox_Any, typeof(double));
        _il.Emit(OpCodes.Br, endLabel);

        _il.MarkLabel(skipUnbox);
        // Not a double - use runtime ToNumber conversion
        _il.Emit(OpCodes.Call, _ctx!.Runtime!.ToNumber);

        _il.MarkLabel(endLabel);
    }

    /// <summary>
    /// Marks the stack as containing an unknown/object type.
    /// Part of IEmitterContext interface for type emitter strategies.
    /// </summary>
    void IEmitterContext.SetStackUnknown() => _helpers.SetStackUnknown();

    /// <summary>
    /// Marks the stack as containing a specific type.
    /// Part of IEmitterContext interface for type emitter strategies.
    /// </summary>
    void IEmitterContext.SetStackType(StackType type) => _helpers.SetStackType(type);

    #endregion

    public AsyncArrowMoveNextEmitter(
        AsyncArrowStateMachineBuilder builder,
        AsyncStateAnalyzer.AsyncFunctionAnalysis analysis,
        TypeProvider types)
        : base(new StateMachineEmitHelpers(builder.MoveNextMethod.GetILGenerator(), types))
    {
        _builder = builder;
        _analysis = analysis;
        _types = types;
        _il = builder.MoveNextMethod.GetILGenerator();
    }

    /// <summary>
    /// Emits the complete MoveNext method body.
    /// </summary>
    public void EmitMoveNext(List<Stmt> body, CompilationContext ctx, Type returnType)
    {
        // Note: _il is initialized in constructor via GetILGenerator()
        _ctx = ctx;

        // Create variable resolver for hoisted fields, locals, and captured variables
        _resolver = new AsyncArrowVariableResolver(_il, _builder, _locals);

        // Create labels for each await state
        for (int i = 0; i < _analysis.AwaitPointCount; i++)
        {
            _stateLabels.Add(_il.DefineLabel());
        }
        _exitLabel = _il.DefineLabel();

        // Declare locals
        _resultLocal = _il.DeclareLocal(_types.Object);
        _exceptionLocal = _il.DeclareLocal(_types.Exception);

        // Begin try block
        _il.BeginExceptionBlock();

        // State dispatch switch
        EmitStateDispatch();

        // Emit the body
        foreach (var stmt in body)
        {
            EmitStatement(stmt);
        }

        // Default return null
        EmitReturnNull();

        // Catch block
        _il.BeginCatchBlock(_types.Exception);
        EmitCatchBlock();

        _il.EndExceptionBlock();

        // Exit label and return
        _il.MarkLabel(_exitLabel);
        _il.Emit(OpCodes.Ret);
    }

    private void EmitStateDispatch()
    {
        if (_stateLabels.Count == 0)
        {
            // No awaits, just run through
            return;
        }

        var defaultLabel = _il.DefineLabel();

        // Load state
        _il.Emit(OpCodes.Ldarg_0);
        _il.Emit(OpCodes.Ldfld, _builder.StateField);

        // Switch on state
        _il.Emit(OpCodes.Switch, [.. _stateLabels]);

        // Default case - continue with normal execution
        _il.MarkLabel(defaultLabel);
    }

    #region StatementEmitterBase Overrides

    protected override FieldBuilder? GetHoistedVariableField(string name) => _builder.GetVariableField(name);

    protected override void RegisterLoopLocal(string name, LocalBuilder local)
    {
        _locals[name] = local;
    }

    protected override void EmitVarDeclaration(Stmt.Var v)
    {
        if (v.Initializer != null)
        {
            EmitExpression(v.Initializer);
            EnsureBoxed();
            StoreVariable(v.Name.Lexeme);
        }
        else
        {
            _il.Emit(OpCodes.Ldnull);
            StoreVariable(v.Name.Lexeme);
        }
    }

    protected override void EmitReturn(Stmt.Return r)
    {
        if (r.Value != null)
        {
            EmitExpression(r.Value);
            EnsureBoxed();
        }
        else
        {
            _il.Emit(OpCodes.Ldnull);
        }
        EmitSetResult();
        _il.Emit(OpCodes.Leave, _exitLabel);
    }

    protected override void EmitTryCatch(Stmt.TryCatch t)
    {
        // Simple try/catch implementation (no await-aware handling yet)
        _il.BeginExceptionBlock();

        foreach (var stmt in t.TryBlock)
            EmitStatement(stmt);

        if (t.CatchBlock != null)
        {
            _il.BeginCatchBlock(_types.Exception);
            if (t.CatchParam != null)
            {
                var exLocal = _il.DeclareLocal(_types.Object);
                _locals[t.CatchParam.Lexeme] = exLocal;
                _il.Emit(OpCodes.Call, _ctx!.Runtime!.WrapException);
                _il.Emit(OpCodes.Stloc, exLocal);
            }
            else
            {
                _il.Emit(OpCodes.Pop);
            }

            foreach (var stmt in t.CatchBlock)
                EmitStatement(stmt);
        }

        if (t.FinallyBlock != null)
        {
            _il.BeginFinallyBlock();
            foreach (var stmt in t.FinallyBlock)
                EmitStatement(stmt);
        }

        _il.EndExceptionBlock();
    }

    #endregion
}
