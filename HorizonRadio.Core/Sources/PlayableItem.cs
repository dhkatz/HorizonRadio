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

    /// <summary>
    /// Expensive, idempotent preparation — resolve a stream URL, open a reader,
    /// fetch art. Safe to call ahead of <see cref="PlayAsync"/> to warm the next
    /// item; <see cref="PlayAsync"/> calls it if it wasn't called already.
    /// Default: nothing to prepare.
    /// </summary>
    public virtual Task PrepareAsync(CancellationToken ct) => Task.CompletedTask;

    /// <summary>
    /// Pump this item's PCM into <paramref name="ctx"/>'s sink until it ends
    /// naturally (returns) or the token fires (throws
    /// <see cref="OperationCanceledException"/> — the engine's "skip"/"stop").
    /// Blocks on the pause gate while paused. Invokes <see cref="PumpContext.OnStarted"/>
    /// once metadata/duration are final so the driver can publish the track.
    /// </summary>
    public abstract Task PlayAsync(PumpContext ctx, CancellationToken ct);
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
}
