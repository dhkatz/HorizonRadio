using System.Diagnostics;
using System.Text.Json;
using System.Threading.Channels;
using HorizonRadio.Core.Friends;
using HorizonRadio.Core.Sources;
using HorizonRadio.Core.Sources.Queue;

namespace HorizonRadio.Core.Party;

/// <summary>
/// A listening party: you host, and friends who join hear what you play, where you are in it. Or you
/// join a friend's. It runs over <see cref="Friends.Friends"/>: your friends see you hosting in your
/// status, and party messages only pass between PCs that have proven whose they are.
///
/// The host tells its guests its player's state (<see cref="IPartyHostSource"/>): the song, the
/// position read at a time on its clock, and pause. It does so when the song or pause changes, when
/// the position jumps (a seek), and every few seconds besides. A guest learns how the host's clock
/// differs from its own (<see cref="PartyClock"/>), so it can tell where the host is now. It plays
/// each song (<see cref="IPartyPlayer"/>) from that point, follows pause, and seeks when it has
/// drifted. A station is tuned into rather than followed. A guest's own pause stands until the host
/// pauses or resumes.
///
/// The queue is shared, as in a jam: the host's is the party's. The host sends it to its guests when
/// it changes, and a guest's queue shows it in place of the guest's own (set aside until it leaves).
/// What a guest does to the queue (add, remove, move, play now, clear, skip, back) is asked of the
/// host, which does it to its own queue.
///
/// Everything runs on one loop; <see cref="Changed"/> is raised off the UI thread.
/// </summary>
public sealed class ListeningParty : IAsyncDisposable
{
    private readonly Friends.Friends _friends;
    private readonly IPartyHostSource _host;
    private readonly IPartyPlayer _player;
    private readonly TimeProvider _time;
    private readonly TimeSpan _tick;
    private readonly TimeSpan _heartbeat;
    private readonly TimeSpan _tolerance;
    private readonly TimeSpan _joinTimeout;
    private readonly CancellationTokenSource _stop = new();
    private readonly Channel<Func<Task>> _work = Channel.CreateUnbounded<Func<Task>>(new UnboundedChannelOptions { SingleReader = true });
    private readonly Lock _lock = new();
    private readonly PartyClock _clock = new();
    private readonly HostAsks _asks;
    private Task? _loop;
    private Task? _ticker;

    // Only the loop touches these.
    private PartyRole _role;
    private readonly Dictionary<string, string> _guests = new(StringComparer.Ordinal); // peer → user id
    private PartyNow? _sentNow;
    private long _sentAt; // our clock when _sentNow was read
    private string? _sentQueue; // as JSON, to tell when it changed
    private long _seq;

    private string? _hostPeer;
    private string? _hostUser;
    private TaskCompletionSource? _welcome;
    private volatile PartyMessage? _latest; // also read by the player as a song starts (ExpectedPosition)
    private string? _playingKey;
    private CancellationTokenSource? _playCts;
    private bool _played;
    private bool? _appliedPause;
    private long _settleUntil;
    private long _nextPing;
    private long _nextReport;
    private string? _problem;
    private bool _finding;

    private PartyView _view = PartyView.None;

    /// <param name="tick">How often the host looks at its player and a guest at its own.</param>
    /// <param name="heartbeat">How often the host repeats its state when nothing changed.</param>
    /// <param name="tolerance">How far a guest may drift before it seeks.</param>
    public ListeningParty(Friends.Friends friends, IPartyHostSource host, IPartyPlayer player, TimeProvider? time = null,
        TimeSpan? tick = null, TimeSpan? heartbeat = null, TimeSpan? tolerance = null, TimeSpan? joinTimeout = null)
    {
        _friends = friends;
        _host = host;
        _player = player;
        _time = time ?? TimeProvider.System;
        _tick = tick ?? TimeSpan.FromSeconds(1);
        _heartbeat = heartbeat ?? TimeSpan.FromSeconds(5);
        _tolerance = tolerance ?? TimeSpan.FromMilliseconds(600);
        _joinTimeout = joinTimeout ?? TimeSpan.FromSeconds(15);
        _asks = new HostAsks(this);
    }

    /// <summary>The party changed: who's in it, what it's playing, or a problem.</summary>
    public event Action? Changed;

