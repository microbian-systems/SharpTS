using SharpTS.Tests.Infrastructure;
using Xunit;

namespace SharpTS.Tests.SharedTests;

/// <summary>
/// Tests for worker_threads-related APIs: SharedArrayBuffer, Atomics,
/// MessageChannel, and structuredClone.
/// </summary>
public class WorkerThreadsTests
{
    #region SharedArrayBuffer Tests

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void SharedArrayBuffer_Constructor_CreatesBufferWithSize(ExecutionMode mode)
    {
        var source = @"
            let sab = new SharedArrayBuffer(16);
            console.log(sab.byteLength);
        ";
        var output = TestHarness.Run(source, mode);
        Assert.Equal("16\n", output);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void SharedArrayBuffer_Slice_CreatesNewBuffer(ExecutionMode mode)
    {
        var source = @"
            let sab = new SharedArrayBuffer(16);
            let sliced = sab.slice(4, 12);
            console.log(sliced.byteLength);
        ";
        var output = TestHarness.Run(source, mode);
        Assert.Equal("8\n", output);
    }

    #endregion

    #region TypedArray over SharedArrayBuffer Tests

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void Int32Array_OverSharedArrayBuffer_SharesMemory(ExecutionMode mode)
    {
        var source = @"
            let sab = new SharedArrayBuffer(16);
            let view1 = new Int32Array(sab);
            let view2 = new Int32Array(sab);
            view1[0] = 42;
            console.log(view2[0]);
        ";
        var output = TestHarness.Run(source, mode);
        Assert.Equal("42\n", output);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void TypedArray_WithByteOffset_CreatesCorrectView(ExecutionMode mode)
    {
        var source = @"
            let sab = new SharedArrayBuffer(16);
            let view = new Int32Array(sab, 4, 2);
            console.log(view.byteOffset);
            console.log(view.length);
        ";
        var output = TestHarness.Run(source, mode);
        Assert.Equal("4\n2\n", output);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void Uint8Array_OverSharedArrayBuffer_WorksCorrectly(ExecutionMode mode)
    {
        var source = @"
            let sab = new SharedArrayBuffer(4);
            let view = new Uint8Array(sab);
            view[0] = 255;
            view[1] = 128;
            console.log(view[0]);
            console.log(view[1]);
        ";
        var output = TestHarness.Run(source, mode);
        Assert.Equal("255\n128\n", output);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void TypedArray_FromLength_CreatesArray(ExecutionMode mode)
    {
        var source = @"
            let arr = new Int32Array(4);
            arr[0] = 10;
            arr[1] = 20;
            arr[2] = 30;
            arr[3] = 40;
            console.log(arr[0]);
            console.log(arr[3]);
            console.log(arr.length);
        ";
        var output = TestHarness.Run(source, mode);
        Assert.Equal("10\n40\n4\n", output);
    }

    #endregion

    #region Atomics Tests

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void Atomics_Load_ReadsValue(ExecutionMode mode)
    {
        var source = @"
            let sab = new SharedArrayBuffer(16);
            let view = new Int32Array(sab);
            view[0] = 42;
            console.log(Atomics.load(view, 0));
        ";
        var output = TestHarness.Run(source, mode);
        Assert.Equal("42\n", output);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void Atomics_Store_WritesValue(ExecutionMode mode)
    {
        var source = @"
            let sab = new SharedArrayBuffer(16);
            let view = new Int32Array(sab);
            Atomics.store(view, 0, 100);
            console.log(view[0]);
        ";
        var output = TestHarness.Run(source, mode);
        Assert.Equal("100\n", output);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void Atomics_Add_AddsAndReturnsOldValue(ExecutionMode mode)
    {
        var source = @"
            let sab = new SharedArrayBuffer(16);
            let view = new Int32Array(sab);
            view[0] = 10;
            let oldValue = Atomics.add(view, 0, 5);
            console.log(oldValue);
            console.log(view[0]);
        ";
        var output = TestHarness.Run(source, mode);
        Assert.Equal("10\n15\n", output);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void Atomics_Sub_SubtractsAndReturnsOldValue(ExecutionMode mode)
    {
        var source = @"
            let sab = new SharedArrayBuffer(16);
            let view = new Int32Array(sab);
            view[0] = 10;
            let oldValue = Atomics.sub(view, 0, 3);
            console.log(oldValue);
            console.log(view[0]);
        ";
        var output = TestHarness.Run(source, mode);
        Assert.Equal("10\n7\n", output);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void Atomics_Exchange_SwapsValues(ExecutionMode mode)
    {
        var source = @"
            let sab = new SharedArrayBuffer(16);
            let view = new Int32Array(sab);
            view[0] = 42;
            let oldValue = Atomics.exchange(view, 0, 100);
            console.log(oldValue);
            console.log(view[0]);
        ";
        var output = TestHarness.Run(source, mode);
        Assert.Equal("42\n100\n", output);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void Atomics_CompareExchange_Success(ExecutionMode mode)
    {
        var source = @"
            let sab = new SharedArrayBuffer(16);
            let view = new Int32Array(sab);
            view[0] = 42;
            let result = Atomics.compareExchange(view, 0, 42, 100);
            console.log(result);
            console.log(view[0]);
        ";
        var output = TestHarness.Run(source, mode);
        Assert.Equal("42\n100\n", output);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void Atomics_CompareExchange_Failure(ExecutionMode mode)
    {
        var source = @"
            let sab = new SharedArrayBuffer(16);
            let view = new Int32Array(sab);
            view[0] = 42;
            let result = Atomics.compareExchange(view, 0, 99, 100);
            console.log(result);
            console.log(view[0]);
        ";
        var output = TestHarness.Run(source, mode);
        Assert.Equal("42\n42\n", output);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void Atomics_And_PerformsBitwiseAnd(ExecutionMode mode)
    {
        var source = @"
            let sab = new SharedArrayBuffer(16);
            let view = new Int32Array(sab);
            view[0] = 0b1111;
            let oldValue = Atomics.and(view, 0, 0b0101);
            console.log(oldValue);
            console.log(view[0]);
        ";
        var output = TestHarness.Run(source, mode);
        Assert.Equal("15\n5\n", output);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void Atomics_Or_PerformsBitwiseOr(ExecutionMode mode)
    {
        var source = @"
            let sab = new SharedArrayBuffer(16);
            let view = new Int32Array(sab);
            view[0] = 0b1010;
            let oldValue = Atomics.or(view, 0, 0b0101);
            console.log(oldValue);
            console.log(view[0]);
        ";
        var output = TestHarness.Run(source, mode);
        Assert.Equal("10\n15\n", output);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void Atomics_Xor_PerformsBitwiseXor(ExecutionMode mode)
    {
        var source = @"
            let sab = new SharedArrayBuffer(16);
            let view = new Int32Array(sab);
            view[0] = 0b1111;
            let oldValue = Atomics.xor(view, 0, 0b0101);
            console.log(oldValue);
            console.log(view[0]);
        ";
        var output = TestHarness.Run(source, mode);
        Assert.Equal("15\n10\n", output);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void Atomics_IsLockFree_ReturnsBooleanForSize(ExecutionMode mode)
    {
        var source = @"
            console.log(Atomics.isLockFree(4));
            console.log(Atomics.isLockFree(8));
        ";
        var output = TestHarness.Run(source, mode);
        Assert.Equal("true\ntrue\n", output);
    }

    #endregion

    #region MessageChannel Tests

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void MessageChannel_Constructor_CreatesTwoPorts(ExecutionMode mode)
    {
        var source = @"
            let channel = new MessageChannel();
            console.log(channel.port1 !== null);
            console.log(channel.port2 !== null);
            console.log(channel.port1 !== channel.port2);
        ";
        var output = TestHarness.Run(source, mode);
        Assert.Equal("true\ntrue\ntrue\n", output);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void MessageChannel_PortOnMessage_ReceivesPostedValue(ExecutionMode mode)
    {
        // #209 (interpreter) / #222 (compiled $MessagePort): port.on() must
        // dispatch through the port's own member table (postMessage/start/
        // close reachable), a 'message' listener implicitly starts the port,
        // and the listener receives the cloned value directly per Node
        // worker_threads semantics.
        var source = @"
            let channel: any = new MessageChannel();
            channel.port2.on('message', (value: any) => {
                console.log('received: ' + value);
                channel.port1.close();
                channel.port2.close();
            });
            channel.port1.postMessage('hello');
        ";
        var output = TestHarness.Run(source, mode);
        Assert.Contains("received: hello", output);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void MessageChannel_MessagesPostedBeforeListener_DeliveredInOrderAfterImplicitStart(ExecutionMode mode)
    {
        // #222: messages posted before any listener exists must queue and be
        // delivered (in order) once a 'message' listener implicitly starts
        // the port.
        var source = @"
            let channel: any = new MessageChannel();
            channel.port1.postMessage('first');
            channel.port1.postMessage('second');
            channel.port2.on('message', (value: any) => {
                console.log('got: ' + value);
                if (value === 'second') {
                    channel.port1.close();
                    channel.port2.close();
                }
            });
        ";
        var output = TestHarness.Run(source, mode);
        Assert.Contains("got: first\ngot: second", output);
    }

    #endregion

    #region StructuredClone Tests

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void StructuredClone_ClonesObject(ExecutionMode mode)
    {
        var source = @"
            let obj = { a: 1, b: 'hello', c: [1, 2, 3] };
            let cloned = structuredClone(obj);
            cloned.a = 999;
            console.log(obj.a);
            console.log(cloned.a);
        ";
        var output = TestHarness.Run(source, mode);
        Assert.Equal("1\n999\n", output);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void StructuredClone_ClonesNestedObjects(ExecutionMode mode)
    {
        var source = @"
            let obj = { nested: { value: 42 } };
            let cloned = structuredClone(obj);
            cloned.nested.value = 100;
            console.log(obj.nested.value);
            console.log(cloned.nested.value);
        ";
        var output = TestHarness.Run(source, mode);
        Assert.Equal("42\n100\n", output);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void StructuredClone_ClonesArrays(ExecutionMode mode)
    {
        var source = @"
            let arr = [1, 2, [3, 4]];
            let cloned = structuredClone(arr);
            cloned[0] = 999;
            console.log(arr[0]);
            console.log(cloned[0]);
        ";
        var output = TestHarness.Run(source, mode);
        Assert.Equal("1\n999\n", output);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void StructuredClone_SharesSharedArrayBuffer(ExecutionMode mode)
    {
        var source = @"
            let sab = new SharedArrayBuffer(16);
            let view1 = new Int32Array(sab);
            view1[0] = 42;

            let clonedSab = structuredClone(sab);
            let view2 = new Int32Array(clonedSab);

            // SharedArrayBuffer is shared by reference, not cloned
            console.log(view2[0]);
            view2[0] = 100;
            console.log(view1[0]);
        ";
        var output = TestHarness.Run(source, mode);
        Assert.Equal("42\n100\n", output);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void StructuredClone_ClonesMap(ExecutionMode mode)
    {
        var source = @"
            let map = new Map<string, number>([['a', 1], ['b', 2]]);
            let cloned = structuredClone(map);
            cloned.set('a', 999);
            console.log(map.get('a'));
            console.log(cloned.get('a'));
        ";
        var output = TestHarness.Run(source, mode);
        Assert.Equal("1\n999\n", output);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void StructuredClone_ClonesSet(ExecutionMode mode)
    {
        var source = @"
            let mySet = new Set([1, 2, 3]);
            let cloned = structuredClone(mySet);
            cloned.add(4);
            console.log(mySet.size);
            console.log(cloned.size);
        ";
        var output = TestHarness.Run(source, mode);
        Assert.Equal("3\n4\n", output);
    }

    #endregion

    #region Worker.terminate() event-loop liveness (#324)

    /// <summary>
    /// Regression for #324: when <c>await worker.terminate()</c> is the only
    /// remaining top-level work and the worker takes longer than the event
    /// loop's 250ms quiescence window to wind down, the parent must stay alive
    /// until the terminate promise settles.
    /// </summary>
    /// <remarks>
    /// The worker keeps its own event loop alive ~500ms via a pending timer, so
    /// the parent's <c>_thread.Join</c> (inside <c>SharpTSWorker.Terminate</c>)
    /// blocks well past the 250ms give-up. That join task is invisible to
    /// <c>HasPendingEventLoopWork</c>; before the fix the parent abandoned the
    /// terminate promise and exited without printing "terminated". The fix Refs
    /// the parent loop for the join's duration — the parent interpreter loop
    /// (interpreter mode) or the emitted <c>$EventLoop</c> (compiled mode, #354).
    /// <c>__dirname</c> routes the harness through the real-disk path so the
    /// spawned worker can load its script. The assertion is positive (output
    /// present) and load-independent — under load the join simply takes longer and
    /// the Ref keeps the loop alive for it, so the test cannot flake the way a
    /// wall-clock window would.
    /// </remarks>
    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void Worker_Terminate_KeepsEventLoopAliveUntilSettled(ExecutionMode mode)
    {
        var files = new Dictionary<string, string>
        {
            ["worker_slow.ts"] = """
                // Hold the worker's event loop open ~500ms so the parent's
                // terminate() thread-join outlasts the 250ms quiescence window.
                setTimeout(() => {}, 500);
                """,
            ["main.ts"] = """
                import { Worker } from "worker_threads";
                const w = new Worker(__dirname + "/worker_slow.ts");
                async function run() {
                    await w.terminate();
                    console.log("terminated");
                }
                run();
                """
        };

        var output = TestHarness.RunModules(files, "main.ts", mode);
        Assert.Contains("terminated", output);
    }

    #endregion

    #region Running-Worker event-loop liveness (#329)

    /// <summary>
    /// Regression for #329: a running worker must keep the parent event loop alive
    /// by default (Node semantics). The worker posts a message back ~400ms after
    /// spawn — past the parent loop's 250ms quiescence window — and the parent's
    /// only pending work is the <c>'message'</c> listener. Before the fix nothing
    /// Ref'd the parent for the worker's running lifetime, so the parent abandoned
    /// the wait at 250ms and exited without ever printing the message.
    /// </summary>
    /// <remarks>
    /// The keep-alive Ref is against whichever loop owns the worker: the parent
    /// interpreter (interpreter mode) or the emitted <c>$EventLoop</c> (compiled
    /// mode, #354 — worker→parent delivery is marshalled onto the loop via the
    /// injected <c>$EventLoop.Schedule</c>). <c>__dirname</c> routes the harness
    /// through the real-disk path so the worker can load its script. The assertion
    /// is positive and load-independent — under load the worker simply posts later
    /// and the running-Ref keeps the parent alive until it does, so the test cannot
    /// flake the way a wall-clock window would.
    /// </remarks>
    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void Worker_RunningWorker_KeepsParentLoopAliveUntilMessage(ExecutionMode mode)
    {
        var files = new Dictionary<string, string>
        {
            ["worker_delayed.ts"] = """
                // Post back after >250ms, holding the worker's own loop open via the
                // pending timer until it fires. The worker then exits naturally.
                setTimeout(() => { postMessage("from-worker"); }, 400);
                """,
            ["main.ts"] = """
                import { Worker } from "worker_threads";
                const w = new Worker(__dirname + "/worker_delayed.ts");
                // The 'message' listener is the parent's ONLY pending work — the
                // running worker must keep the loop alive long enough to deliver.
                w.on("message", (e: any) => {
                    console.log("received:" + e.data);
                });
                """
        };

        var output = TestHarness.RunModules(files, "main.ts", mode);
        Assert.Contains("received:from-worker", output);
    }

    /// <summary>
    /// <c>worker.ref()</c> / <c>worker.unref()</c> are callable, chainable, and a
    /// <c>ref()</c> after an <c>unref()</c> restores the keep-alive so a later
    /// delayed message is still delivered (positive, load-independent assertion).
    /// </summary>
    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void Worker_UnrefThenRef_RestoresKeepAlive(ExecutionMode mode)
    {
        var files = new Dictionary<string, string>
        {
            ["worker_delayed.ts"] = """
                setTimeout(() => { postMessage("again"); }, 400);
                """,
            ["main.ts"] = """
                import { Worker } from "worker_threads";
                const w = new Worker(__dirname + "/worker_delayed.ts");
                w.on("message", (e: any) => {
                    console.log("received:" + e.data);
                });
                w.unref(); // opt out of keep-alive...
                w.ref();   // ...then opt back in — message must still arrive.
                """
        };

        var output = TestHarness.RunModules(files, "main.ts", mode);
        Assert.Contains("received:again", output);
    }

    #endregion
}
