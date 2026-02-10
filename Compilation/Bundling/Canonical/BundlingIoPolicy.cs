namespace SharpTS.Compilation.Bundling.Canonical;

/// <summary>
/// I/O resilience policy for bundle file operations.
/// Provides retry with exponential backoff and jitter for transient failures
/// (file locks, AV scanning races), and atomic file replacement.
/// </summary>
public static class BundlingIoPolicy
{
    /// <summary>
    /// Executes a file operation with retry and exponential backoff for transient I/O errors.
    /// </summary>
    /// <param name="action">The I/O action to attempt.</param>
    /// <param name="maxRetries">Maximum number of retry attempts.</param>
    /// <param name="baseDelayMs">Base delay in milliseconds (doubled each retry).</param>
    /// <param name="description">Description of the operation for error messages.</param>
    public static void ExecuteWithRetry(Action action, int maxRetries = 5, int baseDelayMs = 100, string? description = null)
    {
        Exception? lastException = null;

        for (int attempt = 0; attempt <= maxRetries; attempt++)
        {
            try
            {
                action();
                return;
            }
            catch (IOException ex) when (IsTransient(ex))
            {
                lastException = ex;
                if (attempt < maxRetries)
                {
                    var delay = CalculateDelay(attempt, baseDelayMs);
                    Thread.Sleep(delay);
                }
            }
            catch (UnauthorizedAccessException ex)
            {
                lastException = ex;
                if (attempt < maxRetries)
                {
                    var delay = CalculateDelay(attempt, baseDelayMs);
                    Thread.Sleep(delay);
                }
            }
        }

        var desc = description ?? "file operation";
        throw new IOException($"Failed to complete {desc} after {maxRetries + 1} attempts.", lastException);
    }

    /// <summary>
    /// Writes data to a file using atomic replacement: write to temp file, then move.
    /// Prevents partial/corrupt output if the process crashes or is interrupted.
    /// </summary>
    /// <param name="outputPath">Final output path.</param>
    /// <param name="writeAction">Action that writes data to the provided stream.</param>
    /// <param name="maxRetries">Maximum retry attempts for the final move.</param>
    /// <param name="baseDelayMs">Base delay for retries.</param>
    public static void AtomicWrite(string outputPath, Action<FileStream> writeAction, int maxRetries = 5, int baseDelayMs = 100)
    {
        var dir = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
        {
            Directory.CreateDirectory(dir);
        }

        var tempPath = outputPath + $".tmp.{Guid.NewGuid():N}";
        try
        {
            // Write to temp file
            using (var fs = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                writeAction(fs);
            }

            // Atomic replace: delete target then move temp
            ExecuteWithRetry(() =>
            {
                if (File.Exists(outputPath))
                {
                    File.Delete(outputPath);
                }
                File.Move(tempPath, outputPath);
            }, maxRetries, baseDelayMs, $"replacing output file '{Path.GetFileName(outputPath)}'");
        }
        catch
        {
            // Clean up temp file on any failure
            TryDelete(tempPath);
            throw;
        }
    }

    /// <summary>
    /// Writes bytes to a file using atomic replacement.
    /// </summary>
    public static void AtomicWriteAllBytes(string outputPath, byte[] data, int maxRetries = 5, int baseDelayMs = 100)
    {
        AtomicWrite(outputPath, fs => fs.Write(data, 0, data.Length), maxRetries, baseDelayMs);
    }

    /// <summary>
    /// Attempts to delete a file, swallowing any errors.
    /// </summary>
    public static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
            // Best-effort cleanup
        }
    }

    /// <summary>
    /// Attempts to delete a directory and all contents, swallowing any errors.
    /// </summary>
    public static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
        catch
        {
            // Best-effort cleanup
        }
    }

    /// <summary>
    /// Calculates backoff delay with jitter for the given attempt number.
    /// </summary>
    private static int CalculateDelay(int attempt, int baseDelayMs)
    {
        // Exponential backoff: base * 2^attempt
        var delay = baseDelayMs * (1 << attempt);
        // Add jitter: +-25% of delay
        var jitter = Random.Shared.Next(-delay / 4, delay / 4 + 1);
        return Math.Max(1, delay + jitter);
    }

    /// <summary>
    /// Determines if an IOException is transient (file locked, sharing violation).
    /// </summary>
    private static bool IsTransient(IOException ex)
    {
        // HResult 0x80070020 = ERROR_SHARING_VIOLATION
        // HResult 0x80070021 = ERROR_LOCK_VIOLATION
        const int SharingViolation = unchecked((int)0x80070020);
        const int LockViolation = unchecked((int)0x80070021);

        return ex.HResult is SharingViolation or LockViolation;
    }
}
