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

    [Fact]
    public void Generator_ForEachWritesCapturedBinding_InterpretedIsCorrect()
    {
        // The interpreter handles the write-to-captured-binding case correctly.
        var source = """
            function* g() {
              const a = [1, 2, 3]; let s = "";
              a.forEach(n => s += n);
              yield s;
            }
            console.log(g().next().value);
            """;

        Assert.Equal("123\n", TestHarness.RunInterpreted(source));
    }

    [Fact]
    public void Generator_ForEachWritesCapturedBinding_CompiledRejectsClearly()
    {
        // Compiled mode cannot yet share storage for a mutated capture (would need a function-level
        // display class threaded through the generator state machine, #674). Until then it must
        // FAIL FAST with a clear message rather than silently dropping the write.
        var source = """
            function* g() {
              const a = [1, 2, 3]; let s = "";
              a.forEach(n => s += n);
              yield s;
            }
            console.log(g().next().value);
            """;

        var ex = Assert.Throws<CompileException>(() => TestHarness.RunCompiled(source));
        Assert.Contains("captured from the generator scope", ex.Message);
    }
}
