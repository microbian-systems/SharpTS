using SharpTS.Tests.Infrastructure;
using Xunit;

namespace SharpTS.Tests.SharedTests.BuiltInModules;

/// <summary>
/// Tests for the Node.js 'timers/promises' module: promise-based setTimeout, setImmediate, setInterval.
/// </summary>
[Collection("TimerTests")]
public class TimersPromisesModuleTests
{
    #region Import Tests

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void TimersPromises_Import_Namespace(ExecutionMode mode)
    {
        var files = new Dictionary<string, string>
        {
            ["main.ts"] = """
                import * as timers from 'timers/promises';
                console.log(typeof timers === 'object');
                console.log(typeof timers.setTimeout === 'function');
                console.log(typeof timers.setImmediate === 'function');
                console.log(typeof timers.setInterval === 'function');
                """
        };

        var output = TestHarness.RunModules(files, "main.ts", mode);
        Assert.Equal("true\ntrue\ntrue\ntrue\n", output);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void TimersPromises_Import_Named(ExecutionMode mode)
    {
        var files = new Dictionary<string, string>
        {
            ["main.ts"] = """
                import { setTimeout, setImmediate } from 'timers/promises';
                console.log(typeof setTimeout === 'function');
                console.log(typeof setImmediate === 'function');
                """
        };

        var output = TestHarness.RunModules(files, "main.ts", mode);
        Assert.Equal("true\ntrue\n", output);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void TimersPromises_Import_NodePrefix(ExecutionMode mode)
    {
        var files = new Dictionary<string, string>
        {
            ["main.ts"] = """
                import { setTimeout } from 'node:timers/promises';
                console.log(typeof setTimeout === 'function');
                """
        };

        var output = TestHarness.RunModules(files, "main.ts", mode);
        Assert.Equal("true\n", output);
    }

    #endregion

    #region setTimeout Tests

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void TimersPromises_SetTimeout_ReturnsPromise(ExecutionMode mode)
    {
        var files = new Dictionary<string, string>
        {
            ["main.ts"] = """
                import { setTimeout } from 'timers/promises';
                const p = setTimeout(10);
                console.log(typeof p.then === 'function');
                """
        };

        var output = TestHarness.RunModules(files, "main.ts", mode);
        Assert.Equal("true\n", output);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void TimersPromises_SetTimeout_ResolvesWithValue(ExecutionMode mode)
    {
        var files = new Dictionary<string, string>
        {
            ["main.ts"] = """
                import { setTimeout } from 'timers/promises';
                async function main() {
                    const result = await setTimeout(10, 'hello');
                    console.log(result);
                }
                main();
                """
        };

        var output = TestHarness.RunModules(files, "main.ts", mode);
        Assert.Equal("hello\n", output);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void TimersPromises_SetTimeout_ResolvesWithNumericValue(ExecutionMode mode)
    {
        var files = new Dictionary<string, string>
        {
            ["main.ts"] = """
                import { setTimeout } from 'timers/promises';
                async function main() {
                    const result = await setTimeout(10, 42);
                    console.log(result);
                }
                main();
                """
        };

        var output = TestHarness.RunModules(files, "main.ts", mode);
        Assert.Equal("42\n", output);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void TimersPromises_SetTimeout_DefaultValue(ExecutionMode mode)
    {
        var files = new Dictionary<string, string>
        {
            ["main.ts"] = """
                import { setTimeout } from 'timers/promises';
                async function main() {
                    const result = await setTimeout(10);
                    console.log(result === undefined);
                }
                main();
                """
        };

        var output = TestHarness.RunModules(files, "main.ts", mode);
        Assert.Equal("true\n", output);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void TimersPromises_SetTimeout_ZeroDelay(ExecutionMode mode)
    {
        var files = new Dictionary<string, string>
        {
            ["main.ts"] = """
                import { setTimeout } from 'timers/promises';
                async function main() {
                    const result = await setTimeout(0, 'immediate');
                    console.log(result);
                }
                main();
                """
        };

        var output = TestHarness.RunModules(files, "main.ts", mode);
        Assert.Equal("immediate\n", output);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void TimersPromises_SetTimeout_ThenChaining(ExecutionMode mode)
    {
        var files = new Dictionary<string, string>
        {
            ["main.ts"] = """
                import { setTimeout } from 'timers/promises';
                setTimeout(10, 'world').then((val: string) => {
                    console.log('hello ' + val);
                });
                """
        };

        var output = TestHarness.RunModules(files, "main.ts", mode);
        Assert.Equal("hello world\n", output);
    }

    #endregion

    #region setImmediate Tests

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void TimersPromises_SetImmediate_ReturnsPromise(ExecutionMode mode)
    {
        var files = new Dictionary<string, string>
        {
            ["main.ts"] = """
                import { setImmediate } from 'timers/promises';
                const p = setImmediate();
                console.log(typeof p.then === 'function');
                """
        };

        var output = TestHarness.RunModules(files, "main.ts", mode);
        Assert.Equal("true\n", output);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void TimersPromises_SetImmediate_ResolvesWithValue(ExecutionMode mode)
    {
        var files = new Dictionary<string, string>
        {
            ["main.ts"] = """
                import { setImmediate } from 'timers/promises';
                async function main() {
                    const result = await setImmediate('quick');
                    console.log(result);
                }
                main();
                """
        };

        var output = TestHarness.RunModules(files, "main.ts", mode);
        Assert.Equal("quick\n", output);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void TimersPromises_SetImmediate_DefaultValue(ExecutionMode mode)
    {
        var files = new Dictionary<string, string>
        {
            ["main.ts"] = """
                import { setImmediate } from 'timers/promises';
                async function main() {
                    const result = await setImmediate();
                    console.log(result === undefined);
                }
                main();
                """
        };

        var output = TestHarness.RunModules(files, "main.ts", mode);
        Assert.Equal("true\n", output);
    }

    #endregion

    #region Multiple Timers

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void TimersPromises_MultipleSetTimeout_Sequential(ExecutionMode mode)
    {
        var files = new Dictionary<string, string>
        {
            ["main.ts"] = """
                import { setTimeout } from 'timers/promises';
                async function main() {
                    const a = await setTimeout(10, 'first');
                    const b = await setTimeout(10, 'second');
                    console.log(a);
                    console.log(b);
                }
                main();
                """
        };

        var output = TestHarness.RunModules(files, "main.ts", mode);
        Assert.Equal("first\nsecond\n", output);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void TimersPromises_SetTimeout_MixedWithSetImmediate(ExecutionMode mode)
    {
        var files = new Dictionary<string, string>
        {
            ["main.ts"] = """
                import { setTimeout, setImmediate } from 'timers/promises';
                async function main() {
                    const a = await setImmediate('fast');
                    const b = await setTimeout(10, 'slow');
                    console.log(a);
                    console.log(b);
                }
                main();
                """
        };

        var output = TestHarness.RunModules(files, "main.ts", mode);
        Assert.Equal("fast\nslow\n", output);
    }

    #endregion
}
