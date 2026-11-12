using HorizonRadio.Core.Metadata;
using HorizonRadio.Core.Settings;
using HorizonRadio.Metadata.Apple;
using HorizonRadio.Metadata.MusicBrainz;
using HorizonRadio.Metadata.Spotify;
using HorizonRadio.Metadata.VocaDb;
using HorizonRadio.Sdk.Metadata;
using HorizonRadio.Sdk.Sources.Config;
using HorizonRadio.Source.Spotify;

namespace HorizonRadio.Core.Tests;

/// <summary>
/// The metadata configuration in the settings store: the one-time migration that enables a
/// newly-shipped default provider (iTunes) for existing configs while respecting a provider the
/// user has deliberately disabled, and what's brought over from <c>metadata-config.json</c>.
/// </summary>
public sealed class MetadataConfigMigrationTests : IDisposable
{
    private readonly TempDir _dir = TempDir.Create();

    // The catalog only reads the providers' ids and settings; nothing is looked up.
    private static readonly IMetadataCache Cache = new NoCache();

    private sealed class NoCache : IMetadataCache
    {
        public MetadataCacheEntry? TryGet(string key) => null;
        public void Put(string key, MetadataCacheEntry entry) { }
        public void PutMiss(string key) { }
    }

    // The config store derives its defaults / "introduced" set from the catalog, which the
    // composition root populates at startup. Mirror that here so the migration sees the real
    // provider set (incl. VocaDB, now its own plugin assembly).
    private readonly MetadataCatalog _catalog;

    public MetadataConfigMigrationTests() => _catalog = new MetadataCatalog(
    [
        new SpotifyProviderFactory(Cache, IMetadataTrace.Off,
            new SpotifyConnection(new SpotifyAuthStore(Path.Combine(_dir.Path, "spotify-auth.dat")), "", NoLog.Instance)),
        new ItunesProviderFactory(Cache, IMetadataTrace.Off),
        new MusicBrainzProviderFactory(Cache, IMetadataTrace.Off),
        new VocaDbProviderFactory(Cache, IMetadataTrace.Off),
    ]);

    public void Dispose() => _dir.Dispose();

    private string SettingsPath => Path.Combine(_dir.Path, SettingsStore.FileName);

    // Settings opened beside an older metadata-config.json holding json.
    private SettingsStore OpenWithLegacy(string json)
    {
        File.WriteAllText(Path.Combine(_dir.Path, "metadata-config.json"), json);
        return SettingsStore.Open(SettingsPath);
    }

    [Fact]
    public void Fresh_install_enables_keyless_defaults()
    {
        var store = new MetadataConfigStore(SettingsStore.CreateInMemory(), _catalog);

        Assert.Equal(MetadataCatalog.DefaultEnabledOrder, store.Order);
        Assert.Equal(_catalog.All.Select(f => f.Id), store.Settings.Get(MetadataSettings.Introduced));
    }

    [Fact]
    public void Existing_config_gains_new_defaults_appended()
    {
        using var settings = OpenWithLegacy("""{ "order": ["musicbrainz"] }""");

        var store = new MetadataConfigStore(settings, _catalog);

        // Newly-shipped keyless defaults (iTunes, VocaDB) are appended, NOT inserted ahead
        // of the user's existing provider.
        Assert.Equal(["musicbrainz", "itunes", "vocadb"], store.Order);
        Assert.Contains("itunes", settings.Get(MetadataSettings.Introduced));
        Assert.Contains("vocadb", settings.Get(MetadataSettings.Introduced));
    }

    [Fact]
    public void Migration_preserves_a_user_prioritized_provider()
    {
        // User deliberately made Spotify their #1 metadata source.
        using var settings = OpenWithLegacy("""{ "order": ["spotify", "musicbrainz"] }""");

        var store = new MetadataConfigStore(settings, _catalog);

        // Spotify stays first; the new defaults go after it.
        Assert.Equal(["spotify", "musicbrainz", "itunes", "vocadb"], store.Order);
    }

