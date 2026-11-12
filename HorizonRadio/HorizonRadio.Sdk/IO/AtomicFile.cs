namespace HorizonRadio.Sdk.IO;

/// <summary>
/// Replaces a file all at once: the content is written to a unique temp file next to it,
/// then renamed over the target. A write that fails partway (an exception, a crash) leaves
/// the old file untouched instead of truncated, and two concurrent saves can't interleave
/// their bytes in one file (the last rename wins).
/// </summary>
public static class AtomicFile
{
    /// <summary>Write <paramref name="path"/> through <paramref name="write"/>, atomically.
    /// Creates the directory if needed. If <paramref name="write"/> or the rename throws, the
    /// temp file is removed (best effort) and the exception is rethrown.</summary>
    public static void Write(string path, Action<Stream> write)
    {
        ArgumentNullException.ThrowIfNull(write);
        var full = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        var tmp = $"{full}.{Guid.NewGuid():n}.tmp";
        try
        {
            using (var stream = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                write(stream);
                stream.Flush(flushToDisk: true);
            }
            File.Move(tmp, full, overwrite: true);
        }
        catch
        {
            try { File.Delete(tmp); } catch { /* best effort */ }
            throw;
        }
    }

    /// <summary>Write <paramref name="bytes"/> to <paramref name="path"/>, atomically.</summary>
    public static void WriteAllBytes(string path, byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        Write(path, s => s.Write(bytes));
    }
}
