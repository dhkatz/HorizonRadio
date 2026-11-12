using System.Collections.Concurrent;
using System.Diagnostics;
using HorizonRadio.Core.Diagnostics;
using HorizonRadio.Core.IO;
using HorizonRadio.Core.Metadata;
using HorizonRadio.Core.Plugins;
using HorizonRadio.Core.Settings;
using HorizonRadio.Core.Sources;
using HorizonRadio.Core.Sources.Config;
using HorizonRadio.Core.Tools;
using HorizonRadio.Sdk;
using HorizonRadio.Sdk.Metadata;
using HorizonRadio.Sdk.Models;
using HorizonRadio.Sdk.Sources;
using HorizonRadio.Sdk.Sources.Config;

namespace HorizonRadio.Core.Hosting;

// The host's own implementations of the services plugins take (registered by AddHorizonCore). Each
// is the app's existing code for the job; a plugin module can replace or wrap any of them.

/// <summary>The Console tab (<see cref="ProcessConsole"/>) and the session log.</summary>
internal sealed class HostPluginLog : IPluginLog
{
    public void Write(string tag, string text) => ProcessConsole.AppendBlock(tag, text);

    public void Trace(string tag, string text) => System.Diagnostics.Trace.WriteLine($"[hzn-{tag}] {text}");
}

/// <summary>Starts a process and ties it to the app (<see cref="ChildProcesses.TieToApp"/>).</summary>
internal sealed class HostProcesses : IProcesses
{
    public Process Start(ProcessStartInfo info)
    {
        var process = Process.Start(info) ?? throw new InvalidOperationException($"Couldn't start {info.FileName}.");
        ChildProcesses.TieToApp(process);
        return process;
    }
}

/// <summary>The metadata diagnostics (<see cref="MetadataTrace"/>), switched on in About.</summary>
internal sealed class HostMetadataTrace : IMetadataTrace
{
    public bool Enabled => MetadataTrace.Enabled;

    public void ProviderSearch(string providerId, string query, IReadOnlyList<CatalogCandidate>? considered) =>
        MetadataTrace.ProviderSearch(providerId, query, considered);

    public void Song(string station, string raw, string? parsedArtist, string parsedTitle,
        string confidence, IReadOnlyList<TitleCandidate>? candidates, string modelMode, bool modelWillRun) =>
        MetadataTrace.Song(station, raw, parsedArtist, parsedTitle, confidence, candidates, modelMode, modelWillRun);

    public void Model(string raw, long latencyMs, IReadOnlyList<TitleCandidate> extracted, bool applied) =>
        MetadataTrace.Model(raw, latencyMs, extracted, applied);
}

/// <summary>One audio cache per source under the profile's data folder (<c>cache\audio\&lt;source&gt;</c>),
/// with two downloads at once across them all.</summary>
internal sealed class HostAudioCaches(IAppFolders folders) : IAudioCaches, IDisposable
{
    private readonly ConcurrentDictionary<string, AudioCache> _caches = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _downloads = new(2);

    public AudioCache ForSource(string sourceId) =>
        _caches.GetOrAdd(sourceId, id => new AudioCache(folders.Data("cache", "audio", id), _downloads));

    public void Dispose() => _downloads.Dispose();
}

/// <summary>A source's saved settings, from the settings store (<see cref="SourceConfigStore"/>).</summary>
internal sealed class HostSourceSettings(SourceConfigStore store) : ISourceSettings
{
    public ConfigValues Load(string sourceId, IReadOnlyList<ConfigField> schema) => store.Load(sourceId, schema);
}

/// <summary>The title model as the app last set it up (<see cref="TitleExtractorRuntime"/>).</summary>
internal sealed class HostTitleModel : ITitleModel
{
    public ITitleExtractor? Extractor => TitleExtractorRuntime.Current;

    public TitleModelMode Mode => TitleExtractorRuntime.Mode;
}

/// <summary>Each plugin's settings, in the app's settings store (<see cref="PluginSettings"/>).</summary>
internal sealed class HostPluginSettingsStore(SettingsStore settings) : IPluginSettingsStore
{
    public IPluginSettings ForPlugin(string pluginId, Func<string, bool>? synced = null) =>
        PluginSettings.For(settings, pluginId, synced);
}

/// <summary>The profile's folders (<see cref="AppPaths"/>).</summary>
internal sealed class HostAppFolders : IAppFolders
{
    public string? Profile => AppPaths.Profile;

    public string Data(params string[] parts) => AppPaths.In(parts);

    public string Label(string name) => AppPaths.Label(name);
}

/// <summary>The configured, installed or bundled tool (<see cref="ToolResolver"/>, <see cref="ToolsPaths"/>).</summary>
internal sealed class HostToolLocator : IToolLocator
{
    public string? Find(string kind, string? configuredPath = null) => ToolResolver.Resolve(configuredPath, kind);

    public string InstallPath(string kind) => ToolsPaths.PathFor(kind);
}
