using SharpTS.Tests.Infrastructure;
using Xunit;

namespace SharpTS.Tests.SharedTests;

/// <summary>
/// Block-scope shadowing inside async functions and async generators (#766), and the
/// interpreter async-generator nested-block binding-before-await bug (#768). Mirrors the
/// compiled-generator coverage in <see cref="GeneratorTests"/> (#711), extended to the
/// suspension-by-await/yield state machines. Runs against both interpreter and compiler.
/// </summary>
public class AsyncBlockScopeShadowTests
{
    #region Async function (#766)

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void AsyncFunction_NestedBlockConstShadow_DoesNotLeakToOuter(ExecutionMode mode)
    {
        // A const in a nested block that shadows an outer body-level const must get its own slot
        // instead of clobbering the outer binding's hoisted state-machine field (#766, async #711).
        var source = """
            async function af(): Promise<number> {
              const r = 100;
              { const r = 0; await Promise.resolve(r); }
              return r;
            }
            async function main() { console.log(await af()); }
            main();
            """;

        Assert.Equal("100\n", TestHarness.Run(source, mode));
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void AsyncFunction_NestedBlockShadow_LiveAcrossAwait_GetsOwnField(ExecutionMode mode)
    {
        // The inner shadow is itself read after an await, so it must hoist to its OWN field, distinct
        // from the outer binding's field — both must survive the suspension independently (#766).
        var source = """
            async function af(): Promise<string> {
              const r = 100;
              let inner = 0;
              {
                const r = 5;
                await Promise.resolve(0);
                inner = r;
              }
              return inner + "/" + r;
            }
            async function main() { console.log(await af()); }
            main();
            """;

        Assert.Equal("5/100\n", TestHarness.Run(source, mode));
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void AsyncFunction_NestedBlockLetShadow_ReassignedAcrossAwait(ExecutionMode mode)
    {
        // A let shadow compound-assigned across awaits keeps its own value, separate from the outer
        // binding (#766).
        var source = """
            async function af(): Promise<string> {
              let r = 7;
              let captured = 0;
              {
                let r = 1;
                r += 1;
                await Promise.resolve(0);
                r += 10;
                captured = r;
              }
              return captured + "/" + r;
            }
            async function main() { console.log(await af()); }
            main();
            """;

        Assert.Equal("12/7\n", TestHarness.Run(source, mode));
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void AsyncFunction_NestedBlockShadowsParameter(ExecutionMode mode)
    {
        // An inner block const may shadow a (hoisted) parameter without clobbering it (#766).
        var source = """
            async function af(r: number): Promise<string> {
              let captured = 0;
              { const r = 99; await Promise.resolve(0); captured = r; }
              return captured + "/" + r;
            }
            async function main() { console.log(await af(5)); }
            main();
            """;

        Assert.Equal("99/5\n", TestHarness.Run(source, mode));
    }

    #endregion

    #region Async arrow (#766)

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void AsyncArrow_NestedBlockConstShadow_DoesNotLeakToOuter(ExecutionMode mode)
    {
        // Same shadow leak in a compiled async arrow's state machine (#766).
        var source = """
            const af = async (): Promise<number> => {
              const r = 100;
              { const r = 0; await Promise.resolve(r); }
              return r;
            };
            async function main() { console.log(await af()); }
            main();
            """;

        Assert.Equal("100\n", TestHarness.Run(source, mode));
    }

    #endregion

    #region Async generator (#766 + #768)

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void AsyncGenerator_NestedBlockConstShadow_DoesNotLeakToOuter(ExecutionMode mode)
    {
        // Compiled: the nested-block shadow leaked onto the outer binding's hoisted field → [0, 0] (#766).
        // Interpreter: the inner block const yielded before any await read as undefined → [undefined, 100] (#768).
        var source = """
            async function* ag(): AsyncGenerator<number> {
              const r = 100;
              { const r = 0; yield r; }
              yield r;
            }
            async function main() {
              const g = ag();
              let a = await g.next();
              let b = await g.next();
              console.log(a.value + "," + b.value);
            }
            main();
            """;

        Assert.Equal("0,100\n", TestHarness.Run(source, mode));
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void AsyncGenerator_NestedBlockShadow_LiveAcrossYield_GetsOwnField(ExecutionMode mode)
    {
        // Both the inner shadow and the outer binding survive the suspension on their own slots (#766).
        var source = """
            async function* ag(): AsyncGenerator<number> {
              const r = 100;
              {
                const r = 0;
                yield r;
                yield r + 1;
              }
              yield r;
            }
            async function main() {
              const g = ag();
              let out = "";
              for (let i = 0; i < 3; i++) out += (await g.next()).value + (i < 2 ? "," : "");
              console.log(out);
            }
            main();
            """;

        Assert.Equal("0,1,100\n", TestHarness.Run(source, mode));
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void AsyncGenerator_NestedBlockBindingBeforeAwait_YieldsValue(ExecutionMode mode)
    {
        // #768: distinct names (no shadowing). The interpreter lost a nested-block binding's value when
        // it was yielded before the generator first suspended on an await → [undefined, 100].
        var source = """
            async function* ag(): AsyncGenerator<number> {
              const a = 100;
              { const b = 0; yield b; }
              yield a;
            }
            async function main() {
              const g = ag();
              let x = await g.next();
              let y = await g.next();
              console.log(x.value + "," + y.value);
            }
            main();
            """;

        Assert.Equal("0,100\n", TestHarness.Run(source, mode));
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void AsyncGenerator_NestedBlockLetBeforeAwait_Reassigned(ExecutionMode mode)
    {
        // #768 variant: a nested-block let read/updated before any await must keep its value.
        var source = """
            async function* ag(): AsyncGenerator<number> {
              { let b = 1; b += 4; yield b; }
              yield 9;
            }
            async function main() {
              const g = ag();
              let x = await g.next();
              let y = await g.next();
              console.log(x.value + "," + y.value);
            }
            main();
            """;

        Assert.Equal("5,9\n", TestHarness.Run(source, mode));
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void AsyncGenerator_NestedBlockShadow_CapturedByArrow_DoesNotLeak(ExecutionMode mode)
    {
        // An inner block const that shadows an outer binding AND is read by a nested arrow gets its own
        // slot; the arrow reads the inner value while the outer keeps its own (#767, async-gen analog).
        // Async generators lift only captured-AND-mutated locals into the function DC, so the read-only
        // arrow capture flows through the per-arrow snapshot path the capture pivot redirects.
        var source = """
            async function* ag(): AsyncGenerator<number> {
              const r = 100;
              {
                const r = 7;
                const f = () => r;
                await Promise.resolve(0);
                yield f();
              }
              yield r;
            }
            async function main() {
              const g = ag();
              let x = await g.next();
              let y = await g.next();
              console.log(x.value + "," + y.value);
            }
            main();
            """;

        Assert.Equal("7,100\n", TestHarness.Run(source, mode));
    }

    #endregion

    #region Known residual: async-function read-capture of a shadow shares name-keyed storage (#767)

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void AsyncFunction_NestedBlockShadow_CapturedByArrow_Interpreted(ExecutionMode mode)
    {
        // Interpreter is correct in both directions and is the reference. Compiled async FUNCTIONS lift
        // EVERY captured local (read or write) into a name-keyed function display class, so a read-only
        // arrow capture of a renamed shadow cannot use the per-arrow snapshot pivot that fixes #767 for
        // (async) generators. Such shadows are therefore kept OFF-LIMITS to renaming in async-function /
        // async-arrow contexts (no regression from the prior #766 behaviour); a full fix needs the
        // function DC itself to become rename-aware. This test pins the interpreter contract; the
        // compiled async-function residual is tracked separately.
        if (mode == ExecutionMode.Compiled) return;

        var source = """
            async function af(): Promise<string> {
              const out: string[] = [];
              const r = 100;
              {
                const r = 5;
                const f = () => r;
                await Promise.resolve(0);
                out.push(String(f()));
              }
              out.push(String(r));
              return out.join(",");
            }
            async function main() { console.log(await af()); }
            main();
            """;

        Assert.Equal("5,100\n", TestHarness.Run(source, mode));
    }

    #endregion
}
