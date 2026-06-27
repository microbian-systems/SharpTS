using SharpTS.Execution;
using SharpTS.Tests.Infrastructure;
using Xunit;

namespace SharpTS.Tests.SharedTests;

/// <summary>
/// Realm-isolation invariants for built-in state that must NOT leak across
/// <see cref="Interpreter"/> instances (each Interpreter is its own ECMA-262
/// agent). This is the unit-level guard for the per-realm-ization that moves
/// process-global mutable built-in state onto the Interpreter, following the
/// <c>RegExp.prototype</c> precedent (#101). Phase 1: the <c>Symbol.for</c>
/// registry.
/// </summary>
public class RealmIsolationTests
{
    /// <summary>
    /// The happy path still holds within a single realm in both modes:
    /// <c>Symbol.for</c> is idempotent for a given key and <c>Symbol.keyFor</c>
    /// round-trips a registered symbol while returning <c>undefined</c> for an
    /// unregistered one.
    /// </summary>
    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void SymbolFor_WithinRealm_IsIdempotentAndRoundTrips(ExecutionMode mode)
    {
        var source = """
            const a = Symbol.for("k");
            const b = Symbol.for("k");
            console.log(a === b);
            console.log(Symbol.keyFor(a) === "k");
            console.log(Symbol.keyFor(Symbol("k")) === undefined);
            """;

        var output = TestHarness.Run(source, mode);
        Assert.Equal("true\ntrue\ntrue\n", output);
    }

    /// <summary>
    /// The <c>Symbol.for</c> registry is per-realm: the same key yields distinct
    /// symbols in different interpreters, a symbol registered in one realm is
    /// foreign to another realm's <c>Symbol.keyFor</c>, and a freshly
    /// constructed realm starts empty. Before this was moved onto the
    /// Interpreter, the registry was a process-wide static and these symbols
    /// would have been shared across every realm (and raced across worker
    /// threads). Interpreter-only: exercises the realm-owned registry directly.
    /// </summary>
    [Fact]
    public void SymbolForRegistry_IsPerRealm()
    {
        using var realmA = new Interpreter(TextWriter.Null, TextWriter.Null);
        using var realmB = new Interpreter(TextWriter.Null, TextWriter.Null);

        var a1 = realmA.SymbolFor("shared");
        var a2 = realmA.SymbolFor("shared");
        var b1 = realmB.SymbolFor("shared");

        // Idempotent within a realm.
        Assert.Same(a1, a2);

        // Independent across realms: the same key yields distinct symbols.
        Assert.NotSame(a1, b1);

        // keyFor resolves a realm's own registered symbol...
        Assert.Equal("shared", realmA.SymbolKeyFor(a1));
        Assert.Equal("shared", realmB.SymbolKeyFor(b1));

        // ...but a symbol from another realm is not in this realm's registry.
        Assert.Null(realmB.SymbolKeyFor(a1));
        Assert.Null(realmA.SymbolKeyFor(b1));

        // A pristine realm starts empty — no leakage from A or B.
        using var realmC = new Interpreter(TextWriter.Null, TextWriter.Null);
        Assert.Null(realmC.SymbolKeyFor(a1));
    }
}
