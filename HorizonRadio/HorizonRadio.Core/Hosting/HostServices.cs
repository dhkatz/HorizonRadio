using System.Collections.Concurrent;
using System.Diagnostics;
using HorizonRadio.Core.Diagnostics;
using HorizonRadio.Core.Metadata;
using HorizonRadio.Core.Plugins;
using HorizonRadio.Core.Settings;
using HorizonRadio.Core.Sources;
using HorizonRadio.Core.Sources.Config;
using HorizonRadio.Core.Tools;
using HorizonRadio.Sdk;
using HorizonRadio.Sdk.Metadata;
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

/// <summary>Starts a process. The platform (<see cref="IHostPlatform"/>) wraps it to tie each
/// process to the app's lifetime, where the OS can (a job object on Windows).</summary>
internal sealed class HostProcesses : IProcesses
{
    public Process Start(ProcessStartInfo info) =>
        Process.Start(info) ?? throw new InvalidOperationException($"Couldn't start {info.FileName}.");
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

/// <summary>The user's title-model setting, with no model to run: the title model's module
/// (<c>HorizonRadio.TitleModel</c>) wraps this with the one installed from the Tools tab.</summary>
internal sealed class HostTitleModel(SettingsStore settings) : ITitleModel
{
    public ITitleExtractor? Extractor => null;

    public TitleModelMode Mode => settings.Get(MetadataSettings.TitleModel);
}

/// <summary>Each plugin's settings, in the app's settings store (<see cref="PluginSettings"/>).</summary>
internal sealed class HostPluginSettingsStore(SettingsStore settings) : IPluginSettingsStore
{
    public IPluginSettings ForPlugin(string pluginId, Func<string, bool>? synced = null) =>
        PluginSettings.For(settings, pluginId, synced);
}

/// <summary>Notifications for a host with no window: they go to the Console log.</summary>
internal sealed class LoggedNotifications(IPluginLog log) : INotifications
{
    public void Show(string title, string message, NotificationKind kind = NotificationKind.Info, TimeSpan? duration = null) =>
        log.Write("notice", $"{kind}: {title}: {message}");
}

/// <summary>The configured, installed or bundled tool (<see cref="ToolCatalog"/>).</summary>
internal sealed class HostToolLocator(ToolCatalog tools) : IToolLocator
{
    public string? Find(string kind, string? configuredPath = null) => tools.Resolve(configuredPath, kind);

    public string InstallPath(string kind) => tools.PathFor(kind);
}
