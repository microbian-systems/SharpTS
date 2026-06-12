using SharpTS.Tests.Infrastructure;
using Xunit;

namespace SharpTS.Tests.SharedTests;

/// <summary>
/// Tests for guest classes extending the built-in Promise (#242).
/// Runs against both interpreter and compiler.
/// </summary>
public class PromiseSubclassTests
{
    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void ExtendsPromise_ExecutorConstructionAndBrand(ExecutionMode mode)
    {
        var source = """
            class MyPromise<T> extends Promise<T> {}
            async function main() {
                const p = new MyPromise<number>((resolve, reject) => resolve(41));
                console.log(p instanceof MyPromise);
                console.log(p instanceof Promise);
                console.log(await p);
            }
            main();
            """;

        var output = TestHarness.Run(source, mode);
        Assert.Equal("true\ntrue\n41\n", output);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void ExtendsPromise_MethodsAndFields(ExecutionMode mode)
    {
        var source = """
            class MyPromise<T> extends Promise<T> {
                label: string = "mine";
                tag(): string { return "MyPromise:" + this.label; }
            }
            async function main() {
                const p = new MyPromise<number>((resolve) => resolve(1));
                console.log(p.label);
                console.log(p.tag());
                p.label = "updated";
                console.log(p.tag());
            }
            main();
            """;

        var output = TestHarness.Run(source, mode);
        Assert.Equal("mine\nMyPromise:mine\nMyPromise:updated\n", output);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void ExtendsPromise_StaticSideInheritance_Resolve(ExecutionMode mode)
    {
        var source = """
            class MyPromise<T> extends Promise<T> {}
            async function main() {
                const p = MyPromise.resolve(1);
                console.log(p instanceof MyPromise);
                console.log(p instanceof Promise);
                console.log(await p);
            }
            main();
            """;

        var output = TestHarness.Run(source, mode);
        Assert.Equal("true\ntrue\n1\n", output);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void ExtendsPromise_ThenReturnsSubclass(ExecutionMode mode)
    {
        var source = """
            class MyPromise<T> extends Promise<T> {}
            async function main() {
                const p = new MyPromise<number>((resolve) => resolve(41));
                const q = p.then(v => v + 1);
                console.log(q instanceof MyPromise);
                console.log(await q);
            }
            main();
            """;

        var output = TestHarness.Run(source, mode);
        Assert.Equal("true\n42\n", output);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void ExtendsPromise_RejectAndCatch(ExecutionMode mode)
    {
        var source = """
            class MyPromise<T> extends Promise<T> {}
            async function main() {
                const p = MyPromise.reject(new Error("boom")).catch((e: any) => "caught:" + e.message);
                console.log(p instanceof MyPromise);
                console.log(await p);
            }
            main();
            """;

        var output = TestHarness.Run(source, mode);
        Assert.Equal("true\ncaught:boom\n", output);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void ExtendsPromise_UserConstructorWithSuper(ExecutionMode mode)
    {
        var source = """
            class Tracked<T> extends Promise<T> {
                created: string;
                constructor(executor: (resolve: (v: T) => void, reject: (r: any) => void) => void) {
                    super(executor);
                    this.created = "yes";
                }
            }
            async function main() {
                const t = new Tracked<string>((resolve) => resolve("v"));
                console.log(t.created);
                console.log(t instanceof Tracked, t instanceof Promise);
                console.log(await t);
            }
            main();
            """;

        var output = TestHarness.Run(source, mode);
        Assert.Equal("yes\ntrue true\nv\n", output);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void ExtendsPromise_TransitiveSubclass(ExecutionMode mode)
    {
        var source = """
            class MyPromise<T> extends Promise<T> {}
            class Deeper<T> extends MyPromise<T> {}
            async function main() {
                const t = new Deeper<number>((resolve) => resolve(7));
                console.log(t instanceof Deeper);
                console.log(await t);
                const d = Deeper.resolve("x");
                console.log(d instanceof Deeper);
                console.log(d instanceof MyPromise);
                console.log(d instanceof Promise);
                console.log(await d);
            }
            main();
            """;

        var output = TestHarness.Run(source, mode);
        Assert.Equal("true\n7\ntrue\ntrue\ntrue\nx\n", output);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void ExtendsPromise_Combinators(ExecutionMode mode)
    {
        var source = """
            class MyPromise<T> extends Promise<T> {}
            async function main() {
                const all = MyPromise.all([MyPromise.resolve(3), Promise.resolve(4)]);
                console.log(all instanceof MyPromise);
                const r = await all;
                console.log(r[0] + r[1]);
                const raced = MyPromise.race([MyPromise.resolve("first")]);
                console.log(raced instanceof MyPromise);
                console.log(await raced);
            }
            main();
            """;

        var output = TestHarness.Run(source, mode);
        Assert.Equal("true\n7\ntrue\nfirst\n", output);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void ExtendsPromise_WithResolvers(ExecutionMode mode)
    {
        var source = """
            class MyPromise<T> extends Promise<T> {}
            async function main() {
                const wr = MyPromise.withResolvers();
                wr.resolve(9);
                console.log(await wr.promise);
            }
            main();
            """;

        var output = TestHarness.Run(source, mode);
        Assert.Equal("9\n", output);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void ExtendsPromise_DynamicDispatch_ThenKeepsBrand(ExecutionMode mode)
    {
        var source = """
            class MyPromise<T> extends Promise<T> {}
            async function main() {
                const p: any = MyPromise.resolve(5);
                const r = p.then((v: number) => v * 2);
                console.log(r instanceof MyPromise);
                console.log(await r);
            }
            main();
            """;

        var output = TestHarness.Run(source, mode);
        Assert.Equal("true\n10\n", output);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void ExtendsPromise_BasePromiseUnaffected(ExecutionMode mode)
    {
        var source = """
            class MyPromise<T> extends Promise<T> {}
            async function main() {
                const q = Promise.resolve(7);
                console.log(q instanceof MyPromise);
                console.log(q instanceof Promise);
                console.log(await q.then(v => v * 2));
            }
            main();
            """;

        var output = TestHarness.Run(source, mode);
        Assert.Equal("false\ntrue\n14\n", output);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void ExtendsPromise_TopLevelStaticThen(ExecutionMode mode)
    {
        // #309: the exact repro — inherited static + derived `then` dispatched
        // from the top-level (synchronous) program body, NOT inside an async
        // function. The async-body path (state machine) already worked; the
        // main ILEmitter.EmitCall path had drifted and omitted the derived
        // Promise-static block, so `MyP.resolve(1)` threw
        // "TypeError: undefined is not a function" in compiled mode.
        var source = """
            class MyP extends Promise<number> {}
            MyP.resolve(1).then(v => console.log("got", v));
            """;

        var output = TestHarness.Run(source, mode);
        Assert.Equal("got 1\n", output);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void ExtendsPromise_TopLevelRejectCatch(ExecutionMode mode)
    {
        // #309 sibling: reject/catch on a non-generic subclass from the
        // top-level body.
        var source = """
            class MyP extends Promise<number> {}
            MyP.reject("boom").catch(e => console.log("caught", e));
            """;

        var output = TestHarness.Run(source, mode);
        Assert.Equal("caught boom\n", output);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void ExtendsPromise_ConstructorIdentity(ExecutionMode mode)
    {
        // ECMA-262 §27.2.5.1 / #221: subclass instances report the subclass
        // as .constructor; plain promises report Promise.
        var source = """
            class MyPromise<T> extends Promise<T> {}
            async function main() {
                const p = MyPromise.resolve(1);
                console.log(p.constructor === MyPromise);
                console.log(p.constructor === Promise);
                const q = Promise.resolve(1);
                console.log(q.constructor === Promise);
            }
            main();
            """;

        var output = TestHarness.Run(source, mode);
        Assert.Equal("true\nfalse\ntrue\n", output);
    }

    #region SpeciesConstructor (#221) — then/catch/finally consult @@species

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void Species_ThenRedirectsToPlainPromise(ExecutionMode mode)
    {
        // ECMA-262 §27.2.5.4 / §7.3.22: then builds its result through
        // SpeciesConstructor(promise, %Promise%) = promise.constructor[@@species].
        // A subclass whose @@species returns Promise gets a PLAIN promise from
        // then — not an instance of the subclass.
        var source = """
            class MyP extends Promise<number> {
                static get [Symbol.species]() { return Promise; }
            }
            async function main() {
                const q = MyP.resolve(1).then(v => v + 1);
                console.log(q instanceof MyP);
                console.log(q instanceof Promise);
                console.log(await q);
            }
            main();
            """;

        var output = TestHarness.Run(source, mode);
        Assert.Equal("false\ntrue\n2\n", output);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void Species_DefaultIsReceiverClass(ExecutionMode mode)
    {
        // No @@species override: the inherited Promise[@@species] returns the
        // constructor itself, so then stays subclass-typed.
        var source = """
            class MyP extends Promise<number> {}
            async function main() {
                const q = MyP.resolve(1).then(v => v + 1);
                console.log(q instanceof MyP);
                console.log(await q);
            }
            main();
            """;

        var output = TestHarness.Run(source, mode);
        Assert.Equal("true\n2\n", output);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void Species_ThenRedirectsToOtherSubclass(ExecutionMode mode)
    {
        // @@species may name a different Promise subclass; then constructs
        // through it.
        var source = """
            class Other extends Promise<number> {}
            class MyP extends Promise<number> {
                static get [Symbol.species]() { return Other; }
            }
            async function main() {
                const q = MyP.resolve(1).then(v => v);
                console.log(q instanceof Other);
                console.log(q instanceof MyP);
                console.log(await q);
            }
            main();
            """;

        var output = TestHarness.Run(source, mode);
        Assert.Equal("true\nfalse\n1\n", output);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void Species_UndefinedFallsBackToPromise(ExecutionMode mode)
    {
        // SpeciesConstructor: an @@species of undefined/null defaults to
        // %Promise% (ECMA-262 §7.3.22 step 3-4).
        var source = """
            class MyP extends Promise<number> {
                static get [Symbol.species]() { return undefined as any; }
            }
            async function main() {
                const q = MyP.resolve(1).then(v => v);
                console.log(q instanceof MyP);
                console.log(q instanceof Promise);
            }
            main();
            """;

        var output = TestHarness.Run(source, mode);
        Assert.Equal("false\ntrue\n", output);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void Species_CatchAndFinallyFollowSpecies(ExecutionMode mode)
    {
        // catch (= then(undefined, onRejected)) and finally also route their
        // result through SpeciesConstructor.
        var source = """
            class MyP extends Promise<number> {
                static get [Symbol.species]() { return Promise; }
            }
            async function main() {
                const c = MyP.reject("x").catch(e => 0);
                console.log(c instanceof MyP);
                const f = MyP.resolve(1).finally(() => {});
                console.log(f instanceof MyP);
                console.log(await f);
            }
            main();
            """;

        var output = TestHarness.Run(source, mode);
        Assert.Equal("false\nfalse\n1\n", output);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void Species_CombinatorsUseReceiverConstructorDirectly(ExecutionMode mode)
    {
        // Static methods (resolve/all/race) build through the receiver
        // constructor C directly (e.g. §27.2.4.1 NewPromiseCapability(C)), NOT
        // C[@@species] — so even with @@species → Promise they stay subclass-typed.
        var source = """
            class MyP extends Promise<number> {
                static get [Symbol.species]() { return Promise; }
            }
            async function main() {
                console.log(MyP.resolve(1) instanceof MyP);
                console.log(MyP.all([1, 2]) instanceof MyP);
                console.log(MyP.race([1, 2]) instanceof MyP);
            }
            main();
            """;

        var output = TestHarness.Run(source, mode);
        Assert.Equal("true\ntrue\ntrue\n", output);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void Species_GenericSubclassOverride(ExecutionMode mode)
    {
        // A GENERIC Promise subclass with a @@species override resolves in both
        // modes. The static accessor is registered under the open generic
        // definition (MyP`1) while the receiver's runtime type is closed
        // (MyP<object>); compiled mode reconciles the two (#351).
        var source = """
            class MyP<T> extends Promise<T> {
                static get [Symbol.species]() { return Promise; }
            }
            async function main() {
                const q = MyP.resolve(1).then(v => v);
                console.log(q instanceof MyP);
                console.log(q instanceof Promise);
            }
            main();
            """;

        var output = TestHarness.Run(source, mode);
        Assert.Equal("false\ntrue\n", output);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void Species_GenericSubclassRedirectsToOtherGenericSubclass(ExecutionMode mode)
    {
        // A generic subclass whose @@species names ANOTHER generic Promise
        // subclass: then must construct through that (also generic) species. In
        // compiled mode the species value is the open generic definition
        // (Other`1) and is closed before construction (#351).
        var source = """
            class Other<T> extends Promise<T> {}
            class MyP<T> extends Promise<T> {
                static get [Symbol.species]() { return Other; }
            }
            async function main() {
                const q = MyP.resolve(1).then(v => v);
                console.log(q instanceof Other);
                console.log(q instanceof MyP);
                console.log(await q);
            }
            main();
            """;

        var output = TestHarness.Run(source, mode);
        Assert.Equal("true\nfalse\n1\n", output);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void Species_GenericSubclassStaticReadResolvesOverride(ExecutionMode mode)
    {
        // The guest-visible static read `(MyP as any)[Symbol.species]` of a
        // generic subclass: the class reference is the open generic definition
        // (MyP`1), and reading the static @@species getter must close it before
        // reflective Invoke rather than crashing / returning undefined (#351).
        var source = """
            class MyP<T> extends Promise<T> {
                static get [Symbol.species]() { return Promise; }
            }
            console.log((MyP as any)[Symbol.species] === Promise);
            """;

        var output = TestHarness.Run(source, mode);
        Assert.Equal("true\n", output);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.InterpretedOnly), MemberType = typeof(ExecutionModes))]
    public void Species_ExpandoOverride_Interpreted(ExecutionMode mode)
    {
        // A dynamically-assigned static @@species ((C as any)[Symbol.species] =
        // Promise, #262) is consulted by then in the interpreter. Compiled mode
        // only reads the declared-accessor registry, so the expando is not yet
        // consulted there — tracked by #349.
        var source = """
            class MyP extends Promise<number> {}
            (MyP as any)[Symbol.species] = Promise;
            async function main() {
                const q = MyP.resolve(1).then(v => v);
                console.log(q instanceof MyP);
                console.log(q instanceof Promise);
            }
            main();
            """;

        var output = TestHarness.Run(source, mode);
        Assert.Equal("false\ntrue\n", output);
    }

    #endregion

}
