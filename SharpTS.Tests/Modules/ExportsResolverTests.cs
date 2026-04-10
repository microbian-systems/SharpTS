using System.Text.Json;
using SharpTS.Modules;
using Xunit;

namespace SharpTS.Tests.Modules;

/// <summary>
/// Unit tests for the ExportsResolver algorithm.
/// Tests are constructed with raw JsonElement inputs — no filesystem or interpreter needed.
/// </summary>
public class ExportsResolverTests
{
    private static readonly string[] Conditions = ExportsResolver.DefaultConditions;

    private static JsonElement Parse(string json)
    {
        return JsonDocument.Parse(json).RootElement;
    }

    [Fact]
    public void SimpleStringExports_RootSubpath_ReturnsTarget()
    {
        var exports = Parse("\"./index.ts\"");
        var result = ExportsResolver.ResolvePackageExports(exports, ".", Conditions);
        Assert.Equal("./index.ts", result);
    }

    [Fact]
    public void ConditionsPick_Import()
    {
        var exports = Parse("""{ "import": "./esm.ts", "require": "./cjs.ts" }""");
        var result = ExportsResolver.ResolvePackageExports(exports, ".", Conditions);
        Assert.Equal("./esm.ts", result);
    }

    [Fact]
    public void NodeCondition_BeatsImport()
    {
        var exports = Parse("""{ "types": "./types.ts", "import": "./index.ts" }""");
        var result = ExportsResolver.ResolvePackageExports(exports, ".", Conditions);
        // "types" is not in default conditions (it's for TS tooling, not runtime).
        Assert.Equal("./index.ts", result);
    }

    [Fact]
    public void NodeCondition_BeatsDefault()
    {
        var exports = Parse("""{ "node": "./node.ts", "default": "./index.ts" }""");
        var result = ExportsResolver.ResolvePackageExports(exports, ".", Conditions);
        // "node" is in default conditions and comes before "default".
        Assert.Equal("./node.ts", result);
    }

    [Fact]
    public void SubpathExactMatch()
    {
        var exports = Parse("""{ ".": "./index.ts", "./utils": "./src/utils.ts" }""");
        var result = ExportsResolver.ResolvePackageExports(exports, "./utils", Conditions);
        Assert.Equal("./src/utils.ts", result);
    }

    [Fact]
    public void SubpathNotFound_ReturnsNull()
    {
        var exports = Parse("""{ ".": "./index.ts" }""");
        var result = ExportsResolver.ResolvePackageExports(exports, "./missing", Conditions);
        Assert.Null(result);
    }

    [Fact]
    public void WildcardPattern_SubstitutesMatch()
    {
        var exports = Parse("""{ "./*": "./src/*.ts" }""");
        var result = ExportsResolver.ResolvePackageExports(exports, "./foo", Conditions);
        Assert.Equal("./src/foo.ts", result);
    }

    [Fact]
    public void NullRestriction_BlocksAccess()
    {
        var exports = Parse("""{ ".": "./index.ts", "./internal": null }""");
        var result = ExportsResolver.ResolvePackageExports(exports, "./internal", Conditions);
        Assert.Null(result);
    }

    [Fact]
    public void ArrayFallback_SkipsNullTakesSecond()
    {
        var exports = Parse("""[null, "./fallback.ts"]""");
        var result = ExportsResolver.ResolvePackageExports(exports, ".", Conditions);
        Assert.Equal("./fallback.ts", result);
    }

    [Fact]
    public void NestedConditions_DefaultInsideImport()
    {
        var exports = Parse("""{ ".": { "import": { "types": "./d.ts", "default": "./esm.ts" } } }""");
        var result = ExportsResolver.ResolvePackageExports(exports, ".", Conditions);
        // "types" not in runtime conditions, so "default" wins inside "import".
        Assert.Equal("./esm.ts", result);
    }

    [Fact]
    public void NoDotKeys_ConditionsForRoot()
    {
        var exports = Parse("""{ "import": "./esm.ts" }""");
        var result = ExportsResolver.ResolvePackageExports(exports, ".", Conditions);
        Assert.Equal("./esm.ts", result);
    }

