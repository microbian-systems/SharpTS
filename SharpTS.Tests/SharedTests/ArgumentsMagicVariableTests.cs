using SharpTS.Tests.Infrastructure;
using Xunit;

namespace SharpTS.Tests.SharedTests;

/// <summary>
/// Regression for the previously-missing JS <c>arguments</c> array-like binding.
/// In both interpreter and compiled mode, referencing <c>arguments</c> inside a
/// non-arrow function body used to throw <c>Undefined variable 'arguments'</c>.
/// </summary>
/// <remarks>
/// Fix: interpreter binds <c>arguments</c> in <c>SharpTSFunction.Call</c> /
/// <c>CallV2</c>; compiled mode emits a prologue (<c>ILCompiler.Functions
/// .EmitArgumentsLocalPrologue</c>) that builds a <c>List&lt;object&gt;</c>
/// from the declared parameters — spreading rest-param collections so each
/// caller value occupies its own index. Arrow functions deliberately do NOT
/// bind <c>arguments</c>: per JS spec they inherit from the enclosing
/// non-arrow function via normal lexical scope.
/// </remarks>
public class ArgumentsMagicVariableTests
{
    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void Arguments_LengthAndIndexing(ExecutionMode mode)
    {
        var source = @"
            function f(a: number, b: number): void {
                console.log(arguments.length);
                console.log(arguments[0]);
                console.log(arguments[1]);
            }
            f(10, 20);
        ";
        var output = TestHarness.Run(source, mode);
        Assert.Equal("2\n10\n20\n", output);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void Arguments_WithRestParam_SpreadsToDistinctIndices(ExecutionMode mode)
    {
        // Rest params aggregate trailing args into a collection, but the
        // `arguments` array-like must still surface each caller value at its
        // own index.
        var source = @"
            function sum(...nums: number[]): number {
                let total = 0;
                for (let i = 0; i < arguments.length; i++) {
                    total += arguments[i];
                }
                return total;
            }
            console.log(sum());
            console.log(sum(1));
            console.log(sum(1, 2, 3, 4));
        ";
        var output = TestHarness.Run(source, mode);
        Assert.Equal("0\n1\n10\n", output);
    }

    [Fact]
    public void Arguments_InsideArrow_InheritsFromEnclosingFunction_Interpreted()
    {
        // Per JS spec: `arguments` in an arrow is a lexical reference to the
        // enclosing non-arrow function's binding — arrows never introduce
        // their own `arguments`.
        //
        // Interpreter-only for now: the compiled-mode arrow path doesn't yet
        // hoist `arguments` into the parent function's display class, so a
        // nested arrow that reads it hits "Undefined variable 'arguments'".
        // Tracked as a known limitation; real-world arrow bodies that read
        // the enclosing function's `arguments` are extremely rare (arrows
        // exist in part to fix the `this` wrinkle, not to revisit
        // pre-rest-param variadics).
        var source = @"
            function outer(a: number, b: number): number {
                const inner = (): number => {
                    let total = 0;
                    for (let i = 0; i < arguments.length; i++) {
                        total += arguments[i];
                    }
                    return total;
                };
                return inner();
            }
            console.log(outer(7, 35));
        ";
        var output = TestHarness.Run(source, ExecutionMode.Interpreted);
        Assert.Equal("42\n", output);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void Arguments_InMethodOfClass(ExecutionMode mode)
    {
        var source = @"
            class Widget {
                label(prefix: string, suffix: string): string {
                    // arguments[0] is the prefix; [1] the suffix.
                    return arguments[0] + '-' + arguments[1];
                }
            }
            const w = new Widget();
            console.log(w.label('foo', 'bar'));
        ";
        var output = TestHarness.Run(source, mode);
        Assert.Equal("foo-bar\n", output);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void Arguments_IndexedTraversal(ExecutionMode mode)
    {
        // Indexed access through `arguments.length` is the legacy-JS variadic
        // pattern. We use `...rest` so the TypeScript checker is happy with
        // varying call arities; the prologue's rest-param spread means
        // `arguments` exposes each caller value at its own index, not as one
        // nested array at [0].
        var source = @"
            function collect(...items: string[]): string {
                let acc = '';
                for (let i = 0; i < arguments.length; i++) {
                    acc = acc + String(arguments[i]);
                }
                return acc;
            }
            console.log(collect());
            console.log(collect('a'));
            console.log(collect('a', 'b', 'c'));
        ";
        var output = TestHarness.Run(source, mode);
        Assert.Equal("\na\nabc\n", output);
    }
}
