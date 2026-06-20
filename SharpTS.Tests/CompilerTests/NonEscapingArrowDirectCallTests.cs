using SharpTS.Tests.Infrastructure;
using Xunit;

namespace SharpTS.Tests.CompilerTests;

/// <summary>
/// #858: a non-escaping <c>const NAME = (args) =&gt; …</c> local arrow invoked only by name is compiled
/// to a direct <c>callvirt Invoke</c> on the bare display-class instance (no per-call <c>$TSFunction</c>
/// wrapper / reflective <c>InvokeMethodValue</c> / arg boxing). These tests pin interpreter/compiler
/// parity across the optimized shape AND every escape case that must keep the generic wrapper path:
/// the arrow passed as an argument, returned, stored, recursive (captured), reassigned, or referenced
/// in a non-call position. Correctness — not the codegen shape — is what is asserted; the optimization
/// is transparent, so both modes must agree.
/// </summary>
public class NonEscapingArrowDirectCallTests
{
    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void CapturingArrow_InvokedByName_IsCorrect(ExecutionMode mode)
    {
        // The canonical #858 shape: arrow captures the loop var and is called directly each iteration.
        var source = """
            function work(n: number): number {
                let sum = 0;
                for (let i = 0; i < n; i++) {
                    const addCap = (a: number): number => a + i;
                    sum = sum + addCap(i);
                }
                return sum;
            }
            console.log(work(1000));
            """;
        Assert.Equal("999000\n", TestHarness.Run(source, mode));
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void CapturingArrow_MultipleTypedArgs_IsCorrect(ExecutionMode mode)
    {
        var source = """
            function work(n: number): number {
                let s = 0;
                for (let i = 0; i < n; i++) {
                    const mulCap = (a: number, b: number): number => a * b + i;
                    s = s + mulCap(2, 3);
                }
                return s;
            }
            console.log(work(1000));
            """;
        Assert.Equal("505500\n", TestHarness.Run(source, mode));
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void BooleanReturningArrow_InvokedByName_IsCorrect(ExecutionMode mode)
    {
        var source = """
            function count(n: number): number {
                let c = 0;
                for (let i = 0; i < n; i++) {
                    const isEvenCap = (a: number): boolean => (a + i) % 2 === 0;
                    if (isEvenCap(i)) { c = c + 1; }
                }
                return c;
            }
            console.log(count(10));
            """;
        // i + i is always even -> all 10 iterations counted.
        Assert.Equal("10\n", TestHarness.Run(source, mode));
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void EscapingArrow_PassedAsArgument_KeepsWrapperSemantics(ExecutionMode mode)
    {
        var source = """
            function apply(f: (x: number) => number, v: number): number { return f(v); }
            function run(): number {
                const dbl = (a: number): number => a * 2;
                return apply(dbl, 21);
            }
            console.log(run());
            """;
        Assert.Equal("42\n", TestHarness.Run(source, mode));
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void EscapingArrow_Returned_KeepsWrapperSemantics(ExecutionMode mode)
    {
        var source = """
            function makeAdder(i: number): (a: number) => number {
                const adder = (a: number): number => a + i;
                return adder;
            }
            console.log(makeAdder(10)(5));
            """;
        Assert.Equal("15\n", TestHarness.Run(source, mode));
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void EscapingArrow_StoredInArray_KeepsWrapperSemantics(ExecutionMode mode)
    {
        var source = """
            function run(): number {
                const stored = (a: number): number => a + 1;
                const arr = [stored];
                return arr[0](9);
            }
            console.log(run());
            """;
        Assert.Equal("10\n", TestHarness.Run(source, mode));
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void RecursiveArrow_ViaName_KeepsWrapperSemantics(ExecutionMode mode)
    {
        // The arrow references itself by name from within its own (nested) body, so the name is
        // captured -> excluded from the optimization, but must still compute correctly.
        var source = """
            function run(): number {
                const fact = (k: number): number => k <= 1 ? 1 : k * fact(k - 1);
                return fact(5);
            }
            console.log(run());
            """;
        Assert.Equal("120\n", TestHarness.Run(source, mode));
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void ReassignedBinding_IsDisqualified_AndCorrect(ExecutionMode mode)
    {
        var source = """
            function run(): number {
                let g = (a: number): number => a + 1;
                g = (a: number): number => a + 100;
                return g(7);
            }
            console.log(run());
            """;
        Assert.Equal("107\n", TestHarness.Run(source, mode));
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void SameNameInTwoScopes_IsDisqualified_AndCorrect(ExecutionMode mode)
    {
        // `dup` is declared in two functions; the whole-program single-declaration guard disqualifies
        // it, so both must fall back to the wrapper path and still compute correctly in both modes.
        var source = """
            function a(): number {
                const dup = (x: number): number => x + 1;
                return dup(1);
            }
            function b(): number {
                const dup = (x: number): number => x + 2;
                return dup(1);
            }
            console.log(a() + "," + b());
            """;
        Assert.Equal("2,3\n", TestHarness.Run(source, mode));
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void NonCapturingArrow_InvokedByName_IsCorrect(ExecutionMode mode)
    {
        // Non-capturing arrows fall through to the wrapper path (no display class to key on) but
        // must remain correct.
        var source = """
            function run(): number {
                const idPlus = (x: number): number => x + 5;
                return idPlus(37);
            }
            console.log(run());
            """;
        Assert.Equal("42\n", TestHarness.Run(source, mode));
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void DirectCallArrow_CapturesAcrossIterations_FreshBinding(ExecutionMode mode)
    {
        // Per-iteration `let` capture: each arrow must see its own iteration's `i`. A broken
        // direct-call rewrite that hoisted/shared the display instance would compute a wrong sum.
        var source = """
            function run(): number {
                const results: number[] = [];
                for (let i = 0; i < 5; i++) {
                    const grab = (a: number): number => a + i * 10;
                    results.push(grab(1));
                }
                let total = 0;
                for (let j = 0; j < results.length; j++) { total = total + results[j]; }
                return total;
            }
            console.log(run());
            """;
        // (1+0)+(1+10)+(1+20)+(1+30)+(1+40) = 5 + 100 = 105
        Assert.Equal("105\n", TestHarness.Run(source, mode));
    }
}
