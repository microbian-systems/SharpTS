using SharpTS.Parsing;

namespace SharpTS.Compilation;

public partial class CompilationContext
{
    /// <summary>
    /// Block-body arrow inliner hook (#96 M5). When non-null,
    /// <see cref="StatementEmitterBase"/>'s <c>Stmt.Return</c> dispatch
    /// invokes this handler with the return-value expression instead of
    /// emitting a real <c>Ret</c>. The handler is responsible for emitting
    /// the per-iterator-helper continuation:
    /// <list type="bullet">
    ///   <item><description><c>forEach</c>: discard <paramref name="r"/> and branch to the loop advance label.</description></item>
    ///   <item><description><c>map</c>: emit-and-box <paramref name="r"/>, push to result list, branch to advance.</description></item>
    ///   <item><description><c>filter</c>: emit-and-truthy-check <paramref name="r"/>, gate <c>result.Add(element)</c>, branch to advance.</description></item>
    ///   <item><description><c>find</c>/<c>findLast</c>: emit-and-truthy-check <paramref name="r"/>, on truthy store <c>elementLocal</c> to <c>resultLocal</c> and branch to <c>done</c>.</description></item>
    ///   <item><description><c>findIndex</c>/<c>findLastIndex</c>: similar but stores <c>(double)i</c>.</description></item>
    ///   <item><description><c>some</c>/<c>every</c>: emit-and-truthy-check, on match (truthy for some, falsy for every) store boxed-bool result and branch to <c>done</c>.</description></item>
    ///   <item><description><c>reduce</c>/<c>reduceRight</c>: emit-and-box <paramref name="r"/>, store to <c>accLocal</c>, branch to advance.</description></item>
    /// </list>
    /// Set/cleared exclusively via <see cref="ArrowInlining.BlockBodyArrowEmitter"/> in a try/finally;
    /// nested inlining is not currently supported (the eligibility gate
    /// rejects nested arrows in the body, so a single hook level suffices).
    /// </summary>
    public Action<Expr?>? InlinedReturnHandler { get; set; }
}
