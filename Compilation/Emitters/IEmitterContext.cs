using System.Reflection.Emit;
using SharpTS.Parsing;

namespace SharpTS.Compilation.Emitters;

/// <summary>
/// Abstraction over IL emitters that allows type emitter strategies and call handlers
/// to work with all emitter types: ILEmitter (sync), AsyncMoveNextEmitter,
/// GeneratorMoveNextEmitter, AsyncArrowMoveNextEmitter, and AsyncGeneratorMoveNextEmitter.
/// </summary>
public interface IEmitterContext
{
    /// <summary>
    /// Gets the compilation context containing types, runtime methods, and other compilation state.
    /// </summary>
    CompilationContext Context { get; }

    /// <summary>
    /// Gets the current ILGenerator for emitting IL instructions.
    /// </summary>
    ILGenerator IL { get; }

    /// <summary>
    /// Emits IL for an expression, leaving the result on the evaluation stack.
    /// </summary>
    /// <param name="expr">The expression to emit.</param>
    void EmitExpression(Expr expr);

    /// <summary>
    /// Boxes the value on the stack if the expression type requires it.
    /// Value types (numbers, booleans) need boxing to become object references.
    /// </summary>
    /// <param name="expr">The expression whose result may need boxing.</param>
    void EmitBoxIfNeeded(Expr expr);

    /// <summary>
    /// Ensures the value on the stack is boxed (object reference).
    /// Unlike EmitBoxIfNeeded, this does not take an expression — it boxes based on current stack type.
    /// </summary>
    void EnsureBoxed();

    /// <summary>
    /// Emits an expression and ensures the result is an unboxed double on the stack.
    /// Used when a numeric value is required (e.g., Date setter arguments).
    /// </summary>
    /// <param name="expr">The expression to emit as a double.</param>
    void EmitExpressionAsDouble(Expr expr);

    /// <summary>
    /// Marks the stack as containing an unknown/object type.
    /// </summary>
    void SetStackUnknown();

    /// <summary>
    /// Marks the stack as containing a specific type.
    /// </summary>
    /// <param name="type">The stack type.</param>
    void SetStackType(StackType type);

    /// <summary>
    /// Attempts to emit a console method call (log, error, warn, info, debug, clear, time, timeEnd, timeLog, etc.).
    /// </summary>
    bool TryEmitConsoleMethod(Expr.Call call);

    /// <summary>
    /// Emits IL for the global fetch(url, options?) call.
    /// </summary>
    void EmitFetchCall(List<Expr> arguments);

    /// <summary>
    /// Emits conversion from the current stack value to the target parameter type.
    /// Handles boxing for object, unboxing for value types, union types, and pass-through.
    /// </summary>
    void EmitConversionForParameter(Expr expr, Type targetType);

    /// <summary>
    /// Emits a default value for the given type (null for reference types, 0 for numbers, etc.).
    /// </summary>
    void EmitDefaultForType(Type type);

    /// <summary>
    /// Emits IL that constructs a delegate of the given type pointing at the
    /// arrow's compiled body. For non-capturing arrows: <c>new Func(null, ldftn staticMethod)</c>.
    /// For capturing arrows: allocate the display class instance, populate
    /// captured fields, then <c>new Func(displayInstance, ldftn instanceInvoke)</c>.
    /// On success, leaves the delegate on the stack and returns true. Returns
    /// false (without emitting any IL) when the emitter doesn't support this
    /// path (state-machine emitters) or the arrow shape isn't compatible
    /// (named function expression, async, generator, or other unsupported
    /// configuration). Callers fall through to the legacy <c>$TSFunction</c>
    /// path when this returns false.
    /// </summary>
    bool TryEmitArrowAsDelegate(Expr.ArrowFunction af, Type delegateType);
}
