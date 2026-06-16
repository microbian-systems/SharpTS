using System.Reflection.Emit;
using SharpTS.Parsing;

namespace SharpTS.Compilation;

public partial class AsyncGeneratorMoveNextEmitter
{
    // Expose the state machine's function display class field to the base arrow emitter so a
    // capturing arrow inside the async generator body gets its $functionDC threaded in (#725).
    protected override FieldBuilder? GetFunctionDCField() => _builder.FunctionDCField;

    /// <summary>
    /// Routes reads and writes of a captured-AND-mutated async-generator local/parameter through the
    /// shared function display class (#725) so a write inside an arrow/callback and the generator body
    /// observe the same storage. The async-generator state machine is a reference type, so the DC —
    /// held by its <c>&lt;&gt;__functionDC</c> field — persists across awaits and yields and is the
    /// single source of truth (the async generator never lifts these to async arrows, which it does not
    /// support). Returns false (no DC routing) for variables that stay on the existing hoisted-field /
    /// local snapshot path. Mirrors <see cref="GeneratorMoveNextEmitter"/>.
    /// </summary>
    private bool TryGetFunctionDCField(string name, out FieldBuilder dcField)
    {
        dcField = null!;
        return _builder.FunctionDCField != null &&
               _ctx?.CapturedFunctionLocals?.Contains(name) == true &&
               _ctx.FunctionDisplayClassFields?.TryGetValue(name, out dcField!) == true;
    }

    /// <summary>
    /// Stores the value currently on the stack into <c>this.&lt;&gt;__functionDC.dcField</c>,
    /// consuming it. Caller is responsible for any duplicate it wants to keep as a result.
    /// </summary>
    private void StoreToDCField(FieldBuilder dcField)
    {
        var temp = _il.DeclareLocal(_types.Object);
        _il.Emit(OpCodes.Stloc, temp);
        _il.Emit(OpCodes.Ldarg_0);
        _il.Emit(OpCodes.Ldfld, _builder.FunctionDCField!);
        _il.Emit(OpCodes.Ldloc, temp);
        _il.Emit(OpCodes.Stfld, dcField);
    }

    protected override void EmitVariable(Expr.Variable v)
    {
        if (TryGetFunctionDCField(v.Name.Lexeme, out var dcField))
        {
            _il.Emit(OpCodes.Ldarg_0);
            _il.Emit(OpCodes.Ldfld, _builder.FunctionDCField!);
            _il.Emit(OpCodes.Ldfld, dcField);
            SetStackUnknown();
            return;
        }

        base.EmitVariable(v);
    }

    protected override void EmitVarDeclaration(Stmt.Var v)
    {
        if (TryGetFunctionDCField(v.Name.Lexeme, out var dcField))
        {
            if (v.Initializer != null)
            {
                EmitExpression(v.Initializer);
                EnsureBoxed();
            }
            else
            {
                _il.Emit(OpCodes.Ldsfld, _ctx!.Runtime!.UndefinedInstance);
            }
            StoreToDCField(dcField);
            return;
        }

        base.EmitVarDeclaration(v);
    }

    protected override void EmitAssign(Expr.Assign a)
    {
        if (TryGetFunctionDCField(a.Name.Lexeme, out var dcField))
        {
            EmitExpression(a.Value);
            EnsureBoxed();
            _il.Emit(OpCodes.Dup); // keep a copy as the assignment expression's result
            StoreToDCField(dcField);
            SetStackUnknown();
            return;
        }

        base.EmitAssign(a);
    }

    /// <summary>
    /// Store side of compound assignment, logical assignment, and increment/decrement (the value
    /// is already on the stack). Reads for those operators go through <see cref="EmitVariable"/>,
    /// so routing the store here keeps both ends on the function DC.
    /// </summary>
    protected override void EmitStoreVariable(string name)
    {
        if (TryGetFunctionDCField(name, out var dcField))
        {
            StoreToDCField(dcField);
            return;
        }

        base.EmitStoreVariable(name);
    }
}
