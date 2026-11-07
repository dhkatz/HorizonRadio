using HorizonRadio.Core.Models;

namespace HorizonRadio.Core.Sources;

/// <summary>
/// One concrete thing to play: a single local file or a single resolved
/// YouTube video. It is the leaf unit a mix sequences — distinct from an
/// <see cref="IAudioSource"/>, which owns its own run loop. The driver (the
/// mix engine) owns ordering and transport; an item just resolves itself and
/// pumps PCM to a sink until it ends.
///
/// Splitting "what to play" into discrete, awaitable items (rather than
/// composing whole sources) is what gives the mix a single flat cursor, clean
/// next/seek, and a place to resolve-ahead: <see cref="PrepareAsync"/> does the
/// expensive work (a yt-dlp resolve, opening a reader) and can be called on the
/// next item while the current one is still playing.
/// </summary>
public abstract class PlayableItem
{
    /// <summary>Metadata for the HUD. Preliminary at enumerate time (a filename
    /// or flat-playlist title); remote items refine it (canonical title, art)
    /// during <see cref="PrepareAsync"/> / <see cref="PlayAsync"/>.</summary>
    public Track Metadata { get; protected set; } = Track.Empty;

    /// <summary>Track length once known, else null (progress bar hides). Remote
    /// items learn it on prepare.</summary>
    public TimeSpan? Duration { get; protected set; }

    /// <summary>Position within this item while it plays; <see cref="TimeSpan.Zero"/>
    /// before playback starts.</summary>
    public virtual TimeSpan Position => TimeSpan.Zero;

    /// <summary>Where in the current song this item is, or null when that isn't known (see
    /// <see cref="IPlaybackProgress.SongPosition"/>). A fixed track is one song: its
    /// <see cref="Position"/>, once its length is known.</summary>
    public virtual TimeSpan? SongPosition => Duration is null ? null : Position;

    /// <summary>Whether <see cref="SeekAsync"/> does anything. Defaults false so
    /// stream items (which can report position but not seek) need not override it.</summary>
    public virtual bool CanSeek => false;

    /// <summary>Seek to <paramref name="position"/> within this item. The pump
    /// applies it on its next chunk; default is a no-op.</summary>
    public virtual void Seek(TimeSpan position) { }

    /// <summary>
    /// Expensive, idempotent preparation — resolve a stream URL, open a reader,
    /// fetch art. Safe to call ahead of <see cref="PlayAsync"/> to warm the next
    /// item; <see cref="PlayAsync"/> calls it if it wasn't called already.
    /// Default: nothing to prepare.
    /// </summary>
    public virtual Task PrepareAsync(CancellationToken ct) => Task.CompletedTask;

    /// <summary>
    /// Best-effort metadata for list/queue display ahead of play — canonical
    /// title/artist/album/art where available — WITHOUT the playback side effects of
    /// <see cref="PrepareAsync"/> (no signed stream URL warmed, so nothing expires).
    /// A local file reads its tags; a YouTube item does a metadata-only yt-dlp resolve.
    /// Returns null when nothing better than the current <see cref="Metadata"/> is
    /// available. Callers should bound how many items they call this on.
    /// </summary>
    public virtual Task<Track?> TryGetMetadataAsync(CancellationToken ct) =>
        Task.FromResult<Track?>(null);

    /// <summary>
    /// Pump this item's PCM into <paramref name="ctx"/>'s sink until it ends
    /// naturally (returns) or the token fires (throws
    /// <see cref="OperationCanceledException"/> — the engine's "skip"/"stop").
    /// Blocks on the pause gate while paused. Invokes <see cref="PumpContext.OnStarted"/>
    /// once metadata/duration are final so the driver can publish the track.
    /// </summary>
    public abstract Task PlayAsync(PumpContext ctx, CancellationToken ct);

    private readonly Lock _prepareLock = new();
    private Task? _prepare;

    /// <summary>
    /// Run <paramref name="prepare"/> once for this item, however many callers ask at the same
    /// time. Preparing happens from several places at once: the engine warms the next item while
    /// the current one plays, the queue reads ahead for its list, and the item prepares itself
    /// when it starts playing. A second caller waits for the first one's work rather than seeing
    /// a half-prepared item (and, say, announcing the file name before the tags are read). A
    /// cancelled attempt doesn't count: the next caller starts it again. A failed one does; an
    /// implementation that wants a retry catches the failure and arranges it itself.
    /// </summary>
    protected async Task PrepareOnceAsync(Func<CancellationToken, Task> prepare, CancellationToken ct)
    {
        while (true)
        {
            Task task;
            lock (_prepareLock)
            {
                // Off the caller's thread (it may be the UI thread), under the first caller's token.
                task = _prepare ??= Task.Run(() => prepare(ct), CancellationToken.None);
            }
            try
            {
                await task.WaitAsync(ct).ConfigureAwait(false);
                return;
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                // Whoever started it was cancelled (a skip, a list scrolled away); we still want it.
                lock (_prepareLock)
                {
                    if (ReferenceEquals(_prepare, task)) _prepare = null;
                }
            }
        }
    }
}

/// <summary>
/// The transport/output context a driver hands to each <see cref="PlayableItem"/>.
/// The driver owns the sink and the pause gate (so pause spans item boundaries)
/// and learns when an item actually starts via <see cref="OnStarted"/>.
/// </summary>
public sealed class PumpContext
{
    /// <summary>Where the item's PCM goes (the game pipe / preview tee).</summary>
    public required IPcmSink Sink { get; init; }

    /// <summary>Whether playback is currently paused. The item checks this each
    /// chunk and blocks on <see cref="ResumeGate"/> rather than dropping audio.</summary>
    public Func<bool> IsPaused { get; init; } = static () => false;

    /// <summary>Signaled when playback resumes; the item waits on it while paused.</summary>
    public ManualResetEventSlim ResumeGate { get; init; } = new(initialState: true);

    /// <summary>Invoked once the item's final metadata/duration are known and it
    /// is actually entering playback — the driver publishes the HUD track here.</summary>
    public Action<PlayableItem>? OnStarted { get; init; }

    /// <summary>Invoked when an already-playing item's <see cref="PlayableItem.Metadata"/>
    /// changes mid-play — a live radio station whose song changed underneath a single
    /// item. The driver republishes the HUD track (and re-runs enrichment) here. Most
    /// items never call this; fixed-track items fire <see cref="OnStarted"/> once instead.</summary>
    public Action<PlayableItem>? OnMetadataUpdated { get; init; }

    /// <summary>Where in the item to begin (e.g. resuming a parked station channel);
    /// <see cref="TimeSpan.Zero"/> or <see langword="null"/> means from the top. Items that can
    /// start mid-way call it once per play, as soon as they're able to (seekable items, and
    /// stream items that pass it to their decoder), and start there; others ignore it.</summary>
    public Func<PlayableItem, TimeSpan>? StartPosition { get; init; }
}
