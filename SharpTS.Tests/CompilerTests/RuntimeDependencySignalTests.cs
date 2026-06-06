using SharpTS.Compilation;
using SharpTS.Parsing;
using SharpTS.TypeSystem;
using Xunit;

namespace SharpTS.Tests.CompilerTests;

/// <summary>
/// Verifies the compile-time signal that drives co-locating SharpTS.dll with compiled output:
/// <see cref="ILCompiler.RequiredSharpTSRuntimeReasons"/>. A program that uses none of the
/// SharpTS-runtime-backed features must stay fully standalone (empty reasons); programs that use
/// eval/Proxy/Intl/etc. must report the corresponding reason so the build copies the runtime.
/// </summary>
public class RuntimeDependencySignalTests
{
    private static IReadOnlyCollection<string> ReasonsFor(string source)
    {
        var tokens = new Lexer(source).ScanTokens();
        var statements = new Parser(tokens).ParseOrThrow();
        var typeMap = new TypeChecker().Check(statements);
        var deadCodeInfo = new DeadCodeAnalyzer(typeMap).Analyze(statements);

        var compiler = new ILCompiler("runtime_signal_test");
        compiler.Compile(statements, typeMap, deadCodeInfo);
        return compiler.RequiredSharpTSRuntimeReasons;
    }

    [Fact]
    public void TrivialProgram_RequiresNoRuntime()
    {
        var reasons = ReasonsFor("""
            const xs = [1, 2, 3].map(x => x * 2);
            console.log(xs.join(","));
            """);
        Assert.Empty(reasons);
    }

    [Fact]
    public void Eval_RequiresRuntime()
    {
        var reasons = ReasonsFor("""console.log(eval("1 + 2"));""");
        Assert.Contains("eval()", reasons);
    }

    [Fact]
    public void Proxy_RequiresRuntime()
    {
        var reasons = ReasonsFor("""
            const p: any = new Proxy({ a: 1 }, { get: (t, k) => 42 });
            console.log(p.a);
            """);
        Assert.Contains("Proxy", reasons);
    }

    [Fact]
    public void Intl_RequiresRuntime()
    {
        var reasons = ReasonsFor("""
            const nf = new Intl.NumberFormat("en-US");
            console.log(nf.format(1234.5));
            """);
        Assert.Contains("Intl", reasons);
    }

    [Fact]
    public void Eval_DoesNotFalselyReportOtherFeatures()
    {
        var reasons = ReasonsFor("""console.log(eval("1 + 2"));""");
        Assert.DoesNotContain("Proxy", reasons);
        Assert.DoesNotContain("Intl", reasons);
        Assert.DoesNotContain("vm module", reasons);
    }
}
