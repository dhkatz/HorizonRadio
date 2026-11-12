using HorizonRadio.Core.Devices;
using HorizonRadio.Core.Friends;
using HorizonRadio.Core.Party;
using HorizonRadio.Core.Playback;
using HorizonRadio.Core.Settings;
using HorizonRadio.Core.Sources;
using HorizonRadio.Core.Sources.Config;
using HorizonRadio.Core.Sources.Mixes;
using HorizonRadio.Core.Sources.Queue;
using HorizonRadio.Core.Sources.YouTube;
using HorizonRadio.Sdk.Peers;
using HorizonRadio.Sdk.Sources;

namespace HorizonRadio.Core.Tests;

/// <summary>A party's shared queue end to end: two PCs, each with its real player and party, on an
/// in-memory peer network. Songs are fakes that each PC "finds" on its own by their YouTube links.</summary>
public sealed class PartyQueueTests : IAsyncDisposable
{
    private static readonly Dictionary<string, string> Catalog = new()
    {
        ["aaa"] = "Ievan Polkka",
        ["melt"] = "Melt",
        ["wim"] = "World is Mine",
        ["mine"] = "Rolling Girl",
    };

    private readonly MemoryPeerNetwork _net = new();
    private readonly List<IAsyncDisposable> _running = [];
    private readonly YouTubeSourceFactory _youTube = new();

    public async ValueTask DisposeAsync()
    {
        foreach (var r in Enumerable.Reverse(_running)) await r.DisposeAsync();
    }

    private static string Link(string id) => $"https://www.youtube.com/watch?v={id}";

    private sealed class Pc(string name)
    {
        private readonly Dictionary<string, FakeItem> _items = [];
        private readonly Dictionary<string, int> _finds = [];

        public string Name => name;
        public Friends.Friends Friends { get; set; } = null!;
        public ListeningParty Party { get; set; } = null!;
        public PlaybackRouter Router { get; set; } = null!;
        public QueueModel Model => Router.Player.Model;

        /// <summary>This PC's copy of a song (made the first time it's asked for).</summary>
        public FakeItem Item(string id)
        {
            lock (_items)
            {
                if (!_items.TryGetValue(id, out var item))
                    _items[id] = item = new FakeItem(Catalog[id], TimeSpan.FromMinutes(4), "youtube:" + id);
                return item;
            }
        }

        public bool Has(string id)
        {
            lock (_items) return _items.ContainsKey(id);
        }

        /// <summary>How many times the party looked for a song here.</summary>
        public int Finds(string id)
        {
            lock (_finds) return _finds.GetValueOrDefault(id);
        }

        public Task<PlayableItem> FindAsync(string locator)
        {
            var id = locator[(locator.IndexOf("v=", StringComparison.Ordinal) + 2)..];
            lock (_finds) _finds[id] = _finds.GetValueOrDefault(id) + 1;
            return Task.FromResult<PlayableItem>(Item(id));
        }

        public string[] Queued() => [.. Model.Snapshot().Explicit.Select(q => q.Metadata.Title)];
    }

    private async Task<Pc> NewPc(string name)
    {
        var pc = new Pc(name);
        var settings = SettingsStore.CreateInMemory();
        settings.Set(IdentitySettings.Name, name);
        var peer = _net.Join();
        var friends = new Friends.Friends(settings, _ => Task.FromResult<IPeerNetwork>(peer),
            presenceInterval: TimeSpan.FromMilliseconds(100), joinTimeout: TimeSpan.FromSeconds(20));
        var runner = new SourceRunner(new NullPcmSink());
        var config = new SourceConfigStore();
        var router = new PlaybackRouter(runner, config, new MixStore(), new MixContentResolver(config));
        var finder = new PartySongFinder((_, locator, _) => pc.FindAsync(locator),
            (_, _) => Task.FromResult<IReadOnlyList<PartySource>>([]), id => id == _youTube.Id ? _youTube : null);
        var host = new PlayerPartySource(runner, router.Player, finder);
        var party = new ListeningParty(friends, host, new QueuePartyPlayer(router.Player, finder), tick: TimeSpan.FromMilliseconds(100),
            heartbeat: TimeSpan.FromMilliseconds(500), joinTimeout: TimeSpan.FromSeconds(10));
        _running.Add(runner);
        _running.Add(router);
        _running.Add(peer);
        _running.Add(friends);
        _running.Add(new Disposing(host.Dispose));
        _running.Add(party);
        await friends.StartAsync();
        party.Start();
        (pc.Friends, pc.Party, pc.Router) = (friends, party, router);
        return pc;
    }

