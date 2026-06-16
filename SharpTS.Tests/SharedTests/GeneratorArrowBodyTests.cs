using SharpTS.Diagnostics.Exceptions;
using SharpTS.Tests.Infrastructure;
using Xunit;

namespace SharpTS.Tests.SharedTests;

/// <summary>
/// Arrow / function-expression callbacks written INSIDE a generator (<c>function*</c>) body.
/// The compiled path previously failed to collect arrows nested in a yielded expression (so an
/// array-method callback compiled to a null "not callable" value) and emitted capturing arrows
/// with a null display-class target ("Non-static method requires a target"); a <c>this</c> used
/// only inside such an arrow left the generator's receiver field undefined (NRE). See #435 / #669.
/// The interpreter has always been correct.
/// </summary>
public class GeneratorArrowBodyTests
{
    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void Generator_NonCapturingArrowCallbackInsideYield(ExecutionMode mode)
    {
        // #435: the arrow lives inside the yielded expression, so it was never collected →
        // compiled `map` callback was null ("Array.prototype.map callback is not callable").
        var source = """
            function* g(): Generator<string> {
              const a = [1, 2, 3];
              yield "m=" + a.map(n => n * 2).join(",");
            }
            let s = ""; for (const v of g()) s += v;
            console.log(s);
            """;

        Assert.Equal("m=2,4,6\n", TestHarness.Run(source, mode));
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void Generator_CapturingClosureReadsLoopVariable(ExecutionMode mode)
    {
        // #669: per-iteration `for (let k …)` bindings captured by a closure created inside the
        // generator body. Each closure must observe its own iteration's value (0,1,2).
        var source = """
            function* gen() {
              const fns: any[] = [];
              for (let k = 0; k < 3; k++) { fns.push(() => k); }
              let out = "";
              for (let i = 0; i < fns.length; i++) { out += fns[i](); }
              yield out;
            }
            console.log(gen().next().value);
            """;

        Assert.Equal("012\n", TestHarness.Run(source, mode));
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void Generator_ArrowReadsCapturedLocal(ExecutionMode mode)
    {
        var source = """
            function* g() {
              const base = 10;
              yield [1, 2, 3].map(x => x + base).join(",");
            }
            console.log(g().next().value);
            """;

        Assert.Equal("11,12,13\n", TestHarness.Run(source, mode));
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void Generator_ArrowReadsCapturedParameter(ExecutionMode mode)
    {
        var source = """
            function* g(off: number) {
              yield [1, 2, 3].map(x => x + off).join(",");
            }
            console.log(g(100).next().value);
            """;

        Assert.Equal("101,102,103\n", TestHarness.Run(source, mode));
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void Generator_InstanceMethodArrowCapturesThis(ExecutionMode mode)
    {
        // #435/#669: `this` is referenced only inside the arrow, so the generator analyzer must
        // still materialize the receiver (<>4__this) for the arrow's capture to be non-null.
        var source = """
            class C {
              v = 7;
              *gen() { yield [1, 2, 3].map(x => x + this.v).join(","); }
            }
            console.log(new C().gen().next().value);
            """;

        Assert.Equal("8,9,10\n", TestHarness.Run(source, mode));
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void Generator_ForEachMutatesCapturedObjectNotBinding(ExecutionMode mode)
    {
        // A callback that mutates the captured array OBJECT (push) — not the binding — is fine:
        // the reference is shared, only the binding-write case (#674) is unsupported.
        var source = """
            function* g() {
              const acc: number[] = [];
              [1, 2, 3].forEach(x => acc.push(x * 2));
              yield acc.join(",");
            }
            console.log(g().next().value);
            """;

        Assert.Equal("2,4,6\n", TestHarness.Run(source, mode));
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void Generator_ForEachWritesCapturedBinding(ExecutionMode mode)
    {
        // #674: an arrow that WRITES a captured generator local. The mutation must reach the
        // generator's own storage — a shared function-level display class threaded through the
        // generator state machine in compiled mode (the interpreter has always been correct).
        var source = """
            function* g() {
              const a = [1, 2, 3]; let s = "";
              a.forEach(n => s += n);
              yield s;
            }
            console.log(g().next().value);
            """;

        Assert.Equal("123\n", TestHarness.Run(source, mode));
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void Generator_CallbackAccumulatesIntoCapturedNumber(ExecutionMode mode)
    {
        // #674, numeric accumulator — the canonical `reduce`-by-side-effect shape.
        var source = """
            function* g() {
              let sum = 0;
              [1, 2, 3].forEach(n => sum += n);
              yield sum;
            }
            console.log(g().next().value);
            """;

        Assert.Equal("6\n", TestHarness.Run(source, mode));
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void Generator_MultipleMutatedCaptures(ExecutionMode mode)
    {
        // #674: two distinct captured locals mutated by one callback — each gets its own DC field.
        var source = """
            function* g() {
              let sum = 0; let product = 1;
              [1, 2, 3, 4].forEach(n => { sum += n; product *= n; });
              yield `${sum},${product}`;
            }
            console.log(g().next().value);
            """;

        Assert.Equal("10,24\n", TestHarness.Run(source, mode));
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void Generator_CallbackMutatesCapturedParameter(ExecutionMode mode)
    {
        // #674: the mutated capture is a generator PARAMETER — the stub seeds it into the DC.
        var source = """
            function* g(acc: number) {
              [1, 2, 3].forEach(n => acc += n);
              yield acc;
            }
            console.log(g(100).next().value);
            """;

        Assert.Equal("106\n", TestHarness.Run(source, mode));
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void Generator_NestedCallbackWritesCaptureThroughToGenerator(ExecutionMode mode)
    {
        // #674 (the case the compile-time guard could not see): a NESTED arrow writes a variable
        // captured all the way through to the generator scope. The DC threads through both arrows.
        var source = """
            function* g() {
              let sum = 0;
              [[1, 2], [3, 4]].forEach(row => row.forEach(m => sum += m));
              yield sum;
            }
            console.log(g().next().value);
            """;

        Assert.Equal("10\n", TestHarness.Run(source, mode));
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void Generator_MutatedAndReadOnlyCapturesMixed(ExecutionMode mode)
    {
        // #674: a read-only capture (`base`, by-value snapshot) and a mutated capture (`total`,
        // function DC) coexist in the same callback.
        var source = """
            function* g() {
              const base = 10; let total = 0;
              [1, 2, 3].forEach(n => total += n + base);
              yield total;
            }
            console.log(g().next().value);
            """;

        Assert.Equal("36\n", TestHarness.Run(source, mode));
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void Generator_CapturedWriteSurvivesAcrossYield(ExecutionMode mode)
    {
        // #674: the mutated capture is also live across a yield. The DC lives on a state-machine
        // field, so it persists across the suspension just like a hoisted local.
        var source = """
            function* g() {
              let s = 0;
              yield "before:" + s;
              [1, 2, 3].forEach(n => s += n);
              yield "after:" + s;
            }
            const it = g();
            let out = it.next().value + "|" + it.next().value;
            console.log(out);
            """;

        Assert.Equal("before:0|after:6\n", TestHarness.Run(source, mode));
    }

    [Fact]
    public void Generator_InstanceMethodWritesCapturedBinding_CompiledRejectsClearly()
    {
        // An INSTANCE generator method's state machine is not yet wired with a function display
        // class (#674 covers free `function*` declarations), so a write-to-captured-binding inside
        // one must still FAIL FAST with a clear message rather than silently dropping the write.
        // The interpreter handles it correctly.
        var source = """
            class C {
              *gen() {
                let s = 0;
                [1, 2, 3].forEach(n => s += n);
                yield s;
              }
            }
            console.log(new C().gen().next().value);
            """;

        Assert.Equal("6\n", TestHarness.RunInterpreted(source));
        var ex = Assert.Throws<CompileException>(() => TestHarness.RunCompiled(source));
        Assert.Contains("captured from the generator scope", ex.Message);
    }

    [Fact]
    public void AsyncGenerator_WritesCapturedBinding_CompiledRejectsClearly()
    {
        // The async-generator state machine has no function display class wired yet (#674 lifts the
        // sync free-function generator case). Previously this SILENTLY dropped the write (compiled
        // yielded 0 where the interpreter yields 6); it must now FAIL FAST with a clear message
        // instead of miscompiling.
        var source = """
            async function* g() {
              let sum = 0;
              [1, 2, 3].forEach(n => sum += n);
              yield sum;
            }
            (async () => { for await (const v of g()) console.log(v); })();
            """;

        Assert.Equal("6\n", TestHarness.RunInterpreted(source));
        var ex = Assert.Throws<CompileException>(() => TestHarness.RunCompiled(source));
        Assert.Contains("captured from the generator scope", ex.Message);
    }
}
