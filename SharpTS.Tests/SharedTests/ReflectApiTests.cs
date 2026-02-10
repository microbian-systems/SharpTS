using SharpTS.Tests.Infrastructure;
using Xunit;

namespace SharpTS.Tests.SharedTests;

/// <summary>
/// Tests for the standard ES2015 Reflect API methods.
/// Runs against both interpreter and compiler.
/// </summary>
public class ReflectApiTests
{
    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void Reflect_Has_ChecksPropertyExistence(ExecutionMode mode)
    {
        var source = """
            let obj: any = { x: 1, y: 2 };
            console.log(Reflect.has(obj, "x"));
            console.log(Reflect.has(obj, "z"));
            """;

        var output = TestHarness.Run(source, mode);
        Assert.Equal("true\nfalse\n", output);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void Reflect_DeleteProperty_RemovesProperty(ExecutionMode mode)
    {
        var source = """
            let obj: any = { x: 1, y: 2 };
            let result: boolean = Reflect.deleteProperty(obj, "x");
            console.log(result);
            console.log(Reflect.has(obj, "x"));
            """;

        var output = TestHarness.Run(source, mode);
        Assert.Equal("true\nfalse\n", output);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void Reflect_DeleteProperty_MissingProperty_ReturnsTrue(ExecutionMode mode)
    {
        var source = """
            let obj: any = { x: 1 };
            let result: boolean = Reflect.deleteProperty(obj, "missing");
            console.log(result);
            """;

        var output = TestHarness.Run(source, mode);
        Assert.Equal("true\n", output);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void Reflect_Get_ReadsProperty(ExecutionMode mode)
    {
        var source = """
            let obj: any = { name: "hello", value: 42 };
            console.log(Reflect.get(obj, "name"));
            console.log(Reflect.get(obj, "value"));
            """;

        var output = TestHarness.Run(source, mode);
        Assert.Equal("hello\n42\n", output);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void Reflect_Get_MissingProperty_ReturnsUndefined(ExecutionMode mode)
    {
        var source = """
            let obj: any = { x: 1 };
            console.log(Reflect.get(obj, "missing"));
            """;

        var output = TestHarness.Run(source, mode);
        Assert.Equal("undefined\n", output);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void Reflect_Set_SetsProperty(ExecutionMode mode)
    {
        var source = """
            let obj: any = {};
            let result: boolean = Reflect.set(obj, "x", 42);
            console.log(result);
            console.log(obj.x);
            """;

        var output = TestHarness.Run(source, mode);
        Assert.Equal("true\n42\n", output);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void Reflect_Set_FrozenObject_ReturnsFalse(ExecutionMode mode)
    {
        var source = """
            let obj: any = { x: 1 };
            Object.freeze(obj);
            let result: boolean = Reflect.set(obj, "x", 999);
            console.log(result);
            console.log(obj.x);
            """;

        var output = TestHarness.Run(source, mode);
        Assert.Equal("false\n1\n", output);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void Reflect_OwnKeys_ReturnsAllKeys(ExecutionMode mode)
    {
        var source = """
            let obj: any = { a: 1, b: 2, c: 3 };
            let keys: any = Reflect.ownKeys(obj);
            console.log(keys.length);
            console.log(keys[0]);
            console.log(keys[1]);
            console.log(keys[2]);
            """;

        var output = TestHarness.Run(source, mode);
        Assert.Equal("3\na\nb\nc\n", output);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void Reflect_GetPrototypeOf_ReturnsPrototype(ExecutionMode mode)
    {
        var source = """
            let proto: any = { greet(): string { return "hello"; } };
            let obj: any = Object.create(proto);
            let p: any = Reflect.getPrototypeOf(obj);
            console.log(p === proto);
            """;

        var output = TestHarness.Run(source, mode);
        Assert.Equal("true\n", output);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void Reflect_SetPrototypeOf_SetsPrototype(ExecutionMode mode)
    {
        var source = """
            let obj: any = {};
            let proto: any = { x: 42 };
            let result: boolean = Reflect.setPrototypeOf(obj, proto);
            console.log(result);
            """;

        var output = TestHarness.Run(source, mode);
        Assert.Equal("true\n", output);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void Reflect_SetPrototypeOf_NonExtensible_ReturnsFalse(ExecutionMode mode)
    {
        var source = """
            let obj: any = {};
            Object.preventExtensions(obj);
            let result: boolean = Reflect.setPrototypeOf(obj, { x: 1 });
            console.log(result);
            """;

        var output = TestHarness.Run(source, mode);
        Assert.Equal("false\n", output);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void Reflect_IsExtensible_ReturnsTrueByDefault(ExecutionMode mode)
    {
        var source = """
            let obj: any = {};
            console.log(Reflect.isExtensible(obj));
            """;

        var output = TestHarness.Run(source, mode);
        Assert.Equal("true\n", output);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void Reflect_PreventExtensions_ReturnsTrueAndPrevents(ExecutionMode mode)
    {
        var source = """
            let obj: any = {};
            let result: boolean = Reflect.preventExtensions(obj);
            console.log(result);
            console.log(Reflect.isExtensible(obj));
            """;

        var output = TestHarness.Run(source, mode);
        Assert.Equal("true\nfalse\n", output);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void Reflect_GetOwnPropertyDescriptor_ReturnsDescriptor(ExecutionMode mode)
    {
        var source = """
            let obj: any = { x: 42 };
            let desc: any = Reflect.getOwnPropertyDescriptor(obj, "x");
            console.log(desc.value);
            console.log(desc.writable);
            console.log(desc.enumerable);
            console.log(desc.configurable);
            """;

        var output = TestHarness.Run(source, mode);
        Assert.Equal("42\ntrue\ntrue\ntrue\n", output);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void Reflect_GetOwnPropertyDescriptor_MissingProperty_ReturnsUndefined(ExecutionMode mode)
    {
        var source = """
            let obj: any = { x: 1 };
            let desc: any = Reflect.getOwnPropertyDescriptor(obj, "missing");
            console.log(desc == null);
            """;

        var output = TestHarness.Run(source, mode);
        Assert.Equal("true\n", output);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void Reflect_DefineProperty_DefinesProperty(ExecutionMode mode)
    {
        var source = """
            let obj: any = {};
            let result: boolean = Reflect.defineProperty(obj, "x", { value: 42, writable: true, enumerable: true, configurable: true });
            console.log(result);
            console.log(obj.x);
            """;

        var output = TestHarness.Run(source, mode);
        Assert.Equal("true\n42\n", output);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void Reflect_Apply_CallsFunction(ExecutionMode mode)
    {
        var source = """
            function add(a: number, b: number): number {
                return a + b;
            }
            let result: any = Reflect.apply(add, undefined, [3, 4]);
            console.log(result);
            """;

        var output = TestHarness.Run(source, mode);
        Assert.Equal("7\n", output);
    }

    [Fact]
    public void Reflect_Construct_CreatesInstance_Interpreted()
    {
        var source = """
            class Point {
                x: number;
                y: number;
                constructor(x: number, y: number) {
                    this.x = x;
                    this.y = y;
                }
            }
            let p: any = Reflect.construct(Point, [10, 20]);
            console.log(p.x);
            console.log(p.y);
            """;

        var output = TestHarness.Run(source, ExecutionMode.Interpreted);
        Assert.Equal("10\n20\n", output);
    }
}
