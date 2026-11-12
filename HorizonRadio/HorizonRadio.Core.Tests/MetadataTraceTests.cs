using HorizonRadio.Core.Diagnostics;
using HorizonRadio.Core.IO;
using HorizonRadio.Core.Metadata;
using HorizonRadio.Core.Settings;
using HorizonRadio.Sdk.Metadata;
using HorizonRadio.Sdk.Models;
using Microsoft.Extensions.DependencyInjection;

namespace HorizonRadio.Core.Tests;

/// <summary>
/// The metadata diagnostics as the host's service: one per container, writing into the host's
/// folder, remembered across restarts, and recording the resolver's lookups and the providers' searches.
/// </summary>
public sealed class MetadataTraceTests : IDisposable
{
    private readonly TempDir _dir = TempDir.Create();

    public void Dispose() => _dir.Dispose();

    [Fact]
    public void The_host_has_one_trace_that_plugins_and_the_resolver_share()
    {
        using var settings = SettingsStore.CreateInMemory();
        using var provider = new ServiceCollection()
            .AddHorizonCore(settings, new AppFolders(_dir.Path))
            .BuildServiceProvider();

        var trace = provider.GetRequiredService<MetadataTrace>();
        Assert.Same(trace, provider.GetRequiredService<IMetadataTrace>());
        Assert.Equal(Path.Combine(_dir.Path, "diagnostics"), trace.Directory);
    }

    [Fact]
    public async Task Switched_on_it_records_a_lookup_in_the_hosts_folder_and_stays_on()
    {
        using var settings = SettingsStore.CreateInMemory();
        var log = new LinesLog();
        var folders = new AppFolders(_dir.Path);
        using (var trace = new MetadataTrace(settings, folders, log))
        {
            Assert.False(trace.Enabled);
            trace.SetEnabled(true);

            await using var resolver = new MetadataResolver(NoLog.Instance, trace);
            resolver.Configure([new Searching(trace)], new MetadataPolicy([MetadataPolicy.SourceId, "searching"], new Dictionary<MetadataField, string>()));
            await resolver.ResolveAsync(new Track("Melt", "ryo", null, null, "local", "Local Files"), CancellationToken.None);
        }

        var file = Assert.Single(Directory.GetFiles(Path.Combine(_dir.Path, "diagnostics"), "*.jsonl"));
        var text = await File.ReadAllTextAsync(file);
        Assert.Contains("\"t\":\"resolve\"", text, StringComparison.Ordinal);
        Assert.Contains("\"query\":\"ryo melt\"", text, StringComparison.Ordinal); // the provider's own search
        Assert.Contains(log.Lines, l => l.StartsWith("metadata-trace: resolved:", StringComparison.Ordinal));

        // The next start picks up where the user left it.
        using var next = new MetadataTrace(settings, folders, NoLog.Instance);
        Assert.True(next.Enabled);
    }

    // A provider that records what it searched for, as a catalog plugin does, and finds nothing.
    private sealed class Searching(IMetadataTrace trace) : IMetadataProvider
    {
        public string Id => "searching";

        public Task<MetadataContribution?> ContributeAsync(MetadataQuery query, CancellationToken ct)
        {
            trace.ProviderSearch(Id, "ryo melt", trace.NewCapture());
            return Task.FromResult<MetadataContribution?>(null);
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