    [Fact]
    public void NoDotKeys_NonRootSubpath_ReturnsNull()
    {
        var exports = Parse("""{ "import": "./esm.ts" }""");
        var result = ExportsResolver.ResolvePackageExports(exports, "./foo", Conditions);
        Assert.Null(result);
    }

    [Fact]
    public void TargetWithoutDotSlashPrefix_Invalid()
    {
        var exports = Parse("\"bare-path\"");
        var result = ExportsResolver.ResolvePackageExports(exports, ".", Conditions);
        Assert.Null(result);
    }

    [Fact]
    public void WildcardLongestPrefixWins()
    {
        var exports = Parse("""{ "./*": "./a/*.ts", "./utils/*": "./b/*.ts" }""");
        var result = ExportsResolver.ResolvePackageExports(exports, "./utils/foo", Conditions);
        Assert.Equal("./b/foo.ts", result);
    }

    [Fact]
    public void ImportsResolution_ExactMatch()
    {
        var imports = Parse("""{ "#utils": "./src/utils.ts" }""");
        var result = ExportsResolver.ResolvePackageImports(imports, "#utils", Conditions);
        Assert.Equal("./src/utils.ts", result);
    }

    [Fact]
    public void ImportsResolution_WithConditions()
    {
        var imports = Parse("""{ "#utils": { "import": "./src/utils-esm.ts", "require": "./src/utils-cjs.ts" } }""");
        var result = ExportsResolver.ResolvePackageImports(imports, "#utils", Conditions);
        Assert.Equal("./src/utils-esm.ts", result);
    }

    [Fact]
    public void ImportsResolution_WildcardPattern()
    {
        var imports = Parse("""{ "#internal/*": "./src/internal/*.ts" }""");
        var result = ExportsResolver.ResolvePackageImports(imports, "#internal/foo", Conditions);
        Assert.Equal("./src/internal/foo.ts", result);
    }

    [Fact]
    public void ParsePackageSpecifier_Unscoped_NoSubpath()
    {
        var (name, subpath) = ModuleResolver.ParsePackageSpecifier("lodash");
        Assert.Equal("lodash", name);
        Assert.Equal(".", subpath);
    }

    [Fact]
    public void ParsePackageSpecifier_Unscoped_WithSubpath()
    {
        var (name, subpath) = ModuleResolver.ParsePackageSpecifier("lodash/fp");
        Assert.Equal("lodash", name);
        Assert.Equal("./fp", subpath);
    }

    [Fact]
    public void ParsePackageSpecifier_Scoped_NoSubpath()
    {
        var (name, subpath) = ModuleResolver.ParsePackageSpecifier("@scope/pkg");
        Assert.Equal("@scope/pkg", name);
        Assert.Equal(".", subpath);
    }

    [Fact]
    public void ParsePackageSpecifier_Scoped_WithSubpath()
    {
        var (name, subpath) = ModuleResolver.ParsePackageSpecifier("@scope/pkg/utils");
        Assert.Equal("@scope/pkg", name);
        Assert.Equal("./utils", subpath);
    }

    [Fact]
    public void RootSubpath_WithSubpathMap_ReturnsCorrectEntry()
    {
        var exports = Parse("""{ ".": "./lib/index.ts", "./foo": "./lib/foo.ts" }""");
        var result = ExportsResolver.ResolvePackageExports(exports, ".", Conditions);
        Assert.Equal("./lib/index.ts", result);
    }

    [Fact]
    public void ConditionsObject_InsideSubpathMap()
    {
        var exports = Parse("""{ ".": { "types": "./types/index.d.ts", "import": "./esm/index.ts", "default": "./cjs/index.ts" } }""");
        var result = ExportsResolver.ResolvePackageExports(exports, ".", Conditions);
        // "types" not in runtime conditions, "import" matches first.
        Assert.Equal("./esm/index.ts", result);
    }

    [Fact]
    public void ArrayExports_RootSubpath()
    {
        var exports = Parse("""["./index.ts"]""");
        var result = ExportsResolver.ResolvePackageExports(exports, ".", Conditions);
        Assert.Equal("./index.ts", result);
    }

    [Fact]
    public void ArrayExports_NonRootSubpath_ReturnsNull()
    {
        var exports = Parse("""["./index.ts"]""");
        var result = ExportsResolver.ResolvePackageExports(exports, "./foo", Conditions);
        Assert.Null(result);
    }
}
