using SharpTS.Tests.Infrastructure;
using Xunit;
using Xunit.Abstractions;

namespace SharpTS.Tests.CompilerTests;

/// <summary>
/// Tests that verify compiled assemblies produce valid IL.
/// These tests catch IL generation bugs at test time rather than runtime.
/// </summary>
public class ILVerificationTests
{
    [Fact]
    public void BasicArithmetic_PassesILVerification()
    {
        var source = """
            let x = 10 + 5;
            console.log(x);
            """;

        var (errors, output) = TestHarness.CompileVerifyAndRun(source);

        Assert.Empty(errors);
        Assert.Equal("15\n", output);
    }

    [Fact]
    public void ClassWithMethods_PassesILVerification()
    {
        var source = """
            class Calculator {
                add(a: number, b: number): number {
                    return a + b;
                }
            }
            let calc = new Calculator();
            console.log(calc.add(3, 4));
            """;

        var (errors, output) = TestHarness.CompileVerifyAndRun(source);

        Assert.Empty(errors);
        Assert.Equal("7\n", output);
    }

    [Fact]
    public void AsyncAwait_PassesILVerification()
    {
        var source = """
            async function getValue(): Promise<number> {
                return 42;
            }

            async function main() {
                let result = await getValue();
                console.log(result);
            }

            main();
            """;

        var (errors, output) = TestHarness.CompileVerifyAndRun(source);

        Assert.Empty(errors);
        Assert.Equal("42\n", output);
    }

    [Fact]
    public void Closures_PassesILVerification()
    {
        var source = """
            function makeCounter(): () => number {
                let count = 0;
                return () => {
                    count = count + 1;
                    return count;
                };
            }

            let counter = makeCounter();
            console.log(counter());
            console.log(counter());
            """;

        // For now, just verify IL without running (runtime has issues with rewritten assemblies)
        var errors = TestHarness.CompileAndVerifyOnly(source);

        Assert.Empty(errors);
    }

    [Fact]
    public void Inheritance_PassesILVerification()
    {
        var source = """
            class Animal {
                speak(): string {
                    return "...";
                }
            }

            class Dog extends Animal {
                speak(): string {
                    return "Woof!";
                }
            }

            let dog = new Dog();
            console.log(dog.speak());
            """;

        var (errors, output) = TestHarness.CompileVerifyAndRun(source);

        Assert.Empty(errors);
        Assert.Equal("Woof!\n", output);
    }

    [Fact]
    public void Generators_PassesILVerification()
    {
        var source = """
            function* range(start: number, end: number) {
                for (let i = start; i < end; i = i + 1) {
                    yield i;
                }
            }

            for (let n of range(1, 4)) {
                console.log(n);
            }
            """;

        var (errors, output) = TestHarness.CompileVerifyAndRun(source);

        Assert.Empty(errors);
        Assert.Equal("1\n2\n3\n", output);
    }

    // Regression test for issue #189: CLI `--compile … --verify` output is NOT
    // rewritten to reference-assembly facades (it binds to System.Private.CoreLib),
    // and verifying it against the ref-assembly universe produced thousands of
    // false StackUnexpected/ThrowOrCatchOnlyExceptionType errors.
    [Fact]
    public void CoreLibBoundOutput_PassesILVerification()
    {
        var source = """
            let arr = [1, 2, 3];
            let doubled = arr.map(x => x * 2);
            console.log(doubled);
            """;

        var tempDir = Path.Combine(Path.GetTempPath(), $"sharpts_test_{Guid.NewGuid()}");
        Directory.CreateDirectory(tempDir);
        try
        {
            var dllPath = Path.Combine(tempDir, "test.dll");

            var lexer = new SharpTS.Parsing.Lexer(source);
            var tokens = lexer.ScanTokens();
            var parser = new SharpTS.Parsing.Parser(tokens);
            var statements = parser.ParseOrThrow();

            var checker = new SharpTS.TypeSystem.TypeChecker();
            var typeMap = checker.Check(statements);

            var deadCodeAnalyzer = new SharpTS.Compilation.DeadCodeAnalyzer(typeMap);
            var deadCodeInfo = deadCodeAnalyzer.Analyze(statements);

            // useReferenceAssemblies: false — same shape as plain CLI --compile output
            var compiler = new SharpTS.Compilation.ILCompiler("test", preserveConstEnums: false, useReferenceAssemblies: false, sdkPath: null);
            compiler.Compile(statements, typeMap, deadCodeInfo);
            compiler.Save(dllPath);

            var errors = TestHarness.VerifyIL(dllPath);

            Assert.Empty(errors);
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { /* ignore cleanup errors */ }
        }
    }

    [Fact]
    public void TryCatchFinally_PassesILVerification()
    {
        var source = """
            function test(): string {
                try {
                    throw "test error";
                } catch (e) {
                    return "caught";
                } finally {
                    console.log("finally");
                }
            }

            console.log(test());
            """;

        var (errors, output) = TestHarness.CompileVerifyAndRun(source);

        Assert.Empty(errors);
        Assert.Equal("finally\ncaught\n", output);
    }
}
