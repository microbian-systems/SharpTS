using SharpTS.Tests.Infrastructure;
using Xunit;

namespace SharpTS.Tests.SharedTests.BuiltInModules;

/// <summary>
/// Gap-surfacing tests for the Web Streams API — exercises semantic corners
/// that the base <see cref="StreamsWebBasicTests"/> suite doesn't cover.
/// </summary>
/// <remarks>
/// Purpose: expose differences between the interpreter-side runtime
/// (<c>SharpTS.Runtime.Types.SharpTS*Stream</c>) and the pure-IL emitted
/// compiled-mode types (<c>$ReadableStream</c>, <c>$WritableStream</c>,
/// <c>$TransformStream</c>). Tests that pass in interpreter mode but fail
/// in compiled mode document a real pure-IL gap. Tests marked
/// <see cref="ExecutionModes.InterpretedOnly"/> are known-failing gaps with a
/// comment explaining the compiled-mode limitation.
/// </remarks>
public class StreamsWebSemanticTests
{
    #region Pending reads — queue empty, chunk arrives later

    /// <summary>
    /// Classic push-style ReadableStream: <c>start</c> captures the
    /// controller; chunks are enqueued later from a timer callback. The
    /// reader's <c>read()</c> must park and resolve when a chunk arrives.
    /// </summary>
    /// <remarks>
    /// <para><b>INTERPRETER:</b> the runtime <c>SharpTSReadableStream</c> DOES
    /// have pending-reads parking via <c>TaskCompletionSource&lt;object?&gt;</c>,
    /// but exercising it via a <c>setTimeout</c>-driven enqueue exposes an
    /// event-loop interaction bug: the test hangs for the full harness timeout
    /// (30s) even though the timer fires and <c>EnqueueInternal</c> resolves
    /// the TCS. Root cause likely involves the TCS continuation
    /// (<c>RunContinuationsAsynchronously</c>) being posted to the
    /// <c>InterpreterSynchronizationContext</c> but not actually being
    /// drained back to the awaiting task. Debugging requires instrumenting
    /// the event loop and is deferred.</para>
    ///
    /// <para><b>COMPILED:</b> the pure-IL <c>$ReadableStream.Read()</c> does
    /// not implement pending-reads parking at all. Queue empty → returns
    /// <c>done:true</c> immediately. Fixing requires async state machine
    /// emission.</para>
    /// </remarks>
    [Theory]
    [MemberData(nameof(ExecutionModes.InterpretedOnly), MemberType = typeof(ExecutionModes))]
    public void ReadableStream_PendingReadResolvedByLaterEnqueue(ExecutionMode mode)
    {
        var files = new Dictionary<string, string>
        {
            ["main.ts"] = """
                let ctrl: any;
                const rs = new ReadableStream({
                    start(c) { ctrl = c; }
                });
                const reader = rs.getReader();
                setTimeout(() => {
                    ctrl.enqueue("delayed");
                    ctrl.close();
                }, 10);
                async function run() {
                    const r = await reader.read();
                    console.log(r.value);
                }
                run();
                """
        };

        var output = TestHarness.RunModules(files, "main.ts", mode);
        Assert.Equal("delayed\n", output);
    }

    #endregion

    #region Async pull callback

    /// <summary>
    /// Pull callback returns a Promise that resolves after a tick. Reader
    /// should wait for the pull promise before dequeueing.
    /// </summary>
    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void ReadableStream_AsyncPullCallback(ExecutionMode mode)
    {
        // Compiled mode: pure-IL $ReadableStream.Read() now sync-awaits a
        // Task<object> / $Promise returned from the pull callback via
        // GetAwaiter().GetResult() before retrying the queue drain. Blocks
        // the calling thread but matches PipeTo's sync-pump strategy.
        var files = new Dictionary<string, string>
        {
            ["main.ts"] = """
                let i = 0;
                const rs = new ReadableStream({
                    pull: async (c: any) => {
                        await Promise.resolve();
                        c.enqueue(i++);
                        if (i >= 2) c.close();
                    }
                });
                const reader = rs.getReader();
                async function run() {
                    const r1 = await reader.read();
                    const r2 = await reader.read();
                    const r3 = await reader.read();
                    console.log(r1.value, r2.value, r3.done);
                }
                run();
                """
        };

        var output = TestHarness.RunModules(files, "main.ts", mode);
        Assert.Equal("0 1 true\n", output);
    }

