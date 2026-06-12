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

}
