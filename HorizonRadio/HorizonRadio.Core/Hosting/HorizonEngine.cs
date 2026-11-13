using HorizonRadio.Core.Audio;
using HorizonRadio.Core.Devices;
using HorizonRadio.Core.Diagnostics;
using HorizonRadio.Core.Events;
using HorizonRadio.Core.Fingerprints;
using HorizonRadio.Core.Games;
using HorizonRadio.Core.History;
using HorizonRadio.Core.Input;
using HorizonRadio.Core.Lyrics;
using HorizonRadio.Core.Metadata;
using HorizonRadio.Core.Party;
using HorizonRadio.Core.Peers;
using HorizonRadio.Core.Playback;
using HorizonRadio.Core.Settings;
using HorizonRadio.Core.Sources;
using HorizonRadio.Core.Sources.Config;
using HorizonRadio.Core.Sources.Mixes;
using HorizonRadio.Core.Sources.Queue;
using HorizonRadio.Sdk;
using HorizonRadio.Sdk.Games;
using HorizonRadio.Sdk.Lyrics;
using HorizonRadio.Sdk.Models;
using HorizonRadio.Sdk.Peers;
using Microsoft.Extensions.DependencyInjection;
using FriendsService = HorizonRadio.Core.Friends.Friends;

namespace HorizonRadio.Core.Hosting;

/// <summary>
/// The engine, wired for one game: the audio pipeline into the game (and the local speaker), the
/// runner and the router that owns it, metadata and lyrics, history, events and bound inputs, your
/// devices, friends and parties. It's built from the container's services and knows nothing of a
/// front end: what it raises, it raises on its own threads.
///
/// Built by <see cref="HorizonHost.StartEngine"/>, then started (<see cref="Start"/>) once the front
/// end has subscribed to what it shows. Disposing it stops everything in the order that matters:
/// the peer helper first (friends and your other PCs see this PC leave at once), the router and the
/// runner before the game (nothing still sending it audio), the network last.
/// </summary>
public sealed class HorizonEngine : IAsyncDisposable
{
    private readonly SharedPeerNetwork _peers;
    private readonly PlayerPartySource _partyHost;
    private readonly Lock _gainLock = new();
    private float _sentGain = float.NaN;
    private bool _started;
    private bool _disposed;

