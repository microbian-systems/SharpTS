namespace SharpTS.Compilation;

public partial class GeneratorMoveNextEmitter
{
    // EmitCall: inherited from ExpressionEmitterBase (base handles all 16 dispatch pathways
    // including console methods, static type dispatch, built-in modules, class methods,
    // direct function calls, Promise methods, and generic InvokeValue fallback)
    //
    // Note: The previous override only handled console.log, direct function calls, and
    // Map/Set methods - a subset of what the base provides. Map/Set dispatch is handled
    // by TypeEmitterRegistry in the base.
}
