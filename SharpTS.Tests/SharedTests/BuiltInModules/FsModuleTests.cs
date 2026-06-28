using SharpTS.Tests.Infrastructure;
using Xunit;

namespace SharpTS.Tests.SharedTests.BuiltInModules;

/// <summary>
/// Tests for the built-in 'fs' module (sync APIs only).
/// </summary>
public class FsModuleTests
{
    private static string Uid() => Guid.NewGuid().ToString("N")[..8];
    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void Fs_ExistsSync_ReturnsTrueForExistingFile(ExecutionMode mode)
    {
        var files = new Dictionary<string, string>
        {
            ["main.ts"] = """
                import * as fs from 'fs';
                import * as path from 'path';
                // main.ts exists since we're running it — use __dirname for CWD-independent resolution
                console.log(fs.existsSync(path.join(__dirname, 'main.ts')));
                """
        };

        var output = TestHarness.RunModules(files, "main.ts", mode);
        Assert.Equal("true\n", output);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void Fs_ExistsSync_ReturnsFalseForNonexistentFile(ExecutionMode mode)
    {
        var files = new Dictionary<string, string>
        {
            ["main.ts"] = """
                import * as fs from 'fs';
                console.log(fs.existsSync('nonexistent_file_12345.txt'));
                """
        };

        var output = TestHarness.RunModules(files, "main.ts", mode);
        Assert.Equal("false\n", output);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void Fs_WriteFileSync_And_ReadFileSync_WorkTogether(ExecutionMode mode)
    {
        var uid = Uid();
        var files = new Dictionary<string, string>
        {
            ["main.ts"] = $$"""
                import * as fs from 'fs';
                import * as os from 'os';
                const testFile = os.tmpdir() + '/test_write_read_{{uid}}.txt';
                const testContent = 'Hello, SharpTS!';

                fs.writeFileSync(testFile, testContent);
                const content = fs.readFileSync(testFile, 'utf8');
                console.log(content === testContent);

                // Cleanup
                fs.unlinkSync(testFile);
                """
        };

        var output = TestHarness.RunModules(files, "main.ts", mode);
        Assert.Equal("true\n", output);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void Fs_AppendFileSync_AppendsToFile(ExecutionMode mode)
    {
        var uid = Uid();
        var files = new Dictionary<string, string>
        {
            ["main.ts"] = $$"""
                import * as fs from 'fs';
                import * as os from 'os';
                const testFile = os.tmpdir() + '/test_append_{{uid}}.txt';

                fs.writeFileSync(testFile, 'Line1');
                fs.appendFileSync(testFile, '\nLine2');
                const content = fs.readFileSync(testFile, 'utf8');
                console.log(content.includes('Line1'));
                console.log(content.includes('Line2'));

                // Cleanup
                fs.unlinkSync(testFile);
                """
        };

        var output = TestHarness.RunModules(files, "main.ts", mode);
        Assert.Equal("true\ntrue\n", output);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void Fs_MkdirSync_And_RmdirSync_WorkTogether(ExecutionMode mode)
    {
        var uid = Uid();
        var files = new Dictionary<string, string>
        {
            ["main.ts"] = $$"""
                import * as fs from 'fs';
                import * as os from 'os';
                const testDir = os.tmpdir() + '/test_dir_fs_{{uid}}';

                fs.mkdirSync(testDir);
                console.log(fs.existsSync(testDir));

                fs.rmdirSync(testDir);
                console.log(fs.existsSync(testDir));
                """
        };

        var output = TestHarness.RunModules(files, "main.ts", mode);
        Assert.Equal("true\nfalse\n", output);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void Fs_ReaddirSync_ListsDirectoryContents(ExecutionMode mode)
    {
        var uid = Uid();
        var files = new Dictionary<string, string>
        {
            ["main.ts"] = $$"""
                import * as fs from 'fs';
                import * as os from 'os';
                const testDir = os.tmpdir() + '/test_readdir_{{uid}}';

                fs.mkdirSync(testDir);
                fs.writeFileSync(testDir + '/file1.txt', 'content1');
                fs.writeFileSync(testDir + '/file2.txt', 'content2');

                const entries = fs.readdirSync(testDir);
                console.log(entries.length);

                // Cleanup
                fs.unlinkSync(testDir + '/file1.txt');
                fs.unlinkSync(testDir + '/file2.txt');
                fs.rmdirSync(testDir);
                """
        };

        var output = TestHarness.RunModules(files, "main.ts", mode);
        Assert.Equal("2\n", output);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void Fs_StatSync_ReturnsFileInfo(ExecutionMode mode)
    {
        var uid = Uid();
        var files = new Dictionary<string, string>
        {
            ["main.ts"] = $$"""
                import * as fs from 'fs';
                import * as os from 'os';
                const testFile = os.tmpdir() + '/test_stat_{{uid}}.txt';
                const content = 'Test content for stat';

                fs.writeFileSync(testFile, content);
                const stat = fs.statSync(testFile);

                console.log(stat.isFile());
                console.log(stat.isDirectory());
                console.log(stat.size > 0);

                // Cleanup
                fs.unlinkSync(testFile);
                """
        };

        var output = TestHarness.RunModules(files, "main.ts", mode);
        Assert.Equal("true\nfalse\ntrue\n", output);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void Fs_StatSync_ReturnsDirectoryInfo(ExecutionMode mode)
    {
        var uid = Uid();
        var files = new Dictionary<string, string>
        {
            ["main.ts"] = $$"""
                import * as fs from 'fs';
                import * as os from 'os';
                const testDir = os.tmpdir() + '/test_stat_dir_{{uid}}';

                fs.mkdirSync(testDir);
                const stat = fs.statSync(testDir);

                console.log(stat.isFile());
                console.log(stat.isDirectory());

                // Cleanup
                fs.rmdirSync(testDir);
                """
        };

        var output = TestHarness.RunModules(files, "main.ts", mode);
        Assert.Equal("false\ntrue\n", output);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void Fs_CopyFileSync_CopiesFile(ExecutionMode mode)
    {
        var uid = Uid();
        var files = new Dictionary<string, string>
        {
            ["main.ts"] = $$"""
                import * as fs from 'fs';
                import * as os from 'os';
                const srcFile = os.tmpdir() + '/test_copy_src_{{uid}}.txt';
                const destFile = os.tmpdir() + '/test_copy_dest_{{uid}}.txt';
                const content = 'Content to copy';

                fs.writeFileSync(srcFile, content);
                fs.copyFileSync(srcFile, destFile);

                console.log(fs.existsSync(destFile));
                const copiedContent = fs.readFileSync(destFile, 'utf8');
                console.log(copiedContent === content);

                // Cleanup
                fs.unlinkSync(srcFile);
                fs.unlinkSync(destFile);
                """
        };

        var output = TestHarness.RunModules(files, "main.ts", mode);
        Assert.Equal("true\ntrue\n", output);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void Fs_RenameSync_RenamesFile(ExecutionMode mode)
    {
        var uid = Uid();
        var files = new Dictionary<string, string>
        {
            ["main.ts"] = $$"""
                import * as fs from 'fs';
                import * as os from 'os';
                const oldName = os.tmpdir() + '/test_rename_old_{{uid}}.txt';
                const newName = os.tmpdir() + '/test_rename_new_{{uid}}.txt';

                fs.writeFileSync(oldName, 'content');
                fs.renameSync(oldName, newName);

                console.log(fs.existsSync(oldName));
                console.log(fs.existsSync(newName));

                // Cleanup
                fs.unlinkSync(newName);
                """
        };

        var output = TestHarness.RunModules(files, "main.ts", mode);
        Assert.Equal("false\ntrue\n", output);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void Fs_UnlinkSync_DeletesFile(ExecutionMode mode)
    {
        var uid = Uid();
        var files = new Dictionary<string, string>
        {
            ["main.ts"] = $$"""
                import * as fs from 'fs';
                import * as os from 'os';
                const testFile = os.tmpdir() + '/test_unlink_{{uid}}.txt';

                fs.writeFileSync(testFile, 'content');
                console.log(fs.existsSync(testFile));

                fs.unlinkSync(testFile);
                console.log(fs.existsSync(testFile));
                """
        };

        var output = TestHarness.RunModules(files, "main.ts", mode);
        Assert.Equal("true\nfalse\n", output);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void Fs_AccessSync_DoesNotThrowForExistingFile(ExecutionMode mode)
    {
        var uid = Uid();
        var files = new Dictionary<string, string>
        {
            ["main.ts"] = $$"""
                import * as fs from 'fs';
                import * as os from 'os';
                const testFile = os.tmpdir() + '/test_access_{{uid}}.txt';

                fs.writeFileSync(testFile, 'content');

                let threw = false;
                try {
                    fs.accessSync(testFile);
                } catch (e) {
                    threw = true;
                }
                console.log(threw);

                // Cleanup
                fs.unlinkSync(testFile);
                """
        };

        var output = TestHarness.RunModules(files, "main.ts", mode);
        Assert.Equal("false\n", output);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void Fs_AccessSync_ThrowsForNonexistentFile(ExecutionMode mode)
    {
        var files = new Dictionary<string, string>
        {
            ["main.ts"] = """
                import * as fs from 'fs';

                let threw = false;
                try {
                    fs.accessSync('nonexistent_file_access_test.txt');
                } catch (e) {
                    threw = true;
                }
                console.log(threw);
                """
        };

        var output = TestHarness.RunModules(files, "main.ts", mode);
        Assert.Equal("true\n", output);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void Fs_RmdirSync_WithRecursive_DeletesNestedDirectories(ExecutionMode mode)
    {
        var uniqueDir = $"test_rmdir_recursive_{Guid.NewGuid():N}";
        var files = new Dictionary<string, string>
        {
            ["main.ts"] = $$"""
                import * as fs from 'fs';
                import * as os from 'os';
                const testDir = os.tmpdir() + '/{{uniqueDir}}';

                fs.mkdirSync(testDir);
                fs.mkdirSync(testDir + '/subdir');
                fs.writeFileSync(testDir + '/subdir/file.txt', 'content');

                fs.rmdirSync(testDir, { recursive: true });
                console.log(fs.existsSync(testDir));
                """
        };

        var output = TestHarness.RunModules(files, "main.ts", mode);
        Assert.Equal("false\n", output);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void Fs_Constants_ExportsAccessConstants(ExecutionMode mode)
    {
        var files = new Dictionary<string, string>
        {
            ["main.ts"] = """
                import * as fs from 'fs';
                console.log(fs.constants.F_OK === 0);
                console.log(fs.constants.R_OK === 4);
                console.log(fs.constants.W_OK === 2);
                console.log(fs.constants.X_OK === 1);
                """
        };

        var output = TestHarness.RunModules(files, "main.ts", mode);
        Assert.Equal("true\ntrue\ntrue\ntrue\n", output);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void Fs_TruncateSync_TruncatesFile(ExecutionMode mode)
    {
        var uid = Uid();
        var files = new Dictionary<string, string>
        {
            ["main.ts"] = $$"""
                import * as fs from 'fs';
                import * as os from 'os';
                const testFile = os.tmpdir() + '/test_truncate_{{uid}}.txt';

                fs.writeFileSync(testFile, 'Hello World!');
                const beforeSize = fs.statSync(testFile).size;
                console.log(beforeSize > 0);

                fs.truncateSync(testFile, 5);
                const afterSize = fs.statSync(testFile).size;
                console.log(afterSize === 5);

                const content = fs.readFileSync(testFile, 'utf8');
                console.log(content === 'Hello');

                // Cleanup
                fs.unlinkSync(testFile);
                """
        };

        var output = TestHarness.RunModules(files, "main.ts", mode);
        Assert.Equal("true\ntrue\ntrue\n", output);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void Fs_TruncateSync_ExtendsFileWithZeros(ExecutionMode mode)
    {
        var uid = Uid();
        var files = new Dictionary<string, string>
        {
            ["main.ts"] = $$"""
                import * as fs from 'fs';
                import * as os from 'os';
                const testFile = os.tmpdir() + '/test_truncate_extend_{{uid}}.txt';

                fs.writeFileSync(testFile, 'Hi');
                fs.truncateSync(testFile, 10);

                const stat = fs.statSync(testFile);
                console.log(stat.size === 10);

                // Cleanup
                fs.unlinkSync(testFile);
                """
        };

        var output = TestHarness.RunModules(files, "main.ts", mode);
        Assert.Equal("true\n", output);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void Fs_SymlinkSync_CreatesSymbolicLink(ExecutionMode mode)
    {
        var uid = Uid();
        var files = new Dictionary<string, string>
        {
            ["main.ts"] = $$"""
                import * as fs from 'fs';
                import * as os from 'os';
                const testFile = os.tmpdir() + '/test_symlink_target_{{uid}}.txt';
                const linkPath = os.tmpdir() + '/test_symlink_link_{{uid}}.txt';

                fs.writeFileSync(testFile, 'content');
                fs.symlinkSync(testFile, linkPath);

                console.log(fs.existsSync(linkPath));

                // Cleanup
                fs.unlinkSync(linkPath);
                fs.unlinkSync(testFile);
                """
        };

        var output = TestHarness.RunModules(files, "main.ts", mode);
        Assert.Equal("true\n", output);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void Fs_RealpathSync_ResolvesAbsolutePath(ExecutionMode mode)
    {
        var uid = Uid();
        var files = new Dictionary<string, string>
        {
            ["main.ts"] = $$"""
                import * as fs from 'fs';
                import * as os from 'os';
                const testFile = os.tmpdir() + '/test_realpath_{{uid}}.txt';

                fs.writeFileSync(testFile, 'content');

                const realPath = fs.realpathSync(testFile);
                // realPath should be an absolute path containing the filename
                console.log(realPath.includes('test_realpath_{{uid}}'));
                console.log(realPath.length > 0);

                // Cleanup
                fs.unlinkSync(testFile);
                """
        };

        var output = TestHarness.RunModules(files, "main.ts", mode);
        Assert.Equal("true\ntrue\n", output);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void Fs_UtimesSync_SetsFileTimes(ExecutionMode mode)
    {
        var uid = Uid();
        var files = new Dictionary<string, string>
        {
            ["main.ts"] = $$"""
                import * as fs from 'fs';
                import * as os from 'os';
                const testFile = os.tmpdir() + '/test_utimes_{{uid}}.txt';

                fs.writeFileSync(testFile, 'content');

                // Set times to Unix epoch + 1000000 seconds
                const timestamp = 1000000;
                fs.utimesSync(testFile, timestamp, timestamp);

                // File should still exist and be readable
                console.log(fs.existsSync(testFile));

                // Cleanup
                fs.unlinkSync(testFile);
                """
        };

        var output = TestHarness.RunModules(files, "main.ts", mode);
        Assert.Equal("true\n", output);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void Fs_LstatSync_ReturnsSymlinkInfo(ExecutionMode mode)
    {
        var uid = Uid();
        var files = new Dictionary<string, string>
        {
            ["main.ts"] = $$"""
                import * as fs from 'fs';
                import * as os from 'os';
                const testFile = os.tmpdir() + '/test_lstat_target_{{uid}}.txt';
                const linkPath = os.tmpdir() + '/test_lstat_link_{{uid}}.txt';

                fs.writeFileSync(testFile, 'content');
                fs.symlinkSync(testFile, linkPath);

                const stat = fs.lstatSync(linkPath);
                console.log(stat.isSymbolicLink() === true);

                // Cleanup
                fs.unlinkSync(linkPath);
                fs.unlinkSync(testFile);
                """
        };

        var output = TestHarness.RunModules(files, "main.ts", mode);
        Assert.Equal("true\n", output);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void Fs_ReaddirSync_WithFileTypes_ReturnsDirentObjects(ExecutionMode mode)
    {
        var uid = Uid();
        var files = new Dictionary<string, string>
        {
            ["main.ts"] = $$"""
                import * as fs from 'fs';
                import * as os from 'os';
                const testDir = os.tmpdir() + '/test_readdir_dirent_{{uid}}';

                fs.mkdirSync(testDir);
                fs.writeFileSync(testDir + '/file.txt', 'content');
                fs.mkdirSync(testDir + '/subdir');

                const entries: any = fs.readdirSync(testDir, { withFileTypes: true });
                console.log(entries.length === 2);

                // Find the file entry - check each entry manually
                let fileEntry: any = null;
                let dirEntry: any = null;
                for (let i = 0; i < entries.length; i++) {
                    const e = entries[i];
                    if (e.name === 'file.txt') {
                        fileEntry = e;
                    }
                    if (e.name === 'subdir') {
                        dirEntry = e;
                    }
                }
                console.log(fileEntry !== null);
                console.log(fileEntry.isFile() === true);
                console.log(fileEntry.isDirectory() === false);

                console.log(dirEntry !== null);
                console.log(dirEntry.isDirectory() === true);

                // Cleanup
                fs.unlinkSync(testDir + '/file.txt');
                fs.rmdirSync(testDir + '/subdir');
                fs.rmdirSync(testDir);
                """
        };

        var output = TestHarness.RunModules(files, "main.ts", mode);
        Assert.Equal("true\ntrue\ntrue\ntrue\ntrue\ntrue\n", output);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void Fs_ChmodSync_DoesNotThrowOnUnix(ExecutionMode mode)
    {
        // This test checks that chmodSync doesn't throw on Unix
        // On Windows it's a no-op
        var uid = Uid();
        var files = new Dictionary<string, string>
        {
            ["main.ts"] = $$"""
                import * as fs from 'fs';
                import * as os from 'os';
                const testFile = os.tmpdir() + '/test_chmod_{{uid}}.txt';

                fs.writeFileSync(testFile, 'content');

                let threw = false;
                try {
                    fs.chmodSync(testFile, 420);
                } catch (e) {
                    threw = true;
                }
                console.log(threw === false);

                // Cleanup
                fs.unlinkSync(testFile);
                """
        };

        var output = TestHarness.RunModules(files, "main.ts", mode);
        Assert.Equal("true\n", output);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void Fs_ReadlinkSync_ThrowsForNonSymlink(ExecutionMode mode)
    {
        var uid = Uid();
        var files = new Dictionary<string, string>
        {
            ["main.ts"] = $$"""
                import * as fs from 'fs';
                import * as os from 'os';
                const testFile = os.tmpdir() + '/test_readlink_regular_{{uid}}.txt';

                fs.writeFileSync(testFile, 'content');

                let threw = false;
                try {
                    fs.readlinkSync(testFile);
                } catch (e) {
                    threw = true;
                }
                console.log(threw);

                // Cleanup
                fs.unlinkSync(testFile);
                """
        };

        var output = TestHarness.RunModules(files, "main.ts", mode);
        Assert.Equal("true\n", output);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void Fs_TruncateSync_ThrowsForNonexistentFile(ExecutionMode mode)
    {
        var files = new Dictionary<string, string>
        {
            ["main.ts"] = """
                import * as fs from 'fs';

                let threw = false;
                try {
                    fs.truncateSync('nonexistent_truncate_test.txt', 0);
                } catch (e) {
                    threw = true;
                }
                console.log(threw);
                """
        };

        var output = TestHarness.RunModules(files, "main.ts", mode);
        Assert.Equal("true\n", output);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void Fs_RealpathSync_ThrowsForNonexistentFile(ExecutionMode mode)
    {
        var files = new Dictionary<string, string>
        {
            ["main.ts"] = """
                import * as fs from 'fs';

                let threw = false;
                try {
                    fs.realpathSync('nonexistent_realpath_test.txt');
                } catch (e) {
                    threw = true;
                }
                console.log(threw);
                """
        };

        var output = TestHarness.RunModules(files, "main.ts", mode);
        Assert.Equal("true\n", output);
    }

    #region File Descriptor APIs

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void Fs_OpenSync_ReturnsFileDescriptor(ExecutionMode mode)
    {
        var uid = Uid();
        var files = new Dictionary<string, string>
        {
            ["main.ts"] = $$"""
                import * as fs from 'fs';
                import * as os from 'os';
                const testFile = os.tmpdir() + '/test_open_fd_{{uid}}.txt';

                fs.writeFileSync(testFile, 'content');
                const fd = fs.openSync(testFile, 'r');
                console.log(typeof fd === 'number');
                console.log(fd >= 3); // fd 0-2 are reserved

                fs.closeSync(fd);
                fs.unlinkSync(testFile);
                """
        };

        var output = TestHarness.RunModules(files, "main.ts", mode);
        Assert.Equal("true\ntrue\n", output);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void Fs_CloseSync_ClosesDescriptor(ExecutionMode mode)
    {
        var uid = Uid();
        var files = new Dictionary<string, string>
        {
            ["main.ts"] = $$"""
                import * as fs from 'fs';
                import * as os from 'os';
                const testFile = os.tmpdir() + '/test_close_fd_{{uid}}.txt';

                fs.writeFileSync(testFile, 'content');
                const fd = fs.openSync(testFile, 'r');

                let threw = false;
                try {
                    fs.closeSync(fd);
                } catch (e) {
                    threw = true;
                }
                console.log(threw === false);

                fs.unlinkSync(testFile);
                """
        };

        var output = TestHarness.RunModules(files, "main.ts", mode);
        Assert.Equal("true\n", output);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void Fs_CloseSync_ThrowsForInvalidFd(ExecutionMode mode)
    {
        var files = new Dictionary<string, string>
        {
            ["main.ts"] = """
                import * as fs from 'fs';

                let threw = false;
                try {
                    fs.closeSync(99999);
                } catch (e) {
                    threw = true;
                }
                console.log(threw);
                """
        };

        var output = TestHarness.RunModules(files, "main.ts", mode);
        Assert.Equal("true\n", output);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void Fs_ReadSync_ReadsIntoBuffer(ExecutionMode mode)
    {
        var uid = Uid();
        var files = new Dictionary<string, string>
        {
            ["main.ts"] = $$"""
                import * as fs from 'fs';
                import * as os from 'os';
                import { Buffer } from 'buffer';
                const testFile = os.tmpdir() + '/test_read_fd_{{uid}}.txt';
                const content = 'Hello, World!';

                fs.writeFileSync(testFile, content);
                const fd = fs.openSync(testFile, 'r');
                const buffer = Buffer.alloc(5);
                const bytesRead = fs.readSync(fd, buffer, 0, 5, 0);

                console.log(bytesRead === 5);
                console.log(buffer.toString() === 'Hello');

                fs.closeSync(fd);
                fs.unlinkSync(testFile);
                """
        };

        var output = TestHarness.RunModules(files, "main.ts", mode);
        Assert.Equal("true\ntrue\n", output);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void Fs_WriteSync_WritesFromBuffer(ExecutionMode mode)
    {
        var uid = Uid();
        var files = new Dictionary<string, string>
        {
            ["main.ts"] = $$"""
                import * as fs from 'fs';
                import * as os from 'os';
                import { Buffer } from 'buffer';
                const testFile = os.tmpdir() + '/test_write_fd_{{uid}}.txt';

                const fd = fs.openSync(testFile, 'w');
                const buffer = Buffer.from('Hello');
                const bytesWritten = fs.writeSync(fd, buffer);

                console.log(bytesWritten === 5);
                fs.closeSync(fd);

                const content = fs.readFileSync(testFile, 'utf8');
                console.log(content === 'Hello');

                fs.unlinkSync(testFile);
                """
        };

        var output = TestHarness.RunModules(files, "main.ts", mode);
        Assert.Equal("true\ntrue\n", output);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void Fs_FstatSync_ReturnsStats(ExecutionMode mode)
    {
        var uid = Uid();
        var files = new Dictionary<string, string>
        {
            ["main.ts"] = $$"""
                import * as fs from 'fs';
                import * as os from 'os';
                const testFile = os.tmpdir() + '/test_fstat_{{uid}}.txt';

                fs.writeFileSync(testFile, '12345');
                const fd = fs.openSync(testFile, 'r');
                const stat = fs.fstatSync(fd);

                console.log(stat.isFile() === true);
                console.log(stat.isDirectory() === false);
                console.log(stat.size === 5);

                fs.closeSync(fd);
                fs.unlinkSync(testFile);
                """
        };

        var output = TestHarness.RunModules(files, "main.ts", mode);
        Assert.Equal("true\ntrue\ntrue\n", output);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void Fs_FtruncateSync_TruncatesFile(ExecutionMode mode)
    {
        var uid = Uid();
        var files = new Dictionary<string, string>
        {
            ["main.ts"] = $$"""
                import * as fs from 'fs';
                import * as os from 'os';
                const testFile = os.tmpdir() + '/test_ftruncate_{{uid}}.txt';

                fs.writeFileSync(testFile, 'Hello World!');
                const fd = fs.openSync(testFile, 'r+');

                fs.ftruncateSync(fd, 5);
                fs.closeSync(fd);

                const content = fs.readFileSync(testFile, 'utf8');
                console.log(content === 'Hello');

                fs.unlinkSync(testFile);
                """
        };

        var output = TestHarness.RunModules(files, "main.ts", mode);
        Assert.Equal("true\n", output);
    }

    #endregion

    #region Directory Utilities

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void Fs_MkdtempSync_CreatesUniqueDirectory(ExecutionMode mode)
    {
        var files = new Dictionary<string, string>
        {
            ["main.ts"] = """
                import * as fs from 'fs';

                const tempDir = fs.mkdtempSync('test-');
                console.log(tempDir.includes('test-'));
                console.log(fs.existsSync(tempDir));

                // Cleanup
                fs.rmdirSync(tempDir);
                """
        };

        var output = TestHarness.RunModules(files, "main.ts", mode);
        Assert.Equal("true\ntrue\n", output);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void Fs_ReaddirSync_Recursive_ListsAllEntries(ExecutionMode mode)
    {
        var uid = Uid();
        var files = new Dictionary<string, string>
        {
            ["main.ts"] = $$"""
                import * as fs from 'fs';
                import * as os from 'os';
                const testDir = os.tmpdir() + '/test_readdir_recursive_{{uid}}';

                fs.mkdirSync(testDir);
                fs.mkdirSync(testDir + '/subdir');
                fs.writeFileSync(testDir + '/file.txt', 'content');
                fs.writeFileSync(testDir + '/subdir/nested.txt', 'content');

                const entries = fs.readdirSync(testDir, { recursive: true });
                // Should have at least: file.txt, subdir, subdir/nested.txt
                console.log(entries.length >= 3);

                // Cleanup
                fs.unlinkSync(testDir + '/file.txt');
                fs.unlinkSync(testDir + '/subdir/nested.txt');
                fs.rmdirSync(testDir + '/subdir');
                fs.rmdirSync(testDir);
                """
        };

        var output = TestHarness.RunModules(files, "main.ts", mode);
        Assert.Equal("true\n", output);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void Fs_OpendirSync_ReturnsDir(ExecutionMode mode)
    {
        var uid = Uid();
        var files = new Dictionary<string, string>
        {
            ["main.ts"] = $$"""
                import * as fs from 'fs';
                import * as os from 'os';
                const testDir = os.tmpdir() + '/test_opendir_{{uid}}';

                fs.mkdirSync(testDir);
                fs.writeFileSync(testDir + '/file.txt', 'content');

                const dir: any = fs.opendirSync(testDir);
                console.log(dir.path === testDir);

                const entry = dir.readSync();
                console.log(entry !== null);
                console.log(entry.name === 'file.txt');

                dir.closeSync();

                // Cleanup
                fs.unlinkSync(testDir + '/file.txt');
                fs.rmdirSync(testDir);
                """
        };

        var output = TestHarness.RunModules(files, "main.ts", mode);
        Assert.Equal("true\ntrue\ntrue\n", output);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void Fs_Dir_ReadSync_ReturnsNullWhenDone(ExecutionMode mode)
    {
        var uid = Uid();
        var files = new Dictionary<string, string>
        {
            ["main.ts"] = $$"""
                import * as fs from 'fs';
                import * as os from 'os';
                const testDir = os.tmpdir() + '/test_dir_readall_{{uid}}';

                fs.mkdirSync(testDir);
                fs.writeFileSync(testDir + '/only.txt', 'content');

                const dir: any = fs.opendirSync(testDir);

                // First read should return the file
                const entry1 = dir.readSync();
                console.log(entry1 !== null);

                // Second read should return null
                const entry2 = dir.readSync();
                console.log(entry2 === null);

                dir.closeSync();

                // Cleanup
                fs.unlinkSync(testDir + '/only.txt');
                fs.rmdirSync(testDir);
                """
        };

        var output = TestHarness.RunModules(files, "main.ts", mode);
        Assert.Equal("true\ntrue\n", output);
    }

    #endregion

    #region Hard Links

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void Fs_LinkSync_CreatesHardLink(ExecutionMode mode)
    {
        var uid = Uid();
        var files = new Dictionary<string, string>
        {
            ["main.ts"] = $$"""
                import * as fs from 'fs';
                import * as os from 'os';
                const srcFile = os.tmpdir() + '/test_link_src_{{uid}}.txt';
                const linkFile = os.tmpdir() + '/test_link_dest_{{uid}}.txt';
                const content = 'Hello, Hard Link!';

                fs.writeFileSync(srcFile, content);
                fs.linkSync(srcFile, linkFile);

                console.log(fs.existsSync(linkFile));
                const linkContent = fs.readFileSync(linkFile, 'utf8');
                console.log(linkContent === content);

                // Cleanup
                fs.unlinkSync(srcFile);
                fs.unlinkSync(linkFile);
                """
        };

        var output = TestHarness.RunModules(files, "main.ts", mode);
        Assert.Equal("true\ntrue\n", output);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void Fs_LinkSync_ThrowsForMissingSource(ExecutionMode mode)
    {
        var files = new Dictionary<string, string>
        {
            ["main.ts"] = """
                import * as fs from 'fs';

                let threw = false;
                try {
                    fs.linkSync('nonexistent_source.txt', 'link.txt');
                } catch (e) {
                    threw = true;
                }
                console.log(threw);
                """
        };

        var output = TestHarness.RunModules(files, "main.ts", mode);
        Assert.Equal("true\n", output);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void Fs_LinkSync_ThrowsForExistingDest(ExecutionMode mode)
    {
        var uid = Uid();
        var files = new Dictionary<string, string>
        {
            ["main.ts"] = $$"""
                import * as fs from 'fs';
                import * as os from 'os';
                const srcFile = os.tmpdir() + '/test_link_src2_{{uid}}.txt';
                const destFile = os.tmpdir() + '/test_link_dest2_{{uid}}.txt';

                fs.writeFileSync(srcFile, 'source');
                fs.writeFileSync(destFile, 'dest');

                let threw = false;
                try {
                    fs.linkSync(srcFile, destFile);
                } catch (e) {
                    threw = true;
                }
                console.log(threw);

                // Cleanup
                fs.unlinkSync(srcFile);
                fs.unlinkSync(destFile);
                """
        };

        var output = TestHarness.RunModules(files, "main.ts", mode);
        Assert.Equal("true\n", output);
    }

    #endregion

    #region Mixed Module Imports

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void MixedModuleImports_WorkTogether(ExecutionMode mode)
    {
        var uid = Uid();
        var files = new Dictionary<string, string>
        {
            ["main.ts"] = $$"""
                import * as path from 'path';
                import * as os from 'os';
                import * as fs from 'fs';

                const tempDir = os.tmpdir();
                const testFile = path.join(tempDir, 'sharpts_test_mixed_{{uid}}.txt');
                fs.writeFileSync(testFile, 'mixed test');
                const content = fs.readFileSync(testFile, 'utf8');
                console.log(content);
                fs.unlinkSync(testFile);
                """
        };

        var output = TestHarness.RunModules(files, "main.ts", mode);
        Assert.Equal("mixed test\n", output);
    }

    #endregion

    #region Callback-async parity (facade-derived; #969/#970)

    // Before the primitive:fs migration the compiled emitter had no callback-async
    // fs at all — `fs.readFile(path, cb)` did not compile. The TS facade now derives
    // the callback forms from the promise primitives, so they run identically in
    // both modes. These tests pin that parity.

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void Fs_CallbackReadFile_ReturnsData(ExecutionMode mode)
    {
        var uid = Uid();
        var files = new Dictionary<string, string>
        {
            ["main.ts"] = $$"""
                import * as fs from 'fs';
                import * as os from 'os';
                const file = os.tmpdir() + '/cb_read_{{uid}}.txt';
                fs.writeFileSync(file, 'callback-data');
                fs.readFile(file, 'utf8', (err: any, data: any) => {
                    console.log(err ? 'ERR' : ('DATA:' + data));
                    fs.unlinkSync(file);
                });
                """
        };

        var output = TestHarness.RunModules(files, "main.ts", mode);
        Assert.Equal("DATA:callback-data\n", output);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void Fs_CallbackReadFile_NoEncoding_ReturnsBuffer(ExecutionMode mode)
    {
        var uid = Uid();
        var files = new Dictionary<string, string>
        {
            ["main.ts"] = $$"""
                import * as fs from 'fs';
                import * as os from 'os';
                const file = os.tmpdir() + '/cb_buf_{{uid}}.bin';
                fs.writeFileSync(file, 'abc');
                fs.readFile(file, (err: any, data: any) => {
                    // No encoding => Buffer; toString() recovers the text.
                    console.log(err ? 'ERR' : (data.length + ':' + data.toString()));
                    fs.unlinkSync(file);
                });
                """
        };

        var output = TestHarness.RunModules(files, "main.ts", mode);
        Assert.Equal("3:abc\n", output);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void Fs_CallbackWriteFile_ThenReadBack(ExecutionMode mode)
    {
        var uid = Uid();
        var files = new Dictionary<string, string>
        {
            ["main.ts"] = $$"""
                import * as fs from 'fs';
                import * as os from 'os';
                const file = os.tmpdir() + '/cb_write_{{uid}}.txt';
                fs.writeFile(file, 'written-via-cb', (err: any) => {
                    if (err) { console.log('WRITE-ERR'); return; }
                    console.log('READBACK:' + fs.readFileSync(file, 'utf8'));
                    fs.unlinkSync(file);
                });
                """
        };

        var output = TestHarness.RunModules(files, "main.ts", mode);
        Assert.Equal("READBACK:written-via-cb\n", output);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void Fs_CallbackReadFile_MissingFile_PassesError(ExecutionMode mode)
    {
        var uid = Uid();
        var files = new Dictionary<string, string>
        {
            ["main.ts"] = $$"""
                import * as fs from 'fs';
                import * as os from 'os';
                const missing = os.tmpdir() + '/does_not_exist_{{uid}}.txt';
                fs.readFile(missing, 'utf8', (err: any, data: any) => {
                    console.log('ERR:' + (err ? 'yes' : 'no'));
                });
                """
        };

        var output = TestHarness.RunModules(files, "main.ts", mode);
        Assert.Equal("ERR:yes\n", output);
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void Fs_PromisesReadFile_RoundTrips(ExecutionMode mode)
    {
        // fs.promises.write/readFile round-trips through await in both modes and
        // must resolve byte-identically. (The interpreter backs these with real
        // background I/O; the compiled path stays deterministic Task.FromResult
        // until the #971 event-loop ref-counting lands.)
        var uid = Uid();
        var files = new Dictionary<string, string>
        {
            ["main.ts"] = $$"""
                import { promises as fsp } from 'fs';
                import * as os from 'os';
                const file = os.tmpdir() + '/prom_{{uid}}.txt';
                async function main() {
                    await fsp.writeFile(file, 'promise-data');
                    const data = await fsp.readFile(file, 'utf8');
                    console.log('PROM:' + data);
                    await fsp.unlink(file);
                }
                main();
                """
        };

        var output = TestHarness.RunModules(files, "main.ts", mode);
        Assert.Equal("PROM:promise-data\n", output);
    }

    #endregion

    #region Encoding / binary I/O correctness (#978)

    // Identical asserts across ExecutionModes.All enforce interp==compiled parity.

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void Fs_WriteReadBuffer_BinaryRoundTrip(ExecutionMode mode)
    {
        var uid = Uid();
        var files = new Dictionary<string, string>
        {
            ["main.ts"] = $$"""
                import * as fs from 'fs';
                import * as os from 'os';
                import { Buffer } from 'buffer';
                const f = os.tmpdir() + '/bin_{{uid}}.bin';
                const bin = Buffer.from([0, 255, 128, 10, 0, 200]);
                fs.writeFileSync(f, bin);
                const r = fs.readFileSync(f);
                console.log(r.length === 6 && r[0] === 0 && r[1] === 255 && r[2] === 128 && r[5] === 200);
                fs.unlinkSync(f);
                """
        };
        Assert.Equal("true\n", TestHarness.RunModules(files, "main.ts", mode));
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void Fs_Encoding_HexAndBase64(ExecutionMode mode)
    {
        var uid = Uid();
        var files = new Dictionary<string, string>
        {
            ["main.ts"] = $$"""
                import * as fs from 'fs';
                import * as os from 'os';
                const f = os.tmpdir() + '/enc_{{uid}}.txt';
                fs.writeFileSync(f, '48656c6c6f', 'hex');       // "Hello"
                console.log(fs.readFileSync(f, 'utf8'));
                console.log(fs.readFileSync(f, 'hex'));
                fs.writeFileSync(f, 'SGk=', 'base64');           // "Hi"
                console.log(fs.readFileSync(f, 'utf8'));
                console.log(fs.readFileSync(f, 'base64url'));
                fs.unlinkSync(f);
                """
        };
        Assert.Equal("Hello\n48656c6c6f\nHi\nSGk\n", TestHarness.RunModules(files, "main.ts", mode));
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void Fs_Encoding_Latin1AndUtf16le(ExecutionMode mode)
    {
        var uid = Uid();
        var files = new Dictionary<string, string>
        {
            ["main.ts"] = $$"""
                import * as fs from 'fs';
                import * as os from 'os';
                import { Buffer } from 'buffer';
                const f = os.tmpdir() + '/enc2_{{uid}}.txt';
                fs.writeFileSync(f, Buffer.from([0xe9]));        // é (latin1)
                console.log(fs.readFileSync(f, 'latin1'));
                fs.writeFileSync(f, 'AB', 'utf16le');
                console.log(fs.readFileSync(f, 'hex'));          // 41004200
                console.log(fs.readFileSync(f, 'utf16le'));      // AB
                fs.unlinkSync(f);
                """
        };
        Assert.Equal("é\n41004200\nAB\n", TestHarness.RunModules(files, "main.ts", mode));
    }

    #endregion

    #region Sync option semantics + constants (#979)

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void Fs_MkdirSync_NonRecursive_ThrowsEexistAndEnoent(ExecutionMode mode)
    {
        var uid = Uid();
        var files = new Dictionary<string, string>
        {
            ["main.ts"] = $$"""
                import * as fs from 'fs';
                import * as os from 'os';
                const base = os.tmpdir() + '/mkd_{{uid}}';
                fs.mkdirSync(base);
                let a = 'none';
                try { fs.mkdirSync(base); } catch (e: any) { a = e.code; }   // exists -> EEXIST
                console.log(a);
                let b = 'none';
                try { fs.mkdirSync(base + '/x/y/z'); } catch (e: any) { b = e.code; }  // missing parent -> ENOENT
                console.log(b);
                """
        };
        Assert.Equal("EEXIST\nENOENT\n", TestHarness.RunModules(files, "main.ts", mode));
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void Fs_MkdirSync_Recursive_ReturnsFirstCreated(ExecutionMode mode)
    {
        var uid = Uid();
        var files = new Dictionary<string, string>
        {
            ["main.ts"] = $$"""
                import * as fs from 'fs';
                import * as os from 'os';
                const base = os.tmpdir() + '/mkr_{{uid}}';
                fs.mkdirSync(base);
                const created = fs.mkdirSync(base + '/p/q/r', { recursive: true });
                console.log(typeof created === 'string' && created.endsWith('p'));   // first created = .../p
                console.log(fs.mkdirSync(base + '/p/q/r', { recursive: true }) === undefined); // nothing new
                console.log(fs.existsSync(base + '/p/q/r'));
                """
        };
        Assert.Equal("true\ntrue\ntrue\n", TestHarness.RunModules(files, "main.ts", mode));
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void Fs_AccessSync_WriteOk_ThrowsOnReadOnly(ExecutionMode mode)
    {
        var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"ro_{Uid()}.txt");
        System.IO.File.WriteAllText(path, "x");
        System.IO.File.SetAttributes(path, System.IO.FileAttributes.ReadOnly);
        try
        {
            var jsPath = path.Replace("\\", "\\\\");
            var files = new Dictionary<string, string>
            {
                ["main.ts"] = $$"""
                    import * as fs from 'fs';
                    let w = 'none';
                    try { fs.accessSync('{{jsPath}}', fs.constants.W_OK); w = 'ok'; } catch (e: any) { w = e.code; }
                    console.log(w);
                    let r = 'none';
                    try { fs.accessSync('{{jsPath}}', fs.constants.R_OK); r = 'ok'; } catch (e: any) { r = e.code; }
                    console.log(r);
                    """
            };
            Assert.Equal("EACCES\nok\n", TestHarness.RunModules(files, "main.ts", mode));
        }
        finally
        {
            System.IO.File.SetAttributes(path, System.IO.FileAttributes.Normal);
            System.IO.File.Delete(path);
        }
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void Fs_Constants_AreComplete(ExecutionMode mode)
    {
        var files = new Dictionary<string, string>
        {
            ["main.ts"] = """
                import * as fs from 'fs';
                const c = fs.constants;
                console.log([c.F_OK, c.R_OK, c.W_OK, c.X_OK].join(','));
                console.log([c.S_IRUSR, c.S_IWUSR, c.S_IXUSR, c.S_IRWXU].join(','));
                console.log([c.O_DIRECTORY, c.O_NOFOLLOW, c.O_SYNC, c.O_DSYNC, c.O_NONBLOCK].join(','));
                console.log([c.UV_FS_SYMLINK_DIR, c.UV_FS_SYMLINK_JUNCTION].join(','));
                """
        };
        Assert.Equal("0,4,2,1\n256,128,64,448\n65536,131072,1052672,4096,2048\n1,2\n",
            TestHarness.RunModules(files, "main.ts", mode));
    }

    #endregion

    #region Stats/Dirent unification (#977)

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void Fs_Stat_SyncAndAsyncSameShapeAndPredicates(ExecutionMode mode)
    {
        var uid = Uid();
        var files = new Dictionary<string, string>
        {
            ["main.ts"] = $$"""
                import * as fs from 'fs';
                import * as os from 'os';
                const dir = os.tmpdir() + '/st977_{{uid}}';
                fs.mkdirSync(dir);
                const file = dir + '/f.txt';
                fs.writeFileSync(file, 'hello');
                async function main() {
                    const s = fs.statSync(file);
                    const a = await fs.promises.stat(file);
                    console.log(s.isFile() === true && s.isDirectory() === false);
                    console.log(s.size === 5 && a.size === 5);
                    // sync and async produce the identical key set
                    console.log(JSON.stringify(Object.keys(s).sort()) === JSON.stringify(Object.keys(a).sort()));
                    console.log(fs.statSync(dir).isDirectory() === true);
                    fs.unlinkSync(file);
                    fs.rmdirSync(dir);
                }
                main();
                """
        };
        Assert.Equal("true\ntrue\ntrue\ntrue\n", TestHarness.RunModules(files, "main.ts", mode));
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void Fs_Stat_BigIntOption(ExecutionMode mode)
    {
        var uid = Uid();
        var files = new Dictionary<string, string>
        {
            ["main.ts"] = $$"""
                import * as fs from 'fs';
                import * as os from 'os';
                const file = os.tmpdir() + '/big977_{{uid}}.txt';
                fs.writeFileSync(file, 'x');
                const s: any = fs.statSync(file, { bigint: true });
                console.log(typeof s.size === 'bigint');
                console.log(typeof s.atimeNs === 'bigint');
                console.log(s.isFile() === true);
                fs.unlinkSync(file);
                """
        };
        Assert.Equal("true\ntrue\ntrue\n", TestHarness.RunModules(files, "main.ts", mode));
    }

    [Theory]
    [MemberData(nameof(ExecutionModes.All), MemberType = typeof(ExecutionModes))]
    public void Fs_Dirent_ParentPathAndPredicates(ExecutionMode mode)
    {
        var uid = Uid();
        var files = new Dictionary<string, string>
        {
            ["main.ts"] = $$"""
                import * as fs from 'fs';
                import * as os from 'os';
                const dir = os.tmpdir() + '/de977_{{uid}}';
                fs.mkdirSync(dir);
                fs.writeFileSync(dir + '/f.txt', 'x');
                fs.mkdirSync(dir + '/sub');
                const ents: any = fs.readdirSync(dir, { withFileTypes: true });
                let f: any = null, d: any = null;
                for (let i = 0; i < ents.length; i++) {
                    if (ents[i].name === 'f.txt') f = ents[i];
                    if (ents[i].name === 'sub') d = ents[i];
                }
                console.log(f.isFile() === true && d.isDirectory() === true);
                console.log(typeof f.parentPath === 'string' && f.parentPath.length > 0);
                console.log(f.path === f.parentPath);
                fs.unlinkSync(dir + '/f.txt');
                fs.rmdirSync(dir + '/sub');
                fs.rmdirSync(dir);
                """
        };
        Assert.Equal("true\ntrue\ntrue\n", TestHarness.RunModules(files, "main.ts", mode));
    }

    #endregion
}
