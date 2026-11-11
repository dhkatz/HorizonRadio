using HorizonRadio.Core.Devices;
using HorizonRadio.Core.Friends;
using HorizonRadio.Core.Party;
using HorizonRadio.Core.Peers;
using HorizonRadio.Core.Settings;

namespace HorizonRadio.Core.Tests;

/// <summary>Listening parties between friends, on an in-memory peer network: the host's player is a
/// fake that runs on the real clock, and each guest's a fake that plays what it's given.</summary>
public sealed class ListeningPartyTests : IAsyncDisposable
{
    private static readonly TimeSpan Soon = TimeSpan.FromSeconds(10);
    private readonly MemoryPeerNetwork _net = new();
    private readonly List<IAsyncDisposable> _running = [];

    public async ValueTask DisposeAsync()
    {
        foreach (var r in Enumerable.Reverse(_running)) await r.DisposeAsync();
    }

    private sealed record Person(string Name, Friends.Friends Friends, ListeningParty Party, FakeHost Host, FakePlayer Player, MemoryPeer Peer);

    private async Task<Person> NewPerson(string name, TimeProvider? clock = null)
    {
        var settings = SettingsStore.CreateInMemory();
        settings.Set(IdentitySettings.Name, name);
        var peer = _net.Join();
        var friends = new Friends.Friends(settings, _ => Task.FromResult<IPeerNetwork>(peer),
            presenceInterval: TimeSpan.FromMilliseconds(100), joinTimeout: TimeSpan.FromSeconds(20));
        var host = new FakeHost();
        var player = new FakePlayer(clock ?? TimeProvider.System);
        var party = new ListeningParty(friends, host, player, clock, tick: TimeSpan.FromMilliseconds(100),
            heartbeat: TimeSpan.FromMilliseconds(500), tolerance: TimeSpan.FromMilliseconds(300), joinTimeout: TimeSpan.FromSeconds(10));
        _running.Add(peer);
        _running.Add(friends);
        _running.Add(party);
        await friends.StartAsync();
        party.Start();
        return new Person(name, friends, party, host, player, peer);
    }

    private static async Task Eventually(Func<bool> condition, string what)
    {
        var end = DateTime.UtcNow + Soon;
        while (!condition())
        {
            if (DateTime.UtcNow > end) Assert.Fail($"timed out waiting for {what}");
            await Task.Delay(20);
        }
    }

    private static async Task BecomeFriends(Person a, Person b)
    {
        var code = await a.Friends.InviteAsync();
        await b.Friends.AcceptAsync(code);
        await Eventually(() => a.Friends.All.Any(f => f.IsOnline) && b.Friends.All.Any(f => f.IsOnline), "both to see each other online");
    }

    private static string UserIdOf(Person friend, Person of) => of.Friends.All.Single(f => f.Name == friend.Name).UserId;

    // Sam hosts, Alex joins.
    private async Task<(Person Sam, Person Alex)> PartyOfTwo(TimeProvider? alexClock = null)
    {
        var sam = await NewPerson("Sam");
        var alex = await NewPerson("Alex", alexClock);
        await BecomeFriends(sam, alex);
        await sam.Party.StartHostingAsync();
        await Eventually(() => alex.Friends.All.Single().IsHosting, "Alex to see Sam hosting");
        await alex.Party.JoinAsync(UserIdOf(sam, alex));
        return (sam, alex);
    }

    private static PartySong Song(string key, string title = "Melt", bool live = false) =>
        new(key, title, "ryo", live ? null : 240_000, live, [new PartySource("spotify-driven", "spotify:track:" + key)]);

    [Fact]
    public async Task Friends_see_you_hosting_and_a_guest_hears_your_song_where_you_are()
    {
        var (sam, alex) = await PartyOfTwo();
        sam.Host.Play(Song("1"), at: TimeSpan.FromSeconds(30));

        await Eventually(() => alex.Player.Playing?.Song.Key == "1", "Alex to play Sam's song");
        Assert.Equal(PartyRole.Guest, alex.Party.View.Role);
        Assert.Equal("Sam", alex.Party.View.HostName);
        await Eventually(() => sam.Party.View.Guests.SequenceEqual(["Alex"]), "Sam to see Alex listening along");

        // Started where Sam was when it started, and keeps with Sam.
        await Task.Delay(500);
        var off = alex.Player.Current!.Position - sam.Host.Position;
        Assert.True(off.Duration() < TimeSpan.FromMilliseconds(250), $"Alex is {off.TotalMilliseconds:0} ms off Sam");
    }

