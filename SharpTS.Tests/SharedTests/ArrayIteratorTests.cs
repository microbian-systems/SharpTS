using SharpTS.Tests.Infrastructure;
using Xunit;

namespace SharpTS.Tests.SharedTests;

/// <summary>
/// Tests for array iterator methods (entries, keys, values).
/// Runs against both interpreter and compiler.
/// </summary>
public class ArrayIteratorTests
{
    #region entries() Tests

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void Array_Entries_ReturnsIndexValuePairs(ExecutionMode mode)
    {
        var source = """
            let arr = [10, 20, 30];
            for (let entry of arr.entries()) {
                console.log(entry[0] + ":" + entry[1]);
            }
            """;

        var output = TestHarness.Run(source, mode);
        Assert.Equal("0:10\n1:20\n2:30\n", output);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void Array_Entries_WithManualDestructuring(ExecutionMode mode)
    {
        var source = """
            let arr = ["a", "b", "c"];
            for (let entry of arr.entries()) {
                let i = entry[0];
                let val = entry[1];
                console.log(i + "=" + val);
            }
            """;

        var output = TestHarness.Run(source, mode);
        Assert.Equal("0=a\n1=b\n2=c\n", output);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void Array_Entries_EmptyArray(ExecutionMode mode)
    {
        var source = """
            let arr: number[] = [];
            let count = 0;
            for (let entry of arr.entries()) {
                count++;
            }
            console.log(count);
            """;

        var output = TestHarness.Run(source, mode);
        Assert.Equal("0\n", output);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void Array_Entries_WithMixedTypes(ExecutionMode mode)
    {
        var source = """
            let arr: (number | string)[] = [1, "two", 3];
            for (let entry of arr.entries()) {
                console.log(entry[0] + ":" + entry[1]);
            }
            """;

        var output = TestHarness.Run(source, mode);
        Assert.Equal("0:1\n1:two\n2:3\n", output);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void Array_Entries_WithArrayFrom(ExecutionMode mode)
    {
        var source = """
            let arr = [10, 20];
            let entries = Array.from(arr.entries());
            console.log(entries.length);
            console.log(entries[0][0] + ":" + entries[0][1]);
            console.log(entries[1][0] + ":" + entries[1][1]);
            """;

        var output = TestHarness.Run(source, mode);
        Assert.Equal("2\n0:10\n1:20\n", output);
    }

    #endregion

    #region keys() Tests

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void Array_Keys_ReturnsIndices(ExecutionMode mode)
    {
        var source = """
            let arr = ["a", "b", "c"];
            for (let key of arr.keys()) {
                console.log(key);
            }
            """;

        var output = TestHarness.Run(source, mode);
        Assert.Equal("0\n1\n2\n", output);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void Array_Keys_EmptyArray(ExecutionMode mode)
    {
        var source = """
            let arr: string[] = [];
            let count = 0;
            for (let key of arr.keys()) {
                count++;
            }
            console.log(count);
            """;

        var output = TestHarness.Run(source, mode);
        Assert.Equal("0\n", output);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void Array_Keys_CanAccessArrayElements(ExecutionMode mode)
    {
        var source = """
            let arr = [100, 200, 300];
            for (let i of arr.keys()) {
                console.log(arr[i]);
            }
            """;

        var output = TestHarness.Run(source, mode);
        Assert.Equal("100\n200\n300\n", output);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void Array_Keys_WithArrayFrom(ExecutionMode mode)
    {
        var source = """
            let arr = [10, 20, 30];
            let keys = Array.from(arr.keys());
            console.log(keys.join(","));
            """;

        var output = TestHarness.Run(source, mode);
        Assert.Equal("0,1,2\n", output);
    }

    #endregion

    #region values() Tests

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void Array_Values_ReturnsElements(ExecutionMode mode)
    {
        var source = """
            let arr = [10, 20, 30];
            for (let val of arr.values()) {
                console.log(val);
            }
            """;

        var output = TestHarness.Run(source, mode);
        Assert.Equal("10\n20\n30\n", output);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void Array_Values_EmptyArray(ExecutionMode mode)
    {
        var source = """
            let arr: number[] = [];
            let count = 0;
            for (let val of arr.values()) {
                count++;
            }
            console.log(count);
            """;

        var output = TestHarness.Run(source, mode);
        Assert.Equal("0\n", output);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void Array_Values_WithStrings(ExecutionMode mode)
    {
        var source = """
            let arr = ["hello", "world"];
            for (let val of arr.values()) {
                console.log(val);
            }
            """;

        var output = TestHarness.Run(source, mode);
        Assert.Equal("hello\nworld\n", output);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void Array_Values_WithArrayFrom(ExecutionMode mode)
    {
        var source = """
            let arr = [1, 2, 3];
            let values = Array.from(arr.values());
            console.log(values.join("-"));
            """;

        var output = TestHarness.Run(source, mode);
        Assert.Equal("1-2-3\n", output);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void Array_Values_CountsNullAndUndefined(ExecutionMode mode)
    {
        var source = """
            let arr: (number | null | undefined)[] = [1, null, undefined, 2];
            let count = 0;
            for (let val of arr.values()) {
                count++;
            }
            console.log(count);
            """;

        var output = TestHarness.Run(source, mode);
        Assert.Equal("4\n", output);
    }

    #endregion

    #region Break/Continue Tests

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void Array_Entries_WithBreak(ExecutionMode mode)
    {
        var source = """
            let arr = [1, 2, 3, 4, 5];
            for (let entry of arr.entries()) {
                let i = entry[0];
                let val = entry[1];
                if (val > 2) break;
                console.log(i + ":" + val);
            }
            console.log("done");
            """;

        var output = TestHarness.Run(source, mode);
        Assert.Equal("0:1\n1:2\ndone\n", output);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void Array_Keys_WithContinue(ExecutionMode mode)
    {
        var source = """
            let arr = [10, 20, 30, 40];
            for (let i of arr.keys()) {
                if (i === 1 || i === 3) continue;
                console.log(i);
            }
            """;

        var output = TestHarness.Run(source, mode);
        Assert.Equal("0\n2\n", output);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void Array_Values_WithBreak(ExecutionMode mode)
    {
        var source = """
            let arr = ["a", "b", "c", "d"];
            for (let val of arr.values()) {
                if (val === "c") break;
                console.log(val);
            }
            """;

        var output = TestHarness.Run(source, mode);
        Assert.Equal("a\nb\n", output);
    }

    #endregion

    #region Null and Boolean Stringification Tests

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void Array_Entries_WithNull_StringifiesCorrectly(ExecutionMode mode)
    {
        var source = """
            let arr: (number | null)[] = [1, null, 2];
            for (let entry of arr.entries()) {
                console.log(entry[0] + ":" + entry[1]);
            }
            """;

        var output = TestHarness.Run(source, mode);
        Assert.Equal("0:1\n1:null\n2:2\n", output);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void Array_Entries_WithBoolean_StringifiesCorrectly(ExecutionMode mode)
    {
        var source = """
            let arr: (number | boolean)[] = [1, true, 2, false];
            for (let entry of arr.entries()) {
                console.log(entry[0] + ":" + entry[1]);
            }
            """;

        var output = TestHarness.Run(source, mode);
        Assert.Equal("0:1\n1:true\n2:2\n3:false\n", output);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void StringConcat_WithNull_JavaScriptStyle(ExecutionMode mode)
    {
        var source = """
            let x: string | null = null;
            console.log("value:" + x);
            """;

        var output = TestHarness.Run(source, mode);
        Assert.Equal("value:null\n", output);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void StringConcat_WithBoolean_LowercaseTrue(ExecutionMode mode)
    {
        var source = """
            let x = true;
            console.log("bool:" + x);
            """;

        var output = TestHarness.Run(source, mode);
        Assert.Equal("bool:true\n", output);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void StringConcat_WithBoolean_LowercaseFalse(ExecutionMode mode)
    {
        var source = """
            let x = false;
            console.log("bool:" + x);
            """;

        var output = TestHarness.Run(source, mode);
        Assert.Equal("bool:false\n", output);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void StringConcat_MultipleNullsAndBooleans(ExecutionMode mode)
    {
        var source = """
            let a: number | null = null;
            let b = true;
            let c = false;
            console.log(a + ":" + b + ":" + c);
            """;

        var output = TestHarness.Run(source, mode);
        Assert.Equal("null:true:false\n", output);
    }

    #endregion

    #region Edge Cases

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void Array_Entries_SingleElement(ExecutionMode mode)
    {
        var source = """
            let arr = [42];
            for (let entry of arr.entries()) {
                console.log(entry[0] + ":" + entry[1]);
            }
            """;

        var output = TestHarness.Run(source, mode);
        Assert.Equal("0:42\n", output);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void Array_Keys_LargeArray(ExecutionMode mode)
    {
        var source = """
            let arr = [1, 2, 3, 4, 5, 6, 7, 8, 9, 10];
            let sum = 0;
            for (let i of arr.keys()) {
                sum += i;
            }
            console.log(sum);
            """;

        var output = TestHarness.Run(source, mode);
        // 0+1+2+3+4+5+6+7+8+9 = 45
        Assert.Equal("45\n", output);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void Array_Values_WithObjects(ExecutionMode mode)
    {
        var source = """
            let arr = [{ x: 1 }, { x: 2 }];
            for (let obj of arr.values()) {
                console.log(obj.x);
            }
            """;

        var output = TestHarness.Run(source, mode);
        Assert.Equal("1\n2\n", output);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void Array_Entries_NestedArrays(ExecutionMode mode)
    {
        var source = """
            let arr = [[1, 2], [3, 4]];
            for (let entry of arr.entries()) {
                let i = entry[0];
                let innerArr = entry[1];
                console.log(i + ":" + innerArr.join("-"));
            }
            """;

        var output = TestHarness.Run(source, mode);
        Assert.Equal("0:1-2\n1:3-4\n", output);
    }

    #endregion
}
