using SharpTS.Tests.Infrastructure;
using Xunit;

namespace SharpTS.Tests.SharedTests;

/// <summary>
/// Tests for the BroadcastChannel WHATWG/Node API. Runs against both the
/// tree-walking interpreter and the IL compiler.
/// </summary>
public class BroadcastChannelTests
{
    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void BroadcastChannel_HasName(ExecutionMode mode)
    {
        var source = @"
            const bc = new BroadcastChannel('chan-1');
            console.log(bc.name);
            bc.close();
        ";
        var output = TestHarness.Run(source, mode);
        Assert.Equal("chan-1\n", output);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void BroadcastChannel_DeliversBetweenChannelsWithSameName(ExecutionMode mode)
    {
        var source = @"
            const a = new BroadcastChannel('topic');
            const b = new BroadcastChannel('topic');
            b.on('message', (e: any) => {
                console.log('b got:', e.data);
            });
            a.postMessage('hello');
            a.close();
            b.close();
        ";
        var output = TestHarness.Run(source, mode);
        Assert.Equal("b got: hello\n", output);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void BroadcastChannel_SenderDoesNotReceiveOwnMessages(ExecutionMode mode)
    {
        var source = @"
            const bc = new BroadcastChannel('selfless');
            bc.on('message', (e: any) => {
                console.log('should not see this');
            });
            bc.postMessage('hi');
            bc.close();
            console.log('done');
        ";
        var output = TestHarness.Run(source, mode);
        Assert.Equal("done\n", output);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void BroadcastChannel_DifferentNamesDoNotInteract(ExecutionMode mode)
    {
        var source = @"
            const a = new BroadcastChannel('left');
            const b = new BroadcastChannel('right');
            b.on('message', (_e: any) => {
                console.log('should not see this');
            });
            a.postMessage('hi');
            a.close();
            b.close();
            console.log('done');
        ";
        var output = TestHarness.Run(source, mode);
        Assert.Equal("done\n", output);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void BroadcastChannel_PostAfterCloseThrows(ExecutionMode mode)
    {
        var source = @"
            const bc = new BroadcastChannel('post-after-close');
            bc.close();
            try {
                bc.postMessage('nope');
                console.log('should not reach');
            } catch (e) {
                console.log('caught');
            }
        ";
        var output = TestHarness.Run(source, mode);
        Assert.Equal("caught\n", output);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void BroadcastChannel_DeliversToMultipleSubscribers(ExecutionMode mode)
    {
        var source = @"
            const a = new BroadcastChannel('many');
            const b = new BroadcastChannel('many');
            const c = new BroadcastChannel('many');
            b.on('message', (e: any) => console.log('b:', e.data));
            c.on('message', (e: any) => console.log('c:', e.data));
            a.postMessage('hi');
            a.close();
            b.close();
            c.close();
        ";
        var output = TestHarness.Run(source, mode);
        // b and c each receive — order between them is implementation-defined.
        Assert.Contains("b: hi\n", output);
        Assert.Contains("c: hi\n", output);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void BroadcastChannel_AddEventListenerWorks(ExecutionMode mode)
    {
        var source = @"
            const a = new BroadcastChannel('addel');
            const b = new BroadcastChannel('addel');
            b.addEventListener('message', (e: any) => {
                console.log('got:', e.data);
            });
            a.postMessage('world');
            a.close();
            b.close();
        ";
        var output = TestHarness.Run(source, mode);
        Assert.Equal("got: world\n", output);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void BroadcastChannel_OnmessageSetterWorks(ExecutionMode mode)
    {
        var source = @"
            const a = new BroadcastChannel('onmsg');
            const b = new BroadcastChannel('onmsg');
            b.onmessage = (e: any) => {
                console.log('onmsg:', e.data);
            };
            a.postMessage('slot');
            a.close();
            b.close();
        ";
        var output = TestHarness.Run(source, mode);
        Assert.Equal("onmsg: slot\n", output);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void BroadcastChannel_OnmessageAndOnListenerBothFire(ExecutionMode mode)
    {
        var source = @"
            const a = new BroadcastChannel('both');
            const b = new BroadcastChannel('both');
            b.on('message', (e: any) => console.log('on:', e.data));
            b.onmessage = (e: any) => console.log('onmsg:', e.data);
            a.postMessage('hi');
            a.close();
            b.close();
        ";
        var output = TestHarness.Run(source, mode);
        Assert.Contains("on: hi\n", output);
        Assert.Contains("onmsg: hi\n", output);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void BroadcastChannel_StructuredCloneMutationIsolation(ExecutionMode mode)
    {
        // Per-receiver deep clone: mutating the received object on the listener side
        // must not affect the sender's original object.
        var source = @"
            const a = new BroadcastChannel('clone');
            const b = new BroadcastChannel('clone');
            const payload = { counter: 1, tags: ['a', 'b'] };
            b.on('message', (e: any) => {
                e.data.counter = 999;
                e.data.tags.push('mutated');
            });
            a.postMessage(payload);
            a.close();
            b.close();
            // Sender's payload must be untouched.
            console.log(payload.counter);
            console.log(payload.tags.length);
        ";
        var output = TestHarness.Run(source, mode);
        Assert.Equal("1\n2\n", output);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void BroadcastChannel_WorksInsideAsyncFunction(ExecutionMode mode)
    {
        // Regression: exercises the async state-machine emitter path for
        // `new BroadcastChannel(...)` + postMessage/close inside an `async` context.
        var source = @"
            async function run() {
                const a = new BroadcastChannel('async');
                const b = new BroadcastChannel('async');
                b.on('message', (e: any) => console.log('async got:', e.data));
                a.postMessage('from async');
                a.close();
                b.close();
            }
            run();
        ";
        var output = TestHarness.Run(source, mode);
        Assert.Equal("async got: from async\n", output);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void BroadcastChannel_ModuleQualifiedNewWorks(ExecutionMode mode)
    {
        // Regression: `new wt.BroadcastChannel(name)` via TryEmitModuleQualifiedConstructor.
        var files = new Dictionary<string, string>
        {
            ["main.ts"] = """
                import * as wt from 'worker_threads';
                const a = new wt.BroadcastChannel('mod');
                const b = new wt.BroadcastChannel('mod');
                b.on('message', (e: any) => console.log('mod got:', e.data));
                a.postMessage('via wt');
                a.close();
                b.close();
                """
        };
        var output = TestHarness.RunModules(files, "main.ts", mode);
        Assert.Equal("mod got: via wt\n", output);
    }
}
