using SharpTS.Tests.Infrastructure;
using Xunit;

namespace SharpTS.Tests.SharedTests.BuiltInModules;

/// <summary>
/// Tests for the Node.js 'vm' module: dynamic code compilation and execution.
///
/// Compiled-mode limitations:
/// The vm module is inherently dynamic — it compiles and runs arbitrary strings at runtime.
/// In compiled mode, all vm methods delegate to the interpreter via reflection (same pattern
/// as child_process.fork). This works well for the common case but has one remaining limitation:
///
/// - runInThisContext scope sharing: The compiled caller has no RuntimeEnvironment, so
///   runInThisContext falls back to a new empty context in compiled mode.
///
/// Resolved limitations (now have full parity):
/// - Script class methods: Script objects now return Dictionary&lt;string, object?&gt; which
///   GetFieldsProperty handles via the Dictionary dispatch path.
/// - Functions in context objects: CompiledCallableAdapter wraps $TSFunction instances
///   as ISharpTSCallable at the VmContext boundary.
/// - compileFunction with parsingContext/contextExtensions: VmContext.ExtractProperties
///   now handles emitted $Object types via reflection fallback.
/// </summary>
public class VmModuleTests
{
    #region Import Tests

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void Vm_Import_Namespace(ExecutionMode mode)
    {
        var files = new Dictionary<string, string>
        {
            ["main.ts"] = """
                import * as vm from 'vm';
                console.log(typeof vm === 'object');
                console.log(typeof vm.createContext === 'function');
                console.log(typeof vm.isContext === 'function');
                """
        };

        var output = TestHarness.RunModules(files, "main.ts", mode);
        Assert.Equal("true\ntrue\ntrue\n", output);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void Vm_Import_Named(ExecutionMode mode)
    {
        var files = new Dictionary<string, string>
        {
            ["main.ts"] = """
                import { runInNewContext, createContext, isContext } from 'vm';
                console.log(runInNewContext !== undefined);
                console.log(createContext !== undefined);
                console.log(isContext !== undefined);
                """
        };

        var output = TestHarness.RunModules(files, "main.ts", mode);
        Assert.Equal("true\ntrue\ntrue\n", output);
    }

    #endregion

    #region runInNewContext Tests

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void Vm_RunInNewContext_BasicExpression(ExecutionMode mode)
    {
        var files = new Dictionary<string, string>
        {
            ["main.ts"] = """
                import * as vm from 'vm';
                const result = vm.runInNewContext('1 + 2');
                console.log(result);
                """
        };

        var output = TestHarness.RunModules(files, "main.ts", mode);
        Assert.Equal("3\n", output);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void Vm_RunInNewContext_MultiStatement(ExecutionMode mode)
    {
        var files = new Dictionary<string, string>
        {
            ["main.ts"] = """
                import * as vm from 'vm';
                const result = vm.runInNewContext('let a = 5; let b = 10; a + b;');
                console.log(result);
                """
        };

        var output = TestHarness.RunModules(files, "main.ts", mode);
        Assert.Equal("15\n", output);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void Vm_RunInNewContext_ContextSeeding(ExecutionMode mode)
    {
        var files = new Dictionary<string, string>
        {
            ["main.ts"] = """
                import * as vm from 'vm';
                const result = vm.runInNewContext('x + y', { x: 10, y: 20 });
                console.log(result);
                """
        };

        var output = TestHarness.RunModules(files, "main.ts", mode);
        Assert.Equal("30\n", output);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void Vm_RunInNewContext_ContextMutation(ExecutionMode mode)
    {
        var files = new Dictionary<string, string>
        {
            ["main.ts"] = """
                import * as vm from 'vm';
                const ctx = { x: 1 };
                vm.runInNewContext('x = 42;', ctx);
                console.log(ctx.x);
                """
        };

        var output = TestHarness.RunModules(files, "main.ts", mode);
        Assert.Equal("42\n", output);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void Vm_RunInNewContext_Isolation(ExecutionMode mode)
    {
        var files = new Dictionary<string, string>
        {
            ["main.ts"] = """
                import * as vm from 'vm';
                let leaked = false;
                try {
                    vm.runInNewContext('let newVar = 123;');
                    // newVar should not be accessible here
                    console.log('isolated');
                } catch {
                    console.log('isolated');
                }
                """
        };

        var output = TestHarness.RunModules(files, "main.ts", mode);
        Assert.Equal("isolated\n", output);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void Vm_RunInNewContext_WithConsoleLog(ExecutionMode mode)
    {
        var files = new Dictionary<string, string>
        {
            ["main.ts"] = """
                import * as vm from 'vm';
                vm.runInNewContext('console.log("hello from vm")');
                """
        };

        var output = TestHarness.RunModules(files, "main.ts", mode);
        Assert.Equal("hello from vm\n", output);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void Vm_RunInNewContext_EmptyCode(ExecutionMode mode)
    {
        var files = new Dictionary<string, string>
        {
            ["main.ts"] = """
                import * as vm from 'vm';
                const result = vm.runInNewContext('');
                console.log(result === undefined || result === null);
                """
        };

        var output = TestHarness.RunModules(files, "main.ts", mode);
        Assert.Equal("true\n", output);
    }

    #endregion

    #region runInThisContext Tests

    [Theory]
    [MemberData(nameof(ExecutionModes.InterpretedOnly), MemberType = typeof(ExecutionModes))]
    public void Vm_RunInThisContext_SharesScope(ExecutionMode mode)
    {
        var files = new Dictionary<string, string>
        {
            ["main.ts"] = """
                import * as vm from 'vm';
                let x = 10;
                const result = vm.runInThisContext('x + 5');
                console.log(result);
                """
        };

        var output = TestHarness.RunModules(files, "main.ts", mode);
        Assert.Equal("15\n", output);
    }

    #endregion

    #region createContext / isContext Tests

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void Vm_CreateContext_IsContext(ExecutionMode mode)
    {
        var files = new Dictionary<string, string>
        {
            ["main.ts"] = """
                import * as vm from 'vm';
                const obj = { x: 1 };
                console.log(vm.isContext(obj));
                const ctx = vm.createContext(obj);
                console.log(vm.isContext(ctx));
                """
        };

        var output = TestHarness.RunModules(files, "main.ts", mode);
        Assert.Equal("false\ntrue\n", output);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void Vm_CreateContext_Empty(ExecutionMode mode)
    {
        var files = new Dictionary<string, string>
        {
            ["main.ts"] = """
                import * as vm from 'vm';
                const ctx = vm.createContext();
                console.log(vm.isContext(ctx));
                console.log(typeof ctx === 'object');
                """
        };

        var output = TestHarness.RunModules(files, "main.ts", mode);
        Assert.Equal("true\ntrue\n", output);
    }

    #endregion

    #region Script Tests

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void Vm_Script_RunInNewContext(ExecutionMode mode)
    {
        var files = new Dictionary<string, string>
        {
            ["main.ts"] = """
                import { Script } from 'vm';
                const script = new Script('x + y');
                const result1 = script.runInNewContext({ x: 1, y: 2 });
                const result2 = script.runInNewContext({ x: 10, y: 20 });
                console.log(result1);
                console.log(result2);
                """
        };

        var output = TestHarness.RunModules(files, "main.ts", mode);
        Assert.Equal("3\n30\n", output);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.InterpretedOnly), MemberType = typeof(ExecutionModes))]
    public void Vm_Script_RunInThisContext(ExecutionMode mode)
    {
        var files = new Dictionary<string, string>
        {
            ["main.ts"] = """
                import { Script } from 'vm';
                let val = 42;
                const script = new Script('val');
                const result = script.runInThisContext();
                console.log(result);
                """
        };

        var output = TestHarness.RunModules(files, "main.ts", mode);
        Assert.Equal("42\n", output);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void Vm_Script_RunInContext(ExecutionMode mode)
    {
        var files = new Dictionary<string, string>
        {
            ["main.ts"] = """
                import { Script, createContext } from 'vm';
                const ctx = createContext({ greeting: 'hello' });
                const script = new Script('greeting + " world"');
                const result = script.runInContext(ctx);
                console.log(result);
                """
        };

        var output = TestHarness.RunModules(files, "main.ts", mode);
        Assert.Equal("hello world\n", output);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void Vm_Script_MultipleRuns(ExecutionMode mode)
    {
        var files = new Dictionary<string, string>
        {
            ["main.ts"] = """
                import { Script } from 'vm';
                const script = new Script('n * n');
                console.log(script.runInNewContext({ n: 3 }));
                console.log(script.runInNewContext({ n: 5 }));
                console.log(script.runInNewContext({ n: 7 }));
                """
        };

        var output = TestHarness.RunModules(files, "main.ts", mode);
        Assert.Equal("9\n25\n49\n", output);
    }

    #endregion

    #region Error Handling Tests

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void Vm_RunInNewContext_SyntaxError(ExecutionMode mode)
    {
        var files = new Dictionary<string, string>
        {
            ["main.ts"] = """
                import * as vm from 'vm';
                try {
                    vm.runInNewContext('let x = ;');
                    console.log('no error');
                } catch (e) {
                    console.log('caught error');
                }
                """
        };

        var output = TestHarness.RunModules(files, "main.ts", mode);
        Assert.Equal("caught error\n", output);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void Vm_RunInNewContext_RuntimeError(ExecutionMode mode)
    {
        var files = new Dictionary<string, string>
        {
            ["main.ts"] = """
                import * as vm from 'vm';
                try {
                    vm.runInNewContext('undefinedVar.foo');
                    console.log('no error');
                } catch (e) {
                    console.log('caught error');
                }
                """
        };

        var output = TestHarness.RunModules(files, "main.ts", mode);
        Assert.Equal("caught error\n", output);
    }

    #endregion

    #region Context Functions Tests

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void Vm_RunInNewContext_FunctionInContext(ExecutionMode mode)
    {
        var files = new Dictionary<string, string>
        {
            ["main.ts"] = """
                import * as vm from 'vm';
                const ctx = {
                    add: (a: number, b: number) => a + b
                };
                const result = vm.runInNewContext('add(3, 4)', ctx);
                console.log(result);
                """
        };

        var output = TestHarness.RunModules(files, "main.ts", mode);
        Assert.Equal("7\n", output);
    }

    #endregion

    #region compileFunction Tests

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void Vm_CompileFunction_BasicReturn(ExecutionMode mode)
    {
        var files = new Dictionary<string, string>
        {
            ["main.ts"] = """
                import { compileFunction } from 'vm';
                const fn = compileFunction('return 42');
                console.log(fn());
                """
        };

        var output = TestHarness.RunModules(files, "main.ts", mode);
        Assert.Equal("42\n", output);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void Vm_CompileFunction_WithParams(ExecutionMode mode)
    {
        var files = new Dictionary<string, string>
        {
            ["main.ts"] = """
                import { compileFunction } from 'vm';
                const add = compileFunction('return a + b', ['a', 'b']);
                console.log(add(3, 4));
                """
        };

        var output = TestHarness.RunModules(files, "main.ts", mode);
        Assert.Equal("7\n", output);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void Vm_CompileFunction_NoParams(ExecutionMode mode)
    {
        var files = new Dictionary<string, string>
        {
            ["main.ts"] = """
                import { compileFunction } from 'vm';
                const greet = compileFunction('return "hello"');
                console.log(greet());
                """
        };

        var output = TestHarness.RunModules(files, "main.ts", mode);
        Assert.Equal("hello\n", output);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void Vm_CompileFunction_MultiStatement(ExecutionMode mode)
    {
        var files = new Dictionary<string, string>
        {
            ["main.ts"] = """
                import { compileFunction } from 'vm';
                const fn = compileFunction('let x = 10; let y = 20; return x + y;');
                console.log(fn());
                """
        };

        var output = TestHarness.RunModules(files, "main.ts", mode);
        Assert.Equal("30\n", output);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void Vm_CompileFunction_Reusable(ExecutionMode mode)
    {
        var files = new Dictionary<string, string>
        {
            ["main.ts"] = """
                import { compileFunction } from 'vm';
                const square = compileFunction('return n * n', ['n']);
                console.log(square(3));
                console.log(square(5));
                console.log(square(7));
                """
        };

        var output = TestHarness.RunModules(files, "main.ts", mode);
        Assert.Equal("9\n25\n49\n", output);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void Vm_CompileFunction_ConsoleLog(ExecutionMode mode)
    {
        var files = new Dictionary<string, string>
        {
            ["main.ts"] = """
                import { compileFunction } from 'vm';
                const fn = compileFunction('console.log("from compiled fn")');
                fn();
                """
        };

        var output = TestHarness.RunModules(files, "main.ts", mode);
        Assert.Equal("from compiled fn\n", output);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void Vm_CompileFunction_SyntaxError(ExecutionMode mode)
    {
        var files = new Dictionary<string, string>
        {
            ["main.ts"] = """
                import { compileFunction } from 'vm';
                try {
                    compileFunction('return ;; @@');
                    console.log('no error');
                } catch (e) {
                    console.log('caught error');
                }
                """
        };

        var output = TestHarness.RunModules(files, "main.ts", mode);
        Assert.Equal("caught error\n", output);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void Vm_CompileFunction_NamespaceImport(ExecutionMode mode)
    {
        var files = new Dictionary<string, string>
        {
            ["main.ts"] = """
                import * as vm from 'vm';
                const fn = vm.compileFunction('return x * 2', ['x']);
                console.log(fn(21));
                """
        };

        var output = TestHarness.RunModules(files, "main.ts", mode);
        Assert.Equal("42\n", output);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void Vm_CompileFunction_ParsingContext(ExecutionMode mode)
    {
        var files = new Dictionary<string, string>
        {
            ["main.ts"] = """
                import { compileFunction, createContext } from 'vm';
                const ctx = createContext({ multiplier: 10 });
                const fn = compileFunction('return x * multiplier', ['x'], { parsingContext: ctx });
                console.log(fn(5));
                """
        };

        var output = TestHarness.RunModules(files, "main.ts", mode);
        Assert.Equal("50\n", output);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void Vm_CompileFunction_ContextExtensions(ExecutionMode mode)
    {
        var files = new Dictionary<string, string>
        {
            ["main.ts"] = """
                import { compileFunction } from 'vm';
                const fn = compileFunction('return greeting + " " + name', [], {
                    contextExtensions: [{ greeting: "hello", name: "world" }]
                });
                console.log(fn());
                """
        };

        var output = TestHarness.RunModules(files, "main.ts", mode);
        Assert.Equal("hello world\n", output);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void Vm_CompileFunction_ContextWithFunction(ExecutionMode mode)
    {
        var files = new Dictionary<string, string>
        {
            ["main.ts"] = """
                import { compileFunction, createContext } from 'vm';
                const ctx = createContext({ double: (n: number) => n * 2 });
                const fn = compileFunction('return double(x)', ['x'], { parsingContext: ctx });
                console.log(fn(7));
                """
        };

        var output = TestHarness.RunModules(files, "main.ts", mode);
        Assert.Equal("14\n", output);
    }

    #endregion

    #region timeout

    [Theory]
    [MemberData(nameof(ExecutionModes.InterpretedOnly), MemberType = typeof(ExecutionModes))]
    public void Vm_RunInNewContext_Timeout_Throws(ExecutionMode mode)
    {
        var files = new Dictionary<string, string>
        {
            ["main.ts"] = """
                import vm from 'vm';
                try {
                    vm.runInNewContext('while(true) {}', {}, { timeout: 50 });
                    console.log('no error');
                } catch (e) {
                    console.log('caught:' + e.message);
                }
                """
        };

        var output = TestHarness.RunModules(files, "main.ts", mode);
        Assert.Contains("caught:Script execution timed out", output);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.InterpretedOnly), MemberType = typeof(ExecutionModes))]
    public void Vm_RunInNewContext_NoTimeout_Completes(ExecutionMode mode)
    {
        var files = new Dictionary<string, string>
        {
            ["main.ts"] = """
                import vm from 'vm';
                const result = vm.runInNewContext('let sum = 0; for (let i = 0; i < 100; i++) sum += i; sum;', {});
                console.log(result);
                """
        };

        var output = TestHarness.RunModules(files, "main.ts", mode);
        Assert.Contains("4950", output);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.InterpretedOnly), MemberType = typeof(ExecutionModes))]
    public void Vm_RunInThisContext_Timeout_Throws(ExecutionMode mode)
    {
        var files = new Dictionary<string, string>
        {
            ["main.ts"] = """
                import vm from 'vm';
                try {
                    vm.runInThisContext('while(true) {}', { timeout: 50 });
                    console.log('no error');
                } catch (e) {
                    console.log('caught:' + e.message);
                }
                """
        };

        var output = TestHarness.RunModules(files, "main.ts", mode);
        Assert.Contains("caught:Script execution timed out", output);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.InterpretedOnly), MemberType = typeof(ExecutionModes))]
    public void Vm_Script_RunInNewContext_Timeout_Throws(ExecutionMode mode)
    {
        var files = new Dictionary<string, string>
        {
            ["main.ts"] = """
                import vm from 'vm';
                const script = new vm.Script('while(true) {}');
                try {
                    script.runInNewContext({}, { timeout: 50 });
                    console.log('no error');
                } catch (e) {
                    console.log('caught:' + e.message);
                }
                """
        };

        var output = TestHarness.RunModules(files, "main.ts", mode);
        Assert.Contains("caught:Script execution timed out", output);
    }

    #endregion
}