    public PartyView View
    {
        get
        {
            lock (_lock) return _view;
        }
    }

    public void Start()
    {
        _friends.PeerMessage += OnPeerMessage;
        _friends.PeerLeft += OnPeerLeft;
        _host.Changed += OnHostChanged;
        _loop = Task.Run(RunAsync, CancellationToken.None);
        _ticker = Task.Run(TickAsync, CancellationToken.None);
    }

    // ---- Hosting -------------------------------------------------------------------------------

    /// <summary>Host a party: your friends see it and can join.</summary>
    public Task StartHostingAsync() => OnLoopAsync(async () =>
    {
        if (_role == PartyRole.Hosting) return;
        await LeaveCoreAsync(tellHost: true).ConfigureAwait(false);
        _role = PartyRole.Hosting;
        _problem = null;
        _sentNow = null;
        _sentQueue = null;
        _friends.SetHosting(true);
        Log("hosting");
        Publish();
    });

    /// <summary>End the party you're hosting; your guests are told.</summary>
    public Task StopHostingAsync() => OnLoopAsync(StopHostingCoreAsync);

    private async Task StopHostingCoreAsync()
    {
        if (_role != PartyRole.Hosting) return;
        _role = PartyRole.None;
        _friends.SetHosting(false);
        var guests = _guests.Keys.ToList();
        _guests.Clear();
        foreach (var guest in guests) await SendAsync(guest, new PartyMessage { Type = PartyMessage.End }).ConfigureAwait(false);
        Log("stopped hosting");
        Publish();
    }

    private async Task HostTickAsync(bool force)
    {
        if (_role != PartyRole.Hosting || _guests.Count == 0) return;
        var now = _host.Now;
        var at = NowMs();
        if (force || ShouldSend(now, at)) await BroadcastAsync(now, at).ConfigureAwait(false);
        await BroadcastQueueAsync().ConfigureAwait(false);
    }

    private async Task BroadcastQueueAsync()
    {
        var queue = _host.Queue;
        var json = JsonSerializer.Serialize(queue, PartyMessage.Json);
        if (json == _sentQueue) return;
        _sentQueue = json;
        var message = new PartyMessage { Type = PartyMessage.QueueChanged, Queue = queue };
        foreach (var guest in _guests.Keys.ToList()) await SendAsync(guest, message).ConfigureAwait(false);
    }

    // Whether the guests need telling: a new song, pause or resume, a jump in position, or it's been a while.
    private bool ShouldSend(PartyNow? now, long at)
    {
        var sent = _sentNow;
        if (sent?.Song.Key != now?.Song.Key || sent?.Paused != now?.Paused) return true;
        if (at - _sentAt >= (long)_heartbeat.TotalMilliseconds) return true;
        if (now?.Position is not { } position) return sent?.Position != null;
        if (sent?.Position is not { } before) return true;
        var expected = before + (now.Paused ? TimeSpan.Zero : TimeSpan.FromMilliseconds(at - _sentAt));
        return (position - expected).Duration() > _tolerance;
    }

    private async Task BroadcastAsync(PartyNow? now, long at)
    {
        _sentNow = now;
        _sentAt = at;
        var state = StateMessage(now, at);
        foreach (var guest in _guests.Keys.ToList())
        {
            if (!await SendAsync(guest, state).ConfigureAwait(false)) _guests.Remove(guest);
        }
        Publish();
    }

    private PartyMessage StateMessage(PartyNow? now, long at) => new()
    {
        Type = PartyMessage.State,
        Seq = ++_seq,
        Song = now?.Song,
        PositionMs = now?.Position is { } position ? (long)position.TotalMilliseconds : null,
        At = at,
        Paused = now?.Paused ?? false,
    };

    private async Task OnJoinAsync(FriendPeerMessage from)
    {
        if (_role != PartyRole.Hosting)
        {
            await SendAsync(from.PeerId, new PartyMessage { Type = PartyMessage.Closed }).ConfigureAwait(false);
            return;
        }
        _guests[from.PeerId] = from.UserId;
        Log($"{_friends.NameOf(from.UserId)} joined");
        await SendAsync(from.PeerId, new PartyMessage { Type = PartyMessage.Welcome }).ConfigureAwait(false);
        var at = NowMs();
        var now = _host.Now;
        await SendAsync(from.PeerId, StateMessage(now, at)).ConfigureAwait(false);
        await SendAsync(from.PeerId, new PartyMessage { Type = PartyMessage.QueueChanged, Queue = _host.Queue }).ConfigureAwait(false);
        Publish();
    }

