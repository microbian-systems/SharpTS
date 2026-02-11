using System.Reflection;
using System.Reflection.Emit;
using SharpTS.Parsing;
using SharpTS.TypeSystem;

namespace SharpTS.Compilation;

public partial class ILEmitter
{
    #region Stack Type Tracking

    /// <summary>
    /// Returns the stack type that an expression will produce based on TypeMap.
    /// This method stays in ILEmitter because it requires access to _ctx.TypeMap.
    /// </summary>
    private StackType GetExpressionStackType(Expr expr)
    {
        var type = _ctx.TypeMap?.Get(expr);
        return type switch
        {
            TypeSystem.TypeInfo.Primitive { Type: TokenType.TYPE_NUMBER } => StackType.Double,
            TypeSystem.TypeInfo.Primitive { Type: TokenType.TYPE_BOOLEAN } => StackType.Boolean,
            TypeSystem.TypeInfo.String => StackType.String,
            TypeSystem.TypeInfo.Null => StackType.Null,
            _ => StackType.Unknown
        };
    }

    #endregion

    #region Boxing and Type Conversion - Delegated to StateMachineEmitHelpers

    // Note: EnsureBoxed is inherited from ExpressionEmitterBase
    public new void EnsureDouble() => _helpers.EnsureDouble();
    public new void EnsureBoolean() => _helpers.EnsureBoolean();
    public new void EnsureString() => _helpers.EnsureString();

    #endregion

    #region ILEmitter-only Helpers - Delegated to StateMachineEmitHelpers

    // Arithmetic (box-and-return variants unique to ILEmitter)
    private void EmitAddAndBox() => _helpers.EmitAddAndBox();
    private void EmitSubAndBox() => _helpers.EmitSubAndBox();
    private void EmitMulAndBox() => _helpers.EmitMulAndBox();
    private void EmitDivAndBox() => _helpers.EmitDivAndBox();

    // Variable loads (unique to ILEmitter)
    private void EmitLdloc(LocalBuilder local, Type localType) => _helpers.EmitLdloc(local, localType);
    private void EmitLdarg0Unknown() => _helpers.EmitLdarg0Unknown();
    private void EmitLdsfldUnknown(FieldInfo field) => _helpers.EmitLdsfldUnknown(field);

    // Specialized (unique to ILEmitter)
    private void EmitObjectEqualsBoxed_NoBox() => _helpers.EmitObjectEqualsBoxed_NoBox();

    #endregion
}
