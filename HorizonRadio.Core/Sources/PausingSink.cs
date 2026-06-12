namespace HorizonRadio.Core.Sources;

/// <summary>
/// Wraps an <see cref="IPcmSink"/> with a pause gate: while paused, blocks the
/// producer on the gate instead of forwarding (or dropping) samples, so the
/// upstream — e.g. an ffmpeg subprocess whose stdout pipe fills — back-pressures
/// and stalls in place rather than racing ahead. Factored out so any
/// subprocess-backed <see cref="PlayableItem"/> can reuse the behavior the
/// per-source pumps already implement inline.
/// </summary>
internal sealed class PausingSink(
    IPcmSink inner,
    Func<bool> isPaused,
    ManualResetEventSlim resumeGate,
    CancellationToken ct) : IPcmSink
{
    public bool Send(ReadOnlySpan<short> samples)
    {
        if (isPaused())
        {
            try { resumeGate.Wait(ct); }
            catch (OperationCanceledException) { return false; }
        }

        return inner.Send(samples);
    }
}