    internal HorizonEngine(IServiceProvider services, string gameId, IGameIntegration game)
    {
        Services = services;
        GameId = gameId;
        Game = game;
        var settings = services.GetRequiredService<SettingsStore>();
        var log = services.GetRequiredService<IPluginLog>();
        Sources = services.GetRequiredService<SourceCatalog>();
        SourceConfig = services.GetRequiredService<SourceConfigStore>();
        Mixes = services.GetRequiredService<MixStore>();

        // Tee the pipeline so the active source plays to the game and, when the user turns it on,
        // to a local speaker instead.
        var tee = new TeePcmSink(game.Output);
        Preview = new PreviewController(tee, settings, services.GetRequiredService<ISpeakers>());
        Runner = new SourceRunner(tee);

        // The metadata pipeline: one resolver (the source, then the providers in the user's order,
        // with a policy per field) for play-time enrichment and for lists.
        MetadataConfig = services.GetRequiredService<MetadataConfigStore>();
        Metadata = new MetadataResolver(log, services.GetRequiredService<MetadataTrace>());
        var (contributors, policy) = MetadataConfig.Catalog.BuildPipeline(MetadataConfig);
        Metadata.Configure(contributors, policy);
        Enricher = new EnrichmentService(Runner, Metadata);

        // Lyrics from every lyrics plugin, in their order: synced lyrics first, then the earlier
        // provider. Radio lyrics follow the song by lining the station's audio up against a
        // recording of it (fetched through a source that can, e.g. YouTube).
        Lyrics = new LyricsService(services.GetServices<ILyricsProvider>().InDisplayOrder(), log);
        SongSync = new LiveSongSync(() => Sources.All, services.GetRequiredService<ReferenceStore>(), Metadata, log);

        // The router is the one owner of the engine: everything that changes what plays (the game
        // tuning, the queue getting something to play, a source started or stopped) goes through it,
        // one step at a time. The player is the queue: explicit songs first, then the active mix as
        // its endless tail, which the switcher sets.
        ContentResolver = services.GetRequiredService<MixContentResolver>();
        Router = new PlaybackRouter(Runner, SourceConfig, Mixes, ContentResolver, log: log);
        MixSwitcher = new MixSwitcher(Mixes, Player, Runner, log);
        // Mix entries shown as what they are (titles, artists, covers), looked up through the same
        // sources and metadata the queue uses, and kept on disk between sessions.
        MixEntries = new MixEntryDescriber(ContentResolver, Metadata, services.GetRequiredService<MixEntryInfoStore>());

        // Every song the runner reports, deduped, tagged through the metadata pipeline, kept.
        HistoryStore = services.GetRequiredService<PlayHistoryStore>();
        History = new PlayHistoryService(HistoryStore, Runner, Sources);
        HistoryIdentifier = new HistoryIdentifier(HistoryStore, Metadata, Sources, log);

        // One dispatcher turns an action into a transport, source, mix or volume call, for game
        // events (the rules) and bound inputs alike.
        EventRules = services.GetRequiredService<EventRuleStore>();
        Actions = new ActionDispatcher(Router, SourceConfig, log, MixSwitcher,
            game.Volume is { } gameVolume ? gameVolume.SetDuckGain : null);
        Events = new EventActionExecutor(game.EventSources, EventRules, Actions, log);
        InputBindings = services.GetRequiredService<InputBindingStore>();
        MixLibrary = new MixLibrary(Mixes, MixEntries, InputBindings, log);
        Input = new InputBindingService(services.GetServices<IInputBackend>(), InputBindings, Actions);

        // Your devices and your friends share the peer helper, which starts once either needs it
        // (this PC paired, or pairing; a friend added). Parties run over the friends' connections.
        _peers = new SharedPeerNetwork(services.GetRequiredService<IPeerNetworkFactory>().StartAsync);
        Devices = new MyDevices(settings, _peers.GetAsync, data: [new MixSync(Mixes), new HistorySync(HistoryStore)]);
        Friends = new FriendsService(settings, _peers.GetAsync);
        _partyHost = new PlayerPartySource(Runner, Player);
        Party = new ListeningParty(Friends, _partyHost, new QueuePartyPlayer(Player));

        Runner.TrackChanged += OnTrackChanged;
        Enricher.TrackEnriched += OnTrackEnriched;
        Runner.ActiveSourceChanged += OnActiveSourceChanged;
        Preview.VolumeChanged += OnVolumeChanged;
        game.StateChanged += OnGameStateChanged;
    }

    public IServiceProvider Services { get; }

    /// <summary>The game this engine plays into (a plugin's id, or <see cref="NoGameIntegration.Id"/>).</summary>
    public string GameId { get; }

    public IGameIntegration Game { get; }
    public SourceCatalog Sources { get; }
    public SourceConfigStore SourceConfig { get; }
    public MixStore Mixes { get; }
    public PreviewController Preview { get; }
    public SourceRunner Runner { get; }
    public MetadataConfigStore MetadataConfig { get; }
    public MetadataResolver Metadata { get; }
    public EnrichmentService Enricher { get; }
    public LyricsService Lyrics { get; }
    public LiveSongSync SongSync { get; }
    public MixContentResolver ContentResolver { get; }
    public PlaybackRouter Router { get; }

    /// <summary>The player: the queue.</summary>
    public QueuePlayback Player => Router.Player;

    public MixSwitcher MixSwitcher { get; }

    /// <summary>What each mix entry is, for the mix editor and lists.</summary>
    public MixEntryDescriber MixEntries { get; }
    public PlayHistoryStore HistoryStore { get; }
    public PlayHistoryService History { get; }

    /// <summary>Identifies the radio songs in the history and finds where to replay them.</summary>
    public HistoryIdentifier HistoryIdentifier { get; }
    public EventRuleStore EventRules { get; }
    public ActionDispatcher Actions { get; }
    public EventActionExecutor Events { get; }
    public InputBindingStore InputBindings { get; }

