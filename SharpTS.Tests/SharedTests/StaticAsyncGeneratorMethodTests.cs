using SharpTS.Tests.Infrastructure;
using Xunit;

namespace SharpTS.Tests.SharedTests;

/// <summary>
/// Tests for static async generator methods — `class C { static async *gen() { yield … } }` (#761),
/// the async-generator analog of the static sync generator (#692). The instance form already compiled;
/// this pins the static form, whose async-generator state machine is set up like a free function (no
/// `this`). Runs in both back ends.
/// </summary>
public class StaticAsyncGeneratorMethodTests
{
    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void StaticAsyncGenerator_SingleYields_Works(ExecutionMode mode)
    {
        var source = """
            class C { static async *gen() { yield 1; yield 2; } }
            async function main() { for await (const x of C.gen()) console.log(x); }
            main();
            """;

        Assert.Equal("1\n2\n", TestHarness.Run(source, mode));
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void StaticAsyncGenerator_WithParameterAndAwait_Works(ExecutionMode mode)
    {
        var source = """
            class C {
              static async *range(n: number) {
                for (let i = 0; i < n; i++) { await Promise.resolve(); yield i * 10; }
              }
            }
            async function main() { for await (const x of C.range(3)) console.log(x); }
            main();
            """;

        Assert.Equal("0\n10\n20\n", TestHarness.Run(source, mode));
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void StaticAsyncGenerator_ReadsStaticField_Works(ExecutionMode mode)
    {
        var source = """
            class C {
              static count = 3;
              static async *fromField() { for (let i = 0; i < C.count; i++) yield i; }
            }
            async function main() { for await (const x of C.fromField()) console.log(x); }
            main();
            """;

        Assert.Equal("0\n1\n2\n", TestHarness.Run(source, mode));
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void StaticAsyncGenerator_ExplicitReturnType_Works(ExecutionMode mode)
    {
        // An explicit AsyncGenerator<T> return type doesn't change the lowering path.
        var source = """
            class C { static async *gen(): AsyncGenerator<number> { yield 5; yield 6; } }
            async function main() { for await (const x of C.gen()) console.log(x); }
            main();
            """;

        Assert.Equal("5\n6\n", TestHarness.Run(source, mode));
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void InstanceAsyncGenerator_StillWorks(ExecutionMode mode)
    {
        // Regression guard: the instance form (the path #761 did not touch) keeps working.
        var source = """
            class C { async *gen() { yield 8; } }
            async function main() { for await (const x of new C().gen()) console.log(x); }
            main();
            """;

        Assert.Equal("8\n", TestHarness.Run(source, mode));
    }
}