    #endregion

    #region Async write callback

    /// <summary>
    /// Write callback that does a simple <c>await Promise.resolve()</c> before
    /// recording the chunk. This tests whether the emitted <c>$WritableStream</c>
    /// (via <c>EmitUnwrapResultToTask</c>) correctly unwraps a compiled async
    /// function's returned task.
    /// </summary>
    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void WritableStream_AsyncWriteCallback(ExecutionMode mode)
    {
        var files = new Dictionary<string, string>
        {
            ["main.ts"] = """
                const chunks: string[] = [];
                const ws = new WritableStream({
                    write: async (chunk: any) => {
                        await Promise.resolve();
                        chunks.push(chunk);
                    }
                });
                const writer = ws.getWriter();
                async function run() {
                    await writer.write("a");
                    await writer.write("b");
                    await writer.write("c");
                    console.log(chunks.join(","));
                }
                run();
                """
        };

        var output = TestHarness.RunModules(files, "main.ts", mode);
        Assert.Equal("a,b,c\n", output);
    }

    #endregion

    #region WritableStream controller access from inside write callback

    /// <summary>
    /// User <c>write(chunk, controller)</c> accesses the second argument.
    /// Tests whether the stream actually passes a controller object.
    /// </summary>
    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void WritableStream_WriteCallbackReceivesController(ExecutionMode mode)
    {
        // Compiled mode: $WritableStream now instantiates a real
        // $WritableStreamDefaultController in its constructor and passes it
        // as the second arg to user write(chunk, controller) callbacks.
        var files = new Dictionary<string, string>
        {
            ["main.ts"] = """
                let observedControllerType = "not-called";
                const ws = new WritableStream({
                    write(chunk, controller) {
                        observedControllerType = typeof controller;
                    }
                });
                const writer = ws.getWriter();
                async function run() {
                    await writer.write("data");
                    console.log(observedControllerType);
                }
                run();
                """
        };

        var output = TestHarness.RunModules(files, "main.ts", mode);
        Assert.Equal("object\n", output);
    }

    #endregion

    #region WritableStream concurrent writes serialization

    /// <summary>
    /// Concurrent writes (not awaited individually) should be serialized
    /// internally — only one user write callback runs at a time.
    /// </summary>
    [Theory]
    [MemberData(nameof(ExecutionModes.InterpretedOnly), MemberType = typeof(ExecutionModes))]
    public void WritableStream_ConcurrentWritesAreSerialized(ExecutionMode mode)
    {
        // KNOWN LIMITATION in compiled mode: the emitted $WritableStream has
        // no internal write queue. Write() calls the user callback directly
        // and returns immediately. In interpreter mode SharpTSWritableStream
        // uses AdvanceQueue to ensure at most one in-flight write; the ordering
        // and "ready" promise semantics follow from that.
        //
        // This test schedules three concurrent writes where the user callback
        // records the entry order. With serialization, all three complete
        // before the test's run() moves on. Without it, interleaving is
        // technically allowed but we rely on observable ordering.
        var files = new Dictionary<string, string>
        {
            ["main.ts"] = """
                const order: number[] = [];
                let inflight = 0;
                let maxInflight = 0;
                const ws = new WritableStream({
                    write: async (chunk: any) => {
                        inflight++;
                        if (inflight > maxInflight) maxInflight = inflight;
                        await Promise.resolve();
                        order.push(chunk);
                        inflight--;
                    }
                });
                const writer = ws.getWriter();
                async function run() {
                    const p1 = writer.write(1);
                    const p2 = writer.write(2);
                    const p3 = writer.write(3);
                    await p1; await p2; await p3;
                    console.log(order.join(","), "max=" + maxInflight);
                }
                run();
                """
        };

        var output = TestHarness.RunModules(files, "main.ts", mode);
        Assert.Equal("1,2,3 max=1\n", output);
    }

