using SharpTS.Parsing;
using SharpTS.Tests.Infrastructure;
using Xunit;

namespace SharpTS.Tests.InterpreterTests;

/// <summary>
/// Tests for @DotNetType in interpreter mode. The compile-mode counterpart lives in
/// <c>SharpTS.Tests/CompilerTests/DotNetTypeTests.cs</c> and covers the same
/// scenarios — both modes must produce the same output for each case.
/// </summary>
public class DotNetTypeInterpreterTests
{
    #region Instance methods / constructor

    [Fact]
    public void StringBuilder_Append_Works()
    {
        var source = """
            @DotNetType("System.Text.StringBuilder")
            declare class StringBuilder {
                constructor();
                append(value: string): StringBuilder;
                toString(): string;
            }
            let sb: StringBuilder = new StringBuilder();
            sb.append("Hello");
            sb.append(" ");
            sb.append("World");
            console.log(sb.toString());
            """;

        var output = TestHarness.RunInterpreted(source, DecoratorMode.Legacy);
        Assert.Equal("Hello World\n", output);
    }

    [Fact]
    public void StringBuilder_AppendNumber_Works()
    {
        var source = """
            @DotNetType("System.Text.StringBuilder")
            declare class StringBuilder {
                constructor();
                append(value: number): StringBuilder;
                toString(): string;
            }
            let sb: StringBuilder = new StringBuilder();
            sb.append(42);
            console.log(sb.toString());
            """;

        var output = TestHarness.RunInterpreted(source, DecoratorMode.Legacy);
        Assert.Equal("42\n", output);
    }

    [Fact]
    public void StringBuilder_Length_PropertyAccess_Works()
    {
        var source = """
            @DotNetType("System.Text.StringBuilder")
            declare class StringBuilder {
                constructor();
                append(value: string): StringBuilder;
                readonly length: number;
            }
            let sb: StringBuilder = new StringBuilder();
            sb.append("Hello");
            console.log(sb.length);
            """;

        var output = TestHarness.RunInterpreted(source, DecoratorMode.Legacy);
        Assert.Equal("5\n", output);
    }

    #endregion

    #region Static methods

    [Fact]
    public void Guid_Parse_StaticMethod_Works()
    {
        var source = """
            @DotNetType("System.Guid")
            declare class Guid {
                static parse(input: string): Guid;
                toString(): string;
            }
            let g: Guid = Guid.parse("00000000-0000-0000-0000-000000000000");
            console.log(g.toString());
            """;

        var output = TestHarness.RunInterpreted(source, DecoratorMode.Legacy);
        Assert.Equal("00000000-0000-0000-0000-000000000000\n", output);
    }

    [Fact]
    public void Guid_NewGuid_ReturnsUsableInstance()
    {
        var source = """
            @DotNetType("System.Guid")
            declare class Guid {
                static newGuid(): Guid;
                toString(): string;
            }
            let g: Guid = Guid.newGuid();
            let str: string = g.toString();
            console.log(str.length > 30 ? "valid" : "invalid");
            """;

        var output = TestHarness.RunInterpreted(source, DecoratorMode.Legacy);
        Assert.Equal("valid\n", output);
    }

    #endregion

    #region Properties / value types

    [Fact]
    public void TimeSpan_Properties_Work()
    {
        var source = """
            @DotNetType("System.TimeSpan")
            declare class TimeSpan {
                static fromMinutes(value: number): TimeSpan;
                readonly totalSeconds: number;
                readonly totalMinutes: number;
            }
            let ts: TimeSpan = TimeSpan.fromMinutes(2);
            console.log(ts.totalMinutes);
            console.log(ts.totalSeconds);
            """;

        var output = TestHarness.RunInterpreted(source, DecoratorMode.Legacy);
        Assert.Equal("2\n120\n", output);
    }

    [Fact]
    public void DateTime_StaticProperty_Works()
    {
        var source = """
            @DotNetType("System.DateTime")
            declare class DateTime {
                static readonly now: DateTime;
                readonly year: number;
            }
            let dt: DateTime = DateTime.now;
            console.log(dt.year >= 2024 ? "valid" : "invalid");
            """;

        var output = TestHarness.RunInterpreted(source, DecoratorMode.Legacy);
        Assert.Equal("valid\n", output);
    }

    #endregion

    #region Overload resolution (runtime)

    [Fact]
    public void Convert_ToInt32_PicksDoubleOverloadByDefault()
    {
        // 42.7 → double overload → rounded to nearest even = 43
        var source = """
            @DotNetType("System.Convert")
            declare class Convert {
                static toInt32(value: number): number;
            }
            console.log(Convert.toInt32(42.7));
            """;

        var output = TestHarness.RunInterpreted(source, DecoratorMode.Legacy);
        Assert.Equal("43\n", output);
    }

