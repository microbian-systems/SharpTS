using SharpTS.Tests.Infrastructure;
using SharpTS.TypeSystem.Exceptions;
using Xunit;

namespace SharpTS.Tests.TypeCheckerTests;

/// <summary>
/// The single-/dual-argument dedicated built-in container records — <c>IterableIterator&lt;T&gt;</c> /
/// <c>Iterator&lt;T&gt;</c> (=> <c>TypeInfo.Iterator</c>), <c>WeakRef&lt;T&gt;</c>,
/// <c>FinalizationRegistry&lt;T&gt;</c> — now resolve from a type REFERENCE instead of degrading to
/// <c>any</c> (#456, completing the Map/Set/WeakMap/WeakSet work in #347 / PR&#160;#455). Resolving
/// them also surfaced that the dedicated iterable records lacked a compatibility arm: <c>Iterator</c>,
/// <c>Generator</c>/<c>AsyncGenerator</c> and <c>FinalizationRegistry</c> all fell through to the
/// final <c>return false</c>, so even <c>let g: Generator&lt;number&gt; = gen()</c> spuriously failed.
/// The arms added with this change close that, with a sync <c>Generator</c> accepted where an
/// <c>Iterator</c> is expected (it structurally IS one) so previously-<c>any</c> assignments do not
/// regress.
/// </summary>
public class DedicatedContainerTypeReferenceTests
{
    // ---- IterableIterator / Iterator references are strongly typed (no longer `any`) ----

    [Fact]
    public void IterableIterator_FakeMember_Rejected()
    {
        // The issue's headline example: `it` is now Iterator<number>, not `any`, so an unknown member
        // is a compile-time type error instead of silently passing and throwing at runtime.
        var source = """
            let it: IterableIterator<number> = [1, 2, 3].values();
            it.totallyFakeMethod();
            """;
        Assert.ThrowsAny<TypeCheckException>(() => TestHarness.RunInterpreted(source));
    }

    [Fact]
    public void Iterator_AltSpelling_FakeMember_Rejected()
    {
        // `Iterator<T>` and `IterableIterator<T>` both resolve to the same TypeInfo.Iterator record.
        var source = """
            let it: Iterator<number> = [1, 2, 3].values();
            it.nope();
            """;
        Assert.ThrowsAny<TypeCheckException>(() => TestHarness.RunInterpreted(source));
    }

    [Fact]
    public void IterableIterator_FromArrayValues_Accepted()
    {
        TestHarness.RunInterpreted("let it: IterableIterator<number> = [1, 2, 3].values();");
    }

    [Fact]
    public void IterableIterator_FromMapEntries_Accepted()
    {
        // Map.entries() yields IterableIterator<[K, V]>; the tuple element type must line up.
        var source = """
            const m = new Map<string, number>();
            let it: IterableIterator<[string, number]> = m.entries();
            """;
        TestHarness.RunInterpreted(source);
    }

    [Fact]
    public void Iterator_ElementMismatch_Rejected()
    {
        // The element type is now real: an Iterator<number> cannot satisfy IterableIterator<string>.
        var source = "function f(it: IterableIterator<number>): IterableIterator<string> { return it; }";
        Assert.ThrowsAny<TypeCheckException>(() => TestHarness.RunInterpreted(source));
    }

    [Fact]
    public void Iterator_ThreeTypeArgs_LibSpelling_Accepted()
    {
        // lib.d.ts is Iterator<T, TReturn = any, TNext = any>; SharpTS keeps only the element type but
        // must accept (and ignore) the optional TReturn/TNext, the same way Generator drops them.
        TestHarness.RunInterpreted("let it: Iterator<number, void, undefined> = [1].values();");
    }

    [Fact]
    public void Iterator_TooManyTypeArgs_RejectedWithTs2707()
    {
        // A defaulted type-parameter range (1..3) uses TS2707 ("requires between N and M"), the code
        // tsc emits here — not the exact-count TS2314 the single-arity records below use.
        var source = "let it: Iterator<number, void, undefined, string> = [1].values();";
        var ex = Assert.ThrowsAny<TypeCheckException>(() => TestHarness.RunInterpreted(source));
        Assert.Equal("TS2707", ex.Diagnostic.TsCode);
    }

    // ---- Generator <-> Iterator direction (a sync Generator IS an IterableIterator) ----

    [Fact]
    public void Generator_SelfAssignment_Accepted()
    {
        // Regression that resolving these records exposed: a same-record pair with no compatibility arm
        // fell through to `return false`, so this spuriously failed before this change.
        var source = """
            function* gen(): Generator<number> { yield 1; }
            let g: Generator<number> = gen();
            """;
        TestHarness.RunInterpreted(source);
    }

