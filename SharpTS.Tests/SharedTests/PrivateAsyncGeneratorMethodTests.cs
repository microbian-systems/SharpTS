using SharpTS.Diagnostics.Exceptions;
using SharpTS.Parsing;
using SharpTS.Tests.Infrastructure;
using Xunit;

namespace SharpTS.Tests.SharedTests;

/// <summary>
/// Regression tests for #720: in compiled mode an <c>async</c> or generator ES2022 private method
/// (<c>async #m</c>, <c>*#m</c>, <c>async *#m</c>) was emitted linearly into <c>__private_&lt;name&gt;</c>
/// regardless of its kind — leaving a bare <c>object</c> on the stack for a method declared to return
/// <c>Task&lt;object&gt;</c> (invalid IL: StackUnexpected) and rejecting <c>yield</c> ("Yield not
/// supported"). The interpreter already handled both. The fix routes a private method's body through the
/// same state-machine path as its public counterpart (<c>EmitAsyncMethodBody</c> /
/// <c>EmitGeneratorMethodBody</c> / <c>EmitAsyncGeneratorMethodBody</c>, generalized for the private
/// builder + qualified class name + static), with the parameter-default prologue moving into the state
/// machine entry. The cross-mode theories double as a parity guard.
/// </summary>
public class PrivateAsyncGeneratorMethodTests
{
    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void AsyncPrivateMethod_Instance(ExecutionMode mode)
    {
        // The exact #720 async repro.
        var source = """
            class Q {
              async #p(x: number): Promise<number> { return x; }
              go(): Promise<number> { return this.#p(5); }
            }
            new Q().go().then(v => console.log(v));
            """;

        Assert.Equal("5\n", TestHarness.Run(source, mode));
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void GeneratorPrivateMethod_Instance(ExecutionMode mode)
    {
        // The exact #720 generator repro.
        var source = """
            class G {
              *#p(x: number): Generator<number> { yield x; yield x + 1; }
              go(): number { let s = 0; for (const v of this.#p(5)) s += v; return s; }
            }
            console.log(new G().go());
            """;

        Assert.Equal("11\n", TestHarness.Run(source, mode));
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void AsyncGeneratorPrivateMethod_Instance(ExecutionMode mode)
    {
        var source = """
            class A {
              async *#p(x: number) { yield x; yield x + 1; }
              async go(): Promise<number> { let s = 0; for await (const v of this.#p(5)) s += v; return s; }
            }
            new A().go().then(v => console.log(v));
            """;

        Assert.Equal("11\n", TestHarness.Run(source, mode));
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void StaticAsyncPrivateMethod(ExecutionMode mode)
    {
        var source = """
            class Q {
              static async #p(x: number): Promise<number> { return x * 2; }
              static go(): Promise<number> { return Q.#p(5); }
            }
            Q.go().then(v => console.log(v));
            """;

        Assert.Equal("10\n", TestHarness.Run(source, mode));
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void AsyncPrivateMethod_AwaitsAndReachesOtherPrivateMembers(ExecutionMode mode)
    {
        // The async private method awaits, reads a private field, and calls another private method —
        // confirming the state-machine body keeps full private-member access.
        var source = """
            class C {
              #base = 100;
              async #compute(x: number): Promise<number> {
                const d = await Promise.resolve(x);
                return d + this.#base + this.#double(x);
              }
              #double(x: number): number { return x * 2; }
              async run(): Promise<number> { return await this.#compute(5); }
            }
            new C().run().then(v => console.log(v));
            """;

        Assert.Equal("115\n", TestHarness.Run(source, mode));  // 5 + 100 + 10
    }

    [Fact]
    public void PrivateAsyncAndGeneratorMethods_ProduceValidIL()
    {
        // Pin IL validity directly (the runtime JIT is lenient and would run invalid IL anyway).
        var source = """
            class C {
              #base = 1;
              async #a(x: number): Promise<number> { return x + this.#base; }
              *#g(x: number): Generator<number> { yield x; yield x + this.#base; }
              async *#ag(x: number) { yield x; yield x + this.#base; }
              async run(): Promise<number> {
                let total = await this.#a(10);
                for (const v of this.#g(20)) total += v;
                for await (const v of this.#ag(30)) total += v;
                return total;
              }
            }
            new C().run().then(v => console.log(v));
            """;

        var errors = TestHarness.CompileAndVerifyOnly(source);
        Assert.Empty(errors);
    }

    [Fact]
    public void StaticPrivateGenerator_ReportsCleanError_NotInvalidIL()
    {
        // Static generators are unsupported project-wide (the public static form fails the same way,
        // tracked by #762). A static private generator must report that clean compile error rather than
        // emitting invalid IL — i.e. the fix routes only the supported (instance) generator case.
        var source = """
            class G {
              static *#p(): Generator<number> { yield 1; yield 2; }
              static go(): number { let s = 0; for (const v of G.#p()) s += v; return s; }
            }
            console.log(G.go());
            """;

        Assert.Throws<CompileException>(() => TestHarness.CompileAndVerifyOnly(source, DecoratorMode.None));
    }
}
