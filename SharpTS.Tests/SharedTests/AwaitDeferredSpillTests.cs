using SharpTS.Tests.Infrastructure;
using Xunit;

namespace SharpTS.Tests.SharedTests;

/// <summary>
/// Regression tests for #400: a value spilled before an <c>await</c> and used after it
/// must survive the suspension. Unlike <see cref="AwaitStackSpillTests"/> (which awaits a
/// plain value that completes synchronously and so never suspends), every await here is a
/// genuinely <em>deferred</em> promise (resolved from a later event-loop turn via
/// <c>setTimeout</c>), so the compiled state machine actually re-enters MoveNext. Before
/// the fix the prefix/operand spilled to an IL local was lost on re-entry, because only
/// state-machine fields survive a MoveNext re-entry — the compiler printed the suffix only.
/// Runs against both interpreter and compiler.
/// </summary>
public class AwaitDeferredSpillTests
{
    // A promise that settles from a later event-loop turn, forcing the awaiting state
    // machine to actually suspend and resume (rather than complete synchronously).
    private const string Defer =
        "function defer(v: any, ms: number): Promise<any> { return new Promise(r => setTimeout(() => r(v), ms)); }\n";

    private static string Run(string body, ExecutionMode mode)
        => TestHarness.Run(Defer + "class C { x: any = 0; }\nasync function m() {\n" + body + "\n}\nm();\n", mode);

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void BinaryConcat_PrefixSurvivesDeferredAwait(ExecutionMode mode)
    {
        // The exact shape from the issue: the "concat: " prefix is pushed before the await.
        Assert.Equal("concat: 6\n", Run("""console.log("concat: " + (await defer(6, 5)));""", mode));
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void BinaryConcat_TwoDeferredAwaits(ExecutionMode mode)
    {
        Assert.Equal("L-R\n", Run("""console.log((await defer("L", 5)) + "-" + (await defer("R", 5)));""", mode));
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void TemplateLiteral_TwoDeferredAwaits(ExecutionMode mode)
    {
        Assert.Equal("a1-2\n", Run("""console.log(`${"a"}${await defer(1, 5)}-${await defer(2, 5)}`);""", mode));
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void TaggedTemplate_DeferredAwait(ExecutionMode mode)
    {
        Assert.Equal("p|7\n", Run(
            """
            const tag = (s: any, v: any) => s[0] + "|" + v;
            console.log(tag`p${await defer(7, 5)}`);
            """, mode));
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void ConsoleLog_MultiArg_DeferredAwaitBetweenArgs(ExecutionMode mode)
    {
        Assert.Equal("A B C\n", Run("""console.log("A", await defer("B", 5), "C");""", mode));
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void ArrayLiteral_DeferredAwaitBetweenElements(ExecutionMode mode)
    {
        Assert.Equal("[x, 9, y]\n", Run("""console.log(["x", await defer(9, 5), "y"]);""", mode));
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void ObjectLiteral_DeferredAwaitAfterEarlierProperty(ExecutionMode mode)
    {
        Assert.Equal("""{"a":"x","b":10}""" + "\n", Run(
            """console.log(JSON.stringify({ a: "x", b: await defer(10, 5) }));""", mode));
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void ComputedKeyObjectLiteral_DeferredAwait(ExecutionMode mode)
    {
        Assert.Equal("""{"dyn":12,"other":99}""" + "\n", Run(
            """
            const k: any = "dyn";
            console.log(JSON.stringify({ [k]: await defer(12, 5), other: 99 }));
            """, mode));
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void IndexSet_DeferredAwaitInIndexAndValue(ExecutionMode mode)
    {
        Assert.Equal("8\nZ\n", Run(
            """
            const arr: any = [1, 2, 3];
            arr[0] = await defer(8, 5);
            console.log(arr[0]);
            arr[await defer(0, 5)] = "Z";
            console.log(arr[0]);
            """, mode));
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void CompoundAssign_DeferredAwaitRhs(ExecutionMode mode)
    {
        Assert.Equal("17\n17\n", Run(
            """
            const c = new C();
            c.x = 10;
            c.x += await defer(7, 5);
            console.log(c.x);
            const arr: any = [10];
            arr[0] += await defer(7, 5);
            console.log(arr[0]);
            """, mode));
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void LogicalAssign_DeferredAwaitRhs(ExecutionMode mode)
    {
        Assert.Equal("11\n11\n", Run(
            """
            const c = new C();
            c.x = null;
            c.x ??= await defer(11, 5);
            console.log(c.x);
            const arr: any = [0];
            arr[0] ||= await defer(11, 5);
            console.log(arr[0]);
            """, mode));
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void PropertySet_DeferredAwaitValue(ExecutionMode mode)
    {
        Assert.Equal("5\n", Run(
            """
            const o: any = {};
            o.field = await defer(5, 5);
            console.log(o.field);
            """, mode));
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void DeferredThenableSpecies_PrefixSurvives(ExecutionMode mode)
    {
        // The original issue repro: a Promise subclass whose @@species is a general
        // (non-Promise) thenable that settles its callback from a later turn (#349/#390).
        var source = """
            class Thenable {
              v: any = undefined;
              settled = false;
              cb: any = undefined;
              constructor(executor: (res: any, rej: any) => void) {
                executor(
                  (x: any) => { this.v = x; this.settled = true; if (this.cb) this.cb(x); },
                  (_e: any) => {});
              }
              then(onF: any) { if (this.settled) onF(this.v); else this.cb = onF; }
            }
            class P extends Promise<number> {
              static get [Symbol.species]() { return Thenable as any; }
            }
            async function main() {
              const r: any = P.resolve(5).then((x: number) => x + 1);
              console.log("concat: " + (await r));
            }
            main();
            """;
        Assert.Equal("concat: 6\n", TestHarness.Run(source, mode));
    }
}