    [Fact]
    public void DotNetOverload_Hint_ForcesIntOverload()
    {
        // Hint selects ToInt32(int). Narrowing happens before the call,
        // so 3.7 is truncated to 3 (vs 4 with the default double overload).
        var source = """
            @DotNetType("System.Convert")
            declare class Convert {
                @DotNetOverload("int")
                static toInt32(value: number): number;
            }
            console.log(Convert.toInt32(3.7));
            """;

        var output = TestHarness.RunInterpreted(source, DecoratorMode.Legacy);
        Assert.Equal("3\n", output);
    }

    [Fact]
    public void StringBuilder_PrefersStringOverObject()
    {
        var source = """
            @DotNetType("System.Text.StringBuilder")
            declare class StringBuilder {
                constructor();
                append(value: string): StringBuilder;
                toString(): string;
            }
            let sb: StringBuilder = new StringBuilder();
            sb.append("test");
            console.log(sb.toString());
            """;

        var output = TestHarness.RunInterpreted(source, DecoratorMode.Legacy);
        Assert.Equal("test\n", output);
    }

    #endregion

    #region Params arrays

    [Fact]
    public void StringFormat_WithParams_Works()
    {
        var source = """
            @DotNetType("System.String")
            declare class String {
                static format(format: string, ...args: any[]): string;
            }
            let result: string = String.format("{0} + {1} = {2}", 1, 2, 3);
            console.log(result);
            """;

        var output = TestHarness.RunInterpreted(source, DecoratorMode.Legacy);
        Assert.Equal("1 + 2 = 3\n", output);
    }

    #endregion

    #region Exception mapping

    [Fact]
    public void FormatException_MapsToSyntaxError()
    {
        var source = """
            @DotNetType("System.Guid")
            declare class Guid {
                static parse(input: string): Guid;
            }
            try {
                Guid.parse("not-a-guid");
                console.log("unreachable");
            } catch (e) {
                console.log(e.name);
            }
            """;

        var output = TestHarness.RunInterpreted(source, DecoratorMode.Legacy);
        Assert.Equal("SyntaxError\n", output);
    }

    [Fact]
    public void OverflowException_MapsToRangeError()
    {
        // Convert.ToByte(1000) overflows.
        var source = """
            @DotNetType("System.Convert")
            declare class Convert {
                static toByte(value: number): number;
            }
            try {
                Convert.toByte(1000);
                console.log("unreachable");
            } catch (e) {
                console.log(e.name);
            }
            """;

        var output = TestHarness.RunInterpreted(source, DecoratorMode.Legacy);
        Assert.Equal("RangeError\n", output);
    }

    [Fact]
    public void ExceptionMapping_PreservesMessage()
    {
        var source = """
            @DotNetType("System.Guid")
            declare class Guid {
                static parse(input: string): Guid;
            }
            try {
                Guid.parse("not-a-guid");
            } catch (e) {
                console.log(e.message.length > 0 ? "has message" : "empty");
            }
            """;

        var output = TestHarness.RunInterpreted(source, DecoratorMode.Legacy);
        Assert.Equal("has message\n", output);
    }

    #endregion

    #region Mixed usage

    [Fact]
    public void DotNetTypeAndUserClass_Interop()
    {
        var source = """
            @DotNetType("System.Text.StringBuilder")
            declare class StringBuilder {
                constructor();
                append(value: string): StringBuilder;
                toString(): string;
            }

            class Person {
                name: string;
                constructor(name: string) {
                    this.name = name;
                }
                describe(): string {
                    let sb: StringBuilder = new StringBuilder();
                    sb.append("[");
                    sb.append(this.name);
                    sb.append("]");
                    return sb.toString();
                }
            }

            let p: Person = new Person("Alice");
            console.log(p.describe());
            """;

        var output = TestHarness.RunInterpreted(source, DecoratorMode.Legacy);
        Assert.Equal("[Alice]\n", output);
    }

    #endregion

    #region Error path: type not found

    [Fact]
    public void TypeNotFound_ThrowsInterpreterException()
    {
        var source = """
            @DotNetType("Acme.DoesNotExist")
            declare class Nope {
                static foo(): string;
            }
            Nope.foo();
            """;

        var ex = Assert.ThrowsAny<Exception>(() => TestHarness.RunInterpreted(source, DecoratorMode.Legacy));
        Assert.Contains("not found", ex.Message);
    }

    #endregion
}