    #endregion

    #region ByteLengthQueuingStrategy size weighting

    /// <summary>
    /// <c>ByteLengthQueuingStrategy</c> should weight desiredSize by
    /// <c>chunk.byteLength</c> (or the strategy's <c>size(chunk)</c>), not by
    /// 1 per chunk.
    /// </summary>
    [Theory]
    [MemberData(nameof(ExecutionModes.InterpretedOnly), MemberType = typeof(ExecutionModes))]
    public void ReadableStream_ByteLengthStrategyWeighting(ExecutionMode mode)
    {
        // KNOWN LIMITATION in compiled mode: $ReadableStreamDefaultController.DesiredSize
        // computes `_highWaterMark - _queue.Count` (count-based) instead of
        // accumulating chunk sizes via the strategy's size() function.
        // Fixing this requires calling strategy.size(chunk) on each enqueue
        // and maintaining a running total — straightforward but not done yet.
        var files = new Dictionary<string, string>
        {
            ["main.ts"] = """
                const sizes: number[] = [];
                const rs = new ReadableStream({
                    start(c) {
                        sizes.push(c.desiredSize);        // 10
                        c.enqueue(new Uint8Array(3));     // 3 bytes
                        sizes.push(c.desiredSize);        // 7
                        c.enqueue(new Uint8Array(4));     // 4 bytes
                        sizes.push(c.desiredSize);        // 3
                    }
                }, new ByteLengthQueuingStrategy({ highWaterMark: 10 }));
                console.log(sizes.join(","));
                """
        };

        var output = TestHarness.RunModules(files, "main.ts", mode);
        Assert.Equal("10,7,3\n", output);
    }

    #endregion

    #region tee() — fork into two branches

    /// <summary>
    /// Both branches of a teed ReadableStream should receive every chunk.
    /// </summary>
    /// <remarks>
    /// This originally exposed a race in the interpreter-side tee() — branch1's
    /// constructor triggered a pull whose callback closed over branch2, a
    /// still-null local during construction, causing the first chunk to be
    /// lost. Fixed by switching to the eager-drain approach that the pure-IL
    /// emitted <c>$ReadableStream.Tee</c> already used.
    /// </remarks>
    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void ReadableStream_Tee_BothBranchesReceiveAllChunks(ExecutionMode mode)
    {
        var files = new Dictionary<string, string>
        {
            ["main.ts"] = """
                const rs = new ReadableStream({
                    start(c) {
                        c.enqueue("a");
                        c.enqueue("b");
                        c.enqueue("c");
                        c.close();
                    }
                });
                const branches = rs.tee();
                const r1 = branches[0].getReader();
                const r2 = branches[1].getReader();
                async function run() {
                    const a1 = await r1.read();
                    const a2 = await r1.read();
                    const a3 = await r1.read();
                    const a4 = await r1.read();
                    const b1 = await r2.read();
                    const b2 = await r2.read();
                    const b3 = await r2.read();
                    const b4 = await r2.read();
                    console.log(a1.value, a2.value, a3.value, a4.done);
                    console.log(b1.value, b2.value, b3.value, b4.done);
                }
                run();
                """
        };

        var output = TestHarness.RunModules(files, "main.ts", mode);
        Assert.Equal("a b c true\na b c true\n", output);
    }

    #endregion

    #region pipeTo with AbortSignal

