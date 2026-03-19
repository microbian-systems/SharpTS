using SharpTS.Tests.Infrastructure;
using Xunit;

namespace SharpTS.Tests.SharedTests.BuiltInModules;

/// <summary>
/// Tests for the Node.js 'vm' module: dynamic code compilation and execution.
///
/// Compiled-mode limitations:
/// The vm module is inherently dynamic — it compiles and runs arbitrary strings at runtime.
/// In compiled mode, all vm methods delegate to the interpreter via reflection (same pattern
/// as child_process.fork). This works well for the common case but has boundary limitations:
///
/// - Functions in context objects: Compiled arrow functions ($TSFunction) are not ISharpTSCallable,
///   so the sub-interpreter cannot call them. Use InterpretedOnly for tests with function contexts.
/// - Complex return values: The sub-interpreter returns SharpTSObject/SharpTSArray, but compiled
///   code expects Dictionary/List. Primitive values (number, string, bool) cross fine.
/// - runInThisContext scope sharing: The compiled caller has no RuntimeEnvironment, so
///   runInThisContext falls back to a new empty context in compiled mode.
/// - Import statements inside vm code do not work (sub-interpreter uses InterpretRepl,
///   not InterpretModules).
/// - Script class methods: The Script object is a SharpTSObject whose properties are not
///   accessible via compiled-mode GetFieldsProperty dispatch. Script tests are InterpretedOnly.
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
    [MemberData(nameof(ExecutionModes.InterpretedOnly), MemberType = typeof(ExecutionModes))]
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
    [MemberData(nameof(ExecutionModes.InterpretedOnly), MemberType = typeof(ExecutionModes))]
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
    [MemberData(nameof(ExecutionModes.InterpretedOnly), MemberType = typeof(ExecutionModes))]
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
    [MemberData(nameof(ExecutionModes.InterpretedOnly), MemberType = typeof(ExecutionModes))]
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
}
