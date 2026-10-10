using System.Security.Cryptography;

namespace Mutate4CSharp;

internal static class AtomicOwnedFile
{
    public static void Write(string destination, string lockPath, byte[] bytes,
        Func<string, byte[]?> inspectExisting, bool replaceExisting)
    {
        var fullPath = Path.GetFullPath(destination);
        var fullLockPath = Path.GetFullPath(lockPath);
        var directory = Path.GetDirectoryName(fullPath) ?? throw new IOException("Destination has no parent directory.");
        var lockDirectory = Path.GetDirectoryName(fullLockPath) ?? throw new IOException("Lock has no parent directory.");
        Directory.CreateDirectory(directory);
        Directory.CreateDirectory(lockDirectory);
        RejectSpecialPath(fullLockPath, "Lock");
        var temporary = Path.Combine(directory, $".{Path.GetFileName(fullPath)}.{Guid.NewGuid():N}.tmp");
        FileStream? destinationLock = null;
        try
        {
            try
            {
                destinationLock = new FileStream(fullLockPath, FileMode.OpenOrCreate, FileAccess.Write,
                    FileShare.None, 1, FileOptions.WriteThrough);
            }
            catch (IOException ex)
            {
                throw new IOException($"Destination lock is held: {fullLockPath}", ex);
            }

            var before = inspectExisting(fullPath);
            if (!replaceExisting && before is not null)
            {
                if (!CryptographicOperations.FixedTimeEquals(before, SHA256.HashData(bytes)))
                    throw new IOException("Existing owned state conflicts with the requested publication.");
                return;
            }

            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write,
                       FileShare.None, 4096, FileOptions.WriteThrough))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }

            var after = inspectExisting(fullPath);
            if ((before is null) != (after is null) || before is not null && after is not null &&
                !CryptographicOperations.FixedTimeEquals(before, after))
                throw new IOException("Destination changed while owned state was being written.");
            var retryStarted = System.Diagnostics.Stopwatch.StartNew();
            while (true)
            {
                try
                {
                    File.Move(temporary, fullPath, overwrite: replaceExisting);
                    break;
                }
                catch (IOException ex) when (OperatingSystem.IsWindows() &&
                    (ex.HResult & 0xffff) is 32 or 33 && retryStarted.Elapsed < TimeSpan.FromSeconds(2))
                {
                    Thread.Sleep(25);
                    var current = inspectExisting(fullPath);
                    if ((before is null) != (current is null) || before is not null && current is not null &&
                        !CryptographicOperations.FixedTimeEquals(before, current))
                        throw new IOException("Destination changed while owned state publication was retried.");
                }
            }
        }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); } catch { }
            destinationLock?.Dispose();
        }
    }

    public static void RejectSpecialPath(string path, string kind)
    {
        if (new FileInfo(path).LinkTarget is not null)
            throw new IOException($"{kind} path must be a regular file.");
        if (Directory.Exists(path)) throw new IOException($"{kind} path must be a regular file.");
        if (!File.Exists(path)) return;
        var attributes = File.GetAttributes(path);
        if ((attributes & (FileAttributes.Directory | FileAttributes.Device | FileAttributes.ReparsePoint)) != 0)
            throw new IOException($"{kind} path must be a regular file.");
    }
}