    [Fact]
    public async Task A_guest_whose_clock_is_an_hour_off_still_starts_where_the_host_is()
    {
        var (sam, alex) = await PartyOfTwo(new OffsetClock(TimeSpan.FromHours(1)));
        await Task.Delay(300); // a few pings
        sam.Host.Play(Song("1"), at: TimeSpan.FromSeconds(42));

        await Eventually(() => alex.Player.Current is { Started: true }, "Alex to play");
        var off = alex.Player.Current!.Position - sam.Host.Position;
        Assert.True(off.Duration() < TimeSpan.FromMilliseconds(250), $"Alex is {off.TotalMilliseconds:0} ms off Sam");
    }

    [Fact]
    public async Task A_guest_follows_the_next_song_pause_and_a_seek()
    {
        var (sam, alex) = await PartyOfTwo();
        sam.Host.Play(Song("1"));
        await Eventually(() => alex.Player.Current is { Key: "1", Started: true }, "the first song");

        sam.Host.Play(Song("2", "World is Mine"), at: TimeSpan.FromSeconds(5));
        await Eventually(() => alex.Player.Current is { Key: "2", Started: true }, "the next song");

        sam.Host.SetPaused(true);
        await Eventually(() => alex.Player.Current!.Paused, "Alex to pause with Sam");
        sam.Host.SetPaused(false);
        await Eventually(() => !alex.Player.Current!.Paused, "Alex to play on with Sam");

        sam.Host.Seek(TimeSpan.FromSeconds(120));
        await Eventually(() => (alex.Player.Current!.Position - sam.Host.Position).Duration() < TimeSpan.FromMilliseconds(300)
                && alex.Player.Current.Position > TimeSpan.FromSeconds(100),
            "Alex to follow Sam's seek");
    }

    [Fact]
    public async Task A_guest_that_drifts_is_put_back()
    {
        var (sam, alex) = await PartyOfTwo();
        sam.Host.Play(Song("1"), at: TimeSpan.FromSeconds(10));
        await Eventually(() => alex.Player.Current is { Started: true }, "Alex to play");
        await Task.Delay(2200); // past settling in

        alex.Player.Drift(TimeSpan.FromSeconds(-4)); // stalled for a while
        await Eventually(() => (alex.Player.Current!.Position - sam.Host.Position).Duration() < TimeSpan.FromMilliseconds(300),
            "Alex to catch up");
        Assert.True(alex.Player.Seeks > 0);
    }

    [Fact]
    public async Task A_guests_own_pause_stands_until_the_host_pauses_or_plays()
    {
        var (sam, alex) = await PartyOfTwo();
        sam.Host.Play(Song("1"));
        await Eventually(() => alex.Player.Current is { Started: true }, "Alex to play");
        await Task.Delay(2200);

        await alex.Player.SetPausedAsync(true); // Alex answers the door
        await Task.Delay(800);
        Assert.True(alex.Player.Current!.Paused);

        sam.Host.SetPaused(true);
        await Task.Delay(500);
        sam.Host.SetPaused(false);
        await Eventually(() => !alex.Player.Current!.Paused, "Sam playing on to bring Alex back");
    }

    [Fact]
    public async Task A_guest_paused_when_the_host_moves_on_still_plays_the_next_song()
    {
        var (sam, alex) = await PartyOfTwo();
        sam.Host.Play(Song("1"));
        await Eventually(() => alex.Player.Current is { Key: "1", Started: true }, "the first song");
        sam.Host.SetPaused(true);
        await Eventually(() => alex.Player.Current!.Paused, "Alex to pause with Sam");

        // Alex's player is still paused when the next song is put on, so it won't start by itself.
        sam.Host.Play(Song("2"), at: TimeSpan.FromSeconds(3));
        await Eventually(() => alex.Player.Current is { Key: "2", Started: true, Paused: false }, "the next song to play");
        await Task.Delay(300);
        var off = alex.Player.Current!.Position - sam.Host.Position;
        Assert.True(off.Duration() < TimeSpan.FromMilliseconds(300), $"Alex is {off.TotalMilliseconds:0} ms off Sam");
    }