    private async Task OnAskAsync(FriendPeerMessage from, PartyQueueChange change)
    {
        var what = change.Songs is { } songs ? $" {songs.Count} song{(songs.Count == 1 ? "" : "s")}" : "";
        Log($"{_friends.NameOf(from.UserId)} asked to {change.Op}{what}");
        try
        {
            await _host.ChangeQueueAsync(change).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Log($"changing the queue failed: {ex.Message}");
        }
        await HostTickAsync(force: false).ConfigureAwait(false);
    }

    // ---- Joining -------------------------------------------------------------------------------

    /// <summary>Join the party a friend is hosting: your player plays what theirs does.</summary>
    /// <exception cref="PartyException">They aren't hosting, or didn't answer.</exception>
    public async Task JoinAsync(string userId, CancellationToken ct = default)
    {
        TaskCompletionSource? welcome = null;
        await OnLoopAsync(async () =>
        {
            var friend = _friends.All.FirstOrDefault(f => f.UserId == userId);
            if (friend?.HostingPeer is not { } peer)
                throw new PartyException($"{friend?.Name ?? "They"} isn't hosting a party right now.");
            await StopHostingCoreAsync().ConfigureAwait(false);
            await LeaveCoreAsync(tellHost: true).ConfigureAwait(false);
            _role = PartyRole.Joining;
            _hostPeer = peer;
            _hostUser = userId;
            _problem = null;
            _clock.Reset();
            welcome = _welcome = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Publish();
            if (!await SendAsync(peer, new PartyMessage { Type = PartyMessage.Join }).ConfigureAwait(false))
            {
                await LeaveCoreAsync(tellHost: false).ConfigureAwait(false);
                throw new PartyException($"Couldn't reach {friend.Name}.");
            }
            await PingAsync().ConfigureAwait(false);
        }).ConfigureAwait(false);

        try
        {
            await welcome!.Task.WaitAsync(_joinTimeout, _time, ct).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            await OnLoopAsync(async () =>
            {
                if (ReferenceEquals(_welcome, welcome)) await LeaveCoreAsync(tellHost: true).ConfigureAwait(false);
            }).ConfigureAwait(false);
            throw new PartyException($"{_friends.NameOf(userId) ?? "Your friend"} didn't answer. Try again.");
        }
    }

    /// <summary>Leave the party you're in. What's playing keeps playing.</summary>
    public Task LeaveAsync() => OnLoopAsync(() => LeaveCoreAsync(tellHost: true));

    private async Task LeaveCoreAsync(bool tellHost, string? why = null)
    {
        if (_role is not (PartyRole.Joining or PartyRole.Guest)) return;
        var host = _hostPeer;
        _role = PartyRole.None;
        _hostPeer = null;
        _latest = null;
        _playingKey = null;
        _played = false;
        _finding = false;
        _appliedPause = null;
        if (_playCts != null)
        {
            await _playCts.CancelAsync().ConfigureAwait(false);
            _playCts = null;
        }
        _welcome?.TrySetException(new PartyException(why ?? "You left the party."));
        _welcome = null;
        _problem = why;
        _player.UnshareQueue();
        if (tellHost && host != null) await SendAsync(host, new PartyMessage { Type = PartyMessage.Leave }).ConfigureAwait(false);
        Log(why ?? "left the party");
        Publish();
    }

    private async Task OnStateAsync(PartyMessage state)
    {
        if (_role is not (PartyRole.Joining or PartyRole.Guest)) return;
        if (_latest is { } latest && state.Seq <= latest.Seq) return; // an older one, overtaken
        _latest = state;
        await FollowAsync().ConfigureAwait(false);
        Publish();
    }

