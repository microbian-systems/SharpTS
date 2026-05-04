using SharpTS.Compilation.Emitters;
using SharpTS.Parsing;

namespace SharpTS.Compilation.ArrowInlining;

/// <summary>
/// Block-body arrow inliner support (#96 M5). Walks a block-bodied arrow's
/// statement list with allowed-shape checks, then emits the body with a
/// per-helper <c>InlinedReturnHandler</c> installed on the
/// <see cref="CompilationContext"/>. <c>Stmt.Return v</c> statements
/// re-route to the helper-specific continuation (see
/// <see cref="CompilationContext.InlinedReturnHandler"/>).
/// </summary>
/// <remarks>
/// V1 statement-shape allowlist (rejects anything else; caller falls
/// through to Direct/slow path):
/// <list type="bullet">
///   <item><description><see cref="Stmt.Expression"/></description></item>
///   <item><description><see cref="Stmt.Var"/> (let/const/var)</description></item>
///   <item><description><see cref="Stmt.Const"/></description></item>
///   <item><description><see cref="Stmt.Return"/></description></item>
///   <item><description><see cref="Stmt.If"/> (recurses into branches)</description></item>
///   <item><description><see cref="Stmt.Block"/> (recurses into statements)</description></item>
/// </list>
/// Disallowed in V1: loops (For/While/DoWhile/ForOf/ForIn), Switch,
/// TryCatch, Throw, Function/Class declarations, Break/Continue. These
/// complicate the rewrite (loop break/continue scoping clashes with the
/// inliner's outer loop; throws would propagate naturally but exception
/// scoping inside an arrow body needs careful test coverage we haven't
/// added yet).
/// </remarks>
public static class BlockBodyArrowEmitter
{
    /// <summary>
    /// Walks the statement subtree and returns true iff every statement
    /// matches the V1 allowlist.
    /// </summary>
    public static bool IsAllowedShape(List<Stmt> statements)
    {
        foreach (var s in statements)
        {
            if (!IsAllowedStatement(s)) return false;
        }
        return true;
    }

    private static bool IsAllowedStatement(Stmt s) => s switch
    {
        Stmt.Expression => true,
        Stmt.Var => true,
        Stmt.Const => true,
        Stmt.Return => true,
        Stmt.If i =>
            IsAllowedStatement(i.ThenBranch)
            && (i.ElseBranch is null || IsAllowedStatement(i.ElseBranch)),
        Stmt.Block b =>
            b.Statements is null || IsAllowedShape(b.Statements),
        _ => false,
    };

    /// <summary>
    /// Emits the block body with the given per-helper return handler
    /// installed. Restores any previous handler in a finally block so
    /// nested inliner usage (should it ever arise) doesn't leak.
    /// </summary>
    /// <param name="emitter">The emitter to use for body emission.</param>
    /// <param name="block">The arrow's block body.</param>
    /// <param name="returnHandler">Per-helper rewrite for <c>Stmt.Return</c>.
    /// Receives the (possibly null) return-value expression.</param>
    public static void EmitBlock(IEmitterContext emitter, List<Stmt> block, Action<Expr?> returnHandler)
    {
        var ctx = emitter.Context;
        var prior = ctx.InlinedReturnHandler;
        ctx.InlinedReturnHandler = returnHandler;
        ctx.Locals.EnterScope();
        try
        {
            foreach (var s in block)
            {
                EmitStatementViaEmitter(emitter, s);
            }
        }
        finally
        {
            ctx.Locals.ExitScope();
            ctx.InlinedReturnHandler = prior;
        }
    }

    /// <summary>
    /// Drives a single statement through the emitter's public surface.
    /// We cannot call the protected <c>EmitStatement</c> directly from here
    /// (the inliner doesn't subclass the emitter base). Instead, expression
    /// statements route through <see cref="IEmitterContext.EmitExpression"/>;
    /// <see cref="Stmt.Return"/> dispatches via the handler directly;
    /// <see cref="Stmt.Var"/>, <see cref="Stmt.Const"/>, <see cref="Stmt.If"/>,
    /// and <see cref="Stmt.Block"/> are routed via wrappers that mimic the
    /// emitter's behavior using only public IEmitterContext primitives.
    /// </summary>
    private static void EmitStatementViaEmitter(IEmitterContext emitter, Stmt s)
    {
        var ctx = emitter.Context;
        switch (s)
        {
            case Stmt.Expression es:
                emitter.EmitExpression(es.Expr);
                ctx.IL.Emit(System.Reflection.Emit.OpCodes.Pop);
                break;

            case Stmt.Var v:
            {
                // let/const/var binding: declare a fresh local in the
                // current scope, store initializer (or null) into it.
                var local = ctx.Locals.DeclareLocal(v.Name.Lexeme, ctx.Types.Object);
                if (v.Initializer != null)
                {
                    emitter.EmitExpression(v.Initializer);
                    emitter.EnsureBoxed();
                }
                else
                {
                    ctx.IL.Emit(System.Reflection.Emit.OpCodes.Ldsfld, ctx.Runtime!.UndefinedInstance);
                }
                ctx.IL.Emit(System.Reflection.Emit.OpCodes.Stloc, local);
                break;
            }

            case Stmt.Const c:
            {
                var local = ctx.Locals.DeclareLocal(c.Name.Lexeme, ctx.Types.Object);
                emitter.EmitExpression(c.Initializer);
                emitter.EnsureBoxed();
                ctx.IL.Emit(System.Reflection.Emit.OpCodes.Stloc, local);
                break;
            }

            case Stmt.Return r:
                ctx.InlinedReturnHandler!(r.Value);
                break;

            case Stmt.If i:
            {
                // Standard if-then-else with truthy guard. Mirrors what
                // ILEmitter's normal path does for a simple if. Holds for
                // any IEmitterContext (sync + state-machine) because
                // EmitExpression + IsTruthy + branch ops are universal.
                emitter.EmitExpression(i.Condition);
                emitter.EnsureBoxed();
                ctx.IL.Emit(System.Reflection.Emit.OpCodes.Call, ctx.Runtime!.IsTruthy);

                var elseLabel = ctx.IL.DefineLabel();
                var endLabel = ctx.IL.DefineLabel();
                ctx.IL.Emit(System.Reflection.Emit.OpCodes.Brfalse, elseLabel);
                EmitStatementViaEmitter(emitter, i.ThenBranch);
                ctx.IL.Emit(System.Reflection.Emit.OpCodes.Br, endLabel);
                ctx.IL.MarkLabel(elseLabel);
                if (i.ElseBranch != null)
                    EmitStatementViaEmitter(emitter, i.ElseBranch);
                ctx.IL.MarkLabel(endLabel);
                break;
            }

            case Stmt.Block b:
            {
                ctx.Locals.EnterScope();
                try
                {
                    if (b.Statements != null)
                    {
                        foreach (var inner in b.Statements)
                            EmitStatementViaEmitter(emitter, inner);
                    }
                }
                finally
                {
                    ctx.Locals.ExitScope();
                }
                break;
            }

            default:
                // IsAllowedShape should have prevented us from reaching here.
                throw new InvalidOperationException(
                    $"BlockBodyArrowEmitter: unexpected statement {s.GetType().Name} (eligibility gate should have rejected this body)");
        }
    }
}