    /// <summary>Saving, deleting and adding to mixes (playing one is <see cref="MixSwitcher"/>'s).</summary>
    public MixLibrary MixLibrary { get; }
    public InputBindingService Input { get; }
    public MyDevices Devices { get; }
    public FriendsService Friends { get; }
    public ListeningParty Party { get; }

    /// <summary>Starts what runs on its own: your devices, friends and parties, the game, and
    /// bound inputs. Call once the front end has subscribed to what it shows.</summary>
    public void Start()
    {
        if (_started || _disposed) return;
        _started = true;
        _ = Devices.StartAsync();
        _ = Friends.StartAsync();
        Party.Start();
        Game.Start(new GameHost(Router));
        Input.Start();
        HistoryIdentifier.Start();
    }

    private void OnTrackChanged(Track t)
    {
        Game.NowPlaying?.Show(t);
        Friends.SetListening(ListeningTo(t));
    }

    private void OnTrackEnriched(Track t)
    {
        // A lookup can finish after its track was parked (the game routed elsewhere): don't put a
        // title back up for a channel that's no longer playing.
        if (Runner.ActiveSource == null) return;
        Game.NowPlaying?.Show(t);
        Friends.SetListening(ListeningTo(t));
    }

    // Nothing on the runner any more (stopped, or parked as the game switched channels): the game
    // shouldn't keep showing the last track. A channel that resumes announces its track as it starts.
    // (This also fires with no factory when a channel starts, so check the runner itself.)
    private void OnActiveSourceChanged(Sdk.Sources.IAudioSourceFactory? _)
    {
        if (Runner.ActiveSource != null) return;
        Game.NowPlaying?.Clear();
        Friends.SetListening(null);
    }

    // The master volume doubles as the game's pre-amp: send its tapered gain on every change (live
    // while dragging), but not a value already sent.
    private void OnVolumeChanged(double volume) => SendGain(volume, force: false);

    // The game re-asserts its own station targets, duck gain and title on connect; the master
    // volume is ours to send again, since the game side starts from its conservative default.
    private void OnGameStateChanged(ConnectionState state)
    {
        if (state == ConnectionState.Connected) SendGain(Preview.Volume, force: true);
    }

    private void SendGain(double volume, bool force)
    {
        var gain = VolumeTaper.ToGain(volume);
        lock (_gainLock)
        {
            if (!force && gain.Equals(_sentGain)) return;
            if (Game.Volume?.SetMasterVolume(gain) == true) _sentGain = gain;
        }
    }

    // What your friends see you playing: a real song only (not a station card before its first title).
    private static Friends.Listening? ListeningTo(Track t) =>
        t.Resolvable && !string.IsNullOrWhiteSpace(t.Title)
            ? new Friends.Listening(t.Title, t.Artist, t.SourceDisplay, t.ExternalId)
            : null;

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        // First, and without waiting: the helper closes its connections, so friends and your other
        // PCs see this one leave now.
        _peers.Stop();
        Runner.TrackChanged -= OnTrackChanged;
        Enricher.TrackEnriched -= OnTrackEnriched;
        Runner.ActiveSourceChanged -= OnActiveSourceChanged;
        Preview.VolumeChanged -= OnVolumeChanged;
        Game.StateChanged -= OnGameStateChanged;

        Events.Dispose();
        Input.Dispose();
        History.Dispose(); // unsubscribes from the runner and writes the last save
        MixLibrary.Dispose();
        MixSwitcher.Dispose();
        Preview.Dispose();
        await Enricher.DisposeAsync().ConfigureAwait(false);
        await Metadata.DisposeAsync().ConfigureAwait(false);
        await Router.DisposeAsync().ConfigureAwait(false); // stops the engine; disposes the channels and the player
        await Runner.DisposeAsync().ConfigureAwait(false);
        // After the runner, so nothing is still sending audio to the game.
        await Game.DisposeAsync().ConfigureAwait(false);
        await Party.DisposeAsync().ConfigureAwait(false);
        _partyHost.Dispose();
        await Friends.DisposeAsync().ConfigureAwait(false);
        await Devices.DisposeAsync().ConfigureAwait(false);
        await _peers.DisposeAsync().ConfigureAwait(false); // stops the peer helper
    }
}
