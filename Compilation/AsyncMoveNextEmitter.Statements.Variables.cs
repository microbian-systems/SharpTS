using System.Reflection.Emit;
using SharpTS.Parsing;

namespace SharpTS.Compilation;

public partial class AsyncMoveNextEmitter
{
    protected override FieldBuilder? GetHoistedVariableField(string name) => _builder.GetVariableField(name);

    // Per-binding storage names for block-scoped let/const shadows (#766), shared with the analyzer via
    // the analysis. Empty for the common no-shadow case (and for analyses built without the renamer).
    private static readonly IReadOnlyDictionary<object, string> NoRenames = new Dictionary<object, string>();
    private IReadOnlyDictionary<object, string> BlockScopeRenames => _analysis.BlockScopeRenames ?? NoRenames;

    private static Token RenameToken(Token original, string lexeme) =>
        new(original.Type, lexeme, original.Literal, original.Line, original.Start);

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
        // Resolve a shadowing block-scoped binding to its own storage before any DC routing (#766);
        // a renamed binding is never a captured/DC name, so the DC check below correctly falls through.
        if (BlockScopeRenames.TryGetValue(v, out var renamed))
            v = v with { Name = RenameToken(v.Name, renamed) };

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
        if (BlockScopeRenames.TryGetValue(v, out var renamed))
            v = v with { Name = RenameToken(v.Name, renamed) };

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
        if (BlockScopeRenames.TryGetValue(a, out var renamed))
            a = a with { Name = RenameToken(a.Name, renamed) };

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

    // Const declarations, compound/logical assignment, and increment/decrement reach the variable
    // through the operator node's name token (or the increment operand). Rewriting that token to the
    // shadowing binding's storage name before delegating to the base routes both the read and the
    // write to the right field/local (#766). A renamed binding is never a DC/captured name, so the
    // base path (which re-enters EmitVariable / EmitStoreVariable) resolves it as a plain slot.

    protected override void EmitConstDeclaration(Stmt.Const c)
    {
        if (BlockScopeRenames.TryGetValue(c, out var renamed))
            c = c with { Name = RenameToken(c.Name, renamed) };
        base.EmitConstDeclaration(c);
    }

    protected override void EmitCompoundAssign(Expr.CompoundAssign ca)
    {
        if (BlockScopeRenames.TryGetValue(ca, out var renamed))
            ca = ca with { Name = RenameToken(ca.Name, renamed) };
        base.EmitCompoundAssign(ca);
    }

    protected override void EmitLogicalAssign(Expr.LogicalAssign la)
    {
        if (BlockScopeRenames.TryGetValue(la, out var renamed))
            la = la with { Name = RenameToken(la.Name, renamed) };
        base.EmitLogicalAssign(la);
    }

    protected override void EmitPrefixIncrement(Expr.PrefixIncrement pi)
    {
        if (pi.Operand is Expr.Variable v && BlockScopeRenames.TryGetValue(v, out var renamed))
            pi = pi with { Operand = v with { Name = RenameToken(v.Name, renamed) } };
        base.EmitPrefixIncrement(pi);
    }

    protected override void EmitPostfixIncrement(Expr.PostfixIncrement poi)
    {
        if (poi.Operand is Expr.Variable v && BlockScopeRenames.TryGetValue(v, out var renamed))
            poi = poi with { Operand = v with { Name = RenameToken(v.Name, renamed) } };
        base.EmitPostfixIncrement(poi);
    }
}
