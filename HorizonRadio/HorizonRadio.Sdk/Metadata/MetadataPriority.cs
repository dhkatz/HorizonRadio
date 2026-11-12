using System.Threading;

namespace HorizonRadio.Sdk.Metadata;

/// <summary>How urgently a resolve is needed. The song playing right now is
/// <see cref="Foreground"/>; filling in queue, mix, and History rows is <see cref="Background"/>.</summary>
public enum ResolvePriority
{
    Background,
    Foreground,
}

/// <summary>
/// A resolve's priority, shared by everything working on its behalf. It can only go up:
/// when the song that's playing joins a lookup a background list already started, the
/// lookup is promoted, and every <see cref="RateGate"/> it is queued in sees that the next
/// time it picks who goes first.
/// </summary>
public sealed class PriorityTicket(ResolvePriority priority)
{
    private volatile bool _foreground = priority == ResolvePriority.Foreground;

    public bool IsForeground => _foreground;

    public void Promote() => _foreground = true;
}

/// <summary>
/// The ambient <see cref="PriorityTicket"/> of the resolve the current async flow belongs
/// to. The host's resolver sets it around a lookup; providers never set it or pass it along, their
/// <see cref="RateGate"/> just reads it. Work outside any resolve has no ticket and counts
/// as foreground, since it was asked for directly.
/// </summary>
public static class MetadataPriority
{
    private static readonly AsyncLocal<PriorityTicket?> Ambient = new();

    public static PriorityTicket? Current
    {
        get => Ambient.Value;
        set => Ambient.Value = value;
    }
}
