namespace HorizonRadio.Core.Tests;

/// <summary>
/// Throwaway temp directory, cleaned up on dispose. Gives the local-files
/// player a real path to resolve (and the YouTube factory real tool-path files
/// to stat) without committing fixtures to the repo.
/// </summary>
internal sealed class TempDir : IDisposable
{
    public string Path { get; }

    private TempDir(string path) => Path = path;

    public static TempDir Create()
    {
        var dir = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "hzn-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return new TempDir(dir);
    }

    /// <summary>Create a zero-byte file with the given name and return its full
    /// path. The playlist loader keys off the extension; contents are never
    /// decoded in these tests, and tool checks only stat the path.</summary>
    public string Touch(string name)
    {
        var p = System.IO.Path.Combine(Path, name);
        File.WriteAllBytes(p, []);
        return p;
    }

    /// <summary>Write a short silent 44.1 kHz/16-bit stereo WAV and return its
    /// path — a real, decodable file for exercising the local pump.</summary>
    public string WriteSilentWav(string name, double seconds)
    {
        var p = System.IO.Path.Combine(Path, name);
        var fmt = new NAudio.Wave.WaveFormat(44100, 16, 2);
        using (var w = new NAudio.Wave.WaveFileWriter(p, fmt))
        {
            var silence = new byte[(int)(seconds * fmt.AverageBytesPerSecond)];
            w.Write(silence, 0, silence.Length);
        }
        return p;
    }

    public void Dispose()
    {
        try { Directory.Delete(Path, recursive: true); } catch { /* best effort */ }
    }
}
