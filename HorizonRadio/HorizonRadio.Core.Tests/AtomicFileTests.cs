using HorizonRadio.Core.IO;
using HorizonRadio.Core.Sources.Config;

namespace HorizonRadio.Core.Tests;

/// <summary>
/// Settings are saved through <see cref="AtomicFile"/>: a write that fails partway must
/// leave the previous file as it was (not truncated) and no temp file behind.
/// </summary>
public class AtomicFileTests
{
    private static string[] TempFiles(TempDir dir) => Directory.GetFiles(dir.Path, "*.tmp");

    [Fact]
    public void A_write_that_throws_leaves_the_existing_file_untouched()
    {
        using var dir = TempDir.Create();
        var path = Path.Combine(dir.Path, "settings.json");
        byte[] original = [1, 2, 3, 4, 5];
        File.WriteAllBytes(path, original);

        Assert.Throws<InvalidOperationException>(() => AtomicFile.Write(path, s =>
        {
            s.Write([9, 9, 9]);
            throw new InvalidOperationException("boom");
        }));

        Assert.Equal(original, File.ReadAllBytes(path));
        Assert.Empty(TempFiles(dir));
    }

    [Fact]
    public void A_successful_write_replaces_the_content()
    {
        using var dir = TempDir.Create();
        var path = Path.Combine(dir.Path, "settings.json");
        File.WriteAllBytes(path, [1, 2, 3, 4, 5, 6, 7, 8]);

        AtomicFile.Write(path, s => s.Write([7, 7]));

        Assert.Equal([7, 7], File.ReadAllBytes(path));
        Assert.Empty(TempFiles(dir));
    }

    [Fact]
    public void Creates_the_directory_when_missing()
    {
        using var dir = TempDir.Create();
        var path = Path.Combine(dir.Path, "nested", "settings.json");

        AtomicFile.WriteAllBytes(path, [42]);

        Assert.Equal([42], File.ReadAllBytes(path));
    }

    [Fact]
    public void A_source_config_save_that_fails_partway_keeps_the_previous_settings()
    {
        using var dir = TempDir.Create();
        var path = Path.Combine(dir.Path, "sources.json");
        new SourceConfigStore { GameId = "fh6", LastSelectedId = "local" }.SaveToDisk(path);
        var before = File.ReadAllBytes(path);

        // NaN isn't valid JSON: the writer throws after the earlier fields were written,
        // which used to leave sources.json truncated (and the next start empty).
        new SourceConfigStore { GameId = "other", PreviewVolume = double.NaN }.SaveToDisk(path);

        Assert.Equal(before, File.ReadAllBytes(path));
        Assert.Empty(TempFiles(dir));
        var reloaded = SourceConfigStore.LoadFromDisk(path);
        Assert.Equal("fh6", reloaded.GameId);
        Assert.Equal("local", reloaded.LastSelectedId);
    }
}
