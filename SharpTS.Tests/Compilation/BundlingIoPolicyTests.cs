using SharpTS.Compilation.Bundling.Canonical;
using Xunit;

namespace SharpTS.Tests.Compilation;

/// <summary>
/// Unit tests for BundlingIoPolicy retry, backoff, and atomic write behavior.
/// </summary>
public class BundlingIoPolicyTests
{
    [Fact]
    public void ExecuteWithRetry_SucceedsOnFirstAttempt()
    {
        int callCount = 0;
        BundlingIoPolicy.ExecuteWithRetry(() => callCount++, maxRetries: 3, baseDelayMs: 1);
        Assert.Equal(1, callCount);
    }

    [Fact]
    public void ExecuteWithRetry_RetriesOnTransientIOException()
    {
        int callCount = 0;
        // Simulate sharing violation (0x80070020)
        BundlingIoPolicy.ExecuteWithRetry(() =>
        {
            callCount++;
            if (callCount < 3)
                throw CreateSharingViolation();
        }, maxRetries: 5, baseDelayMs: 1);

        Assert.Equal(3, callCount); // Failed twice, succeeded on third
    }

    [Fact]
    public void ExecuteWithRetry_RetriesOnUnauthorizedAccessException()
    {
        int callCount = 0;
        BundlingIoPolicy.ExecuteWithRetry(() =>
        {
            callCount++;
            if (callCount < 2)
                throw new UnauthorizedAccessException("Locked by AV");
        }, maxRetries: 5, baseDelayMs: 1);

        Assert.Equal(2, callCount);
    }

    [Fact]
    public void ExecuteWithRetry_ThrowsAfterMaxRetries()
    {
        int callCount = 0;
        var ex = Assert.Throws<IOException>(() =>
        {
            BundlingIoPolicy.ExecuteWithRetry(() =>
            {
                callCount++;
                throw CreateSharingViolation();
            }, maxRetries: 2, baseDelayMs: 1, description: "test op");
        });

        // Initial attempt + 2 retries = 3 calls total
        Assert.Equal(3, callCount);
        // The wrapping IOException should contain the description
        Assert.Contains("test op", ex.Message);
        Assert.Contains("3 attempts", ex.Message);
        // The inner exception should be the original sharing violation
        Assert.NotNull(ex.InnerException);
    }

    [Fact]
    public void ExecuteWithRetry_DoesNotRetryNonTransientIOException()
    {
        int callCount = 0;
        Assert.Throws<IOException>(() =>
        {
            BundlingIoPolicy.ExecuteWithRetry(() =>
            {
                callCount++;
                throw new IOException("File not found"); // HResult 0 = not transient
            }, maxRetries: 3, baseDelayMs: 1);
        });

        Assert.Equal(1, callCount); // No retries for non-transient
    }

    [Fact]
    public void AtomicWriteAllBytes_CreatesFile()
    {
        var tempPath = Path.Combine(Path.GetTempPath(), $"iopolicy_test_{Guid.NewGuid():N}.bin");
        try
        {
            var data = new byte[] { 1, 2, 3, 4, 5 };
            BundlingIoPolicy.AtomicWriteAllBytes(tempPath, data, maxRetries: 1, baseDelayMs: 1);

            Assert.True(File.Exists(tempPath));
            Assert.Equal(data, File.ReadAllBytes(tempPath));
        }
        finally
        {
            BundlingIoPolicy.TryDelete(tempPath);
        }
    }

    [Fact]
    public void AtomicWriteAllBytes_ReplacesExistingFile()
    {
        var tempPath = Path.Combine(Path.GetTempPath(), $"iopolicy_test_{Guid.NewGuid():N}.bin");
        try
        {
            File.WriteAllBytes(tempPath, [0xFF, 0xFF]);
            var newData = new byte[] { 1, 2, 3 };
            BundlingIoPolicy.AtomicWriteAllBytes(tempPath, newData, maxRetries: 1, baseDelayMs: 1);

            Assert.Equal(newData, File.ReadAllBytes(tempPath));
        }
        finally
        {
            BundlingIoPolicy.TryDelete(tempPath);
        }
    }

    [Fact]
    public void AtomicWrite_CleansUpTempOnFailure()
    {
        var tempPath = Path.Combine(Path.GetTempPath(), $"iopolicy_test_{Guid.NewGuid():N}.bin");
        var tempDir = Path.GetDirectoryName(tempPath)!;

        Assert.Throws<InvalidOperationException>(() =>
        {
            BundlingIoPolicy.AtomicWrite(tempPath, _ =>
            {
                throw new InvalidOperationException("Write failed");
            }, maxRetries: 0, baseDelayMs: 1);
        });

        // The temp file should have been cleaned up
        Assert.False(File.Exists(tempPath));
        // No .tmp files should remain
        var tmpFiles = Directory.GetFiles(tempDir, Path.GetFileName(tempPath) + ".tmp.*");
        Assert.Empty(tmpFiles);
    }

    [Fact]
    public void AtomicWriteAllBytes_CreatesDirectoryIfNeeded()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"iopolicy_test_dir_{Guid.NewGuid():N}");
        var tempPath = Path.Combine(tempDir, "output.bin");
        try
        {
            Assert.False(Directory.Exists(tempDir));
            BundlingIoPolicy.AtomicWriteAllBytes(tempPath, [42], maxRetries: 1, baseDelayMs: 1);

            Assert.True(File.Exists(tempPath));
            Assert.Equal(new byte[] { 42 }, File.ReadAllBytes(tempPath));
        }
        finally
        {
            BundlingIoPolicy.TryDeleteDirectory(tempDir);
        }
    }

    [Fact]
    public void TryDelete_SwallowsErrors_OnNonExistentFile()
    {
        // Should not throw
        BundlingIoPolicy.TryDelete(Path.Combine(Path.GetTempPath(), "nonexistent_file_12345.bin"));
    }

    [Fact]
    public void TryDeleteDirectory_SwallowsErrors_OnNonExistentDir()
    {
        BundlingIoPolicy.TryDeleteDirectory(Path.Combine(Path.GetTempPath(), "nonexistent_dir_12345"));
    }

    /// <summary>
    /// Creates an IOException with HRESULT for ERROR_SHARING_VIOLATION.
    /// </summary>
    private static IOException CreateSharingViolation()
    {
        const int SharingViolation = unchecked((int)0x80070020);
        var ex = new IOException("The process cannot access the file because it is being used by another process.");
        // Set HResult via reflection (IOException doesn't expose a setter)
        typeof(Exception).GetProperty("HResult")!.SetValue(ex, SharingViolation);
        return ex;
    }
}
