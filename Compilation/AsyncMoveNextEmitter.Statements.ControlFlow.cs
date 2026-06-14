using System.Reflection;
using System.Reflection.Emit;
using SharpTS.Parsing;

namespace SharpTS.Compilation;

public partial class AsyncMoveNextEmitter
{
    protected override void EmitReturn(Stmt.Return r)
    {
        if (r.Value != null)
        {
            EmitExpression(r.Value);
            EnsureBoxed();
            if (_returnValueLocal != null)
                _il.Emit(OpCodes.Stloc, _returnValueLocal);
        }
        else if (_returnValueLocal != null)
        {
            // Bare `return;` resolves the promise with `undefined`, not null (#587) — mirrors
            // the generator GeneratorMoveNextEmitter.EmitReturn else. A genuine `return null;`
            // takes the branch above and still resolves with null.
            _il.Emit(OpCodes.Ldsfld, _ctx!.Runtime!.UndefinedInstance);
            _il.Emit(OpCodes.Stloc, _returnValueLocal);
        }

        // If we're inside a try with finally-with-awaits, set pending return flag
        // and jump to after-finally label (which will then complete the return)
        if (_pendingReturnFlagLocal != null && _afterFinallyLabel != null)
        {
            _il.Emit(OpCodes.Ldc_I4_1);
            _il.Emit(OpCodes.Stloc, _pendingReturnFlagLocal);
            // Use Leave to exit the protected region to the after-finally label
            _il.Emit(OpCodes.Leave, _afterFinallyLabel.Value);
            return;
        }

        // Jump to set result
        _il.Emit(OpCodes.Leave, _setResultLabel);
    }

    // EmitIf: inherited from StatementEmitterBase (identical logic)
}
