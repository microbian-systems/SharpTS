using SharpTS.Parsing;

namespace SharpTS.Compilation;

public partial class AsyncMoveNextEmitter
{
    // EmitWhile: inherited from StatementEmitterBase (identical logic)

    protected override void EmitForOf(Stmt.ForOf f)
    {
        if (f.IsAsync)
        {
            // for await...of uses the shared async-iterator lowering in StatementEmitterBase.
            EmitForAwaitOf(f);
            return;
        }

        // Sync for...of: delegate to base (uses DeclareLoopVariable/EmitStoreLoopVariable
        // overrides in this class to handle hoisted state machine fields)
        base.EmitForOf(f);
    }

    // EmitForAwaitOf: inherited from StatementEmitterBase. The async-function, async-arrow,
    // and async-generator emitters now share one async-iterator lowering (#430/#645).

    // EmitDoWhile: inherited from StatementEmitterBase (identical logic)
    // EmitForIn: inherited from StatementEmitterBase (uses DeclareLoopVariable/EmitStoreLoopVariable
    // overrides in this class to handle hoisted state machine fields)
}
