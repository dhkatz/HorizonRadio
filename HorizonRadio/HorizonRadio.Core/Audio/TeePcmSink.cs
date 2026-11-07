using System;
using HorizonRadio.Core.Sources;

namespace HorizonRadio.Core.Audio;

/// <summary>
/// An <see cref="IPcmSink"/> that routes each chunk to a primary sink (the
/// game's audio input, absent when the active game integration plays nothing
/// in-game) and/or an optional, runtime-swappable preview sink (a local
/// speaker). The output picker selects one destination at a time — the game
/// bridge by default, or a speaker for testing — so the primary can be gated
/// off while previewing locally. Letting <see cref="SourceRunner"/> hold one
/// of these instead of the pipe sink directly means the destination can be
/// switched mid-playback without restarting the source.
/// </summary>
public sealed class TeePcmSink(IPcmSink? primary) : IPcmSink
{
    // One immutable value, swapped with a single volatile write; Send reads it
    // once, so a chunk racing a re-route goes wholly by the old route or the new
    // one, never to both outputs or neither.
    private volatile TeeRoute _route = TeeRoute.PrimaryOnly;

    /// <summary>Where subsequent chunks go.</summary>
    public TeeRoute Route
    {
        get => _route;
        set => _route = value ?? throw new ArgumentNullException(nameof(value));
    }

    public bool Send(ReadOnlySpan<short> samples)
    {
        var route = _route;
        bool previewSent = false;
        if (route.Preview != null)
        {
            try { previewSent = route.Preview.Send(samples); } catch { /* best-effort */ }
        }

        bool primarySent = route.ToPrimary && primary?.Send(samples) == true;

        // True if any destination accepted the chunk, so the source doesn't
        // treat a deliberately-gated primary as a stalled sink.
        return primarySent || previewSent;
    }
}

/// <summary>A <see cref="TeePcmSink"/>'s destinations: the primary (game) sink, and/or
/// a preview (local speaker) sink.</summary>
public sealed record TeeRoute(bool ToPrimary, IPcmSink? Preview)
{
    /// <summary>The game only: the default, and local monitoring off.</summary>
    public static readonly TeeRoute PrimaryOnly = new(true, null);

    /// <summary>Nowhere: the chosen speaker is unreachable, and the game stays
    /// off so audio doesn't leak there while the picker says speakers.</summary>
    public static readonly TeeRoute Nowhere = new(false, null);

    /// <summary>The speaker only: the picker is a single-destination switch.</summary>
    public static TeeRoute PreviewOnly(IPcmSink preview) => new(false, preview);
}
