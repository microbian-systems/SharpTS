using SharpTS.Tests.Infrastructure;
using Xunit;

namespace SharpTS.Tests.SharedTests;

/// <summary>
/// Tests for invoking non-callable values (#260). Both modes must throw a guest
/// TypeError ("&lt;typeof&gt; is not a function") instead of silently evaluating to
/// null — the compiled-mode silent no-op masked dispatch regressions like #239.
/// </summary>
public class NonCallableInvocationTests
{
    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void NullCallee_ThrowsTypeError(ExecutionMode mode)
    {
        var source = """
            const f: any = null;
            try {
                f("x");
                console.log("no throw");
            } catch (e: any) {
                console.log(e instanceof TypeError, e.message);
            }
            """;

        var output = TestHarness.Run(source, mode);
        Assert.Equal("true object is not a function\n", output);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void UndefinedAndMissingMethod_ThrowTypeError(ExecutionMode mode)
    {
        var source = """
            function check(label: string, fn: () => void) {
                try { fn(); console.log(label, "no throw"); }
                catch (e: any) { console.log(label, e instanceof TypeError, e.message); }
            }
            const u: any = undefined;
            check("u", () => u());
            const obj: any = {};
            check("missing", () => obj.nope());
            const withNull: any = { m: null };
            check("nullMember", () => withNull.m(1));
            """;

        var output = TestHarness.Run(source, mode);
        Assert.Equal(
            "u true undefined is not a function\n" +
            "missing true undefined is not a function\n" +
            "nullMember true object is not a function\n",
            output);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void NonCallablePrimitive_ThrowsTypeError(ExecutionMode mode)
    {
        var source = """
            const n: any = 42;
            try { n(); console.log("no throw"); }
            catch (e: any) { console.log(e instanceof TypeError, e.message); }
            """;

        var output = TestHarness.Run(source, mode);
        Assert.Equal("true number is not a function\n", output);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void OptionalCall_OfNullish_ReturnsUndefined(ExecutionMode mode)
    {
        var source = """
            const g: any = null;
            console.log(g?.());
            const h: any = { m: undefined };
            console.log(h.m?.());
            """;

        var output = TestHarness.Run(source, mode);
        Assert.Equal("undefined\nundefined\n", output);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void OptionalChainMethodCall_ShortCircuitsWholeChain(ExecutionMode mode)
    {
        // ECMA-262 §13.3: a.b?.m(x) yields undefined when a.b is nullish — the
        // call must not be attempted. Compiled mode used to lean on the silent
        // non-callable no-op for this (surfaced by the yaml package's
        // tag.test?.test(value) once #260 made the fallback throw).
        var source = """
            const o: any = {};
            console.log(o.test?.test("x"));
            const arr: any = [{ a: 1 }, { test: /x/ }];
            console.log(arr.find((t: any) => t.test?.test("x"))?.test + "");
            const n: any = null;
            console.log(n?.m());
            """;

        var output = TestHarness.Run(source, mode);
        Assert.Equal("undefined\n/x/\nundefined\n", output);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void ForAwaitBreak_WithoutIteratorReturn_DoesNotThrow(ExecutionMode mode)
    {
        // iterator.return() is optional per the iterator protocol; the for-await
        // cleanup path must skip it (not throw) when absent.
        var source = """
            async function* gen() { yield 1; yield 2; }
            async function main() {
                for await (const v of gen()) {
                    console.log("got", v);
                    break;
                }
                console.log("done");
            }
            main();
            """;

        var output = TestHarness.Run(source, mode);
        Assert.Equal("got 1\ndone\n", output);
    }
}
