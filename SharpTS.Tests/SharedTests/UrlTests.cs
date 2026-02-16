using SharpTS.Tests.Infrastructure;
using Xunit;

namespace SharpTS.Tests.SharedTests;

/// <summary>
/// Tests for the global URL and URLSearchParams APIs.
/// Tests run against both interpreter and compiler modes.
/// </summary>
public class UrlTests
{
    // ========== URL Constructor ==========

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void URL_Constructor_ParsesFullUrl(ExecutionMode mode)
    {
        var source = @"
            const u = new URL(""https://example.com/path?q=1#hash"");
            console.log(u.href);
        ";
        var output = TestHarness.Run(source, mode);
        Assert.Equal("https://example.com/path?q=1#hash\n", output);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void URL_Constructor_WithBase(ExecutionMode mode)
    {
        var source = @"
            const u = new URL(""/path"", ""https://example.com"");
            console.log(u.href);
        ";
        var output = TestHarness.Run(source, mode);
        Assert.Equal("https://example.com/path\n", output);
    }

    // ========== URL Properties ==========

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void URL_Protocol(ExecutionMode mode)
    {
        var source = @"
            const u = new URL(""https://example.com"");
            console.log(u.protocol);
        ";
        var output = TestHarness.Run(source, mode);
        Assert.Equal("https:\n", output);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void URL_Host_DefaultPort(ExecutionMode mode)
    {
        var source = @"
            const u = new URL(""https://example.com/path"");
            console.log(u.host);
        ";
        var output = TestHarness.Run(source, mode);
        Assert.Equal("example.com\n", output);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void URL_Host_NonDefaultPort(ExecutionMode mode)
    {
        var source = @"
            const u = new URL(""https://example.com:8080/path"");
            console.log(u.host);
        ";
        var output = TestHarness.Run(source, mode);
        Assert.Equal("example.com:8080\n", output);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void URL_Hostname(ExecutionMode mode)
    {
        var source = @"
            const u = new URL(""https://example.com:8080/path"");
            console.log(u.hostname);
        ";
        var output = TestHarness.Run(source, mode);
        Assert.Equal("example.com\n", output);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void URL_Port_NonDefault(ExecutionMode mode)
    {
        var source = @"
            const u = new URL(""https://example.com:8080/path"");
            console.log(u.port);
        ";
        var output = TestHarness.Run(source, mode);
        Assert.Equal("8080\n", output);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void URL_Port_Default_Empty(ExecutionMode mode)
    {
        var source = @"
            const u = new URL(""https://example.com/path"");
            console.log(u.port);
        ";
        var output = TestHarness.Run(source, mode);
        Assert.Equal("\n", output);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void URL_Pathname(ExecutionMode mode)
    {
        var source = @"
            const u = new URL(""https://example.com/path/to/resource"");
            console.log(u.pathname);
        ";
        var output = TestHarness.Run(source, mode);
        Assert.Equal("/path/to/resource\n", output);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void URL_Search(ExecutionMode mode)
    {
        var source = @"
            const u = new URL(""https://example.com/path?q=1&r=2"");
            console.log(u.search);
        ";
        var output = TestHarness.Run(source, mode);
        Assert.Equal("?q=1&r=2\n", output);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void URL_Hash(ExecutionMode mode)
    {
        var source = @"
            const u = new URL(""https://example.com/path#section"");
            console.log(u.hash);
        ";
        var output = TestHarness.Run(source, mode);
        Assert.Equal("#section\n", output);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void URL_Origin(ExecutionMode mode)
    {
        var source = @"
            const u = new URL(""https://example.com:8080/path"");
            console.log(u.origin);
        ";
        var output = TestHarness.Run(source, mode);
        Assert.Equal("https://example.com:8080\n", output);
    }

    // ========== URL Methods ==========

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void URL_ToString(ExecutionMode mode)
    {
        var source = @"
            const u = new URL(""https://example.com/path"");
            console.log(u.toString());
        ";
        var output = TestHarness.Run(source, mode);
        Assert.Equal("https://example.com/path\n", output);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void URL_ToJSON(ExecutionMode mode)
    {
        var source = @"
            const u = new URL(""https://example.com/path"");
            console.log(u.toJSON());
        ";
        var output = TestHarness.Run(source, mode);
        Assert.Equal("https://example.com/path\n", output);
    }

    // ========== URL.searchParams ==========

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void URL_SearchParams_Get(ExecutionMode mode)
    {
        var source = @"
            const u = new URL(""https://example.com/path?q=hello&r=world"");
            console.log(u.searchParams.get(""q""));
            console.log(u.searchParams.get(""r""));
        ";
        var output = TestHarness.Run(source, mode);
        Assert.Equal("hello\nworld\n", output);
    }

    // ========== URLSearchParams Constructor ==========

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void URLSearchParams_Constructor_Empty(ExecutionMode mode)
    {
        var source = @"
            const sp = new URLSearchParams();
            console.log(sp.toString());
        ";
        var output = TestHarness.Run(source, mode);
        Assert.Equal("\n", output);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void URLSearchParams_Constructor_String(ExecutionMode mode)
    {
        var source = @"
            const sp = new URLSearchParams(""a=1&b=2"");
            console.log(sp.toString());
        ";
        var output = TestHarness.Run(source, mode);
        Assert.Equal("a=1&b=2\n", output);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void URLSearchParams_Constructor_Object(ExecutionMode mode)
    {
        var source = @"
            const sp = new URLSearchParams({a: ""1"", b: ""2""});
            console.log(sp.get(""a""));
            console.log(sp.get(""b""));
        ";
        var output = TestHarness.Run(source, mode);
        Assert.Equal("1\n2\n", output);
    }

    // ========== URLSearchParams Methods ==========

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void URLSearchParams_Get_ReturnsNull(ExecutionMode mode)
    {
        var source = @"
            const sp = new URLSearchParams(""a=1"");
            console.log(sp.get(""missing""));
        ";
        var output = TestHarness.Run(source, mode);
        Assert.Equal("null\n", output);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void URLSearchParams_Has(ExecutionMode mode)
    {
        var source = @"
            const sp = new URLSearchParams(""a=1&b=2"");
            console.log(sp.has(""a""));
            console.log(sp.has(""c""));
        ";
        var output = TestHarness.Run(source, mode);
        Assert.Equal("true\nfalse\n", output);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void URLSearchParams_Set(ExecutionMode mode)
    {
        var source = @"
            const sp = new URLSearchParams(""a=1"");
            sp.set(""a"", ""2"");
            console.log(sp.get(""a""));
        ";
        var output = TestHarness.Run(source, mode);
        Assert.Equal("2\n", output);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void URLSearchParams_Append(ExecutionMode mode)
    {
        var source = @"
            const sp = new URLSearchParams(""a=1"");
            sp.append(""a"", ""2"");
            sp.append(""b"", ""3"");
            console.log(sp.toString());
        ";
        var output = TestHarness.Run(source, mode);
        Assert.Equal("a=1&a=2&b=3\n", output);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void URLSearchParams_Delete(ExecutionMode mode)
    {
        var source = @"
            const sp = new URLSearchParams(""a=1&b=2&a=3"");
            sp.delete(""a"");
            console.log(sp.toString());
        ";
        var output = TestHarness.Run(source, mode);
        Assert.Equal("b=2\n", output);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void URLSearchParams_GetAll(ExecutionMode mode)
    {
        var source = @"
            const sp = new URLSearchParams(""a=1&b=2&a=3"");
            const all = sp.getAll(""a"");
            console.log(all.length);
            console.log(all[0]);
            console.log(all[1]);
        ";
        var output = TestHarness.Run(source, mode);
        Assert.Equal("2\n1\n3\n", output);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void URLSearchParams_Sort(ExecutionMode mode)
    {
        var source = @"
            const sp = new URLSearchParams(""c=3&a=1&b=2"");
            sp.sort();
            console.log(sp.toString());
        ";
        var output = TestHarness.Run(source, mode);
        Assert.Equal("a=1&b=2&c=3\n", output);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void URLSearchParams_Size(ExecutionMode mode)
    {
        var source = @"
            const sp = new URLSearchParams(""a=1&b=2&c=3"");
            console.log(sp.size);
        ";
        var output = TestHarness.Run(source, mode);
        Assert.Equal("3\n", output);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void URLSearchParams_ToString(ExecutionMode mode)
    {
        var source = @"
            const sp = new URLSearchParams(""hello=world&foo=bar"");
            console.log(sp.toString());
        ";
        var output = TestHarness.Run(source, mode);
        Assert.Equal("hello=world&foo=bar\n", output);
    }
}
