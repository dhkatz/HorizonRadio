using System;
using HorizonRadio.Core.Models;

namespace HorizonRadio.Core.Sources.Queue;

/// <summary>
/// One concrete, resolved thing sitting in the global queue: a single
/// <see cref="PlayableItem"/> plus a stable id the UI uses to target it
/// (remove / reorder / play-now) and a flag recording where it came from.
///
/// Explicit one-offs (the + button, quick-play, "add a mix to the queue") are
/// wrapped at enqueue time with <see cref="FromContext"/> false. Items pulled
/// lazily from the active mix context are wrapped with it true, so the engine
/// and sidebar can tell "your queue" from "the radio that's feeding it".
/// </summary>
public sealed class QueueItem(PlayableItem item, bool fromContext = false, string? id = null)
{
    /// <summary>Stable identity for UI targeting; unique per queued instance
    /// (the same file queued twice gets two ids).</summary>
    public string Id { get; } = id ?? Guid.NewGuid().ToString("n");

    public PlayableItem Item { get; } = item;

    /// <summary>True when this item was produced by the mix context generator
    /// rather than added explicitly by the user.</summary>
    public bool FromContext { get; } = fromContext;

    public Track Metadata => Item.Metadata;

    private Func<TimeSpan>? _joinAt;

    /// <summary>Where this item's first play starts, worked out as it starts: joining a play under
    /// way elsewhere (a listening party's host). A later play of it (a restart, a step back onto
    /// it) is from the top.</summary>
    public Func<TimeSpan>? JoinAt
    {
        init => _joinAt = value;
    }

    /// <summary>The first play's start, taken once (see <see cref="JoinAt"/>).</summary>
    internal TimeSpan TakeJoinAt() =>
        Interlocked.Exchange(ref _joinAt, null) is { } at && at() is { Ticks: > 0 } start ? start : TimeSpan.Zero;
}