    /// <remarks>
    /// Same event-loop interaction problem as <see cref="ReadableStream_PendingReadResolvedByLaterEnqueue"/>
    /// — the <c>setTimeout</c>-driven abort never wakes up the pump loop's
    /// awaiting read. Interpreter pipeTo WILL honour the signal if it's
    /// already aborted when pipeTo starts; the mid-pipe abort path is what
    /// hangs. Compiled-mode $ReadableStream.PipeTo doesn't extract the signal
    /// from opts at all (not implemented). Both deferred.
    /// </remarks>
    [Theory]
    [MemberData(nameof(ExecutionModes.InterpretedOnly), MemberType = typeof(ExecutionModes))]
    public void ReadableStream_PipeTo_AbortSignalCancelsSourceAndAbortsDest(ExecutionMode mode)
    {
        var files = new Dictionary<string, string>
        {
            ["main.ts"] = """
                let i = 0;
                const source = new ReadableStream({
                    pull(c) { c.enqueue(i++); },
                    cancel(reason) { console.log("source-canceled"); }
                });
                const dest = new WritableStream({
                    write(chunk) { /* accept everything */ },
                    abort(reason) { console.log("dest-aborted"); }
                });
                const ac = new AbortController();
                setTimeout(() => ac.abort(), 0);
                async function run() {
                    try {
                        await source.pipeTo(dest, { signal: ac.signal });
                        console.log("pipe-completed-normally");
                    } catch (e) {
                        console.log("pipe-rejected");
                    }
                }
                run();
                """
        };

        var output = TestHarness.RunModules(files, "main.ts", mode);
        // The signal handler fires, pipeTo's pump sees the abort, calls
        // dest.abort and source.cancel, then rejects. The catch block runs.
        Assert.Contains("dest-aborted", output);
        Assert.Contains("source-canceled", output);
        Assert.Contains("pipe-rejected", output);
        Assert.DoesNotContain("pipe-completed-normally", output);
    }

    #endregion

    #region TransformStream controller.terminate

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void TransformStream_ControllerTerminateInsideTransform(ExecutionMode mode)
    {
        // Interpreter-side: FIXED. terminate() now closes the readable (per
        // WHATWG spec) rather than erroring it, matching what `reader.read()`
        // expects after the stream stops.
        //
        // Compiled-mode: the emitted $TransformSinkHolder passes the
        // underlying $ReadableStream directly as the "controller" arg to the
        // user's transform(chunk, controller) callback. The readable exposes
        // Enqueue/CloseStream/ErrorStream which JS-side dispatch finds as
        // enqueue/close/error. "terminate" maps to CloseStream via the
        // PascalCase reflection fallback? No — "terminate" doesn't PascalCase
        // to "CloseStream". It would need a dedicated $TransformStreamDefaultController
        // class to expose terminate. Compiled mode expected to fail.
        var files = new Dictionary<string, string>
        {
            ["main.ts"] = """
                const ts = new TransformStream({
                    transform(chunk, c) {
                        if (chunk === "stop") {
                            c.terminate();
                            return;
                        }
                        c.enqueue(chunk.toUpperCase());
                    }
                });
                const writer = ts.writable.getWriter();
                const reader = ts.readable.getReader();
                async function run() {
                    writer.write("a");
                    writer.write("b");
                    writer.write("stop");
                    const r1 = await reader.read();
                    const r2 = await reader.read();
                    const r3 = await reader.read();
                    console.log(r1.value, r2.value, r3.done);
                }
                run();
                """
        };

        var output = TestHarness.RunModules(files, "main.ts", mode);
        Assert.Equal("A B true\n", output);
    }

    #endregion

    #region ReadableStream Symbol.asyncIterator

    [Theory]
    [MemberData(nameof(ExecutionModes.InterpretedOnly), MemberType = typeof(ExecutionModes))]
    public void ReadableStream_AsyncIteratorForAwaitOf(ExecutionMode mode)
    {
        // KNOWN LIMITATION in both modes (gap from original review): neither
        // the interpreter-side SharpTSReadableStream nor the emitted
        // $ReadableStream implements [Symbol.asyncIterator]. Node 18+ makes
        // `for await (const chunk of stream)` work on ReadableStream directly;
        // we don't. This test is marked interpreter-only and is expected to
        // fail even there — it's a "surface the gap" marker.
        var files = new Dictionary<string, string>
        {
            ["main.ts"] = """
                const rs = new ReadableStream({
                    start(c) {
                        c.enqueue(1);
                        c.enqueue(2);
                        c.enqueue(3);
                        c.close();
                    }
                });
                async function run() {
                    const collected: number[] = [];
                    for await (const chunk of rs) {
                        collected.push(chunk);
                    }
                    console.log(collected.join(","));
                }
                run();
                """
        };

        var output = TestHarness.RunModules(files, "main.ts", mode);
        Assert.Equal("1,2,3\n", output);
    }

