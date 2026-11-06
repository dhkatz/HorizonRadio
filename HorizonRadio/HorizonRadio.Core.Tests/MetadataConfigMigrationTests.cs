using HorizonRadio.Core.Metadata;
using HorizonRadio.Core.Metadata.Apple;
using HorizonRadio.Core.Metadata.MusicBrainz;
using HorizonRadio.Core.Metadata.Spotify;
using HorizonRadio.Core.Metadata.VocaDb;

namespace HorizonRadio.Core.Tests;

/// <summary>
/// The one-time migration that enables a newly-shipped default provider (iTunes) for
/// existing configs, while respecting a provider the user has deliberately disabled.
/// </summary>
public class MetadataConfigMigrationTests
{
    // The config store derives its defaults / "introduced" set from the catalog, which the
    // composition root populates at startup. Mirror that here so the migration sees the real
    // provider set (incl. VocaDB, now its own plugin assembly).
    public MetadataConfigMigrationTests() => MetadataCatalog.Initialize(
    [
        new SpotifyMetadataPlugin(),
        new ItunesMetadataPlugin(),
        new MusicBrainzMetadataPlugin(),
        new VocaDbMetadataPlugin(),
    ]);

    private static string WriteConfig(TempDir dir, string json)
    {
        var path = Path.Combine(dir.Path, "metadata-config.json");
        File.WriteAllText(path, json);
        return path;
    }

    [Fact]
    public void Fresh_install_enables_keyless_defaults()
    {
        using var dir = TempDir.Create();
        var store = MetadataConfigStore.LoadFromDisk(Path.Combine(dir.Path, "does-not-exist.json"));

        Assert.Equal(MetadataCatalog.DefaultEnabledOrder, store.Order);
    }

    [Fact]
    public void Existing_config_gains_new_defaults_appended()
    {
        using var dir = TempDir.Create();
        var path = WriteConfig(dir, """{ "order": ["musicbrainz"] }""");

        var store = MetadataConfigStore.LoadFromDisk(path);

        // Newly-shipped keyless defaults (iTunes, VocaDB) are appended, NOT inserted ahead
        // of the user's existing provider.
        Assert.Equal(["musicbrainz", "itunes", "vocadb"], store.Order);
        Assert.Contains("itunes", store.Introduced);
        Assert.Contains("vocadb", store.Introduced);
    }

    [Fact]
    public void Migration_preserves_a_user_prioritized_provider()
    {
        using var dir = TempDir.Create();
        // User deliberately made Spotify their #1 metadata source.
        var path = WriteConfig(dir, """{ "order": ["spotify", "musicbrainz"] }""");

        var store = MetadataConfigStore.LoadFromDisk(path);

        // Spotify stays first; the new defaults go after it.
        Assert.Equal(["spotify", "musicbrainz", "itunes", "vocadb"], store.Order);
    }

    [Fact]
    public void Disabled_provider_is_not_re_added()
    {
        using var dir = TempDir.Create();
        // The user has already seen every provider (all introduced) and removed iTunes.
        var path = WriteConfig(dir, """
        { "order": ["musicbrainz"], "introduced": ["itunes", "musicbrainz", "spotify", "vocadb"] }
        """);

        var store = MetadataConfigStore.LoadFromDisk(path);

        Assert.Equal(["musicbrainz"], store.Order);
    }

    [Fact]
    public void Migration_persists_across_a_save_round_trip()
    {
        using var dir = TempDir.Create();
        var path = WriteConfig(dir, """{ "order": ["musicbrainz"] }""");

        MetadataConfigStore.LoadFromDisk(path).SaveToDisk(path);
        var reloaded = MetadataConfigStore.LoadFromDisk(path);

        Assert.Equal(["musicbrainz", "itunes", "vocadb"], reloaded.Order);
        Assert.Contains("itunes", reloaded.Introduced);
    }
}