    [Fact]
    public async Task A_station_is_tuned_into_and_its_song_changes_dont_retune_it()
    {
        var (sam, alex) = await PartyOfTwo();
        var station = new PartySong("live:radio:radio://abc", "Melt", "ryo", null, true, [new PartySource("radio", "radio://abc")]);
        sam.Host.Play(station);
        await Eventually(() => alex.Player.Playing?.Song.Key == station.Key, "Alex to tune in");

        sam.Host.Play(station with { Title = "Ievan Polkka", Artist = "Otomania" }); // the station played on
        await Eventually(() => alex.Party.View.Song?.Title == "Ievan Polkka", "Alex to see the station's next song");
        Assert.Equal(1, alex.Player.Plays);
    }

    [Fact]
    public async Task A_song_the_guest_cant_find_is_said_and_the_next_one_plays()
    {
        var (sam, alex) = await PartyOfTwo();
        alex.Player.Has = s => s.Key != "rare";
        sam.Host.Play(Song("rare", "B-side"));
        await Eventually(() => alex.Party.View.Problem?.Contains("Couldn't find \"B-side\"") == true, "Alex to hear it wasn't found");

        sam.Host.Play(Song("2"));
        await Eventually(() => alex.Player.Current?.Key == "2", "the next song to play");
        Assert.Null(alex.Party.View.Problem);
    }

    [Fact]
    public async Task Ending_the_party_tells_the_guests_and_their_music_plays_on()
    {
        var (sam, alex) = await PartyOfTwo();
        sam.Host.Play(Song("1"));
        await Eventually(() => alex.Player.Current is { Started: true }, "Alex to play");

        await sam.Party.StopHostingAsync();
        await Eventually(() => alex.Party.View.Role == PartyRole.None, "Alex to be told");
        Assert.Contains("isn't hosting", alex.Party.View.Problem);
        await Eventually(() => !alex.Friends.All.Single().IsHosting, "Sam's status to stop showing a party");
        Assert.NotNull(alex.Player.Current); // still playing
    }

    [Fact]
    public async Task A_guest_leaving_is_dropped_from_the_party()
    {
        var (sam, alex) = await PartyOfTwo();
        await Eventually(() => sam.Party.View.Guests.Count == 1, "Alex in the party");

        await alex.Party.LeaveAsync();
        await Eventually(() => sam.Party.View.Guests.Count == 0, "Sam to see Alex leave");
        Assert.Equal(PartyRole.None, alex.Party.View.Role);
    }

    [Fact]
    public async Task The_party_ends_for_a_guest_when_the_host_goes_offline()
    {
        var (sam, alex) = await PartyOfTwo();
        sam.Peer.GoOffline();
        await Eventually(() => alex.Party.View.Role == PartyRole.None, "the party to end");
        Assert.Contains("went offline", alex.Party.View.Problem);
    }

    [Fact]
    public async Task Joining_a_friend_who_isnt_hosting_says_so()
    {
        var sam = await NewPerson("Sam");
        var alex = await NewPerson("Alex");
        await BecomeFriends(sam, alex);

        var ex = await Assert.ThrowsAsync<PartyException>(() => alex.Party.JoinAsync(UserIdOf(sam, alex)));
        Assert.Contains("isn't hosting", ex.Message);
    }

    [Fact]
    public async Task Someone_who_isnt_a_friend_cant_join()
    {
        var sam = await NewPerson("Sam");
        await sam.Party.StartHostingAsync();
        var stranger = _net.Join();
        _running.Add(stranger);

        stranger.SendUnchecked(sam.Peer, new PartyMessageJson("party.join").Json);
        await Task.Delay(500);
        Assert.Empty(sam.Party.View.Guests);
    }

