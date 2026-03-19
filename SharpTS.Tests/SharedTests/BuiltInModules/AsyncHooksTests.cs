using SharpTS.Tests.Infrastructure;
using Xunit;

namespace SharpTS.Tests.SharedTests.BuiltInModules;

/// <summary>
/// Tests for the async_hooks module AsyncLocalStorage class.
/// </summary>
public class AsyncHooksTests
{
    #region Import Patterns

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void AsyncHooks_NamedImport(ExecutionMode mode)
    {
        var files = new Dictionary<string, string>
        {
            ["main.ts"] = """
                import { AsyncLocalStorage } from 'async_hooks';
                const als = new AsyncLocalStorage();
                console.log(typeof als === 'object');
                """
        };

        var output = TestHarness.RunModules(files, "./main.ts", mode);
        Assert.Equal("true\n", output);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void AsyncHooks_NamespaceImport(ExecutionMode mode)
    {
        var files = new Dictionary<string, string>
        {
            ["main.ts"] = """
                import * as async_hooks from 'async_hooks';
                const als = new async_hooks.AsyncLocalStorage();
                console.log(typeof als === 'object');
                """
        };

        var output = TestHarness.RunModules(files, "./main.ts", mode);
        Assert.Equal("true\n", output);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void AsyncHooks_NodePrefixImport(ExecutionMode mode)
    {
        var files = new Dictionary<string, string>
        {
            ["main.ts"] = """
                import { AsyncLocalStorage } from 'node:async_hooks';
                const als = new AsyncLocalStorage();
                console.log(typeof als === 'object');
                """
        };

        var output = TestHarness.RunModules(files, "./main.ts", mode);
        Assert.Equal("true\n", output);
    }

    #endregion

    #region Constructor

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void AsyncLocalStorage_Constructor(ExecutionMode mode)
    {
        var files = new Dictionary<string, string>
        {
            ["main.ts"] = """
                import { AsyncLocalStorage } from 'async_hooks';
                const als = new AsyncLocalStorage();
                console.log(als !== null);
                console.log(als !== undefined);
                """
        };

        var output = TestHarness.RunModules(files, "./main.ts", mode);
        Assert.Equal("true\ntrue\n", output);
    }

    #endregion

    #region getStore

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void AsyncLocalStorage_GetStore_ReturnsUndefined_Outside(ExecutionMode mode)
    {
        var files = new Dictionary<string, string>
        {
            ["main.ts"] = """
                import { AsyncLocalStorage } from 'async_hooks';
                const als = new AsyncLocalStorage();
                console.log(als.getStore() == undefined);
                """
        };

        var output = TestHarness.RunModules(files, "./main.ts", mode);
        Assert.Equal("true\n", output);
    }

    #endregion

    #region run

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void AsyncLocalStorage_Run_SetsStore(ExecutionMode mode)
    {
        var files = new Dictionary<string, string>
        {
            ["main.ts"] = """
                import { AsyncLocalStorage } from 'async_hooks';
                const als = new AsyncLocalStorage();
                als.run("hello", () => {
                    console.log(als.getStore());
                });
                """
        };

        var output = TestHarness.RunModules(files, "./main.ts", mode);
        Assert.Equal("hello\n", output);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void AsyncLocalStorage_Run_RestoresAfter(ExecutionMode mode)
    {
        var files = new Dictionary<string, string>
        {
            ["main.ts"] = """
                import { AsyncLocalStorage } from 'async_hooks';
                const als = new AsyncLocalStorage();
                als.run("inside", () => {});
                console.log(als.getStore() == undefined);
                """
        };

        var output = TestHarness.RunModules(files, "./main.ts", mode);
        Assert.Equal("true\n", output);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void AsyncLocalStorage_Run_ReturnsCallbackResult(ExecutionMode mode)
    {
        var files = new Dictionary<string, string>
        {
            ["main.ts"] = """
                import { AsyncLocalStorage } from 'async_hooks';
                const als = new AsyncLocalStorage();
                const result = als.run("store", () => 42);
                console.log(result);
                """
        };

        var output = TestHarness.RunModules(files, "./main.ts", mode);
        Assert.Equal("42\n", output);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void AsyncLocalStorage_Run_NestedRuns(ExecutionMode mode)
    {
        var files = new Dictionary<string, string>
        {
            ["main.ts"] = """
                import { AsyncLocalStorage } from 'async_hooks';
                const als = new AsyncLocalStorage();
                als.run("outer", () => {
                    console.log(als.getStore());
                    als.run("inner", () => {
                        console.log(als.getStore());
                    });
                    console.log(als.getStore());
                });
                """
        };

        var output = TestHarness.RunModules(files, "./main.ts", mode);
        Assert.Equal("outer\ninner\nouter\n", output);
    }

    #endregion

    #region enterWith

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void AsyncLocalStorage_EnterWith_SetsStore(ExecutionMode mode)
    {
        var files = new Dictionary<string, string>
        {
            ["main.ts"] = """
                import { AsyncLocalStorage } from 'async_hooks';
                const als = new AsyncLocalStorage();
                als.enterWith("entered");
                console.log(als.getStore());
                """
        };

        var output = TestHarness.RunModules(files, "./main.ts", mode);
        Assert.Equal("entered\n", output);
    }

    #endregion

    #region exit

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void AsyncLocalStorage_Exit_ClearsStore(ExecutionMode mode)
    {
        var files = new Dictionary<string, string>
        {
            ["main.ts"] = """
                import { AsyncLocalStorage } from 'async_hooks';
                const als = new AsyncLocalStorage();
                als.run("active", () => {
                    als.exit(() => {
                        console.log(als.getStore() == undefined);
                    });
                    console.log(als.getStore());
                });
                """
        };

        var output = TestHarness.RunModules(files, "./main.ts", mode);
        Assert.Equal("true\nactive\n", output);
    }

    #endregion

    #region disable

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void AsyncLocalStorage_Disable_PreventsAccess(ExecutionMode mode)
    {
        var files = new Dictionary<string, string>
        {
            ["main.ts"] = """
                import { AsyncLocalStorage } from 'async_hooks';
                const als = new AsyncLocalStorage();
                als.run("data", () => {
                    console.log(als.getStore());
                    als.disable();
                    console.log(als.getStore() == undefined);
                });
                """
        };

        var output = TestHarness.RunModules(files, "./main.ts", mode);
        Assert.Equal("data\ntrue\n", output);
    }

    #endregion

    #region Multiple Instances

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void AsyncLocalStorage_MultipleInstances_Independent(ExecutionMode mode)
    {
        var files = new Dictionary<string, string>
        {
            ["main.ts"] = """
                import { AsyncLocalStorage } from 'async_hooks';
                const als1 = new AsyncLocalStorage();
                const als2 = new AsyncLocalStorage();
                als1.run("first", () => {
                    als2.run("second", () => {
                        console.log(als1.getStore());
                        console.log(als2.getStore());
                    });
                });
                """
        };

        var output = TestHarness.RunModules(files, "./main.ts", mode);
        Assert.Equal("first\nsecond\n", output);
    }

    #endregion

    // Note: Async context propagation tests (across await, Promise.then, setTimeout) require
    // deeper integration with ExecutionContext flow in the timer and promise infrastructure.
    // These will be added when the event loop properly captures and restores ExecutionContext.

    #region Store with Object Values

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void AsyncLocalStorage_Run_WithObjectStore(ExecutionMode mode)
    {
        var files = new Dictionary<string, string>
        {
            ["main.ts"] = """
                import { AsyncLocalStorage } from 'async_hooks';
                const als = new AsyncLocalStorage();
                als.run({ requestId: "abc-123" }, () => {
                    const store = als.getStore();
                    console.log(store.requestId);
                });
                """
        };

        var output = TestHarness.RunModules(files, "./main.ts", mode);
        Assert.Equal("abc-123\n", output);
    }

    #endregion
}
