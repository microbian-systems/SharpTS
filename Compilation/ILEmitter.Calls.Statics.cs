namespace SharpTS.Compilation;

public partial class ILEmitter
{
    // EmitGlobalParseInt, EmitGlobalParseFloat, EmitGlobalIsNaN, EmitGlobalIsFinite,
    // EmitStructuredClone: moved to ExpressionEmitterBase.CallHelpers.cs so all emitters
    // benefit. ILEmitter's EmitCall override dispatches these inline before the handler
    // chain, so they are still handled optimally with EmitBoxIfNeeded.
}
