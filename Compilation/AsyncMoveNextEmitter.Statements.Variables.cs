using System.Reflection.Emit;
using SharpTS.Parsing;

namespace SharpTS.Compilation;

public partial class AsyncMoveNextEmitter
{
    protected override FieldBuilder? GetHoistedVariableField(string name) => _builder.GetVariableField(name);

    /// <summary>
    /// Checks whether a variable is a captured function local with a function DC field available.
    /// </summary>
    private bool TryGetFunctionDCField(string name, out FieldBuilder dcField)
    {
        dcField = null!;
        return _builder.FunctionDCField != null &&
               _ctx?.CapturedFunctionLocals?.Contains(name) == true &&
               _ctx.FunctionDisplayClassFields?.TryGetValue(name, out dcField!) == true;
    }

    /// <summary>
    /// Stores a value (already on stack) to the function DC field for a captured variable.
    /// Does NOT consume the value from the stack — caller must handle that.
    /// </summary>
    private void StoreToDCField(string name, FieldBuilder dcField)
    {
        var temp = _il.DeclareLocal(_types.Object);
        _il.Emit(OpCodes.Stloc, temp);
        _il.Emit(OpCodes.Ldarg_0);
        _il.Emit(OpCodes.Ldfld, _builder.FunctionDCField!);
        _il.Emit(OpCodes.Ldloc, temp);
        _il.Emit(OpCodes.Stfld, dcField);
        _il.Emit(OpCodes.Ldloc, temp); // restore value to stack
    }

    /// <summary>
    /// Overrides variable declaration to write to BOTH the hoisted field AND the function DC.
    /// The hoisted field is needed by async arrows (which capture from the outer state machine).
    /// The function DC is needed by sync arrows (which share the DC reference).
    /// </summary>
    protected override void EmitVarDeclaration(Stmt.Var v)
    {
        string name = v.Name.Lexeme;

        if (TryGetFunctionDCField(name, out var dcField))
        {
            // Write to both: first evaluate initializer
            if (v.Initializer != null)
            {
                EmitExpression(v.Initializer);
                EnsureBoxed();
            }
            else
            {
                _il.Emit(OpCodes.Ldsfld, _ctx!.Runtime!.UndefinedInstance);
            }

            // Store to function DC
            StoreToDCField(name, dcField);

            // Also store to hoisted field (for async arrow captures)
            var hoistedField = _builder.GetVariableField(name);
            if (hoistedField != null)
            {
                var temp = _il.DeclareLocal(_types.Object);
                _il.Emit(OpCodes.Stloc, temp);
                _il.Emit(OpCodes.Ldarg_0);
                _il.Emit(OpCodes.Ldloc, temp);
                _il.Emit(OpCodes.Stfld, hoistedField);
            }
            else
            {
                _il.Emit(OpCodes.Pop);
            }
            return;
        }

        base.EmitVarDeclaration(v);
    }

    /// <summary>
    /// Overrides variable load to route captured function locals through the function display class.
    /// </summary>
    protected override void EmitVariable(Expr.Variable v)
    {
        string name = v.Name.Lexeme;

        if (TryGetFunctionDCField(name, out var dcField))
        {
            _il.Emit(OpCodes.Ldarg_0);
            _il.Emit(OpCodes.Ldfld, _builder.FunctionDCField!);
            _il.Emit(OpCodes.Ldfld, dcField);
            SetStackUnknown();
            return;
        }

        base.EmitVariable(v);
    }

    /// <summary>
    /// Overrides assignment to write to BOTH the function DC AND the hoisted field.
    /// </summary>
    protected override void EmitAssign(Expr.Assign a)
    {
        string name = a.Name.Lexeme;

        if (TryGetFunctionDCField(name, out var dcField))
        {
            EmitExpression(a.Value);
            EnsureBoxed();
            _il.Emit(OpCodes.Dup); // keep a copy as the expression result

            // Store to function DC
            StoreToDCField(name, dcField);

            // Also store to hoisted field
            var hoistedField = _builder.GetVariableField(name);
            if (hoistedField != null)
            {
                var temp = _il.DeclareLocal(_types.Object);
                _il.Emit(OpCodes.Stloc, temp);
                _il.Emit(OpCodes.Ldarg_0);
                _il.Emit(OpCodes.Ldloc, temp);
                _il.Emit(OpCodes.Stfld, hoistedField);
            }
            else
            {
                _il.Emit(OpCodes.Pop);
            }

            SetStackUnknown();
            return;
        }

        base.EmitAssign(a);
    }

    /// <summary>
    /// Overrides variable store to write to BOTH the function DC AND the hoisted field.
    /// Used by compound assignment, logical assignment, and update expressions.
    /// </summary>
    protected override void EmitStoreVariable(string name)
    {
        if (TryGetFunctionDCField(name, out var dcField))
        {
            // Value is on stack — store to both locations
            StoreToDCField(name, dcField);

            // Also store to hoisted field
            var hoistedField = _builder.GetVariableField(name);
            if (hoistedField != null)
            {
                var temp = _il.DeclareLocal(_types.Object);
                _il.Emit(OpCodes.Stloc, temp);
                _il.Emit(OpCodes.Ldarg_0);
                _il.Emit(OpCodes.Ldloc, temp);
                _il.Emit(OpCodes.Stfld, hoistedField);
            }
            else
            {
                _il.Emit(OpCodes.Pop);
            }
            return;
        }

        base.EmitStoreVariable(name);
    }
}
