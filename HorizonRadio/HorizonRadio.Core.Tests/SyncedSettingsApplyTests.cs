using HorizonRadio.Core.Metadata;
using HorizonRadio.Core.Playback;
using HorizonRadio.Core.Settings;
using HorizonRadio.Core.Sources;
using HorizonRadio.Core.Sources.Config;
using HorizonRadio.Core.Sources.Mixes;
using HorizonRadio.Sdk.Metadata;
using HorizonRadio.Sdk.Sources;
using HorizonRadio.Sdk.Sources.Config;

namespace HorizonRadio.Core.Tests;

/// <summary>
/// A preference changed on another of your PCs arrives as a settings change, with no window to
/// pass it on: the engine follows it itself.
/// </summary>
public sealed class SyncedSettingsApplyTests
{
    [Fact]
    public async Task Shuffle_turned_on_elsewhere_reaches_the_player()
    {
        var config = new SourceConfigStore();
        await using var runner = new SourceRunner(new NullSink());
        await using var router = new PlaybackRouter(runner, config, new MixStore(),
            new MixContentResolver(config, TestSources.Catalog));
        Assert.False(runner.Shuffle);

        config.Settings.Set(PlaybackSettings.Shuffle, true);

        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!runner.Shuffle && DateTime.UtcNow < deadline) await Task.Delay(20);
        Assert.True(runner.Shuffle);
    }

    [Fact]
    public async Task A_provider_order_changed_elsewhere_reaches_the_resolver()
    {
        using var settings = SettingsStore.CreateInMemory();
        var config = new MetadataConfigStore(settings, new MetadataCatalog([new Provider()]));
        config.Order = [];
        await using var resolver = new MetadataResolver(NoLog.Instance);
        using var pipeline = new MetadataPipeline(config, resolver);
        Assert.False(resolver.HasContributors);

        settings.Set(MetadataSettings.Order, ["fake"]);
        Assert.True(resolver.HasContributors);

        // Saved from the Metadata page: the settings, then the resolver.
        pipeline.Save([("fake", new ConfigValues())], [], new Dictionary<MetadataField, string>());
        Assert.False(resolver.HasContributors);
        Assert.Empty(config.Order);
    }

    private sealed class NullSink : IPcmSink
    {
        public bool Send(ReadOnlySpan<short> samples) => true;
    }

    private sealed class Provider : IMetadataProviderFactory, IMetadataProvider
    {
        public string Id => "fake";

        public int SortOrder => 0;

        public string DisplayName => "Fake";

        public string? Description => null;

        public IReadOnlyList<ConfigField> Schema => [];

        public IMetadataProvider Create(ConfigValues values) => this;

        public Task<MetadataContribution?> ContributeAsync(MetadataQuery query, CancellationToken ct) =>
            Task.FromResult<MetadataContribution?>(null);

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
