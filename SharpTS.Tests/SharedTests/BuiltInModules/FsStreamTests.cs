using SharpTS.Tests.Infrastructure;
using Xunit;

namespace SharpTS.Tests.SharedTests.BuiltInModules;

/// <summary>
/// Tests for fs.createReadStream() and fs.createWriteStream().
/// </summary>
public class FsStreamTests
{
    private static string EscapePath(string path) => path.Replace("\\", "\\\\");

    #region createReadStream

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void CreateReadStream_ReadsFile(ExecutionMode mode)
    {
        var tempFile = Path.GetTempFileName();
        try
        {
            File.WriteAllText(tempFile, "hello world");
            var p = EscapePath(tempFile);

            var files = new Dictionary<string, string>
            {
                ["main.ts"] = "import * as fs from 'fs';\n" +
                    "const stream = fs.createReadStream('" + p + "', { encoding: 'utf8' });\n" +
                    "const chunks: string[] = [];\n" +
                    "stream.on('data', (chunk: string) => { chunks.push(chunk); });\n" +
                    "stream.on('end', () => { console.log(chunks.join('')); });\n"
            };

            var output = TestHarness.RunModules(files, "main.ts", mode);
            Assert.Equal("hello world\n", output);
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void CreateReadStream_EndEvent(ExecutionMode mode)
    {
        var tempFile = Path.GetTempFileName();
        try
        {
            File.WriteAllText(tempFile, "test data");
            var p = EscapePath(tempFile);

            var files = new Dictionary<string, string>
            {
                ["main.ts"] = "import * as fs from 'fs';\n" +
                    "const stream = fs.createReadStream('" + p + "', { encoding: 'utf8' });\n" +
                    "let ended = false;\n" +
                    "stream.on('data', () => {});\n" +
                    "stream.on('end', () => { ended = true; });\n" +
                    "console.log(ended);\n"
            };

            var output = TestHarness.RunModules(files, "main.ts", mode);
            Assert.Equal("true\n", output);
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void CreateReadStream_Path_Property(ExecutionMode mode)
    {
        var tempFile = Path.GetTempFileName();
        try
        {
            File.WriteAllText(tempFile, "test");
            var p = EscapePath(tempFile);

            var files = new Dictionary<string, string>
            {
                ["main.ts"] = "import * as fs from 'fs';\n" +
                    "const stream = fs.createReadStream('" + p + "');\n" +
                    "console.log(stream.path === '" + p + "');\n"
            };

            var output = TestHarness.RunModules(files, "main.ts", mode);
            Assert.Equal("true\n", output);
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    #endregion

    #region createWriteStream

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void CreateWriteStream_WritesFile(ExecutionMode mode)
    {
        var tempFile = Path.GetTempFileName();
        try
        {
            var p = EscapePath(tempFile);

            var files = new Dictionary<string, string>
            {
                ["main.ts"] = "import * as fs from 'fs';\n" +
                    "const stream = fs.createWriteStream('" + p + "');\n" +
                    "stream.write('hello');\n" +
                    "stream.write(' world');\n" +
                    "stream.end();\n" +
                    "console.log('done');\n"
            };

            var output = TestHarness.RunModules(files, "main.ts", mode);
            Assert.Equal("done\n", output);

            var content = File.ReadAllText(tempFile);
            Assert.Equal("hello world", content);
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void CreateWriteStream_End_WritesAndCloses(ExecutionMode mode)
    {
        var tempFile = Path.GetTempFileName();
        try
        {
            var p = EscapePath(tempFile);

            var files = new Dictionary<string, string>
            {
                ["main.ts"] = "import * as fs from 'fs';\n" +
                    "const stream = fs.createWriteStream('" + p + "');\n" +
                    "stream.write('first ');\n" +
                    "stream.end('last');\n" +
                    "console.log('done');\n"
            };

            var output = TestHarness.RunModules(files, "main.ts", mode);
            Assert.Equal("done\n", output);

            var content = File.ReadAllText(tempFile);
            Assert.Equal("first last", content);
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void CreateWriteStream_Path_Property(ExecutionMode mode)
    {
        var tempFile = Path.GetTempFileName();
        try
        {
            var p = EscapePath(tempFile);

            var files = new Dictionary<string, string>
            {
                ["main.ts"] = "import * as fs from 'fs';\n" +
                    "const stream = fs.createWriteStream('" + p + "');\n" +
                    "console.log(stream.path === '" + p + "');\n" +
                    "stream.end();\n"
            };

            var output = TestHarness.RunModules(files, "main.ts", mode);
            Assert.Equal("true\n", output);
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void CreateReadStream_PipeToWriteStream(ExecutionMode mode)
    {
        var srcFile = Path.GetTempFileName();
        var dstFile = Path.GetTempFileName();
        try
        {
            File.WriteAllText(srcFile, "piped content");
            var srcPath = EscapePath(srcFile);
            var dstPath = EscapePath(dstFile);

            var files = new Dictionary<string, string>
            {
                ["main.ts"] = "import * as fs from 'fs';\n" +
                    "const readStream = fs.createReadStream('" + srcPath + "');\n" +
                    "const writeStream = fs.createWriteStream('" + dstPath + "');\n" +
                    "readStream.pipe(writeStream);\n" +
                    "console.log('done');\n"
            };

            var output = TestHarness.RunModules(files, "main.ts", mode);
            Assert.Equal("done\n", output);

            var content = File.ReadAllText(dstFile);
            Assert.Equal("piped content", content);
        }
        finally
        {
            File.Delete(srcFile);
            File.Delete(dstFile);
        }
    }

    #endregion
}