    // Bring the player in line with the host: put on its song, then follow its pause and position.
    private async Task FollowAsync()
    {
        if (_latest is not { } state) return;
        if (state.Song is not { } song)
        {
            _playingKey = null; // the host stopped: the next song starts afresh
            return;
        }
        if (song.Key != _playingKey)
        {
            StartPlaying(song);
            return;
        }
        if (_played) await CorrectAsync(state, song).ConfigureAwait(false);
    }

    private void StartPlaying(PartySong song)
    {
        _playCts?.Cancel();
        var cts = _playCts = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
        _playingKey = song.Key;
        _played = false;
        _appliedPause = null;
        _finding = true;
        _problem = null;
        Log($"playing \"{song.Title}\" ({song.Key})");
        _ = Task.Run(async () =>
        {
            bool ok;
            string? why = null;
            try
            {
                ok = await _player.PlayAsync(song, ExpectedPosition, cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (PartyException ex)
            {
                Log($"playing \"{song.Title}\" failed: {ex.Message}");
                ok = false;
                why = ex.Message;
            }
            catch (Exception ex)
            {
                Log($"playing \"{song.Title}\" failed: {ex.Message}");
                ok = false;
                why = $"Couldn't play \"{song.Title}\" here.";
            }
            _work.Writer.TryWrite(() =>
            {
                Played(song, ok, why);
                return Task.CompletedTask;
            });
        }, CancellationToken.None);
    }

    // why: the player's reason a song it has couldn't play; null when nothing here has it.
    private void Played(PartySong song, bool ok, string? why)
    {
        if (song.Key != _playingKey) return;
        _finding = false;
        _played = ok;
        _settleUntil = NowMs() + 2000; // let it start before judging where it is
        if (!ok)
        {
            _problem = (why ?? $"Couldn't find \"{song.Title}\"{(song.Artist.Length > 0 ? " by " + song.Artist : "")} here.") +
                       " Waiting for the next song.";
        }
        Publish();
    }

    private async Task CorrectAsync(PartyMessage state, PartySong song)
    {
        var at = NowMs();
        if (_player.Current is not { } current || current.Key != song.Key) return;

        // Pause first, whether or not the song has begun: a player paused before it starts never
        // starts it.
        if (_appliedPause != state.Paused)
        {
            _appliedPause = state.Paused;
            if (current.Paused != state.Paused)
            {
                await _player.SetPausedAsync(state.Paused).ConfigureAwait(false);
                _settleUntil = Math.Max(_settleUntil, at + 1000);
                return;
            }
        }
        if (at < _settleUntil || !current.Started) return;
        if (song.Live || state.Paused || current.Paused || !current.CanSeek) return;

        var expected = ExpectedPosition();
        if (at >= _nextReport)
        {
            // For the Console: how far behind (-) or ahead (+) of the host this PC is playing.
            _nextReport = at + 10_000;
            Log($"in step with the host to {(current.Position - expected).TotalMilliseconds:+0;-0} ms (clock {_clock.OffsetMs:+0;-0} ms)");
        }
        if (song.DurationMs is { } duration && expected.TotalMilliseconds > duration - 2000) return; // let it end
        if ((expected - current.Position).Duration() <= _tolerance) return;
        Log($"off by {(current.Position - expected).TotalMilliseconds:0} ms: seeking to {expected}");
        await _player.SeekAsync(expected).ConfigureAwait(false);
        _settleUntil = at + 3000;
    }

    /// <summary>Where the host is in its song now, by our estimate of its clock.</summary>
    private TimeSpan ExpectedPosition()
    {
        if (_latest is not { PositionMs: { } position } state) return TimeSpan.Zero;
        var elapsed = state.Paused ? 0 : _clock.HostNow(NowMs()) - state.At;
        return TimeSpan.FromMilliseconds(Math.Max(0, position + elapsed));
    }

    private Task PingAsync()
    {
        _nextPing = NowMs() + (_clock.Samples < 5 ? 500 : 30_000);
        return _hostPeer is { } host ? SendAsync(host, new PartyMessage { Type = PartyMessage.Ping, T0 = NowMs() }) : Task.CompletedTask;
    }

    private async Task GuestTickAsync()
    {
        if (_role is not (PartyRole.Joining or PartyRole.Guest)) return;
        if (NowMs() >= _nextPing) await PingAsync().ConfigureAwait(false);
        if (_role == PartyRole.Guest) await FollowAsync().ConfigureAwait(false);
    }

    // ---- Messages ------------------------------------------------------------------------------

    private void OnPeerMessage(FriendPeerMessage received)
    {
        if (PartyMessage.From(received.Message) is not { } message) return;
        _work.Writer.TryWrite(() => OnMessageAsync(received, message));
    }

    private async Task OnMessageAsync(FriendPeerMessage from, PartyMessage message)
    {
        var fromHost = from.PeerId == _hostPeer && _role is PartyRole.Joining or PartyRole.Guest;
        switch (message.Type)
        {
            case PartyMessage.Join:
                await OnJoinAsync(from).ConfigureAwait(false);
                break;
            case PartyMessage.Leave:
                if (_guests.Remove(from.PeerId))
                {
                    Log($"{_friends.NameOf(from.UserId)} left");
                    Publish();
                }
                break;
            case PartyMessage.Ping when _role == PartyRole.Hosting && _guests.ContainsKey(from.PeerId):
                await SendAsync(from.PeerId, new PartyMessage { Type = PartyMessage.Pong, T0 = message.T0, At = NowMs() }).ConfigureAwait(false);
                break;
            case PartyMessage.Pong when fromHost:
                _clock.Add(message.T0, message.At, NowMs());
                break;
            case PartyMessage.Welcome when fromHost:
                _role = PartyRole.Guest;
                _player.ShareQueue(_asks);
                _welcome?.TrySetResult();
                _welcome = null;
                Log($"joined {_friends.NameOf(from.UserId)}'s party");
                Publish();
                break;
            case PartyMessage.State when fromHost:
                await OnStateAsync(message).ConfigureAwait(false);
                break;
            case PartyMessage.QueueChanged when fromHost && _role == PartyRole.Guest && message.Queue is { } queue:
                _player.MirrorQueue(queue);
                break;
            case PartyMessage.Ask when _role == PartyRole.Hosting && _guests.ContainsKey(from.PeerId) && message.Change is { } change:
                await OnAskAsync(from, change).ConfigureAwait(false);
                break;
            case PartyMessage.Closed or PartyMessage.End when fromHost:
                await LeaveCoreAsync(tellHost: false, $"{_friends.NameOf(from.UserId) ?? "Your friend"} isn't hosting anymore.").ConfigureAwait(false);
                break;
        }
    }

    private void OnPeerLeft(string peerId) => _work.Writer.TryWrite(async () =>
    {
        if (_guests.Remove(peerId)) Publish();
        if (peerId == _hostPeer && _role is PartyRole.Joining or PartyRole.Guest)
            await LeaveCoreAsync(tellHost: false, $"{_friends.NameOf(_hostUser ?? "") ?? "Your friend"} went offline, so the party ended.").ConfigureAwait(false);
    });

    private void OnHostChanged() => _work.Writer.TryWrite(() => HostTickAsync(force: false));

    // A guest asks the host to change the party's queue.
    private void Ask(PartyQueueChange change) => _work.Writer.TryWrite(async () =>
    {
        if (_role != PartyRole.Guest || _hostPeer is not { } host) return;
        Log($"asking the host to {change.Op}");
        await SendAsync(host, new PartyMessage { Type = PartyMessage.Ask, Change = change }).ConfigureAwait(false);
    });

    /// <summary>What a guest's user does to the party's queue, asked of the host.</summary>
    private sealed class HostAsks(ListeningParty party) : IQueueOwner
    {
        // Small enough messages for a relayed connection.
        private const int SongsPerMessage = 50;

        public void Add(IReadOnlyList<PlayableItem> items, bool playNow)
        {
            var songs = items.Select(item => PartySongs.Of(item, "")).ToList();
            for (var i = 0; i < songs.Count; i += SongsPerMessage)
            {
                party.Ask(new PartyQueueChange
                {
                    Op = PartyQueueChange.Add,
                    Songs = songs.GetRange(i, Math.Min(SongsPerMessage, songs.Count - i)),
                    PlayNow = playNow && i == 0,
                });
            }
        }

        public void Remove(string id) => party.Ask(new PartyQueueChange { Op = PartyQueueChange.Remove, Id = id });

        public void MoveTo(string id, string targetId) => party.Ask(new PartyQueueChange { Op = PartyQueueChange.MoveTo, Id = id, To = targetId });

        public void Move(string id, int delta) => party.Ask(new PartyQueueChange { Op = PartyQueueChange.Move, Id = id, By = delta });

        public void PlayNow(string id) => party.Ask(new PartyQueueChange { Op = PartyQueueChange.Play, Id = id });

        public void Clear() => party.Ask(new PartyQueueChange { Op = PartyQueueChange.Clear });

        public void Skip() => party.Ask(new PartyQueueChange { Op = PartyQueueChange.Skip });

        public void Back() => party.Ask(new PartyQueueChange { Op = PartyQueueChange.Back });
    }

    private async Task<bool> SendAsync(string peerId, PartyMessage message)
    {
        try
        {
            return await _friends.SendToFriendAsync(peerId, message.ToJson(), _stop.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    // ---- The loop ------------------------------------------------------------------------------

    private Task OnLoopAsync(Func<Task> work)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_work.Writer.TryWrite(async () =>
            {
                try
                {
                    await work().ConfigureAwait(false);
                    done.TrySetResult();
                }
                catch (Exception ex)
                {
                    done.TrySetException(ex);
                }
            }))
        {
            done.TrySetException(new ObjectDisposedException(nameof(ListeningParty)));
        }
        return done.Task;
    }

    private async Task RunAsync()
    {
        await foreach (var work in _work.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            try
            {
                await work().ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                Log($"failed: {ex}");
            }
        }
    }

    private async Task TickAsync()
    {
        try
        {
            while (true)
            {
                await Task.Delay(_tick, _time, _stop.Token).ConfigureAwait(false);
                _work.Writer.TryWrite(async () =>
                {
                    await HostTickAsync(force: false).ConfigureAwait(false);
                    await GuestTickAsync().ConfigureAwait(false);
                    if (_role == PartyRole.Hosting) Publish(); // what's playing, for the host's own page
                });
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private long NowMs() => _time.GetUtcNow().ToUnixTimeMilliseconds();

    private void Publish()
    {
        var view = _role switch
        {
            PartyRole.Hosting => new PartyView(PartyRole.Hosting, null,
                [.. _guests.Values.Select(u => _friends.NameOf(u) ?? "Friend").Order(StringComparer.CurrentCultureIgnoreCase)],
                _host.Now?.Song, false, _problem),
            PartyRole.Joining or PartyRole.Guest => new PartyView(_role, _friends.NameOf(_hostUser ?? "") ?? "Friend", [],
                _latest?.Song, _finding, _problem),
            _ => PartyView.None with { Problem = _problem },
        };
        lock (_lock)
        {
            if (Same(_view, view)) return;
            _view = view;
        }
        try
        {
            Changed?.Invoke();
        }
        catch (Exception ex)
        {
            Log($"a Changed handler failed: {ex}");
        }
    }

    // A song is told apart by its key and title (its sources list is a new one each time it's read).
    private static bool Same(PartyView a, PartyView b) =>
        a.Role == b.Role && a.HostName == b.HostName && a.Guests.SequenceEqual(b.Guests) && a.Song?.Key == b.Song?.Key
        && a.Song?.Title == b.Song?.Title && a.Song?.Artist == b.Song?.Artist && a.Finding == b.Finding && a.Problem == b.Problem;

    private static void Log(string message) => Trace.WriteLine($"[hzn-party] {message}");

    private int _disposed;

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _friends.PeerMessage -= OnPeerMessage;
        _friends.PeerLeft -= OnPeerLeft;
        _host.Changed -= OnHostChanged;
        // Say goodbye first, so guests and the host aren't left waiting for a timeout.
        try
        {
            await OnLoopAsync(async () =>
            {
                await StopHostingCoreAsync().ConfigureAwait(false);
                await LeaveCoreAsync(tellHost: true).ConfigureAwait(false);
            }).WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is TimeoutException or ObjectDisposedException)
        {
        }
        await _stop.CancelAsync().ConfigureAwait(false);
        _work.Writer.TryComplete();
        if (_loop != null) await _loop.ConfigureAwait(false);
        if (_ticker != null) await _ticker.ConfigureAwait(false);
        _stop.Dispose();
    }
}