    [Fact]
    public void The_clock_takes_the_fastest_round_trip()
    {
        var clock = new PartyClock();
        // The host's clock is 10 s ahead. A slow answer (900 ms out, 100 ms back) misleads by 400 ms.
        clock.Add(sentMs: 0, hostMs: 10_900, receivedMs: 1_000);
        Assert.Equal(10_400, clock.OffsetMs);
        clock.Add(sentMs: 2_000, hostMs: 12_020, receivedMs: 2_040); // a quick one: off by at most 20 ms
        Assert.Equal(10_000, clock.OffsetMs);
        Assert.Equal(15_000, clock.HostNow(5_000));
    }

    private sealed record PartyMessageJson(string Type)
    {
        public System.Text.Json.JsonElement Json => System.Text.Json.JsonSerializer.SerializeToElement(new { type = Type });
    }

    /// <summary>A clock that's off by a fixed amount.</summary>
    private sealed class OffsetClock(TimeSpan offset) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => System.GetUtcNow() + offset;
    }

    /// <summary>The host's player, running on the real clock.</summary>
    private sealed class FakeHost : IPartyHostSource
    {
        private readonly Lock _lock = new();
        private PartySong? _song;
        private TimeSpan _base;
        private DateTime _since;
        private bool _paused;

        public event Action? Changed;

        public TimeSpan Position
        {
            get
            {
                lock (_lock) return _paused ? _base : _base + (DateTime.UtcNow - _since);
            }
        }

        public PartyNow? Now
        {
            get
            {
                lock (_lock) return _song is { } song ? new PartyNow(song, song.Live ? null : Position, _paused) : null;
            }
        }

        public void Play(PartySong song, TimeSpan at = default)
        {
            lock (_lock)
            {
                _song = song;
                _base = at;
                _since = DateTime.UtcNow;
                _paused = false;
            }
            Changed?.Invoke();
        }

        public void SetPaused(bool paused)
        {
            lock (_lock)
            {
                _base = Position;
                _since = DateTime.UtcNow;
                _paused = paused;
            }
            Changed?.Invoke();
        }

        public void Seek(TimeSpan to)
        {
            lock (_lock)
            {
                _base = to;
                _since = DateTime.UtcNow;
            }
            Changed?.Invoke();
        }
    }

    /// <summary>A guest's player: takes a moment to find a song, then plays it on the real clock. Like
    /// the queue, a song put on while it's paused doesn't start until it's played.</summary>
    private sealed class FakePlayer(TimeProvider clock) : IPartyPlayer
    {
        private readonly Lock _lock = new();
        private TimeSpan _base;
        private DateTime _since;
        private bool _paused;
        private bool _started;
        private Func<TimeSpan>? _startAt;

        public sealed record Track(PartySong Song);

        public Track? Playing { get; private set; }
        public Func<PartySong, bool> Has { get; set; } = _ => true;
        public int Plays { get; private set; }
        public int Seeks { get; private set; }

        public async Task<bool> PlayAsync(PartySong song, Func<TimeSpan> startAt, CancellationToken ct)
        {
            await Task.Delay(150, ct); // finding it
            if (!Has(song)) return false;
            _ = clock; // the party works out startAt on its own clock
            lock (_lock)
            {
                Playing = new Track(song);
                Plays++;
                _startAt = startAt;
                _started = false;
                if (!_paused) Begin();
            }
            return true;
        }

        // Under the lock.
        private void Begin()
        {
            _base = _startAt!();
            _since = DateTime.UtcNow;
            _started = true;
        }

        private TimeSpan Position => !_started ? TimeSpan.Zero : _paused ? _base : _base + (DateTime.UtcNow - _since);

        public PartyPlayerState? Current
        {
            get
            {
                lock (_lock)
                {
                    return Playing is { } playing
                        ? new PartyPlayerState(playing.Song.Key, Position, _started, _paused, !playing.Song.Live)
                        : null;
                }
            }
        }

        public Task SeekAsync(TimeSpan position)
        {
            lock (_lock)
            {
                _base = position;
                _since = DateTime.UtcNow;
                Seeks++;
            }
            return Task.CompletedTask;
        }

        public Task SetPausedAsync(bool paused)
        {
            lock (_lock)
            {
                _base = Position;
                _since = DateTime.UtcNow;
                _paused = paused;
                if (!paused && !_started && Playing != null) Begin();
            }
            return Task.CompletedTask;
        }

        public void Drift(TimeSpan by)
        {
            lock (_lock) _base += by;
        }
    }
}
