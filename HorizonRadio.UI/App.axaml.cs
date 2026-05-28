using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using HorizonRadio.Core.Ipc;
using HorizonRadio.Core.Metadata;
using HorizonRadio.Core.Models;
using HorizonRadio.Core.Sources;
using HorizonRadio.Core.Sources.Config;
using HorizonRadio.UI.ViewModels;
using HorizonRadio.UI.Views;

namespace HorizonRadio.UI;

public partial class App : Application
{
    private IpcClient?            _ipc;
    private PcmPipeClient?        _pcm;
    private SourceRunner?         _runner;
    private SourceConfigStore?    _store;
    private EnrichmentService?    _enricher;
    private MetadataConfigStore?  _metaStore;

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // Config persistence is read once at startup; the Sources VM
            // mutates and saves it as the user changes selections.
            _store = SourceConfigStore.LoadFromDisk();

            // PCM ingress to the DLL. The runner sends through this sink;
            // it survives DLL disconnects transparently (writes drop until
            // the pipe reconnects).
            _pcm = new PcmPipeClient();
            _pcm.Start();
            var sink = new PcmPipeSink(_pcm);

            _runner = new SourceRunner(sink);

            // Metadata pipeline. The service starts with no enricher;
            // MetadataViewModel applies whatever the user had selected
            // last run (or "None" by default) in its constructor.
            _metaStore   = MetadataConfigStore.LoadFromDisk();
            var cache    = new MetadataCache();
            _enricher    = new EnrichmentService(_runner, enricher: null);
            var metaVm   = new MetadataViewModel(_metaStore, cache, _enricher);

            var vm = new MainWindowViewModel(_runner, _store, metaVm);
            desktop.MainWindow = new MainWindow { DataContext = vm };

            // Control IPC: track/stats/source events from the DLL.
            // Track changes from the local source feed into NowPlaying too,
            // so the UI updates even before the DLL pushes any metadata.
            _ipc = new IpcClient();
            _ipc.Connected    += () => Dispatcher.UIThread.Post(() => vm.SetConnection(ConnectionState.Connected));
            _ipc.Disconnected += () => Dispatcher.UIThread.Post(() => vm.SetConnection(ConnectionState.Disconnected));
            _ipc.StatsUpdated += s  => Dispatcher.UIThread.Post(() => vm.Stats.Apply(s));
            // No subscription to _ipc.TrackChanged: the UI is the
            // authoritative source for "what's playing" now. Subscribing
            // would create an echo loop — we push the track to the DLL
            // via SendTrack, the DLL re-publishes it on its event
            // channel without album art bytes, and the echo overwrites
            // the in-process Track (clobbering AlbumArt).
            _ipc.Start();

            _runner.TrackChanged += t =>
            {
                // Forward to the in-app HUD on the UI thread.
                Dispatcher.UIThread.Post(() => vm.NowPlaying.Apply(t));
                // And push to the DLL so the in-game radio HUD reflects
                // the same track. Best-effort: a no-op while the DLL
                // isn't connected (FH6 not running). Runs on whatever
                // thread fired TrackChanged — IpcClient serializes
                // writes internally, so this is safe.
                _ipc?.SendTrack(t);
            };

            // Re-publish enriched tracks to both the in-app HUD and
            // the DLL so the in-game HUD picks up canonical fields.
            _enricher.TrackEnriched += t =>
            {
                Dispatcher.UIThread.Post(() => vm.NowPlaying.Apply(t));
                _ipc?.SendTrack(t);
            };

            desktop.ShutdownRequested += async (_, _) =>
            {
                if (_enricher != null) await _enricher.DisposeAsync();
                if (_runner   != null) await _runner.DisposeAsync();
                if (_ipc      != null) await _ipc.DisposeAsync();
                if (_pcm      != null) await _pcm.DisposeAsync();
            };

            vm.SetConnection(ConnectionState.Connecting);
        }

        base.OnFrameworkInitializationCompleted();
    }
}