    [Fact]
    public void AsyncGenerator_SelfAssignment_Accepted()
    {
        var source = """
            async function* gen(): AsyncGenerator<number> { yield 1; }
            let g: AsyncGenerator<number> = gen();
            """;
        TestHarness.RunInterpreted(source);
    }

    [Fact]
    public void Generator_AssignableToIterableIterator_Accepted()
    {
        // Generator<T> extends IterableIterator<T>; without this arm the assignment would regress now
        // that IterableIterator<number> is strongly typed rather than `any`.
        var source = """
            function* gen(): Generator<number> { yield 1; }
            let it: IterableIterator<number> = gen();
            """;
        TestHarness.RunInterpreted(source);
    }

    [Fact]
    public void Iterator_NotAssignableToGenerator_Rejected()
    {
        // The relation is asymmetric: a plain Iterator is not a Generator.
        var source = "function f(it: IterableIterator<number>): Generator<number> { return it; }";
        Assert.ThrowsAny<TypeCheckException>(() => TestHarness.RunInterpreted(source));
    }

    [Fact]
    public void AsyncGenerator_NotAssignableToSyncIterator_Rejected()
    {
        // Sync and async iterator hierarchies are distinct: an AsyncGenerator is not a sync Iterator.
        var source = """
            async function* agen(): AsyncGenerator<number> { yield 1; }
            function f(): IterableIterator<number> { return agen(); }
            """;
        Assert.ThrowsAny<TypeCheckException>(() => TestHarness.RunInterpreted(source));
    }

    [Fact]
    public void StructuralIteratorObject_AssignableToIterator_Accepted()
    {
        // tsc types Iterator<T> structurally: a hand-written object with a `next` method satisfies it.
        // Before #456 this was accepted because the annotation was `any`; rejecting it now would regress
        // the idiom (e.g. ArrayStaticTests.Array_From_CustomIterator). The element type is intentionally
        // not re-checked for structural sources — that, and structural for...of, are tracked in #485.
        var source = """
            function make(): Iterator<number> {
              let i = 0;
              return {
                next(): IteratorResult<number> {
                  return i < 3 ? { value: ++i, done: false } : { value: 0, done: true };
                }
              };
            }
            """;
        TestHarness.RunInterpreted(source);
    }

    // ---- WeakRef references are strongly typed ----

    [Fact]
    public void WeakRef_FakeMember_Rejected()
    {
        var source = """
            let wr: WeakRef<{ name: string }> = new WeakRef({ name: "x" });
            wr.totallyFakeMethod();
            """;
        Assert.ThrowsAny<TypeCheckException>(() => TestHarness.RunInterpreted(source));
    }

    [Fact]
    public void WeakRef_Deref_ReturnsTargetType_Accepted()
    {
        // deref() returns T | undefined; the non-null assertion narrows to the target's member type.
        // (Truthiness `if (d)` narrowing of T | undefined is a separate, pre-existing gap, so `!` is
        // used here to keep the test focused on #456's strong typing of the deref() return.)
        var source = """
            let wr: WeakRef<{ name: string }> = new WeakRef({ name: "x" });
            let n: string = wr.deref()!.name;
            """;
        TestHarness.RunInterpreted(source);
    }

    [Fact]
    public void WeakRef_Deref_TargetMemberMismatch_Rejected()
    {
        // The target type is real, so reading a non-existent member off it is an error.
        var source = """
            let wr: WeakRef<{ name: string }> = new WeakRef({ name: "x" });
            let n: number = wr.deref()!.missing;
            """;
        Assert.ThrowsAny<TypeCheckException>(() => TestHarness.RunInterpreted(source));
    }

    [Fact]
    public void WeakRef_TargetMismatch_Rejected()
    {
        var source = "function f(a: WeakRef<{ a: number }>): WeakRef<{ b: number }> { return a; }";
        Assert.ThrowsAny<TypeCheckException>(() => TestHarness.RunInterpreted(source));
    }

    [Fact]
    public void WeakRef_TooManyTypeArgs_RejectedWithTs2314()
    {
        var source = "let wr: WeakRef<object, string> = new WeakRef({});";
        var ex = Assert.ThrowsAny<TypeCheckException>(() => TestHarness.RunInterpreted(source));
        Assert.Equal("TS2314", ex.Diagnostic.TsCode);
    }

    // ---- FinalizationRegistry references are strongly typed ----

