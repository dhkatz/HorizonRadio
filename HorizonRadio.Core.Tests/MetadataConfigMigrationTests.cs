using HorizonRadio.Core.Metadata;

namespace HorizonRadio.Core.Tests;

/// <summary>
/// The one-time migration that enables a newly-shipped default provider (iTunes) for
/// existing configs, while respecting a provider the user has deliberately disabled.
/// </summary>
public class MetadataConfigMigrationTests
{
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
    public void Existing_config_gains_itunes_at_the_front()
    {
        using var dir = TempDir.Create();
        var path = WriteConfig(dir, """{ "order": ["musicbrainz"] }""");

        var store = MetadataConfigStore.LoadFromDisk(path);

        // The newly-shipped keyless defaults (iTunes, VocaDB) are added in front of the
        // existing provider, in their canonical order.
        Assert.Equal(["itunes", "musicbrainz", "vocadb"], store.Order);
        Assert.Contains("itunes", store.Introduced);
        Assert.Contains("vocadb", store.Introduced);
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

        Assert.Equal(["itunes", "musicbrainz", "vocadb"], reloaded.Order);
        Assert.Contains("itunes", reloaded.Introduced);
    }
}
