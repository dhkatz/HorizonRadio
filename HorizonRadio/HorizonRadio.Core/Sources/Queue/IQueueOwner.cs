using System.Collections.Generic;
using HorizonRadio.Sdk.Sources;

namespace HorizonRadio.Core.Sources.Queue;

/// <summary>
/// The queue while it's someone else's: a listening party's guest shares its host's queue
/// (<see cref="QueueModel.Share"/>). What the user does to the queue is asked of its owner, who
/// does it and sends the queue back (<see cref="QueueModel.Mirror"/>). Ids are the owner's.
/// Called from any thread; returns at once.
/// </summary>
public interface IQueueOwner
{
    /// <summary>Add songs to the end of the queue; <paramref name="playNow"/> plays the first now.</summary>
    void Add(IReadOnlyList<PlayableItem> items, bool playNow);

    void Remove(string id);

    /// <summary>Move a song to where <paramref name="targetId"/> is (a drag and drop).</summary>
    void MoveTo(string id, string targetId);

    /// <summary>Move a song up (<paramref name="delta"/> &lt; 0) or down.</summary>
    void Move(string id, int delta);

    /// <summary>Play a queued song now, skipping those before it.</summary>
    void PlayNow(string id);

    void Clear();

    /// <summary>Skip the song playing.</summary>
    void Skip();

    /// <summary>Go back a song.</summary>
    void Back();
}