    [Fact]
    public void FinalizationRegistry_FakeMember_Rejected()
    {
        var source = """
            let fr: FinalizationRegistry<string> = new FinalizationRegistry((v: string) => {});
            fr.nope();
            """;
        Assert.ThrowsAny<TypeCheckException>(() => TestHarness.RunInterpreted(source));
    }

    [Fact]
    public void FinalizationRegistry_Register_TargetThenHeldValue_Accepted()
    {
        // register(target: WeakKey, heldValue: T, unregisterToken?: WeakKey). The target is an
        // arbitrary object and the SECOND argument carries the element type T — the previously
        // unobserved signature (annotation was `any`) had T mis-placed at parameter 0, which this
        // change corrects.
        var source = """
            function use(fr: FinalizationRegistry<string>): void {
              const target = { id: 1 };
              fr.register(target, "held");
              fr.register(target, "held", target);
              fr.unregister(target);
            }
            """;
        TestHarness.RunInterpreted(source);
    }

    [Fact]
    public void FinalizationRegistry_Register_WrongHeldValueType_Rejected()
    {
        // The held value (second argument) must match T; a number is not a string.
        var source = """
            function use(fr: FinalizationRegistry<string>): void {
              fr.register({ id: 1 }, 42);
            }
            """;
        Assert.ThrowsAny<TypeCheckException>(() => TestHarness.RunInterpreted(source));
    }

    [Fact]
    public void FinalizationRegistry_HeldValueMismatch_Rejected()
    {
        var source = "function f(a: FinalizationRegistry<string>): FinalizationRegistry<number> { return a; }";
        Assert.ThrowsAny<TypeCheckException>(() => TestHarness.RunInterpreted(source));
    }

    [Fact]
    public void FinalizationRegistry_TooManyTypeArgs_RejectedWithTs2314()
    {
        var source = "let fr: FinalizationRegistry<string, number> = new FinalizationRegistry((v: string) => {});";
        var ex = Assert.ThrowsAny<TypeCheckException>(() => TestHarness.RunInterpreted(source));
        Assert.Equal("TS2314", ex.Diagnostic.TsCode);
    }

    // ---- conditional-type `infer` extraction (DecomposeBuiltInContainer) ----

    [Fact]
    public void ConditionalInfer_FromIterableIterator_BindsElement()
    {
        var source = """
            type ElemOf<T> = T extends IterableIterator<infer V> ? V : "none";
            function f(): ElemOf<IterableIterator<number>> { return "hello"; }
            """;
        Assert.ThrowsAny<TypeCheckException>(() => TestHarness.RunInterpreted(source));
    }

    [Fact]
    public void ConditionalInfer_FromIterableIterator_CorrectBranchAccepted()
    {
        var source = """
            type ElemOf<T> = T extends IterableIterator<infer V> ? V : "none";
            let x: ElemOf<IterableIterator<number>> = 5;
            """;
        TestHarness.RunInterpreted(source);
    }

    [Fact]
    public void ConditionalInfer_FromWeakRef_BindsTarget()
    {
        var source = """
            type TargetOf<T> = T extends WeakRef<infer V> ? V : never;
            function f(): TargetOf<WeakRef<string>> { return 42; }
            """;
        Assert.ThrowsAny<TypeCheckException>(() => TestHarness.RunInterpreted(source));
    }

    [Fact]
    public void ConditionalInfer_FromFinalizationRegistry_BindsHeldValue()
    {
        var source = """
            type HeldOf<T> = T extends FinalizationRegistry<infer V> ? V : never;
            function f(): HeldOf<FinalizationRegistry<string>> { return 42; }
            """;
        Assert.ThrowsAny<TypeCheckException>(() => TestHarness.RunInterpreted(source));
    }

    // ---- regression guard: ordinary use of these annotations type-checks and runs in BOTH modes ----

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void AnnotatedIterables_BehaveIdenticallyAcrossModes(ExecutionMode mode)
    {
        // The annotations are type-level only; they must not perturb interpretation or IL codegen.
        var source = """
            function* gen(): Generator<number> { yield 1; yield 2; yield 3; }
            let g: Generator<number> = gen();
            let it: IterableIterator<number> = [10, 20].values();
            let sum = 0;
            for (const x of g) { sum += x; }
            for (const y of it) { sum += y; }
            const wr: WeakRef<{ v: number }> = new WeakRef({ v: 100 });
            sum += wr.deref()!.v;
            console.log(sum);
            """;
        Assert.Equal("136\n", TestHarness.Run(source, mode));
    }
}
