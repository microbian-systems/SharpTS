using SharpTS.Diagnostics.Exceptions;
using SharpTS.Parsing;

namespace SharpTS.Compilation;

public partial class GeneratorMoveNextEmitter
{
    /// <summary>
    /// Arrow emission inside a generator <c>MoveNext</c>. Delegates the actual emission to the
    /// base <see cref="ExpressionEmitterBase.EmitArrowFunction"/> (which instantiates the display
    /// class for capturing arrows via the GetThisField / GetHoistedVariableField hooks), but first
    /// rejects the one shape the generator path cannot yet honour: an arrow that <em>writes</em> to
    /// a variable it captures from the enclosing generator scope.
    /// </summary>
    protected override void EmitArrowFunction(Expr.ArrowFunction af)
    {
        // Capturing arrows snapshot their captures BY VALUE into the arrow's display class. That is
        // correct for read-only captures (the common `yield arr.map(x => x + base)` case), but if the
        // arrow assigns to a captured variable, the write lands on the private snapshot and never
        // reaches the generator's own storage — the mutation is silently lost. Honouring it needs a
        // shared function-level display class threaded through the generator state machine (as plain
        // and async functions already have, but generators do not — see #674). Fail fast with a clear
        // message rather than miscompiling `arr.forEach(n => sum += n)` to a wrong result.
        if (!af.IsAsync &&
            _ctx?.DisplayClassFields != null &&
            _ctx.DisplayClassFields.TryGetValue(af, out var captureFields) &&
            captureFields.Count > 0)
        {
            var written = CapturedWriteAnalysis.CollectImmediateWrites(af);
            written.IntersectWith(captureFields.Keys);
            if (written.Count > 0)
            {
                var names = string.Join(", ", written.OrderBy(n => n, System.StringComparer.Ordinal));
                throw new CompileException(
                    $"Compiled mode does not yet support an arrow/callback inside a generator (function*) " +
                    $"body that writes to a variable captured from the generator scope ({names}). The write " +
                    $"would be lost. Rewrite the mutation outside the callback (e.g. a for…of loop), or run " +
                    $"in interpreted mode. (#674)");
            }
        }

        base.EmitArrowFunction(af);
    }
}
