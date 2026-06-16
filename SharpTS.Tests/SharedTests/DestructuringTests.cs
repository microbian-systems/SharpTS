using SharpTS.Tests.Infrastructure;
using Xunit;

namespace SharpTS.Tests.SharedTests;

/// <summary>
/// Tests for destructuring assignment (arrays and objects). Runs against both interpreter and compiler.
/// </summary>
public class DestructuringTests
{
    #region Array Destructuring

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void ArrayDestructuring_BasicAssignment(ExecutionMode mode)
    {
        var source = """
            const arr: number[] = [1, 2, 3];
            const [a, b] = arr;
            console.log(a);
            console.log(b);
            """;

        var output = TestHarness.Run(source, mode);
        Assert.Equal("1\n2\n", output);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void ArrayDestructuring_WithRest(ExecutionMode mode)
    {
        var source = """
            const [head, ...tail] = [1, 2, 3, 4];
            console.log(head);
            console.log(tail.length);
            console.log(tail[0]);
            console.log(tail[1]);
            """;

        var output = TestHarness.Run(source, mode);
        Assert.Equal("1\n3\n2\n3\n", output);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void ArrayDestructuring_WithHoles(ExecutionMode mode)
    {
        var source = """
            const [first, , third] = [1, 2, 3];
            console.log(first);
            console.log(third);
            """;

        var output = TestHarness.Run(source, mode);
        Assert.Equal("1\n3\n", output);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void ArrayDestructuring_TrailingComma(ExecutionMode mode)
    {
        var source = """
            const [t1, t2,] = [100, 200];
            console.log(t1);
            console.log(t2);
            """;

        var output = TestHarness.Run(source, mode);
        Assert.Equal("100\n200\n", output);
    }

    #endregion

    #region Object Destructuring

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void ObjectDestructuring_BasicAssignment(ExecutionMode mode)
    {
        var source = """
            const obj = { name: "Alice", age: 30 };
            const { name, age } = obj;
            console.log(name);
            console.log(age);
            """;

        var output = TestHarness.Run(source, mode);
        Assert.Equal("Alice\n30\n", output);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void ObjectDestructuring_WithRename(ExecutionMode mode)
    {
        var source = """
            const obj = { name: "Bob" };
            const { name: userName } = obj;
            console.log(userName);
            """;

        var output = TestHarness.Run(source, mode);
        Assert.Equal("Bob\n", output);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void ObjectDestructuring_WithDefault(ExecutionMode mode)
    {
        var source = """
            const obj: any = {};
            const { missing: value = "default" } = obj;
            console.log(value);
            """;

        var output = TestHarness.Run(source, mode);
        Assert.Equal("default\n", output);
    }

    #endregion

    #region Iterable (non-indexable) Sources (#685)

    // Array destructuring follows the iterator protocol, so it must work over any
    // iterable — not just index-addressable arrays/strings. Before #685 the parser
    // desugared `[a, b] = src` to positional index access, which mis-evaluated (and
    // the type checker rejected) generators, Set and Map.

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void ArrayDestructuring_OverGenerator(ExecutionMode mode)
    {
        var source = """
            function* g() { yield 10; yield 20; }
            const [a, b] = g();
            console.log(a);
            console.log(b);
            """;

        var output = TestHarness.Run(source, mode);
        Assert.Equal("10\n20\n", output);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void ArrayDestructuring_OverGeneratorWithRest(ExecutionMode mode)
    {
        var source = """
            function* g() { yield 1; yield 2; yield 3; yield 4; }
            const [first, ...rest] = g();
            console.log(first);
            console.log(rest.length);
            console.log(rest[0]);
            console.log(rest[1]);
            console.log(rest[2]);
            """;

        var output = TestHarness.Run(source, mode);
        Assert.Equal("1\n3\n2\n3\n4\n", output);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void ArrayDestructuring_OverSet(ExecutionMode mode)
    {
        var source = """
            const s = new Set<number>([1, 2, 3]);
            const [x, y] = s;
            console.log(x);
            console.log(y);
            """;

        var output = TestHarness.Run(source, mode);
        Assert.Equal("1\n2\n", output);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void ArrayDestructuring_OverSetWithHoleAndDefault(ExecutionMode mode)
    {
        // Set has three values [10, 20, 30]; index 3 is missing → default applies.
        var source = """
            const s = new Set<number>([10, 20, 30]);
            const [, second, , fourth = 99] = s;
            console.log(second);
            console.log(fourth);
            """;

        var output = TestHarness.Run(source, mode);
        Assert.Equal("20\n99\n", output);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void ArrayDestructuring_OverMapEntry(ExecutionMode mode)
    {
        // Iterating a Map yields [key, value] entries.
        var source = """
            const m = new Map<string, number>([["k", 1]]);
            const [first] = m;
            console.log(first[0]);
            console.log(first[1]);
            """;

        var output = TestHarness.Run(source, mode);
        Assert.Equal("k\n1\n", output);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void ArrayDestructuring_NestedOverMapEntries(ExecutionMode mode)
    {
        // Nested pattern destructures each [key, value] entry positionally.
        var source = """
            const m = new Map<string, number>([["a", 1], ["b", 2]]);
            const [[k1, v1], [k2, v2]] = m;
            console.log(k1);
            console.log(v1);
            console.log(k2);
            console.log(v2);
            """;

        var output = TestHarness.Run(source, mode);
        Assert.Equal("a\n1\nb\n2\n", output);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void ArrayDestructuring_OverCustomIterable(ExecutionMode mode)
    {
        var source = """
            const iterable = {
                [Symbol.iterator]() {
                    let i = 0;
                    return {
                        next() {
                            return i < 3
                                ? { value: i++ * 10, done: false }
                                : { value: undefined, done: true };
                        }
                    };
                }
            };
            const [a, b, c] = iterable;
            console.log(a);
            console.log(b);
            console.log(c);
            """;

        var output = TestHarness.Run(source, mode);
        Assert.Equal("0\n10\n20\n", output);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void ArrayDestructuring_GeneratorElementTypeIsUsable(ExecutionMode mode)
    {
        // The destructured bindings carry the iterable's element type (number), so
        // arithmetic type-checks and runs — exercising the type-checker side of #685.
        var source = """
            function* g() { yield 5; yield 7; }
            const [a, b] = g();
            console.log(a + b);
            console.log(a * b);
            """;

        var output = TestHarness.Run(source, mode);
        Assert.Equal("12\n35\n", output);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void ArrayDestructuring_FastPathArrayUnchanged(ExecutionMode mode)
    {
        // Regression guard: the iterator-protocol normalization must not disturb the
        // existing array/tuple fast path (arrays pass straight through).
        var source = """
            const arr: number[] = [100, 200, 300];
            const [a, ...rest] = arr;
            console.log(a);
            console.log(rest.length);
            const tup: [string, number] = ["x", 7];
            const [s, n] = tup;
            console.log(s);
            console.log(n + 1);
            """;

        var output = TestHarness.Run(source, mode);
        Assert.Equal("100\n2\nx\n8\n", output);
    }

    #endregion

    #region Nested Destructuring

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void NestedDestructuring_Arrays(ExecutionMode mode)
    {
        var source = """
            const nested: number[][] = [[1, 2], [3, 4]];
            const [[a, b], [c, d]] = nested;
            console.log(a);
            console.log(b);
            console.log(c);
            console.log(d);
            """;

        var output = TestHarness.Run(source, mode);
        Assert.Equal("1\n2\n3\n4\n", output);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void NestedDestructuring_Objects(ExecutionMode mode)
    {
        var source = """
            const user = { profile: { email: "test@test.com" } };
            const { profile: { email } } = user;
            console.log(email);
            """;

        var output = TestHarness.Run(source, mode);
        Assert.Equal("test@test.com\n", output);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void MixedDestructuring_ObjectWithArray(ExecutionMode mode)
    {
        var source = """
            const data = { items: [1, 2, 3] };
            const { items: [m1, m2] } = data;
            console.log(m1);
            console.log(m2);
            """;

        var output = TestHarness.Run(source, mode);
        Assert.Equal("1\n2\n", output);
    }

    #endregion
}
