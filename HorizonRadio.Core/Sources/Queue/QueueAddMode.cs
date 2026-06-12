namespace HorizonRadio.Core.Sources.Queue;

/// <summary>
/// What starting a mix does to an already-populated queue. The queue's tail is a
/// single infinite "context" (one mix), so the two choices are genuinely
/// different: <see cref="Replace"/> swaps the radio station the queue draws from,
/// while <see cref="Add"/> snapshots one lap of the mix as finite explicit items
/// ahead of whatever context is already running.
/// </summary>
public enum QueueAddMode
{
    /// <summary>Make this mix the queue's context (the infinite tail), replacing
    /// any current context. Explicit one-offs are kept.</summary>
    Replace,

    /// <summary>Append one lap of this mix's tracks as explicit queue items,
    /// leaving the current context as the ongoing tail.</summary>
    Add,
}
