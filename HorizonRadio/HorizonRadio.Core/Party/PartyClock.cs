namespace HorizonRadio.Core.Party;

/// <summary>
/// A guest's estimate of the host's clock, so a position the host read at its time can be carried
/// forward to now. PCs' clocks differ (by seconds, when one hasn't synced in a while), so the guest
/// asks: it notes its time, the host answers with its own, and the guest notes when the answer came.
/// The host's time was read somewhere in that round trip, so taking it as the middle is off by at
/// most half the trip; the fastest of the recent round trips is kept, as the one least delayed on
/// the way.
/// </summary>
public sealed class PartyClock
{
    private const int Kept = 8;

    private readonly Lock _lock = new();
    private readonly List<(long RoundTrip, long Offset)> _samples = [];

    /// <summary>How many answers it has heard.</summary>
    public int Samples
    {
        get
        {
            lock (_lock) return _samples.Count;
        }
    }

    /// <summary>The host's clock minus ours, in ms (0 until it has heard an answer).</summary>
    public long OffsetMs
    {
        get
        {
            lock (_lock) return _samples.Count == 0 ? 0 : _samples.MinBy(s => s.RoundTrip).Offset;
        }
    }

    /// <summary>An answer: we asked at <paramref name="sentMs"/>, the host's clock said
    /// <paramref name="hostMs"/>, and it came back at <paramref name="receivedMs"/> (ours).</summary>
    public void Add(long sentMs, long hostMs, long receivedMs)
    {
        if (receivedMs < sentMs) return;
        var offset = hostMs - (sentMs + receivedMs) / 2;
        lock (_lock)
        {
            _samples.Add((receivedMs - sentMs, offset));
            if (_samples.Count > Kept) _samples.RemoveAt(0);
        }
    }

    /// <summary>The host's clock now, given ours.</summary>
    public long HostNow(long localMs) => localMs + OffsetMs;

    public void Reset()
    {
        lock (_lock) _samples.Clear();
    }
}
