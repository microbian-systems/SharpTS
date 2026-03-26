namespace SharpTS.Compilation;

public partial class ILEmitter
{
    // EmitSetTimeout, EmitClearTimeout, EmitSetInterval, EmitClearInterval,
    // EmitQueueMicrotask: moved to ExpressionEmitterBase.CallHelpers.cs so all emitters
    // benefit. ILEmitter's EmitCall override handles timer dispatch through the
    // CallHandlerRegistry which calls these methods on the base class.
}
