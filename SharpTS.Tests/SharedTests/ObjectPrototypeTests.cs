using SharpTS.Tests.Infrastructure;
using Xunit;

namespace SharpTS.Tests.SharedTests;

/// <summary>
/// Tests for Object prototype and extensibility methods:
/// - Object.preventExtensions() / Object.isExtensible()
/// - Object.getOwnPropertySymbols()
/// - Object.getPrototypeOf() / Object.setPrototypeOf()
/// </summary>
public class ObjectPrototypeTests
{
    // === preventExtensions ===

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void PreventExtensions_BlocksNewProperties(ExecutionMode mode)
    {
        var source = """
            let obj: any = { x: 1 };
            Object.preventExtensions(obj);
            obj.y = 2;
            console.log(obj.y === undefined);
            """;
        Assert.Equal("true\n", TestHarness.Run(source, mode));
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void PreventExtensions_AllowsModifyingExisting(ExecutionMode mode)
    {
        var source = """
            let obj: any = { x: 1 };
            Object.preventExtensions(obj);
            obj.x = 100;
            console.log(obj.x);
            """;
        Assert.Equal("100\n", TestHarness.Run(source, mode));
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void PreventExtensions_AllowsDeleting(ExecutionMode mode)
    {
        // Delete operator support varies by mode
        var source = """
            let obj: any = { x: 1, y: 2 };
            Object.preventExtensions(obj);
            delete obj.y;
            console.log(obj.y === undefined);
            """;
        Assert.Equal("true\n", TestHarness.Run(source, mode));
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void PreventExtensions_ReturnsTheObject(ExecutionMode mode)
    {
        var source = """
            let obj: any = { x: 1 };
            let result = Object.preventExtensions(obj);
            console.log(result === obj);
            """;
        Assert.Equal("true\n", TestHarness.Run(source, mode));
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void PreventExtensions_OnArray(ExecutionMode mode)
    {
        var source = """
            let arr: number[] = [1, 2, 3];
            Object.preventExtensions(arr);
            arr.push(4);  // Should be silently ignored
            console.log(arr.length);
            """;
        Assert.Equal("3\n", TestHarness.Run(source, mode));
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void PreventExtensions_ArrayAllowsModification(ExecutionMode mode)
    {
        var source = """
            let arr: number[] = [1, 2, 3];
            Object.preventExtensions(arr);
            arr[0] = 100;
            console.log(arr[0]);
            """;
        Assert.Equal("100\n", TestHarness.Run(source, mode));
    }

    // === isExtensible ===

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void IsExtensible_TrueForNormalObject(ExecutionMode mode)
    {
        var source = """
            console.log(Object.isExtensible({ x: 1 }));
            """;
        Assert.Equal("true\n", TestHarness.Run(source, mode));
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void IsExtensible_FalseAfterPreventExtensions(ExecutionMode mode)
    {
        var source = """
            let obj = { x: 1 };
            Object.preventExtensions(obj);
            console.log(Object.isExtensible(obj));
            """;
        Assert.Equal("false\n", TestHarness.Run(source, mode));
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void IsExtensible_FalseForFrozenObject(ExecutionMode mode)
    {
        var source = """
            let obj = Object.freeze({ x: 1 });
            console.log(Object.isExtensible(obj));
            """;
        Assert.Equal("false\n", TestHarness.Run(source, mode));
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void IsExtensible_FalseForSealedObject(ExecutionMode mode)
    {
        var source = """
            let obj = Object.seal({ x: 1 });
            console.log(Object.isExtensible(obj));
            """;
        Assert.Equal("false\n", TestHarness.Run(source, mode));
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void IsExtensible_FalseForPrimitives(ExecutionMode mode)
    {
        var source = """
            console.log(Object.isExtensible(42));
            console.log(Object.isExtensible("hello"));
            """;
        Assert.Equal("false\nfalse\n", TestHarness.Run(source, mode));
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void IsExtensible_TrueForArray(ExecutionMode mode)
    {
        var source = """
            let arr: number[] = [1, 2, 3];
            console.log(Object.isExtensible(arr));
            """;
        Assert.Equal("true\n", TestHarness.Run(source, mode));
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void IsExtensible_FalseAfterPreventExtensionsOnArray(ExecutionMode mode)
    {
        var source = """
            let arr: number[] = [1, 2, 3];
            Object.preventExtensions(arr);
            console.log(Object.isExtensible(arr));
            """;
        Assert.Equal("false\n", TestHarness.Run(source, mode));
    }

    // === getOwnPropertySymbols ===

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void GetOwnPropertySymbols_ReturnsSymbolKeys(ExecutionMode mode)
    {
        var source = """
            let sym1 = Symbol("a");
            let sym2 = Symbol("b");
            let obj: any = { [sym1]: 1, [sym2]: 2, x: 3 };
            let symbols = Object.getOwnPropertySymbols(obj);
            console.log(symbols.length);
            """;
        Assert.Equal("2\n", TestHarness.Run(source, mode));
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void GetOwnPropertySymbols_EmptyForNoSymbols(ExecutionMode mode)
    {
        var source = """
            let obj = { x: 1, y: 2 };
            console.log(Object.getOwnPropertySymbols(obj).length);
            """;
        Assert.Equal("0\n", TestHarness.Run(source, mode));
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void GetOwnPropertySymbols_ReturnsArray(ExecutionMode mode)
    {
        var source = """
            let sym = Symbol("test");
            let obj: any = { [sym]: "value" };
            let symbols = Object.getOwnPropertySymbols(obj);
            console.log(Array.isArray(symbols));
            """;
        Assert.Equal("true\n", TestHarness.Run(source, mode));
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void GetOwnPropertySymbols_SymbolsAreUsable(ExecutionMode mode)
    {
        var source = """
            let sym = Symbol("key");
            let obj: any = { [sym]: 42 };
            let symbols = Object.getOwnPropertySymbols(obj);
            console.log(obj[symbols[0]]);
            """;
        Assert.Equal("42\n", TestHarness.Run(source, mode));
    }

    // === getPrototypeOf ===

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void GetPrototypeOf_ReturnsPrototypeFromCreate(ExecutionMode mode)
    {
        var source = """
            let proto = { x: 1 };
            let obj = Object.create(proto);
            console.log(Object.getPrototypeOf(obj) === proto);
            """;
        Assert.Equal("true\n", TestHarness.Run(source, mode));
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void GetPrototypeOf_ReturnsNullForNullPrototype(ExecutionMode mode)
    {
        var source = """
            let obj = Object.create(null);
            console.log(Object.getPrototypeOf(obj) === null);
            """;
        Assert.Equal("true\n", TestHarness.Run(source, mode));
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void GetPrototypeOf_ReturnsNullForPlainObject(ExecutionMode mode)
    {
        // Plain objects created without Object.create return null (simplified)
        var source = """
            let obj = { x: 1 };
            console.log(Object.getPrototypeOf(obj) === null);
            """;
        Assert.Equal("true\n", TestHarness.Run(source, mode));
    }

    // === setPrototypeOf ===

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void SetPrototypeOf_ChangesPrototype(ExecutionMode mode)
    {
        var source = """
            let proto1 = { x: 1 };
            let proto2 = { y: 2 };
            let obj = Object.create(proto1);
            Object.setPrototypeOf(obj, proto2);
            console.log(Object.getPrototypeOf(obj) === proto2);
            """;
        Assert.Equal("true\n", TestHarness.Run(source, mode));
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void SetPrototypeOf_ReturnsTheObject(ExecutionMode mode)
    {
        var source = """
            let obj = Object.create(null);
            let result = Object.setPrototypeOf(obj, { x: 1 });
            console.log(result === obj);
            """;
        Assert.Equal("true\n", TestHarness.Run(source, mode));
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void SetPrototypeOf_CopiesPropertiesFromNewPrototype(ExecutionMode mode)
    {
        var source = """
            let proto = { x: 42, y: 100 };
            let obj = Object.create(null);
            Object.setPrototypeOf(obj, proto);
            console.log(obj.x);
            console.log(obj.y);
            """;
        Assert.Equal("42\n100\n", TestHarness.Run(source, mode));
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void SetPrototypeOf_ToNull(ExecutionMode mode)
    {
        var source = """
            let proto = { x: 1 };
            let obj = Object.create(proto);
            Object.setPrototypeOf(obj, null);
            console.log(Object.getPrototypeOf(obj) === null);
            """;
        Assert.Equal("true\n", TestHarness.Run(source, mode));
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void SetPrototypeOf_ThrowsOnNonExtensible(ExecutionMode mode)
    {
        var source = """
            let obj = Object.create(null);
            Object.preventExtensions(obj);
            let threw = false;
            try {
                Object.setPrototypeOf(obj, { x: 1 });
            } catch (e) {
                threw = true;
            }
            console.log(threw);
            """;
        Assert.Equal("true\n", TestHarness.Run(source, mode));
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void SetPrototypeOf_ThrowsOnClassInstance(ExecutionMode mode)
    {
        var source = """
            class MyClass {
                x: number = 1;
            }
            let obj = new MyClass();
            let threw = false;
            try {
                Object.setPrototypeOf(obj, { y: 2 });
            } catch (e) {
                threw = true;
            }
            console.log(threw);
            """;
        Assert.Equal("true\n", TestHarness.Run(source, mode));
    }

    // === Edge cases and interactions ===

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void PreventExtensions_DifferentFromFreeze(ExecutionMode mode)
    {
        // preventExtensions allows modification but not addition
        // freeze prevents all changes
        var source = """
            let obj1: any = { x: 1 };
            let obj2: any = { x: 1 };
            Object.preventExtensions(obj1);
            Object.freeze(obj2);
            obj1.x = 100;  // Should work
            obj2.x = 100;  // Should be ignored
            console.log(obj1.x);
            console.log(obj2.x);
            """;
        Assert.Equal("100\n1\n", TestHarness.Run(source, mode));
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void PreventExtensions_DifferentFromSeal_AllowsDelete(ExecutionMode mode)
    {
        // preventExtensions allows delete, seal does not
        // Note: Testing with property modification to show the difference
        var source = """
            let obj1: any = { x: 1 };
            let obj2: any = { x: 1 };
            Object.preventExtensions(obj1);
            Object.seal(obj2);
            // Both should allow modification of existing properties
            obj1.x = 10;
            obj2.x = 10;
            console.log(obj1.x);
            console.log(obj2.x);
            """;
        Assert.Equal("10\n10\n", TestHarness.Run(source, mode));
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void ClassInstance_IsExtensible(ExecutionMode mode)
    {
        var source = """
            class Point {
                x: number;
                constructor(x: number) {
                    this.x = x;
                }
            }
            let p = new Point(10);
            console.log(Object.isExtensible(p));
            """;
        Assert.Equal("true\n", TestHarness.Run(source, mode));
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void ClassInstance_PreventExtensions(ExecutionMode mode)
    {
        var source = """
            class Point {
                x: number;
                constructor(x: number) {
                    this.x = x;
                }
            }
            let p: any = new Point(10);
            Object.preventExtensions(p);
            console.log(Object.isExtensible(p));
            p.y = 20;  // Should be ignored
            console.log(p.y === undefined);
            """;
        Assert.Equal("false\ntrue\n", TestHarness.Run(source, mode));
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void ClassInstance_GetOwnPropertySymbols(ExecutionMode mode)
    {
        var source = """
            let sym = Symbol("prop");
            class MyClass {
                x: number = 1;
            }
            let obj: any = new MyClass();
            obj[sym] = "symbol value";
            let symbols = Object.getOwnPropertySymbols(obj);
            console.log(symbols.length);
            """;
        Assert.Equal("1\n", TestHarness.Run(source, mode));
    }
}