    #endregion

    #region Compiled-mode-only: pure-IL pending-reads parking

    /// <summary>
    /// Compiled-mode variant of <see cref="ReadableStream_PendingReadResolvedByLaterEnqueue"/>.
    /// Exercises the pure-IL <c>$ReadableStream</c> pending-reads parking path
    /// where <c>reader.read()</c> is called before any chunks are available,
    /// then a microtask-scheduled <c>enqueue</c> resolves the parked
    /// <see cref="TaskCompletionSource{T}"/>.
    /// </summary>
    /// <remarks>
    /// Uses <c>Promise.resolve().then(...)</c> rather than <c>setTimeout</c>
    /// because compiled-mode <c>await new Promise(r => setTimeout(r, 10))</c>
    /// is a separate known-broken path (timer-driven async resumption) and
    /// orthogonal to the parking feature being validated here.
    /// </remarks>
    [Theory]
    [MemberData(nameof(ExecutionModes.CompiledOnly), MemberType = typeof(ExecutionModes))]
    public void ReadableStream_PendingReadResolvedByLaterEnqueue_Compiled(ExecutionMode mode)
    {
        var files = new Dictionary<string, string>
        {
            ["main.ts"] = """
                let ctrl: any;
                const rs = new ReadableStream({
                    start(c) { ctrl = c; }
                });
                const reader = rs.getReader();
                Promise.resolve().then(() => {
                    ctrl.enqueue("delayed");
                    ctrl.close();
                });
                async function run() {
                    const r = await reader.read();
                    console.log(r.value);
                }
                run();
                """
        };

        var output = TestHarness.RunModules(files, "main.ts", mode);
        Assert.Equal("delayed\n", output);
    }

    /// <summary>
    /// Verifies that closing a <c>$ReadableStream</c> that has a parked
    /// reader drains the pending-reads queue with <c>{value:undefined, done:true}</c>,
    /// not leaving the reader hanging forever.
    /// </summary>
    [Theory]
    [MemberData(nameof(ExecutionModes.CompiledOnly), MemberType = typeof(ExecutionModes))]
    public void ReadableStream_PendingReadResolvedByLaterClose_Compiled(ExecutionMode mode)
    {
        var files = new Dictionary<string, string>
        {
            ["main.ts"] = """
                let ctrl: any;
                const rs = new ReadableStream({
                    start(c) { ctrl = c; }
                });
                const reader = rs.getReader();
                Promise.resolve().then(() => { ctrl.close(); });
                async function run() {
                    const r = await reader.read();
                    console.log(r.done);
                }
                run();
                """
        };

        var output = TestHarness.RunModules(files, "main.ts", mode);
        Assert.Equal("true\n", output);
    }

    /// <summary>
    /// Pre-aborted <see cref="AbortController"/> signal passed in <c>pipeTo</c>
    /// options. Pure-IL <c>$ReadableStream.PipeTo</c> should check the signal
    /// before each read, call <c>writer.abort(reason)</c> +
    /// <c>source.cancel(reason)</c>, and reject the returned task.
    /// </summary>
    /// <remarks>
    /// Compiled-mode only: the interpreter-side variant is blocked by the
    /// top-level GetResult event-loop issue (see
    /// <see cref="ReadableStream_PipeTo_AbortSignalCancelsSourceAndAbortsDest"/>).
    /// This variant uses a pre-aborted signal, so no mid-pipe timer-driven
    /// abort is needed, and issue #22 (timer-callback dispatch of
    /// $PromiseResolveCallback) does not apply.
    /// </remarks>
    [Theory]
    [MemberData(nameof(ExecutionModes.CompiledOnly), MemberType = typeof(ExecutionModes))]
    public void ReadableStream_PipeTo_PreAbortedSignal_Compiled(ExecutionMode mode)
    {
        var files = new Dictionary<string, string>
        {
            ["main.ts"] = """
                const source = new ReadableStream({
                    start(c) { c.enqueue("a"); c.enqueue("b"); c.close(); },
                    cancel(reason) { console.log("source-canceled"); }
                });
                const dest = new WritableStream({
                    write(chunk) { /* accept */ },
                    abort(reason) { console.log("dest-aborted"); }
                });
                const ac = new AbortController();
                ac.abort();
                async function run() {
                    try {
                        await source.pipeTo(dest, { signal: ac.signal });
                        console.log("pipe-completed-normally");
                    } catch {
                        console.log("pipe-rejected");
                    }
                }
                run();
                """
        };

        var output = TestHarness.RunModules(files, "main.ts", mode);
        Assert.Contains("dest-aborted", output);
        Assert.Contains("source-canceled", output);
        Assert.Contains("pipe-rejected", output);
        Assert.DoesNotContain("pipe-completed-normally", output);
    }

