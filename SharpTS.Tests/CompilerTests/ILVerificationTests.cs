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
    public void FunctionReturningStringConcat_PassesILVerification()
    {
        // A `: string` function whose body produces a runtime-helper result (string concat
        // or member get) previously left an `object` on the stack where the verifier expected
        // a string, raising StackUnexpected even though it ran correctly. (#275)
        var source = """
            function cat(a: string): string { return "hi " + a; }
            function viaLocal(a: string): string { let r = "x" + a; return r; }
            function withNumber(a: number): string { return "val=" + a; }
            interface Named { name: string; }
            function pick(n: Named): string { return n.name; }
            console.log(cat("z"));
            console.log(viaLocal("y"));
            console.log(withNumber(42));
            console.log(pick({ name: "w" }));
            """;

        var (errors, output) = TestHarness.CompileVerifyAndRun(source);

        Assert.Empty(errors);
        Assert.Equal("hi z\nxy\nval=42\nw\n", output);
    }

    [Fact]
    public void FunctionReturningTypedCollection_PassesILVerification()
    {
        // A function declared `: number[]` (or Map/Set, or async Promise<T[]>) maps its return
        // slot to List<T>/Dictionary<,>/HashSet<>, but the runtime value is a dynamic
        // $Array/TSMap/TSSet carried as object — not CLR-assignable to the declared collection.
        // That left an object on the stack where the verifier expected the collection, raising
        // StackUnexpected even though it ran correctly. The return type now falls back to object. (#278)
        var source = """
            function nums(): number[] { return [1, 2, 3]; }
            function strs(): string[] { return ["a", "b"]; }
            function mkMap(): Map<string, number> { const m = new Map<string, number>(); m.set("x", 1); return m; }
            function mkSet(): Set<number> { const s = new Set<number>(); s.add(5); return s; }
            class Box { getNums(): number[] { return [7, 8]; } }
            console.log(nums().length);
            console.log(strs().join(","));
            console.log(mkMap().get("x"));
            console.log(mkSet().has(5));
            console.log(new Box().getNums().length);
            """;

        var (errors, output) = TestHarness.CompileVerifyAndRun(source);

        Assert.Empty(errors);
        Assert.Equal("3\na,b\n1\ntrue\n2\n", output);
    }

    [Fact]
    public void ClassGetPropertyWithTypedGetter_PassesILVerification()
    {
        // The compiler-generated GetProperty dispatch helper invokes a typed getter (e.g. the
        // auto getter for a `public x: number` parameter property, or an explicit `get`) and
        // returned its value-type result into GetProperty's object slot without boxing — the
        // verifier reported StackUnexpected even though it ran. Generic-class getters returning a
        // type parameter had the same gap. The getter result is now boxed. (#279)
        var source = """
            class Foo { constructor(public x: number) {} }
            class Bar {
                private _v: number = 10;
                get doubled(): number { return this._v * 2; }
                get label(): string { return "bar"; }
                get flag(): boolean { return true; }
            }
            class Box<T> {
                constructor(public item: T) {}
                get value(): T { return this.item; }
            }
            function mkFoo(): Foo { return new Foo(7); }
            const b = new Bar();
            console.log(mkFoo().x);
            console.log(b.doubled);
            console.log(b.label);
            console.log(b.flag);
            console.log(new Box<number>(99).value);
            console.log(new Box<string>("hi").value);
            """;

        var (errors, output) = TestHarness.CompileVerifyAndRun(source);

        Assert.Empty(errors);
        Assert.Equal("7\n20\nbar\ntrue\n99\nhi\n", output);
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
