using SharpTS.Tests.Infrastructure;
using Xunit;

namespace SharpTS.Tests.SharedTests;

/// <summary>
/// Tests for static generator methods — `class C { static *gen() { yield … } }` (#692). The instance
/// form already compiled; this pins the static form, whose state machine is set up like a free
/// function (no `this`). Runs in both back ends.
/// </summary>
public class StaticGeneratorMethodTests
{
    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void StaticGenerator_SingleYield_Works(ExecutionMode mode)
    {
        var source = """
            class C { static *gen() { yield 7; } }
            console.log([...C.gen()][0]);
            """;

        Assert.Equal("7\n", TestHarness.Run(source, mode));
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void StaticGenerator_WithParameter_Works(ExecutionMode mode)
    {
        var source = """
            class C { static *range(n: number) { for (let i = 0; i < n; i++) yield i * 2; } }
            console.log([...C.range(3)].join(","));
            for (const x of C.range(2)) console.log(x);
            """;

        Assert.Equal("0,2,4\n0\n2\n", TestHarness.Run(source, mode));
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void StaticGenerator_ReadsStaticField_Works(ExecutionMode mode)
    {
        var source = """
            class C {
              static count = 3;
              static *fromField() { for (let i = 0; i < C.count; i++) yield i; }
            }
            console.log([...C.fromField()].join(","));
            """;

        Assert.Equal("0,1,2\n", TestHarness.Run(source, mode));
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void StaticGenerator_ExplicitReturnType_Works(ExecutionMode mode)
    {
        // Per #692: an explicit Generator<T> return type doesn't change the lowering path.
        var source = """
            class C { static *gen(): Generator<number> { yield 1; yield 2; yield 3; } }
            console.log([...C.gen()].length);
            """;

        Assert.Equal("3\n", TestHarness.Run(source, mode));
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void InstanceGenerator_StillWorks(ExecutionMode mode)
    {
        // Regression guard: the instance form (the path #692 did not touch) keeps working.
        var source = """
            class C { *gen() { yield 7; } }
            console.log([...new C().gen()][0]);
            """;

        Assert.Equal("7\n", TestHarness.Run(source, mode));
    }
}