    [Fact]
    public void Disabled_provider_is_not_re_added()
    {
        // The user has already seen every provider (all introduced) and removed iTunes.
        using var settings = OpenWithLegacy("""
        { "order": ["musicbrainz"], "introduced": ["itunes", "musicbrainz", "spotify", "vocadb"] }
        """);

        Assert.Equal(["musicbrainz"], new MetadataConfigStore(settings, _catalog).Order);
    }

    [Fact]
    public void Every_provider_disabled_stays_disabled()
    {
        using var settings = OpenWithLegacy("""
        { "order": [], "introduced": ["itunes", "musicbrainz", "spotify", "vocadb"] }
        """);

        Assert.Empty(new MetadataConfigStore(settings, _catalog).Order);
    }

    [Fact]
    public void Migration_persists_across_a_restart()
    {
        using (var settings = OpenWithLegacy("""{ "order": ["musicbrainz"] }"""))
            _ = new MetadataConfigStore(settings, _catalog);

        using var reopened = SettingsStore.Open(SettingsPath);
        Assert.Equal(["musicbrainz", "itunes", "vocadb"], reopened.Get(MetadataSettings.Order));
        Assert.Contains("itunes", reopened.Get(MetadataSettings.Introduced));
    }

    [Fact]
    public void A_single_selected_provider_from_before_the_order_becomes_the_order()
    {
        using var settings = OpenWithLegacy("""{ "selected": "spotify" }""");

        Assert.Equal(["spotify", "itunes", "musicbrainz", "vocadb"], new MetadataConfigStore(settings, _catalog).Order);
    }

    [Fact]
    public void Overrides_title_model_and_provider_forms_are_brought_over()
    {
        using var settings = OpenWithLegacy("""
        {
          "order": ["spotify"],
          "forced": { "Art": "spotify", "Nonsense": "x" },
          "titleModelMode": "always",
          "perProvider": { "spotify": { "clientId": "abc", "clientSecret": "s3cret" } }
        }
        """);

        var store = new MetadataConfigStore(settings, _catalog);

        Assert.Equal(new Dictionary<MetadataField, string> { [MetadataField.Art] = "spotify" }, store.Forced);
        Assert.Equal(TitleModelMode.Always, store.TitleModelMode);
        var schema = _catalog.Find("spotify")!.Schema;
        var values = store.Load("spotify", schema);
        Assert.Equal("abc", values.GetString("clientId"));
        Assert.Equal("s3cret", values.GetString("clientSecret"));
    }

    [Fact]
    public void A_credential_kept_in_the_clear_becomes_a_secret_once_its_form_loads()
    {
        if (!OperatingSystem.IsWindows()) return;
        using (var settings = OpenWithLegacy("""{ "perProvider": { "spotify": { "clientSecret": "s3cret" } } }"""))
        {
            var key = MetadataConfigStore.KeyFor("spotify", "clientSecret");
            Assert.Equal(SettingScope.Device, settings.ScopeOf(key));

            new MetadataConfigStore(settings, _catalog).Load("spotify", _catalog.Find("spotify")!.Schema);

            Assert.Equal(SettingScope.Secret, settings.ScopeOf(key));
        }

        Assert.DoesNotContain("s3cret", File.ReadAllText(SettingsPath), StringComparison.Ordinal);
    }

    [Fact]
    public void Order_and_overrides_are_written_to_the_settings()
    {
        var settings = SettingsStore.CreateInMemory();
        var store = new MetadataConfigStore(settings, _catalog);

        store.Order = ["vocadb"];
        store.Forced = new Dictionary<MetadataField, string> { [MetadataField.Year] = "musicbrainz" };
        var contact = new ConfigValues();
        contact.Set("contact", "me@example.com");
        store.Save("musicbrainz", contact);

        var again = new MetadataConfigStore(settings, _catalog);
        Assert.Equal(["vocadb"], again.Order);
        Assert.Equal("musicbrainz", again.Forced[MetadataField.Year]);
        Assert.Equal(SettingScope.Synced, settings.ScopeOf(MetadataSettings.Order.Key));
        Assert.Equal(SettingScope.Device, settings.ScopeOf(MetadataConfigStore.KeyFor("musicbrainz", "contact")));
    }
}
