namespace SharpTS.Runtime;

/// <summary>
/// Formerly reset the process-global mutable built-in state shared across
/// <c>Interpreter</c> instances. That state has now all been moved per-realm
/// onto the <c>Interpreter</c> — <c>RegExp.prototype</c> (#101), the
/// <c>Symbol.for</c> registry, <c>Math</c>, and the <c>String</c>/<c>Number</c>/
/// <c>Boolean</c> prototypes — so a fresh <c>new Interpreter(...)</c> starts
/// with a pristine realm by construction and there is nothing left to reset.
///
/// <para>
/// <see cref="ResetMutableBuiltInState"/> is therefore a no-op. It is retained
/// only so the Test262 runner's per-test call site keeps compiling; both the
/// method and that call are slated for removal in the final per-realm cleanup
/// (worker_threads isolation, Phase 5), together with a concurrency regression
/// test. Unlike the old reset, the per-realm model is also safe under
/// concurrently-executing realms (worker threads), which the reset was not.
/// </para>
/// </summary>
public static class RealmState
{
    /// <summary>
    /// No-op: all previously process-global mutable built-in state is now owned
    /// per-realm by the <c>Interpreter</c>, so each realm is pristine by
    /// construction. Retained as a no-op for the Test262 runner's call site.
    /// </summary>
    public static void ResetMutableBuiltInState()
    {
        // Intentionally empty — see the type remarks.
    }
}