    /// <summary>
    /// Verifies that erroring a <c>$ReadableStream</c> that has a parked
    /// reader rejects the pending-reads queue via <c>TrySetException</c>.
    /// </summary>
    [Theory]
    [MemberData(nameof(ExecutionModes.CompiledOnly), MemberType = typeof(ExecutionModes))]
    public void ReadableStream_PendingReadRejectedByLaterError_Compiled(ExecutionMode mode)
    {
        var files = new Dictionary<string, string>
        {
            ["main.ts"] = """
                let ctrl: any;
                const rs = new ReadableStream({
                    start(c) { ctrl = c; }
                });
                const reader = rs.getReader();
                Promise.resolve().then(() => { ctrl.error("boom"); });
                async function run() {
                    try {
                        await reader.read();
                        console.log("no-throw");
                    } catch (e) {
                        console.log("threw");
                    }
                }
                run();
                """
        };

        var output = TestHarness.RunModules(files, "main.ts", mode);
        Assert.Equal("threw\n", output);
    }

    #endregion

    #region Promise-object surfaces (#223: reader/writer promises must support .then)

    /// <summary>
    /// #223: <c>reader.read()</c>/<c>reader.closed</c>/<c>reader.cancel()</c>
    /// must return Promise objects usable with <c>.then()</c>/<c>.catch()</c>,
    /// not raw Tasks whose property access throws.
    /// </summary>
    [Theory]
    [MemberData(nameof(ExecutionModes.InterpretedOnly), MemberType = typeof(ExecutionModes))]
    public void ReadableStreamReader_ReadAndClosed_AreThenable(ExecutionMode mode)
    {
        var files = new Dictionary<string, string>
        {
            ["main.ts"] = """
                import { ReadableStream as RS } from "stream/web";
                const rs = new (RS as any)({ start(c: any) { c.enqueue("z"); c.close(); } });
                const reader = rs.getReader();
                reader.closed.then(() => console.log("closed-resolved"));
                reader.read().then((r: any) => console.log("read:", r.value, r.done));
                """
        };

        var output = TestHarness.RunModules(files, "main.ts", mode);
        Assert.Contains("read: z false", output);
        Assert.Contains("closed-resolved", output);
    }

    /// <summary>
    /// #223 audit follow-through: writer <c>write()</c>/<c>close()</c>/<c>ready</c>/
    /// <c>closed</c> must be thenable Promise objects as well.
    /// </summary>
    [Theory]
    [MemberData(nameof(ExecutionModes.InterpretedOnly), MemberType = typeof(ExecutionModes))]
    public void WritableStreamWriter_WriteReadyClosed_AreThenable(ExecutionMode mode)
    {
        var files = new Dictionary<string, string>
        {
            ["main.ts"] = """
                const chunks: string[] = [];
                const ws = new WritableStream({
                    write(chunk) { chunks.push(chunk as string); }
                });
                const writer = (ws as any).getWriter();
                writer.ready.then(() => console.log("ready"));
                writer.write("a").then(() => {
                    console.log("wrote:", chunks.join(","));
                    writer.close().then(() => console.log("close-resolved"));
                    writer.closed.then(() => console.log("closed-resolved"));
                });
                """
        };

        var output = TestHarness.RunModules(files, "main.ts", mode);
        Assert.Contains("ready", output);
        Assert.Contains("wrote: a", output);
        Assert.Contains("close-resolved", output);
        Assert.Contains("closed-resolved", output);
    }

    #endregion
}
