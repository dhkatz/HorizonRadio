using System.Collections.Generic;

namespace HorizonRadio.Core.Sources.Queue;

/// <summary>
/// An immutable, point-in-time view of the queue for the sidebar to render: the
/// item playing now, the explicit "next in queue" items (exact), and a rolling
/// peek of upcoming context tracks ("next from: &lt;ContextName&gt;"). Produced by
/// <see cref="QueueModel.Snapshot"/> under its lock so the UI never reads a
/// half-mutated queue.
/// </summary>
public sealed record QueueSnapshot(
    QueueItem? Current,
    bool CurrentFromContext,
    IReadOnlyList<QueueItem> Explicit,
    string? ContextName,
    IReadOnlyList<QueuePreview> ContextPeek);
