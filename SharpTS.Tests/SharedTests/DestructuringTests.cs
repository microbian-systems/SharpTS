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

    #region String Rest Element (#753)

    // #753: a rest element over a STRING source must collect a fresh ARRAY of characters, not bind
    // the trailing substring. Non-rest character bindings are identical either way.
    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void ArrayDestructuring_StringRest_BindsCharArray(ExecutionMode mode)
    {
        var source = """
            const [a, ...rest] = "hello";
            console.log(a);
            console.log(Array.isArray(rest));
            console.log(rest.length);
            console.log(rest.join("-"));
            """;

        var output = TestHarness.Run(source, mode);
        Assert.Equal("h\ntrue\n4\ne-l-l-o\n", output);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void ArrayDestructuring_StringNonRest_Unchanged(ExecutionMode mode)
    {
        var source = """
            const [a, b, c = "Z"] = "hi";
            console.log(a, b, c);
            """;

        var output = TestHarness.Run(source, mode);
        Assert.Equal("h i Z\n", output);
    }

    #endregion

    #region Assignment Destructuring (#754)

    // #754: destructuring assignment to EXISTING l-values (no const/let), array and object patterns.
    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void AssignmentDestructuring_ArrayBasicAndSwap(ExecutionMode mode)
    {
        var source = """
            let a = 0, b = 0;
            [a, b] = [1, 2];
            console.log(a, b);
            [a, b] = [b, a];
            console.log(a, b);
            """;

        var output = TestHarness.Run(source, mode);
        Assert.Equal("1 2\n2 1\n", output);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void AssignmentDestructuring_DefaultsHolesAndRest(ExecutionMode mode)
    {
        var source = """
            let c = 0, d = 0;
            [c, d = 9] = [5];
            console.log(c, d);
            let x, y;
            [, x, , y] = [1, 2, 3, 4];
            console.log(x, y);
            let head: number, tail: number[];
            [head, ...tail] = [1, 2, 3, 4];
            console.log(head, Array.isArray(tail), tail.join(","));
            """;

        var output = TestHarness.Run(source, mode);
        Assert.Equal("5 9\n2 4\n1 true 2,3,4\n", output);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void AssignmentDestructuring_MemberTargets(ExecutionMode mode)
    {
        var source = """
            const o: any = {};
            const arr: number[] = [0, 0];
            [o.p, arr[1]] = [10, 20];
            console.log(o.p, arr[1]);
            """;

        var output = TestHarness.Run(source, mode);
        Assert.Equal("10 20\n", output);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void AssignmentDestructuring_Object_RenameRestAndDefault(ExecutionMode mode)
    {
        var source = """
            let pa: number, pb: number, rr: any;
            ({ a: pa, b: pb, ...rr } = { a: 1, b: 2, z: 9 });
            console.log(pa, pb, JSON.stringify(rr));
            const src: any = { p: 3 };
            let ox, oy;
            ({ p: ox, q: oy = 4 } = src);
            console.log(ox, oy);
            """;

        var output = TestHarness.Run(source, mode);
        Assert.Equal("1 2 {\"z\":9}\n3 4\n", output);
    }

    // The right-hand side is normalized through the #685 iterator protocol, so a non-indexable
    // iterable (Set) destructures by assignment just like a declaration.
    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void AssignmentDestructuring_IteratorSource(ExecutionMode mode)
    {
        var source = """
            let s1, s2;
            [s1, s2] = new Set<number>([11, 22]);
            console.log(s1, s2);
            """;

        var output = TestHarness.Run(source, mode);
        Assert.Equal("11 22\n", output);
    }

    // An assignment expression evaluates to its right-hand side (the ORIGINAL rhs, not the
    // normalized array), so it composes in expression position and chains.
    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void AssignmentDestructuring_ExpressionPositionAndChained(ExecutionMode mode)
    {
        var source = """
            let e1, e2;
            const ret = ([e1, e2] = [100, 200]);
            console.log(JSON.stringify(ret), e1, e2);
            let a, b, c, d;
            [a, b] = [c, d] = [1, 2];
            console.log(a, b, c, d);
            """;

        var output = TestHarness.Run(source, mode);
        Assert.Equal("[100,200] 100 200\n1 2 1 2\n", output);
    }

    #endregion
}