    private sealed class Disposing(Action dispose) : IAsyncDisposable
    {
        public ValueTask DisposeAsync()
        {
            dispose();
            return ValueTask.CompletedTask;
        }
    }

    private static async Task BecomeFriends(Pc a, Pc b)
    {
        var code = await a.Friends.InviteAsync();
        await b.Friends.AcceptAsync(code);
        await Eventually.Until(() => a.Friends.All.Any(f => f.IsOnline) && b.Friends.All.Any(f => f.IsOnline),
            "both to see each other online", 10_000);
    }

    [Fact]
    public async Task A_guest_adds_skips_and_removes_on_the_hosts_queue_and_hears_each_song_as_the_host_plays_it()
    {
        var sam = await NewPc("Sam");
        var alex = await NewPc("Alex");
        await BecomeFriends(sam, alex);

        // Sam plays one song with Melt next; Alex has a queue of their own.
        sam.Model.AppendExplicit([sam.Item("aaa"), sam.Item("melt")]);
        await sam.Router.Player.EnsureActiveAsync();
        await Eventually.Until(() => sam.Item("aaa").Plays == 1, "Sam to play");
        alex.Model.AppendExplicit([alex.Item("mine")]);

        await sam.Party.StartHostingAsync();
        await Eventually.Until(() => alex.Friends.All.Single().IsHosting, "Alex to see Sam hosting", 10_000);
        await alex.Party.JoinAsync(alex.Friends.All.Single().UserId);

        // Alex's queue is the party's now: Sam's, with Melt next, found and loaded ahead while the
        // song before plays.
        await Eventually.Until(() => alex.Model.Snapshot().Shared && alex.Queued() is ["Melt"], "Alex to see Sam's queue");
        await Eventually.Until(() => alex.Has("aaa") && alex.Item("aaa").Plays == 1, "Alex to play Sam's song", 10_000);
        await Eventually.Until(() => alex.Has("melt") && alex.Item("melt").Prefetches > 0, "Alex to load Melt ahead", 10_000);

        // Alex adds a song: it goes on Sam's queue, and so on Alex's.
        alex.Model.AppendExplicit([new FakeItem("World is Mine", null, "youtube:wim")]);
        await Eventually.Until(() => sam.Queued() is ["Melt", "World is Mine"], "the song to be on Sam's queue");
        await Eventually.Until(() => alex.Queued() is ["Melt", "World is Mine"], "Alex to see it there");

        // Alex skips: Sam goes on to Melt, and Alex plays it from what it loaded ahead.
        await alex.Router.Player.ActiveEngine!.NextAsync();
        await Eventually.Until(() => sam.Item("melt").Plays == 1, "Sam to skip to Melt");
        await Eventually.Until(() => alex.Item("melt").Plays == 1, "Alex to play Melt", 10_000);
        Assert.Equal(1, alex.Finds("melt"));
        await Eventually.Until(() => alex.Queued() is ["World is Mine"], "Melt to leave the list as it plays");

        // Alex removes their song from the party's queue.
        alex.Model.RemoveExplicit(alex.Model.Snapshot().Explicit.Single().Id);
        await Eventually.Until(() => sam.Queued() is [] && alex.Queued() is [], "the song to be gone for both");

        // Leaving, Alex gets their own queue back.
        await alex.Party.LeaveAsync();
        Assert.False(alex.Model.Snapshot().Shared);
        Assert.Equal(["Rolling Girl"], alex.Queued());
    }
}
