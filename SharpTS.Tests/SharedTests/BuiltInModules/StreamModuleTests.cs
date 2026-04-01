using SharpTS.Tests.Infrastructure;
using Xunit;

namespace SharpTS.Tests.SharedTests.BuiltInModules;

/// <summary>
/// Tests for the stream module (Readable, Writable, Duplex, Transform, PassThrough).
/// </summary>
public class StreamModuleTests
{
    #region Import Patterns

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void Stream_NamedImport_Readable(ExecutionMode mode)
    {
        var files = new Dictionary<string, string>
        {
            ["main.ts"] = """
                import { Readable } from 'stream';
                const readable = new Readable();
                console.log(typeof readable);
                """
        };

        var output = TestHarness.RunModules(files, "main.ts", mode);
        Assert.Equal("object\n", output);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void Stream_NamedImport_Writable(ExecutionMode mode)
    {
        var files = new Dictionary<string, string>
        {
            ["main.ts"] = """
                import { Writable } from 'stream';
                const writable = new Writable();
                console.log(typeof writable);
                """
        };

        var output = TestHarness.RunModules(files, "main.ts", mode);
        Assert.Equal("object\n", output);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.InterpretedOnly), MemberType = typeof(ExecutionModes))]
    public void Stream_NamedImport_All(ExecutionMode mode)
    {
        var files = new Dictionary<string, string>
        {
            ["main.ts"] = """
                import { Readable, Writable, Duplex, Transform, PassThrough } from 'stream';
                console.log(typeof Readable);
                console.log(typeof Writable);
                console.log(typeof Duplex);
                console.log(typeof Transform);
                console.log(typeof PassThrough);
                """
        };

        var output = TestHarness.RunModules(files, "main.ts", mode);
        Assert.Equal("function\nfunction\nfunction\nfunction\nfunction\n", output);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void Stream_NamespaceImport(ExecutionMode mode)
    {
        var files = new Dictionary<string, string>
        {
            ["main.ts"] = """
                import * as stream from 'stream';
                const readable = new stream.Readable();
                console.log(typeof readable);
                """
        };

        var output = TestHarness.RunModules(files, "main.ts", mode);
        Assert.Equal("object\n", output);
    }

    #endregion

    #region Readable Stream

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void Readable_Push_Read(ExecutionMode mode)
    {
        var files = new Dictionary<string, string>
        {
            ["main.ts"] = """
                import { Readable } from 'stream';
                const readable = new Readable();

                readable.push('hello');
                readable.push(' world');
                readable.push(null);

                const data = readable.read();
                console.log(data);
                """
        };

        var output = TestHarness.RunModules(files, "main.ts", mode);
        Assert.Equal("hello world\n", output);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void Readable_Read_ReturnsNull_WhenEmpty(ExecutionMode mode)
    {
        var files = new Dictionary<string, string>
        {
            ["main.ts"] = """
                import { Readable } from 'stream';
                const readable = new Readable();

                const data = readable.read();
                console.log(data === null);
                """
        };

        var output = TestHarness.RunModules(files, "main.ts", mode);
        Assert.Equal("true\n", output);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void Readable_Push_Null_SignalsEnd(ExecutionMode mode)
    {
        var files = new Dictionary<string, string>
        {
            ["main.ts"] = """
                import { Readable } from 'stream';
                const readable = new Readable();

                console.log(readable.readableEnded);
                readable.push(null);
                console.log(readable.readableEnded);
                """
        };

        var output = TestHarness.RunModules(files, "main.ts", mode);
        Assert.Equal("false\ntrue\n", output);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void Readable_Properties(ExecutionMode mode)
    {
        var files = new Dictionary<string, string>
        {
            ["main.ts"] = """
                import { Readable } from 'stream';
                const readable = new Readable();

                console.log(readable.readable);
                console.log(readable.readableEnded);
                console.log(readable.readableLength);

                readable.push('data');
                console.log(readable.readableLength);
                """
        };

        var output = TestHarness.RunModules(files, "main.ts", mode);
        Assert.Equal("true\nfalse\n0\n1\n", output);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void Readable_Destroy(ExecutionMode mode)
    {
        var files = new Dictionary<string, string>
        {
            ["main.ts"] = """
                import { Readable } from 'stream';
                const readable = new Readable();

                console.log(readable.destroyed);
                readable.destroy();
                console.log(readable.destroyed);
                console.log(readable.readable);
                """
        };

        var output = TestHarness.RunModules(files, "main.ts", mode);
        Assert.Equal("false\ntrue\nfalse\n", output);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void Readable_EndEvent(ExecutionMode mode)
    {
        var files = new Dictionary<string, string>
        {
            ["main.ts"] = """
                import { Readable } from 'stream';
                const readable = new Readable();

                readable.on('end', () => {
                    console.log('end event fired');
                });

                readable.push('data');
                readable.push(null);
                """
        };

        var output = TestHarness.RunModules(files, "main.ts", mode);
        Assert.Equal("end event fired\n", output);
    }

    #endregion

    #region Writable Stream

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void Writable_Write_WithCallback(ExecutionMode mode)
    {
        var files = new Dictionary<string, string>
        {
            ["main.ts"] = """
                import { Writable } from 'stream';

                const chunks: string[] = [];
                const writable = new Writable({
                    write(chunk: string, encoding: string, callback: () => void) {
                        chunks.push(chunk);
                        callback();
                    }
                });

                writable.write('hello');
                writable.write(' world');
                writable.end();

                console.log(chunks.join(''));
                """
        };

        var output = TestHarness.RunModules(files, "main.ts", mode);
        Assert.Equal("hello world\n", output);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void Writable_Properties(ExecutionMode mode)
    {
        var files = new Dictionary<string, string>
        {
            ["main.ts"] = """
                import { Writable } from 'stream';
                const writable = new Writable();

                console.log(writable.writable);
                console.log(writable.writableEnded);
                console.log(writable.writableFinished);

                writable.end();

                console.log(writable.writable);
                console.log(writable.writableEnded);
                console.log(writable.writableFinished);
                """
        };

        var output = TestHarness.RunModules(files, "main.ts", mode);
        Assert.Equal("true\nfalse\nfalse\nfalse\ntrue\ntrue\n", output);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void Writable_FinishEvent(ExecutionMode mode)
    {
        var files = new Dictionary<string, string>
        {
            ["main.ts"] = """
                import { Writable } from 'stream';
                const writable = new Writable();

                writable.on('finish', () => {
                    console.log('finish event fired');
                });

                writable.end();
                """
        };

        var output = TestHarness.RunModules(files, "main.ts", mode);
        Assert.Equal("finish event fired\n", output);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void Writable_Cork_Uncork(ExecutionMode mode)
    {
        var files = new Dictionary<string, string>
        {
            ["main.ts"] = """
                import { Writable } from 'stream';

                const chunks: string[] = [];
                const writable = new Writable({
                    write(chunk: string, encoding: string, callback: () => void) {
                        chunks.push('wrote: ' + chunk);
                        callback();
                    }
                });

                writable.cork();
                writable.write('a');
                writable.write('b');
                console.log('before uncork: ' + chunks.length);
                writable.uncork();
                console.log('after uncork: ' + chunks.length);
                """
        };

        var output = TestHarness.RunModules(files, "main.ts", mode);
        Assert.Equal("before uncork: 0\nafter uncork: 2\n", output);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void Writable_End_WithChunk(ExecutionMode mode)
    {
        var files = new Dictionary<string, string>
        {
            ["main.ts"] = """
                import { Writable } from 'stream';

                const chunks: string[] = [];
                const writable = new Writable({
                    write(chunk: string, encoding: string, callback: () => void) {
                        chunks.push(chunk);
                        callback();
                    }
                });

                writable.write('hello');
                writable.end(' world');

                console.log(chunks.join(''));
                """
        };

        var output = TestHarness.RunModules(files, "main.ts", mode);
        Assert.Equal("hello world\n", output);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void Writable_Final_Callback(ExecutionMode mode)
    {
        var files = new Dictionary<string, string>
        {
            ["main.ts"] = """
                import { Writable } from 'stream';

                const events: string[] = [];
                const writable = new Writable({
                    write(chunk: string, encoding: string, callback: () => void) {
                        events.push('write: ' + chunk);
                        callback();
                    },
                    final(callback: () => void) {
                        events.push('final called');
                        callback();
                    }
                });

                writable.write('data');
                writable.end();

                console.log(events.join(', '));
                """
        };

        var output = TestHarness.RunModules(files, "main.ts", mode);
        Assert.Equal("write: data, final called\n", output);
    }

    #endregion

    #region Pipe

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void Readable_Pipe_Writable(ExecutionMode mode)
    {
        var files = new Dictionary<string, string>
        {
            ["main.ts"] = """
                import { Readable, Writable } from 'stream';

                const chunks: string[] = [];
                const readable = new Readable();
                const writable = new Writable({
                    write(chunk: string, encoding: string, callback: () => void) {
                        chunks.push(chunk);
                        callback();
                    }
                });

                readable.push('hello');
                readable.push(' world');
                readable.push(null);

                readable.pipe(writable);

                console.log(chunks.join(''));
                """
        };

        var output = TestHarness.RunModules(files, "main.ts", mode);
        Assert.Equal("hello world\n", output);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void Readable_Pipe_ReturnsDestination(ExecutionMode mode)
    {
        var files = new Dictionary<string, string>
        {
            ["main.ts"] = """
                import { Readable, Writable } from 'stream';

                const readable = new Readable();
                const writable = new Writable();

                const result = readable.pipe(writable);
                console.log(result === writable);
                """
        };

        var output = TestHarness.RunModules(files, "main.ts", mode);
        Assert.Equal("true\n", output);
    }

    #endregion

    #region Duplex Stream

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void Duplex_ReadAndWrite(ExecutionMode mode)
    {
        var files = new Dictionary<string, string>
        {
            ["main.ts"] = """
                import { Duplex } from 'stream';

                const chunks: string[] = [];
                const duplex = new Duplex({
                    write(chunk: string, encoding: string, callback: () => void) {
                        chunks.push('write: ' + chunk);
                        callback();
                    }
                });

                // Write side
                duplex.write('hello');

                // Read side
                duplex.push('world');
                const data = duplex.read();

                console.log(chunks[0]);
                console.log(data);
                """
        };

        var output = TestHarness.RunModules(files, "main.ts", mode);
        Assert.Equal("write: hello\nworld\n", output);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void Duplex_Properties(ExecutionMode mode)
    {
        var files = new Dictionary<string, string>
        {
            ["main.ts"] = """
                import { Duplex } from 'stream';
                const duplex = new Duplex();

                console.log(duplex.readable);
                console.log(duplex.writable);
                console.log(duplex.readableEnded);
                console.log(duplex.writableEnded);
                """
        };

        var output = TestHarness.RunModules(files, "main.ts", mode);
        Assert.Equal("true\ntrue\nfalse\nfalse\n", output);
    }

    #endregion

    #region Transform Stream

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void Transform_BasicTransformation(ExecutionMode mode)
    {
        var files = new Dictionary<string, string>
        {
            ["main.ts"] = """
                import { Transform } from 'stream';

                const transform = new Transform({
                    transform(chunk: string, encoding: string, callback: any) {
                        callback(null, chunk.toUpperCase());
                    }
                });

                transform.write('hello');
                transform.end();

                const result = transform.read();
                console.log(result);
                """
        };

        var output = TestHarness.RunModules(files, "main.ts", mode);
        Assert.Equal("HELLO\n", output);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void Transform_Pipe_Chain(ExecutionMode mode)
    {
        var files = new Dictionary<string, string>
        {
            ["main.ts"] = """
                import { Readable, Transform, Writable } from 'stream';

                const chunks: string[] = [];

                const readable = new Readable();
                const transform = new Transform({
                    transform(chunk: string, encoding: string, callback: any) {
                        // Add prefix to each chunk
                        callback(null, '[' + chunk + ']');
                    }
                });
                const writable = new Writable({
                    write(chunk: string, encoding: string, callback: () => void) {
                        chunks.push(chunk);
                        callback();
                    }
                });

                readable.push('hello');
                readable.push('world');
                readable.push(null);

                readable.pipe(transform).pipe(writable);

                console.log(chunks.join(' '));
                """
        };

        var output = TestHarness.RunModules(files, "main.ts", mode);
        Assert.Equal("[hello] [world]\n", output);
    }

    #endregion

    #region PassThrough Stream

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void PassThrough_PassesDataUnchanged(ExecutionMode mode)
    {
        var files = new Dictionary<string, string>
        {
            ["main.ts"] = """
                import { PassThrough } from 'stream';

                const passThrough = new PassThrough();

                passThrough.write('hello');
                passThrough.write(' world');
                passThrough.end();

                const result = passThrough.read();
                console.log(result);
                """
        };

        var output = TestHarness.RunModules(files, "main.ts", mode);
        Assert.Equal("hello world\n", output);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void PassThrough_InPipeline(ExecutionMode mode)
    {
        var files = new Dictionary<string, string>
        {
            ["main.ts"] = """
                import { Readable, PassThrough, Writable } from 'stream';

                const chunks: string[] = [];

                const readable = new Readable();
                const passThrough = new PassThrough();
                const writable = new Writable({
                    write(chunk: string, encoding: string, callback: () => void) {
                        chunks.push(chunk);
                        callback();
                    }
                });

                readable.push('hello');
                readable.push(' world');
                readable.push(null);

                readable.pipe(passThrough).pipe(writable);

                console.log(chunks.join(''));
                """
        };

        var output = TestHarness.RunModules(files, "main.ts", mode);
        Assert.Equal("hello world\n", output);
    }

    #endregion

    #region Events

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void Stream_CloseEvent(ExecutionMode mode)
    {
        var files = new Dictionary<string, string>
        {
            ["main.ts"] = """
                import { Readable } from 'stream';
                const readable = new Readable();

                readable.on('close', () => {
                    console.log('close event fired');
                });

                readable.destroy();
                """
        };

        var output = TestHarness.RunModules(files, "main.ts", mode);
        Assert.Equal("close event fired\n", output);
    }

    #endregion

    #region Flowing Mode

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void Readable_FlowingMode_DataEvent(ExecutionMode mode)
    {
        var files = new Dictionary<string, string>
        {
            ["main.ts"] = """
                import { Readable } from 'stream';
                const readable = new Readable();
                const chunks: string[] = [];

                readable.on('data', (chunk: string) => {
                    chunks.push(chunk);
                });

                readable.push('hello');
                readable.push(' world');
                readable.push(null);

                console.log(chunks.join(''));
                """
        };

        var output = TestHarness.RunModules(files, "main.ts", mode);
        Assert.Equal("hello world\n", output);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void Readable_FlowingMode_EndEvent(ExecutionMode mode)
    {
        var files = new Dictionary<string, string>
        {
            ["main.ts"] = """
                import { Readable } from 'stream';
                const readable = new Readable();
                let ended = false;

                readable.on('data', (chunk: string) => {
                    // consume data
                });
                readable.on('end', () => {
                    ended = true;
                });

                readable.push('data');
                readable.push(null);

                console.log(ended);
                """
        };

        var output = TestHarness.RunModules(files, "main.ts", mode);
        Assert.Equal("true\n", output);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void Readable_FlowingMode_Pause_Resume(ExecutionMode mode)
    {
        var files = new Dictionary<string, string>
        {
            ["main.ts"] = """
                import { Readable } from 'stream';
                const readable = new Readable();
                const chunks: string[] = [];

                readable.on('data', (chunk: string) => {
                    chunks.push(chunk);
                });

                readable.push('first');
                readable.pause();
                readable.push('second');
                readable.push('third');

                console.log('paused: ' + chunks.length);
                readable.resume();
                console.log('resumed: ' + chunks.length);
                """
        };

        var output = TestHarness.RunModules(files, "main.ts", mode);
        Assert.Equal("paused: 1\nresumed: 3\n", output);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void Readable_Pipe_EntersFlowingMode(ExecutionMode mode)
    {
        var files = new Dictionary<string, string>
        {
            ["main.ts"] = """
                import { Readable, Writable } from 'stream';
                const readable = new Readable();
                const chunks: string[] = [];
                const writable = new Writable({
                    write(chunk: string, encoding: string, callback: () => void) {
                        chunks.push(chunk);
                        callback();
                    }
                });

                readable.pipe(writable);

                // After pipe, pushing should flow data
                readable.push('hello');
                readable.push(' world');
                readable.push(null);

                console.log(chunks.join(''));
                """
        };

        var output = TestHarness.RunModules(files, "main.ts", mode);
        Assert.Equal("hello world\n", output);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void Readable_ReadableFlowing_Property(ExecutionMode mode)
    {
        var files = new Dictionary<string, string>
        {
            ["main.ts"] = """
                import { Readable } from 'stream';
                const readable = new Readable();

                console.log(readable.readableFlowing);
                readable.on('data', () => {});
                console.log(readable.readableFlowing);
                readable.pause();
                console.log(readable.readableFlowing);
                """
        };

        var output = TestHarness.RunModules(files, "main.ts", mode);
        Assert.Equal("false\ntrue\nfalse\n", output);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void Readable_FlowingMode_MultiplePush(ExecutionMode mode)
    {
        var files = new Dictionary<string, string>
        {
            ["main.ts"] = """
                import { Readable } from 'stream';
                const readable = new Readable();
                let count = 0;

                readable.on('data', () => {
                    count++;
                });

                readable.push('a');
                readable.push('b');
                readable.push('c');
                readable.push(null);

                console.log(count);
                """
        };

        var output = TestHarness.RunModules(files, "main.ts", mode);
        Assert.Equal("3\n", output);
    }

    #endregion

    #region Drain

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void Writable_WritableHighWaterMark_Property(ExecutionMode mode)
    {
        var files = new Dictionary<string, string>
        {
            ["main.ts"] = """
                import { Writable } from 'stream';
                const writable = new Writable();
                console.log(writable.writableHighWaterMark);
                """
        };

        var output = TestHarness.RunModules(files, "main.ts", mode);
        Assert.Equal("16384\n", output);
    }

    #endregion

    #region stream.finished()

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void Stream_Finished_CallsCallbackAfterReadableEnds(ExecutionMode mode)
    {
        var files = new Dictionary<string, string>
        {
            ["main.ts"] = """
                import { Readable, finished } from 'stream';
                const r = new Readable();
                finished(r, (err: any) => {
                    console.log('finished', err === null || err === undefined ? 'ok' : 'err');
                });
                r.push('data');
                r.push(null);
                """
        };

        var output = TestHarness.RunModules(files, "main.ts", mode);
        Assert.Contains("finished ok", output);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void Stream_Finished_CallsCallbackAfterWritableFinishes(ExecutionMode mode)
    {
        var files = new Dictionary<string, string>
        {
            ["main.ts"] = """
                import { Writable, finished } from 'stream';
                const w = new Writable({
                    write(chunk: any, enc: any, cb: any) { cb(); }
                });
                finished(w, (err: any) => {
                    console.log('finished', err === null || err === undefined ? 'ok' : 'err');
                });
                w.end();
                """
        };

        var output = TestHarness.RunModules(files, "main.ts", mode);
        Assert.Contains("finished ok", output);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void Stream_Finished_CallsCallbackWithErrorOnStreamError(ExecutionMode mode)
    {
        var files = new Dictionary<string, string>
        {
            ["main.ts"] = """
                import { Readable, finished } from 'stream';
                const r = new Readable();
                finished(r, (err: any) => {
                    console.log('error:', err !== null && err !== undefined);
                });
                r.destroy(new Error('boom'));
                """
        };

        var output = TestHarness.RunModules(files, "main.ts", mode);
        Assert.Contains("error: true", output);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void Stream_Finished_CleanupRemovesListeners(ExecutionMode mode)
    {
        var files = new Dictionary<string, string>
        {
            ["main.ts"] = """
                import { Readable, finished } from 'stream';
                const r = new Readable();
                const cleanup = finished(r, (err: any) => {
                    console.log('should not fire');
                });
                cleanup();
                r.push(null);
                console.log('done');
                """
        };

        var output = TestHarness.RunModules(files, "main.ts", mode);
        Assert.DoesNotContain("should not fire", output);
        Assert.Contains("done", output);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void Stream_Finished_OptionsReadableFalse(ExecutionMode mode)
    {
        var files = new Dictionary<string, string>
        {
            ["main.ts"] = """
                import { Readable, finished } from 'stream';
                const r = new Readable();
                let called = false;
                finished(r, { readable: false }, (err: any) => {
                    called = true;
                });
                r.push(null);
                console.log('called:', called);
                """
        };

        var output = TestHarness.RunModules(files, "main.ts", mode);
        // With readable: false, it should NOT fire on 'end'
        Assert.Contains("called: false", output);
    }

    #endregion

    #region stream.pipeline()

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void Stream_Pipeline_ReadableToWritable(ExecutionMode mode)
    {
        var files = new Dictionary<string, string>
        {
            ["main.ts"] = """
                import { Readable, Writable, pipeline } from 'stream';
                const chunks: string[] = [];
                const r = new Readable();
                const w = new Writable({
                    write(chunk: any, enc: any, cb: any) {
                        chunks.push(chunk);
                        cb();
                    }
                });
                pipeline(r, w, (err: any) => {
                    console.log('callback:', err === null || err === undefined ? 'ok' : 'err');
                });
                r.push('hello');
                r.push('world');
                r.push(null);
                console.log('chunks:', chunks.join(','));
                """
        };

        var output = TestHarness.RunModules(files, "main.ts", mode);
        Assert.Contains("chunks: hello,world", output);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.InterpretedOnly), MemberType = typeof(ExecutionModes))]
    public void Stream_Pipeline_ReadableTransformWritable(ExecutionMode mode)
    {
        var files = new Dictionary<string, string>
        {
            ["main.ts"] = """
                import { Readable, Transform, Writable, pipeline } from 'stream';
                const results: string[] = [];
                const r = new Readable();
                const t = new Transform({
                    transform(chunk: any, enc: any, cb: any) {
                        cb(null, chunk.toUpperCase());
                    }
                });
                const w = new Writable({
                    write(chunk: any, enc: any, cb: any) {
                        results.push(chunk);
                        cb();
                    }
                });
                pipeline(r, t, w, (err: any) => {
                    console.log('done:', err === null || err === undefined ? 'ok' : 'err');
                    console.log('results:', results.join(','));
                });
                r.push('hello');
                r.push(null);
                """
        };

        var output = TestHarness.RunModules(files, "main.ts", mode);
        Assert.Contains("done: ok", output);
        Assert.Contains("results: HELLO", output);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void Stream_Pipeline_ReturnsDestinationStream(ExecutionMode mode)
    {
        var files = new Dictionary<string, string>
        {
            ["main.ts"] = """
                import { Readable, Writable, pipeline } from 'stream';
                const r = new Readable();
                const w = new Writable();
                const result = pipeline(r, w, (err: any) => {});
                console.log(result === w);
                r.push(null);
                """
        };

        var output = TestHarness.RunModules(files, "main.ts", mode);
        Assert.Equal("true\n", output);
    }

    #endregion

    #region Readable.from()

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void Stream_ReadableFrom_ArrayObjectMode(ExecutionMode mode)
    {
        var files = new Dictionary<string, string>
        {
            ["main.ts"] = """
                import { Readable } from 'stream';
                const r = Readable.from([1, 2, 3]);
                console.log(r.readableObjectMode);
                const a = r.read();
                const b = r.read();
                const c = r.read();
                console.log(a, b, c);
                """
        };

        var output = TestHarness.RunModules(files, "main.ts", mode);
        Assert.Contains("true", output);
        Assert.Contains("1 2 3", output);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void Stream_ReadableFrom_StringArray(ExecutionMode mode)
    {
        var files = new Dictionary<string, string>
        {
            ["main.ts"] = """
                import { Readable } from 'stream';
                const r = Readable.from(['a', 'b']);
                const x = r.read();
                const y = r.read();
                console.log(x, y);
                """
        };

        var output = TestHarness.RunModules(files, "main.ts", mode);
        Assert.Contains("a b", output);
    }

    #endregion

    #region Missing Events

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void Stream_PauseEvent_FiresOnPause(ExecutionMode mode)
    {
        var files = new Dictionary<string, string>
        {
            ["main.ts"] = """
                import { Readable } from 'stream';
                const r = new Readable();
                r.on('pause', () => console.log('paused'));
                r.pause();
                """
        };

        var output = TestHarness.RunModules(files, "main.ts", mode);
        Assert.Contains("paused", output);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void Stream_ResumeEvent_FiresOnResume(ExecutionMode mode)
    {
        var files = new Dictionary<string, string>
        {
            ["main.ts"] = """
                import { Readable } from 'stream';
                const r = new Readable();
                r.on('resume', () => console.log('resumed'));
                r.pause();
                r.resume();
                """
        };

        var output = TestHarness.RunModules(files, "main.ts", mode);
        Assert.Contains("resumed", output);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void Stream_PrefinishEvent_FiresBeforeFinish(ExecutionMode mode)
    {
        var files = new Dictionary<string, string>
        {
            ["main.ts"] = """
                import { Writable } from 'stream';
                const events: string[] = [];
                const w = new Writable({
                    write(chunk: any, enc: any, cb: any) { cb(); }
                });
                w.on('prefinish', () => events.push('prefinish'));
                w.on('finish', () => events.push('finish'));
                w.end();
                console.log(events.join(','));
                """
        };

        var output = TestHarness.RunModules(files, "main.ts", mode);
        Assert.Contains("prefinish,finish", output);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.InterpretedOnly), MemberType = typeof(ExecutionModes))]
    public void Stream_AutoDestroy_DestroysAfterEnd(ExecutionMode mode)
    {
        var files = new Dictionary<string, string>
        {
            ["main.ts"] = """
                import { Writable } from 'stream';
                const events: string[] = [];
                const w = new Writable({
                    write(chunk: any, enc: any, cb: any) { cb(); },
                    autoDestroy: true
                });
                w.on('close', () => events.push('close'));
                w.on('finish', () => events.push('finish'));
                w.end();
                console.log(events.join(','));
                """
        };

        var output = TestHarness.RunModules(files, "main.ts", mode);
        Assert.Contains("finish", output);
        Assert.Contains("close", output);
    }

    #endregion

    #region highWaterMark

    [Theory]
    [MemberData(nameof(ExecutionModes.InterpretedOnly), MemberType = typeof(ExecutionModes))]
    public void Stream_HighWaterMark_PushReturnsFalse(ExecutionMode mode)
    {
        var files = new Dictionary<string, string>
        {
            ["main.ts"] = """
                import { Readable } from 'stream';
                const r = new Readable({ highWaterMark: 5 });
                const r1 = r.push('abc');
                const r2 = r.push('def');
                console.log(r1, r2);
                """
        };

        var output = TestHarness.RunModules(files, "main.ts", mode);
        Assert.Contains("true false", output);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void Stream_HighWaterMark_CustomValue(ExecutionMode mode)
    {
        var files = new Dictionary<string, string>
        {
            ["main.ts"] = """
                import { Readable } from 'stream';
                const r = new Readable({ highWaterMark: 100 });
                let allTrue = true;
                for (let i = 0; i < 10; i++) {
                    if (!r.push('data')) { allTrue = false; break; }
                }
                console.log(allTrue);
                """
        };

        var output = TestHarness.RunModules(files, "main.ts", mode);
        Assert.Contains("true", output);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.InterpretedOnly), MemberType = typeof(ExecutionModes))]
    public void Stream_HighWaterMark_ObjectModeDefault16(ExecutionMode mode)
    {
        var files = new Dictionary<string, string>
        {
            ["main.ts"] = """
                import { Readable } from 'stream';
                const r = new Readable({ objectMode: true });
                let count = 0;
                for (let i = 0; i < 20; i++) {
                    const ok = r.push(i);
                    count++;
                    if (!ok) break;
                }
                console.log(count);
                """
        };

        var output = TestHarness.RunModules(files, "main.ts", mode);
        Assert.Contains("16", output);
    }

    #endregion

    #region toArray, forEach, isReadable, isWritable

    [Theory]
    [MemberData(nameof(ExecutionModes.InterpretedOnly), MemberType = typeof(ExecutionModes))]
    public void Stream_Readable_ToArray(ExecutionMode mode)
    {
        var files = new Dictionary<string, string>
        {
            ["main.ts"] = """
                import { Readable } from 'stream';
                const r = new Readable({ objectMode: true });
                r.push(1);
                r.push(2);
                r.push(3);
                const arr = r.toArray();
                console.log(arr.length, arr[0], arr[1], arr[2]);
                """
        };

        var output = TestHarness.RunModules(files, "main.ts", mode);
        Assert.Contains("3 1 2 3", output);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.InterpretedOnly), MemberType = typeof(ExecutionModes))]
    public void Stream_Readable_ForEach(ExecutionMode mode)
    {
        var files = new Dictionary<string, string>
        {
            ["main.ts"] = """
                import { Readable } from 'stream';
                const r = new Readable({ objectMode: true });
                r.push('a');
                r.push('b');
                const items: string[] = [];
                r.forEach((chunk: any) => items.push(chunk));
                console.log(items.join(','));
                """
        };

        var output = TestHarness.RunModules(files, "main.ts", mode);
        Assert.Contains("a,b", output);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.InterpretedOnly), MemberType = typeof(ExecutionModes))]
    public void Stream_IsReadable_IsWritable(ExecutionMode mode)
    {
        var files = new Dictionary<string, string>
        {
            ["main.ts"] = """
                import { Readable, Writable } from 'stream';
                const r = new Readable();
                const w = new Writable();
                console.log(Readable.isReadable(r));
                console.log(Readable.isReadable(w));
                console.log(Writable.isWritable(w));
                console.log(Writable.isWritable(r));
                """
        };

        var output = TestHarness.RunModules(files, "main.ts", mode);
        Assert.Equal("true\nfalse\ntrue\nfalse\n", output);
    }

    #endregion

    #region map/filter

    [Theory]
    [MemberData(nameof(ExecutionModes.InterpretedOnly), MemberType = typeof(ExecutionModes))]
    public void Stream_Readable_Map(ExecutionMode mode)
    {
        var files = new Dictionary<string, string>
        {
            ["main.ts"] = """
                import { Readable, Writable } from 'stream';
                const r = new Readable({ objectMode: true });
                const mapped = r.map((x: any) => x * 2);
                const results: number[] = [];
                const w = new Writable({
                    objectMode: true,
                    write(chunk: any, enc: any, cb: any) {
                        results.push(chunk);
                        cb();
                    }
                });
                mapped.pipe(w);
                r.push(1);
                r.push(2);
                r.push(3);
                r.push(null);
                console.log(results.join(','));
                """
        };

        var output = TestHarness.RunModules(files, "main.ts", mode);
        Assert.Contains("2,4,6", output);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.InterpretedOnly), MemberType = typeof(ExecutionModes))]
    public void Stream_Readable_Filter(ExecutionMode mode)
    {
        var files = new Dictionary<string, string>
        {
            ["main.ts"] = """
                import { Readable, Writable } from 'stream';
                const r = new Readable({ objectMode: true });
                const filtered = r.filter((x: any) => x > 2);
                const results: number[] = [];
                const w = new Writable({
                    objectMode: true,
                    write(chunk: any, enc: any, cb: any) {
                        results.push(chunk);
                        cb();
                    }
                });
                filtered.pipe(w);
                r.push(1);
                r.push(2);
                r.push(3);
                r.push(4);
                r.push(null);
                console.log(results.join(','));
                """
        };

        var output = TestHarness.RunModules(files, "main.ts", mode);
        Assert.Contains("3,4", output);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.InterpretedOnly), MemberType = typeof(ExecutionModes))]
    public void Stream_Readable_MapFilter_Chaining(ExecutionMode mode)
    {
        var files = new Dictionary<string, string>
        {
            ["main.ts"] = """
                import { Readable, Writable } from 'stream';
                const r = new Readable({ objectMode: true });
                const result = r.map((x: any) => x * 2).filter((x: any) => x > 4);
                const results: number[] = [];
                const w = new Writable({
                    objectMode: true,
                    write(chunk: any, enc: any, cb: any) {
                        results.push(chunk);
                        cb();
                    }
                });
                result.pipe(w);
                r.push(1);
                r.push(2);
                r.push(3);
                r.push(4);
                r.push(null);
                console.log(results.join(','));
                """
        };

        var output = TestHarness.RunModules(files, "main.ts", mode);
        Assert.Contains("6,8", output);
    }

    #endregion

    #region addAbortSignal

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void Stream_AddAbortSignal_ReturnsStream(ExecutionMode mode)
    {
        var files = new Dictionary<string, string>
        {
            ["main.ts"] = """
                import { Readable, addAbortSignal } from 'stream';
                const r = new Readable();
                const ac = new AbortController();
                const result = addAbortSignal(ac.signal, r);
                console.log(result === r);
                """
        };

        var output = TestHarness.RunModules(files, "main.ts", mode);
        Assert.Equal("true\n", output);
    }

    #endregion
}
