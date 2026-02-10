using SharpTS.Tests.Infrastructure;
using Xunit;

namespace SharpTS.Tests.SharedTests;

/// <summary>
/// Tests for inner function declarations (function inside function).
/// Verifies hoisting, closure capture, recursion, and nesting.
/// </summary>
public class InnerFunctionTests
{
    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void InnerFunction_Basic(ExecutionMode mode)
    {
        var source = """
            function outer(): void {
                function inner(): string {
                    return "hello";
                }
                console.log(inner());
            }
            outer();
            """;

        var output = TestHarness.Run(source, mode);
        Assert.Equal("hello\n", output);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void InnerFunction_WithParameters(ExecutionMode mode)
    {
        var source = """
            function outer(): void {
                function add(a: number, b: number): number {
                    return a + b;
                }
                console.log(add(3, 4));
            }
            outer();
            """;

        var output = TestHarness.Run(source, mode);
        Assert.Equal("7\n", output);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void InnerFunction_Recursive(ExecutionMode mode)
    {
        var source = """
            function outer(): void {
                function factorial(n: number): number {
                    if (n <= 1) return 1;
                    return n * factorial(n - 1);
                }
                console.log(factorial(5));
            }
            outer();
            """;

        var output = TestHarness.Run(source, mode);
        Assert.Equal("120\n", output);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void InnerFunction_CapturingOuterVariable(ExecutionMode mode)
    {
        var source = """
            function outer(): void {
                let x: number = 10;
                function inner(): number {
                    return x + 5;
                }
                console.log(inner());
            }
            outer();
            """;

        var output = TestHarness.Run(source, mode);
        Assert.Equal("15\n", output);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void InnerFunction_ModifyingCapturedVariable(ExecutionMode mode)
    {
        var source = """
            function outer(): void {
                let count: number = 0;
                function increment(): void {
                    count = count + 1;
                }
                increment();
                increment();
                increment();
                console.log(count);
            }
            outer();
            """;

        var output = TestHarness.Run(source, mode);
        Assert.Equal("3\n", output);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void InnerFunction_MultipleInnerFunctions(ExecutionMode mode)
    {
        var source = """
            function outer(): void {
                function greet(name: string): string {
                    return "Hello, " + name;
                }
                function farewell(name: string): string {
                    return "Goodbye, " + name;
                }
                console.log(greet("Alice"));
                console.log(farewell("Bob"));
            }
            outer();
            """;

        var output = TestHarness.Run(source, mode);
        Assert.Equal("Hello, Alice\nGoodbye, Bob\n", output);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void InnerFunction_DeclaredBeforeUse(ExecutionMode mode)
    {
        // Inner function declared before first call (standard order)
        var source = """
            function outer(): void {
                function inner(): string {
                    return "declared first";
                }
                console.log(inner());
            }
            outer();
            """;

        var output = TestHarness.Run(source, mode);
        Assert.Equal("declared first\n", output);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void InnerFunction_CapturingParameter(ExecutionMode mode)
    {
        var source = """
            function outer(x: number): void {
                function inner(): number {
                    return x * 2;
                }
                console.log(inner());
            }
            outer(21);
            """;

        var output = TestHarness.Run(source, mode);
        Assert.Equal("42\n", output);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void InnerFunction_ReturnedAsValue(ExecutionMode mode)
    {
        // Inner function used as a closure factory
        var source = """
            function makeCounter(): any {
                let count: number = 0;
                function increment(): number {
                    count = count + 1;
                    return count;
                }
                return increment;
            }
            const counter = makeCounter();
            console.log(counter());
            console.log(counter());
            console.log(counter());
            """;

        var output = TestHarness.Run(source, mode);
        Assert.Equal("1\n2\n3\n", output);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void InnerFunction_SimpleRecursion(ExecutionMode mode)
    {
        // Simplest possible recursive inner function - no typed params, string result
        var source = """
            function outer(): void {
                function countdown(n): string {
                    if (n <= 0) return "done";
                    return n + "," + countdown(n - 1);
                }
                console.log(countdown(3));
            }
            outer();
            """;

        var output = TestHarness.Run(source, mode);
        Assert.Equal("3,2,1,done\n", output);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void InnerFunction_SingleSelfCall(ExecutionMode mode)
    {
        // Just one self-call to isolate the most basic recursive behavior
        var source = """
            function outer(): void {
                function selfCall(n): string {
                    if (n <= 0) return "base";
                    return selfCall(0);
                }
                console.log(selfCall(1));
            }
            outer();
            """;

        var output = TestHarness.Run(source, mode);
        Assert.Equal("base\n", output);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void InnerFunction_SelfRefCheck(ExecutionMode mode)
    {
        // Check that the self-reference local is accessible as a function
        var source = """
            function outer(): void {
                function selfCall(): string {
                    let f = selfCall;
                    return "ok";
                }
                console.log(selfCall());
            }
            outer();
            """;

        var output = TestHarness.Run(source, mode);
        Assert.Equal("ok\n", output);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void InnerFunction_NestedInnerFunctions(ExecutionMode mode)
    {
        // Function inside function inside function
        var source = """
            function outer(): void {
                function middle(): string {
                    function inner(): string {
                        return "deep";
                    }
                    return inner();
                }
                console.log(middle());
            }
            outer();
            """;

        var output = TestHarness.Run(source, mode);
        Assert.Equal("deep\n", output);
    }
}
