using HorizonRadio.Core.Diagnostics;
using HorizonRadio.Core.Games;
using HorizonRadio.Core.IO;
using HorizonRadio.Core.Plugins;
using HorizonRadio.Core.Settings;
using HorizonRadio.Core.Sources;
using HorizonRadio.Core.Sources.Mixes;
using HorizonRadio.Sdk;
using Microsoft.Extensions.DependencyInjection;

namespace HorizonRadio.Core.Hosting;

/// <summary>How a front end starts Horizon Radio (<see cref="HorizonHost.Build"/>).</summary>
public sealed class HorizonHostOptions
{
    /// <summary>Where this instance keeps its files: settings, stores, caches, logs. Default: the
    /// profile's folder (<see cref="AppFolders.ForProfile"/>). A test runs on a temporary one.</summary>
    public AppFolders? Folders { get; init; }

    /// <summary>The settings to run on; the host owns them from here. Default: <c>settings.json</c>
    /// in <see cref="Folders"/>.</summary>
    public SettingsStore? Settings { get; init; }

    /// <summary>The OS code to run on (<c>HorizonRadio.Platform</c>'s <c>HostPlatforms.Current()</c>).
    /// Default: none, Core's portable defaults (no speakers), which is what a test wants.</summary>
    public IHostPlatform? Platform { get; init; }

    /// <summary>The plugin and module types to register. Default: the scan of the app's plugin
    /// assemblies (<see cref="PluginDiscovery.PluginTypes"/>).</summary>
    public IEnumerable<Type>? PluginTypes { get; init; }

    /// <summary>The front end's own services, registered after Core's and before the plugins'
    /// (dialogs for <c>IConfirmations</c>, say), so a plugin can still replace them.</summary>
    public Action<IServiceCollection>? ConfigureServices { get; init; }

    /// <summary>Whether this session's log goes to a file (<see cref="LogFile"/>, in <c>logs</c>). Default: yes.</summary>
    public bool WriteLogFile { get; init; } = true;
}

/// <summary>
/// One running Horizon Radio, whatever front end it has: the window, the CLI, a test. It opens the
/// settings, builds the container (Core's services, the OS's, the front end's, then the plugins'), brings old
/// settings over, and then starts the engine for the chosen game (<see cref="StartEngine"/>).
/// Disposing it stops the engine, then the container, then the settings (writing anything still
/// pending), then the log.
///
/// The front end only adds itself: it reads what it shows from <see cref="Engine"/> and the
/// container, and asks the user to pick a game when <see cref="ChosenGame"/> can't.
/// </summary>
public sealed class HorizonHost : IAsyncDisposable
{
    private readonly ServiceProvider _services;
    private readonly bool _ownsLogFile;
    private bool _disposed;

    private HorizonHost(ServiceProvider services, SettingsStore settings, bool ownsLogFile)
    {
        _services = services;
        Settings = settings;
        _ownsLogFile = ownsLogFile;
        Games = services.GetServices<IGamePlugin>().InDisplayOrder();
    }

    /// <summary>Opens the settings and builds the container.</summary>
    /// <exception cref="InvalidOperationException">A service asks for something nobody registers
    /// (the container is built with <c>ValidateOnBuild</c>).</exception>
    public static HorizonHost Build(HorizonHostOptions? options = null)
    {
        options ??= new HorizonHostOptions();
        // The log and the settings open before the container, so they're in the host's folders even
        // when a module replaces IAppFolders; the stores and caches follow the replacement.
        var folders = options.Folders ?? AppFolders.ForProfile();
        // Everything the Console tab shows, and the app's own debug lines, also go to a file per
        // session, so a failure can be read afterwards.
        if (options.WriteLogFile) LogFile.Start(folders.Data("logs"));
        // Every setting, in one file; the first run on it brings over the older per-feature files.
        var settings = options.Settings ?? SettingsStore.Open(folders.Data(SettingsStore.FileName));
        var services = new ServiceCollection();
        services.AddHorizonCore(settings, folders);
        // The OS's services over Core's portable defaults; the front end and the plugins come after,
        // so they can still replace one.
        if (options.Platform is { } platform)
        {
            platform.ConfigureServices(services);
            ProcessConsole.Append("plugins", $"platform: {platform.Name}");
        }
        options.ConfigureServices?.Invoke(services);
        // Last, so a module can replace or wrap any service above; the Console tab says when one does.
        services.AddPlugins(options.PluginTypes ?? PluginDiscovery.PluginTypes(),
            line => ProcessConsole.Append("plugins", line));
        var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true });
        // Opt-in metadata diagnostics start capturing now if the user left them on, before any
        // source can record, so the first song of the session is captured too.
        provider.GetRequiredService<MetadataTrace>();

        // Before any game is built: its settings must already hold anything migrated into them.
        // Saving drops the legacy fields so the migration can't re-apply later.
        var mixes = provider.GetRequiredService<MixStore>();
        if (LegacySettingsMigration.MigrateStations(settings, mixes).MixesChanged) mixes.SaveToDisk();
        // A first run on mixes turns the old profiles.json into one-entry mixes.
        MixMigration.MaybeMigrate(mixes, provider.GetRequiredService<SourceCatalog>(),
            provider.GetRequiredService<IAppFolders>().Data("profiles.json"));

        return new HorizonHost(provider, settings, options.WriteLogFile);
    }

    /// <summary>The container: Core's services, the front end's and the plugins'.</summary>
    public IServiceProvider Services => _services;

    public SettingsStore Settings { get; }

    /// <summary>The game plugins, in display order.</summary>
    public IReadOnlyList<IGamePlugin> Games { get; }

    /// <summary>The engine, once <see cref="StartEngine"/> has run.</summary>
    public HorizonEngine? Engine { get; private set; }

    /// <summary>The game to start with: the saved one, else a game the user already has settings
    /// for (remembered from now on), else null, when the front end asks the user.</summary>
    public string? ChosenGame()
    {
        var ids = Games.Select(g => g.Id).ToList();
        var saved = Settings.Get(GameSelection.Chosen);
        var id = GameSelection.Resolve(saved, ids, [.. ids.Where(i => PluginSettings.HasSettings(Settings, i))]);
        if (id != null && id != saved) Settings.Set(GameSelection.Chosen, id);
        return id;
    }

    /// <summary>Builds the engine for <paramref name="gameId"/> (a game plugin's id, or
    /// <see cref="NoGameIntegration.Id"/>). The front end subscribes to what it shows, then calls
    /// <see cref="HorizonEngine.Start"/>.</summary>
    /// <exception cref="InvalidOperationException">The engine was already built.</exception>
    public HorizonEngine StartEngine(string gameId)
    {
        if (Engine != null) throw new InvalidOperationException("The engine is already built.");
        var game = gameId == NoGameIntegration.Id
            ? new NoGameIntegration()
            : Games.First(g => g.Id == gameId).Create();
        Engine = new HorizonEngine(_services, gameId, game);
        return Engine;
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        if (Engine != null) await Engine.DisposeAsync().ConfigureAwait(false);
        // The container owns the leaf services (stores, caches) and the plugins' (Spotify's
        // connection and players among them).
        await _services.DisposeAsync().ConfigureAwait(false);
        Settings.Dispose(); // writes any change still waiting to be saved
        if (_ownsLogFile) LogFile.Stop();
    }
}
