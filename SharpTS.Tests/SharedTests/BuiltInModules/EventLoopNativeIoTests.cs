using SharpTS.Tests.Infrastructure;
using Xunit;

namespace SharpTS.Tests.SharedTests.BuiltInModules;

/// <summary>
/// Event-loop liveness contract for in-flight native I/O: a program whose only pending work is
/// a slow built-in async operation must NOT be abandoned by the interpreter's "never-settling
/// promise" quiescence heuristic (WaitForPromise / RunEventLoop give up after 250ms without
/// visible work). BuiltInAsyncMethod Refs the loop for the lifetime of the native task — the
/// promise-API counterpart of the callback-API convention from #205.
///
/// Regression test for the FsPromisesNamespace_Stat empty-output CI failure: on a slow runner
/// (AV scan, cold disk) the first fs write exceeded the 250ms quiescence window, the top-level
/// promise was abandoned, and the program exited silently with no output. The injected latency
/// (SHARPTS_TEST_FS_ASYNC_DELAY_MS) reproduces that timing deterministically: without the Ref
/// accounting this test fails 100% of the time with empty output.
/// </summary>
public class EventLoopNativeIoTests
{
    [Fact]
    public void SlowNativeIo_KeepsInterpreterEventLoopAlive()
    {
        // 600ms > 2x the 250ms quiescence window. Env var is process-wide, so concurrently
        // running fs tests also get the latency during this test's window — they still pass,
        // just slower; the value is kept as small as decisively-over-the-window allows.
        Environment.SetEnvironmentVariable("SHARPTS_TEST_FS_ASYNC_DELAY_MS", "600");
        try
        {
            var files = new Dictionary<string, string>
            {
                ["main.ts"] = """
                    import * as fs from 'fs';

                    async function test() {
                        await fs.promises.writeFile('test-eventloop-slowio.txt', 'content');
                        const stats = await fs.promises.stat('test-eventloop-slowio.txt');
                        await fs.promises.unlink('test-eventloop-slowio.txt');
                        console.log(stats.isFile());
                    }

                    test();
                """
            };

            var output = TestHarness.RunModules(files, "main.ts", ExecutionMode.Interpreted);
            Assert.Equal("true\n", output);
        }
        finally
        {
            Environment.SetEnvironmentVariable("SHARPTS_TEST_FS_ASYNC_DELAY_MS", null);
        }
    }
}
