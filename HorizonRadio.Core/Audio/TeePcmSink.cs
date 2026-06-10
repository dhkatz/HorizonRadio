using System;
using HorizonRadio.Core.Sources;

namespace HorizonRadio.Core.Audio;

/// <summary>
/// An <see cref="IPcmSink"/> that routes each chunk to a primary sink (the
/// game pipe) and/or an optional, runtime-swappable preview sink (a local
/// speaker). The output picker selects one destination at a time — the game
/// bridge by default, or a speaker for testing — so the primary can be gated
/// off while previewing locally. Letting <see cref="SourceRunner"/> hold one
/// of these instead of the pipe sink directly means the destination can be
/// switched mid-playback without restarting the source.
/// </summary>
public sealed class TeePcmSink(IPcmSink primary) : IPcmSink
{
    // Swapped atomically; Send reads each once so an attach/detach or gate
    // toggle racing a chunk either includes that chunk or doesn't, never tears.
    private volatile IPcmSink? _preview;
    private volatile bool _primaryEnabled = true;

    /// <summary>Route subsequent chunks to <paramref name="preview"/>. Replaces
    /// any current preview.</summary>
    public void AttachPreview(IPcmSink preview) => _preview = preview;

    /// <summary>Stop routing to the preview sink.</summary>
    public void DetachPreview() => _preview = null;

    /// <summary>Gate the primary (game) sink. Disabled while previewing to a
    /// local device so the picker behaves as a single-destination switch.</summary>
    public void SetPrimaryEnabled(bool enabled) => _primaryEnabled = enabled;

    public bool Send(ReadOnlySpan<short> samples)
    {
        var preview = _preview;
        bool previewSent = false;
        if (preview != null)
        {
            try { previewSent = preview.Send(samples); } catch { /* best-effort */ }
        }

        bool primarySent = _primaryEnabled && primary.Send(samples);

        // True if any destination accepted the chunk, so the source doesn't
        // treat a deliberately-gated primary as a stalled sink.
        return primarySent || previewSent;
    }
}
