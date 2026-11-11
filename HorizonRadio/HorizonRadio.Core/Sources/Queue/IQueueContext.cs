using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using HorizonRadio.Sdk.Sources;

namespace HorizonRadio.Core.Sources.Queue;

/// <summary>
/// The infinite tail the queue draws from once its explicit items run out: a mix
/// (<see cref="Mixes.MixContextProvider"/>). The engine pulls one track at a time; the sidebar
/// peeks at what's coming.
/// </summary>
public interface IQueueContext
{
    string DisplayName { get; }

    /// <summary>Whether it can yield anything at all (a mix with no entries yields nothing).</summary>
    bool HasEntries { get; }

    /// <summary>The next track, resolving lazily. Null on cancellation, or when it has nothing.</summary>
    Task<PlayableItem?> NextAsync(CancellationToken ct);

    /// <summary>The track <see cref="NextAsync"/> will hand out next, without consuming it, so it
    /// can be loaded ahead (the same instance <see cref="NextAsync"/> then returns). Null when it
    /// can't be known yet (the next lap's order isn't drawn until it starts).</summary>
    Task<PlayableItem?> PeekNextAsync(CancellationToken ct) => Task.FromResult<PlayableItem?>(null);

    /// <summary>A best-effort window of what's coming, without consuming it.</summary>
    IReadOnlyList<QueuePreview> Peek(int count);

    /// <summary>Turn shuffle on or off for what hasn't played yet.</summary>
    void SetShuffle(bool enabled);
}
