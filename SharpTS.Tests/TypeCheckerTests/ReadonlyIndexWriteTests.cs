using SharpTS.Tests.Infrastructure;
using SharpTS.TypeSystem.Exceptions;
using Xunit;

namespace SharpTS.Tests.TypeCheckerTests;

/// <summary>
/// Writing through a numeric index of a readonly tuple or readonly array must be rejected with
/// TS2542 ("Index signature in type '…' only permits reading."), matching tsc (#509). The element
/// type may even be compatible — readonly is a property of the receiver's index signature, not of
/// the value — so the readonly check fires ahead of the element-type compatibility check. Keyed off
/// the tuple/array's own <c>IsReadonly</c>: an array whose only readonly part is its element stays
/// writable at the slot level. Type-checker-only; errors are mode-independent so these run interpreted.
/// </summary>
public class ReadonlyIndexWriteTests
{
    private static void AssertTsCode(string expected, string source)
    {
        var ex = Assert.ThrowsAny<TypeCheckException>(() => TestHarness.RunInterpreted(source));
        Assert.Equal(expected, ex.Diagnostic.TsCode);
    }

    // ---- readonly tuples (`as const`, `readonly [...]`) reject index writes ----

    [Fact]
    public void AsConstTuple_IndexWrite_SameValue_RejectedTS2542()
    {
        // `arr[0] = 1` writes the value already there, but a readonly tuple permits reading only.
        AssertTsCode("TS2542", "const arr = [1, 2, 3] as const; arr[0] = 1;");
    }

    [Fact]
    public void AsConstTuple_IndexWrite_WrongValue_IsReadonly_NotElementMismatch()
    {
        // Before #509 a wrong value surfaced as TS2322 (element mismatch); the readonly receiver is
        // the more specific, correct diagnosis — TS2542 — and fires first.
        AssertTsCode("TS2542", "const arr = [1, 2, 3] as const; arr[0] = 9;");
    }

    [Fact]
    public void ReadonlyTupleAnnotation_IndexWrite_RejectedTS2542()
    {
        AssertTsCode("TS2542", "const a: readonly [number, number] = [1, 2]; a[0] = 5;");
    }

    // ---- readonly arrays (`readonly T[]`, `ReadonlyArray<T>`) reject index writes ----

    [Fact]
    public void ReadonlyArrayAnnotation_IndexWrite_RejectedTS2542()
    {
        // The pre-existing gap independent of `as const`: a `readonly number[]` annotation.
        AssertTsCode("TS2542", "const a: readonly number[] = [1, 2, 3]; a[0] = 5;");
    }

    [Fact]
    public void ReadonlyArrayUtility_IndexWrite_RejectedTS2542()
    {
        // ReadonlyArray<T> routes through the interface readonly-number-index guard; regression cover.
        AssertTsCode("TS2542", "const a: ReadonlyArray<number> = [1, 2, 3]; a[0] = 5;");
    }

    [Fact]
    public void ReadonlyArray_CanonicalStringKey_IndexWrite_RejectedTS2542()
    {
        // `a["0"]` is the canonical numeric-string key — an element write too, so also TS2542.
        AssertTsCode("TS2542", "const a: readonly number[] = [1]; a[\"0\"] = 5;");
    }

    // Note: readonly arrays reached through a *union* (`readonly number[] | number[]`) are a separate,
    // pre-existing gap — the union collapses the readonly and mutable members during construction — and
    // are tracked in their own follow-up rather than here, to keep this change scoped to direct writes.

    // ---- mutable receivers and reads stay accepted (no over-rejection) ----

    [Fact]
    public void MutableArray_IndexWrite_Accepted()
    {
        TestHarness.RunInterpreted("const a: number[] = [1, 2, 3]; a[0] = 9;");
    }

    [Fact]
    public void MutableTuple_IndexWrite_Accepted()
    {
        TestHarness.RunInterpreted("const a: [number, number] = [1, 2]; a[0] = 9;");
    }

    [Fact]
    public void ReadonlyArray_IndexRead_Accepted()
    {
        // Reading a readonly array/tuple by index is always fine — only writes are blocked.
        TestHarness.RunInterpreted("const a = [1, 2, 3] as const; const x: number = a[0];");
    }

    [Fact]
    public void MutableArray_WithReadonlyElement_SlotWrite_Accepted()
    {
        // `[{ n: 1 } as const]` is a MUTABLE array of a readonly-element type — the slot is writable,
        // so the element-only readonly must not trigger TS2542 (the fix keys off the array's own flag).
        TestHarness.RunInterpreted("const arr = [{ n: 1 } as const]; arr[0] = arr[0];");
    }
}
