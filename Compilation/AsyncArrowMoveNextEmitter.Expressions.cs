using System.Reflection.Emit;
using SharpTS.Parsing;

namespace SharpTS.Compilation;

public partial class AsyncArrowMoveNextEmitter
{
    // EmitExpression dispatch is inherited from ExpressionEmitterBase

    protected override void EmitLiteral(Expr.Literal lit)
    {
        if (lit.Value == null)
        {
            _il.Emit(OpCodes.Ldnull);
            SetStackType(StackType.Null);
        }
        else if (lit.Value is double d)
        {
            _il.Emit(OpCodes.Ldc_R8, d);
            _il.Emit(OpCodes.Box, typeof(double));
            SetStackUnknown();
        }
        else if (lit.Value is string s)
        {
            _il.Emit(OpCodes.Ldstr, s);
            SetStackType(StackType.String);
        }
        else if (lit.Value is bool b)
        {
            _il.Emit(b ? OpCodes.Ldc_I4_1 : OpCodes.Ldc_I4_0);
            _il.Emit(OpCodes.Box, typeof(bool));
            SetStackUnknown();
        }
        else
        {
            _il.Emit(OpCodes.Ldnull);
            SetStackType(StackType.Null);
        }
    }

    protected override void EmitThis()
    {
        // Load 'this' from outer state machine if captured
        if (_builder.Captures.Contains("this"))
        {
            // Get outer state machine's ThisField (non-standalone path)
            if (_ctx?.AsyncArrowOuterBuilders?.TryGetValue(_builder.Arrow, out var outerBuilder) == true &&
                outerBuilder.ThisField != null)
            {
                _il.Emit(OpCodes.Ldarg_0);
                _il.Emit(OpCodes.Ldfld, _builder.OuterStateMachineField!);
                _il.Emit(OpCodes.Unbox, _builder.OuterStateMachineType!);
                _il.Emit(OpCodes.Ldfld, outerBuilder.ThisField);
                SetStackUnknown();
                return;
            }

            // Standalone async arrow: 'this' captured as a standalone field in this state machine
            if (_builder.IsStandalone && _builder.StandaloneCaptureFields.TryGetValue("this", out var thisField))
            {
                _il.Emit(OpCodes.Ldarg_0);
                _il.Emit(OpCodes.Ldfld, thisField);
                SetStackUnknown();
                return;
            }
        }

        _il.Emit(OpCodes.Ldnull);
        SetStackType(StackType.Null);
    }

    protected override void EmitSuper(Expr.Super s)
    {
        // Load hoisted 'this' from outer state machine if captured
        if (_builder.Captures.Contains("this"))
        {
            if (_ctx?.AsyncArrowOuterBuilders?.TryGetValue(_builder.Arrow, out var outerBuilder) == true &&
                outerBuilder.ThisField != null)
            {
                _il.Emit(OpCodes.Ldarg_0);
                _il.Emit(OpCodes.Ldfld, _builder.OuterStateMachineField!);
                _il.Emit(OpCodes.Unbox, _builder.OuterStateMachineType!);
                _il.Emit(OpCodes.Ldfld, outerBuilder.ThisField);
            }
            else
            {
                _il.Emit(OpCodes.Ldnull);
            }
        }
        else
        {
            _il.Emit(OpCodes.Ldnull);
        }

        _il.Emit(OpCodes.Ldstr, s.Method?.Lexeme ?? "constructor");
        _il.Emit(OpCodes.Call, _ctx!.Runtime!.GetSuperMethod);
        SetStackUnknown();
    }
}
