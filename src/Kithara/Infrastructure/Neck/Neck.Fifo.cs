using System.Runtime.InteropServices;

namespace Kithara.Infrastructure.Neck;

public sealed partial class Neck
{
    // 0666 — shared Compose volume; modules + Kithara both open the pipe.
    private const uint FifoMode = 0x1B6;

    /// <summary>Absolute path of the Struna FIFO without creating the node.</summary>
    public string GetStrunaFifoPath(Guid strunaId)
    {
        if (strunaId == Guid.Empty)
        {
            throw new ArgumentException("Struna id is required.", nameof(strunaId));
        }

        return Path.Combine(_fifoRoot, "strunas", $"{strunaId:D}.pcm");
    }

    /// <summary>Ensures the FIFO exists for an alive Struna; returns the path for <c>audio_endpoint</c>.</summary>
    public Task<string> EnsureStrunaFifoAsync(Guid strunaId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var path = GetStrunaFifoPath(strunaId);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        if (File.Exists(path))
        {
            return Task.FromResult(path);
        }

        CreateNamedPipe(path);
        _logger.LogInformation("Created Struna FIFO {Path}", path);
        return Task.FromResult(path);
    }

    /// <summary>Removes the Struna FIFO on teardown.</summary>
    public Task RemoveStrunaFifoAsync(Guid strunaId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var path = GetStrunaFifoPath(strunaId);
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
                _logger.LogInformation("Removed Struna FIFO {Path}", path);
            }
        }
        catch (IOException ex)
        {
            _logger.LogWarning(ex, "Failed to remove Struna FIFO {Path}", path);
        }
        catch (UnauthorizedAccessException ex)
        {
            _logger.LogWarning(ex, "Failed to remove Struna FIFO {Path}", path);
        }

        return Task.CompletedTask;
    }

    private static void CreateNamedPipe(string path)
    {
        if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS() || OperatingSystem.IsFreeBSD())
        {
            var rc = MkFifo(path, FifoMode);
            if (rc == 0)
            {
                return;
            }

            var errno = Marshal.GetLastWin32Error();
            // EEXIST — another creator won the race, or leftover node.
            if (errno == 17 && File.Exists(path))
            {
                return;
            }

            throw new IOException($"mkfifo failed for '{path}' (errno {errno}).");
        }

        // Non-Unix hosts (dev/test): regular file placeholder so callers still get a path.
        using var _ = File.Open(path, FileMode.CreateNew, FileAccess.Write, FileShare.ReadWrite);
    }

    [DllImport("libc", SetLastError = true, EntryPoint = "mkfifo")]
    private static extern int MkFifo([MarshalAs(UnmanagedType.LPUTF8Str)] string pathname, uint mode);
}
