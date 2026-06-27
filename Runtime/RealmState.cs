using SharpTS.Runtime.Types;

namespace SharpTS.Runtime;

/// <summary>
/// Resets process-global mutable built-in state that is shared across
/// <c>Interpreter</c> instances rather than owned per-realm.
///
/// <para>
/// Most built-ins are fine as process-wide singletons because their members
/// are immutable. The exceptions are the primitive prototypes
/// (<see cref="SharpTSNumberPrototype"/>, <see cref="SharpTSBooleanPrototype"/>,
/// <see cref="SharpTSStringPrototype"/>) and <see cref="SharpTSMath"/>, which
/// each carry a guest-writable <c>_extras</c> bag. Guest code mutates these
/// (e.g. <c>Number.prototype.toString = fn</c>, <c>Boolean.prototype[0] = …</c>,
/// <c>Math.x = …</c>), and because the backing objects are static singletons the
/// writes survive into every later <c>new Interpreter(...)</c> in the same
/// process. (<c>RegExp.prototype</c> — issue #101 — and the <c>Symbol.for</c>
/// registry are already per-realm on the <c>Interpreter</c>; these primitive
/// prototypes and <c>Math</c> are the remaining vectors, tracked for the same
/// per-realm treatment.)
/// </para>
///
/// <para>
/// This is only observable when one process runs several realms — the Test262
/// runner, which executes thousands of guest scripts back-to-back in each
/// worker. Left unreset, an earlier test's mutation flips the outcome of a
/// later, order-dependent test, making the conformance results
/// non-deterministic (issue #964 follow-up). The runner calls
/// <see cref="ResetMutableBuiltInState"/> before each test so every script sees
/// a pristine realm. Normal single-program execution (CLI, one Interpreter)
/// never needs this, and the main test suite does not use it.
/// </para>
///
/// <para>
/// Not thread-safe with respect to a concurrently-executing interpreter: it
/// must only be called when no other realm is live in the process (the Test262
/// worker runs its tests serially). The principled long-term fix is to make
/// these prototypes per-realm like <c>RegExp.prototype</c>; that is a larger
/// refactor of the hot primitive-dispatch paths and is tracked separately.
/// </para>
/// </summary>
public static class RealmState
{
    /// <summary>
    /// Clears every process-global mutable built-in vector listed above. Cheap
    /// (nulls a few dictionary references and clears two small maps); safe to
    /// call once per test.
    /// </summary>
    public static void ResetMutableBuiltInState()
    {
        SharpTSMath.Instance.ClearExtras();
        SharpTSNumberPrototype.Instance.ClearExtras();
        SharpTSBooleanPrototype.Instance.ClearExtras();
        SharpTSStringPrototype.Instance.ClearExtras();
    }
}
