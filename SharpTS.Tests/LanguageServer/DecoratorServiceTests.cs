using SharpTS.LanguageServer.Services;
using Xunit;

namespace SharpTS.Tests.LanguageServer;

public class DecoratorServiceTests
{
    private readonly DecoratorService _svc = new(); // in-process resolver (BCL)

    [Fact]
    public void Hover_OnDotNetType_ShowsResolvedClrType()
    {
        var hover = _svc.Hover("@DotNetType(\"System.Text.StringBuilder\")", line: 0, character: 5);
        Assert.NotNull(hover);
        Assert.Contains("System.Text.StringBuilder", hover!.Contents.MarkupContent!.Value);
    }

    [Fact]
    public void Hover_OnBuiltinDecorator_ShowsDescription()
    {
        var hover = _svc.Hover("@DotNetOverload(\"int\")", line: 0, character: 3);
        Assert.NotNull(hover);
        Assert.Contains("overload", hover!.Contents.MarkupContent!.Value, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Hover_OnUnresolvableType_FallsBackToDecoratorDescription()
    {
        // Type doesn't resolve, but @DotNetType is a known builtin -> still describe the decorator.
        var hover = _svc.Hover("@DotNetType(\"System.Nope\")", line: 0, character: 5);
        Assert.NotNull(hover);
        Assert.Contains("DotNetType", hover!.Contents.MarkupContent!.Value);
    }

    [Fact]
    public void Hover_OnNonDecorator_ReturnsNull()
        => Assert.Null(_svc.Hover("const x: number = 1;", line: 0, character: 6));

    [Fact]
    public void Completion_AfterAt_OffersBuiltins()
    {
        var list = _svc.Completion("@", line: 0, character: 1);
        Assert.NotNull(list);
        Assert.Contains(list!.Items, i => i.Label == "DotNetType");
        Assert.Contains(list.Items, i => i.Label == "DotNetMethod");
    }

    [Fact]
    public void Completion_WithPrefix_Filters()
    {
        var list = _svc.Completion("@DotNetT", line: 0, character: 8);
        Assert.NotNull(list);
        Assert.Contains(list!.Items, i => i.Label == "DotNetType");
        Assert.DoesNotContain(list.Items, i => i.Label == "DotNetMethod");
    }

    [Fact]
    public void Completion_NotAfterAt_ReturnsNull()
        => Assert.Null(_svc.Completion("const x = 1", line: 0, character: 7));
}
