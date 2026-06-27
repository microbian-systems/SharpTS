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

    /// <summary>
    /// Within a realm, the bare <c>Math</c> global and <c>globalThis.Math</c>
    /// (dotted and bracketed) are the same object, and guest-added properties
    /// are visible through all three. Math is extensible per ECMA-262.
    /// Interpreter-only: per-realm-izing Math is interpreter-scoped (compiled
    /// mode emits its own Math/globalThis handling and is out of scope here).
    /// </summary>
    [Fact]
    public void Math_Identity_AndExtensibility_HoldWithinRealm()
    {
        var source = """
            console.log(globalThis.Math === Math);
            console.log(globalThis["Math"] === Math);
            (Math as any).answer = 42;
            console.log((Math as any).answer);
            console.log((globalThis.Math as any).answer);
            """;

        var output = TestHarness.Run(source, ExecutionMode.Interpreted);
        Assert.Equal("true\ntrue\n42\n42\n", output);
    }

    /// <summary>
    /// User-added Math properties are per-realm: a property attached to Math in
    /// one interpreter is invisible in another, and a pristine realm sees only
    /// the built-in surface. Before Math was moved onto the Interpreter, the
    /// extras bag was a process-wide singleton shared across every realm (and
    /// raced across worker threads). Interpreter-only: inspects realm state via
    /// the realm-owned Math instance.
    /// </summary>
    [Fact]
    public void Math_Extras_ArePerRealm()
    {
        using var realmA = new Interpreter(TextWriter.Null, TextWriter.Null);
        using var realmB = new Interpreter(TextWriter.Null, TextWriter.Null);

        // Distinct Math objects per realm.
        Assert.NotSame(realmA.GetMath(), realmB.GetMath());

        // A guest write in realm A lands on realm A's Math only.
        realmA.GetMath().SetExtra("answer", 42.0);
        Assert.True(realmA.GetMath().HasExtra("answer"));
        Assert.False(realmB.GetMath().HasExtra("answer"));

        // A pristine realm has no extras.
        using var realmC = new Interpreter(TextWriter.Null, TextWriter.Null);
        Assert.False(realmC.GetMath().HasExtra("answer"));
    }

    /// <summary>
    /// Within a realm, <c>String.prototype</c> has stable identity, accepts
    /// guest-added properties (ECMA-262: the primitive prototypes are ordinary
    /// objects), and the built-in methods still dispatch. Interpreter-only:
    /// per-realm-izing the primitive prototypes is interpreter-scoped (compiled
    /// mode emits its own prototype handling).
    /// </summary>
    [Fact]
    public void StringPrototype_Identity_AndExtensibility_HoldWithinRealm()
    {
        var source = """
            console.log(String.prototype === String.prototype);
            (String.prototype as any).foo = 7;
            console.log((String.prototype as any).foo);
            console.log("x".toUpperCase());
            """;

        var output = TestHarness.Run(source, ExecutionMode.Interpreted);
        Assert.Equal("true\n7\nX\n", output);
    }

    /// <summary>
    /// User-added properties on String/Number/Boolean.prototype are per-realm:
    /// distinct prototype objects per interpreter, and a write in one realm is
    /// invisible in another. Before these were moved onto the Interpreter the
    /// <c>_extras</c> bag was a process-wide singleton shared across every realm
    /// (and raced across worker threads). Interpreter-only.
    /// </summary>
    [Fact]
    public void PrimitivePrototypeExtras_ArePerRealm()
    {
        using var realmA = new Interpreter(TextWriter.Null, TextWriter.Null);
        using var realmB = new Interpreter(TextWriter.Null, TextWriter.Null);

        // Distinct prototype objects per realm.
        Assert.NotSame(realmA.GetStringPrototype(), realmB.GetStringPrototype());
        Assert.NotSame(realmA.GetNumberPrototype(), realmB.GetNumberPrototype());
        Assert.NotSame(realmA.GetBooleanPrototype(), realmB.GetBooleanPrototype());

        // Writes in realm A stay in realm A.
        realmA.GetStringPrototype().SetExtra("foo", 1.0);
        realmA.GetNumberPrototype().SetExtra("bar", 2.0);
        realmA.GetBooleanPrototype().SetExtra("baz", 3.0);

        Assert.True(realmA.GetStringPrototype().HasExtra("foo"));
        Assert.False(realmB.GetStringPrototype().HasExtra("foo"));
        Assert.False(realmB.GetNumberPrototype().HasExtra("bar"));
        Assert.False(realmB.GetBooleanPrototype().HasExtra("baz"));
    }
}
