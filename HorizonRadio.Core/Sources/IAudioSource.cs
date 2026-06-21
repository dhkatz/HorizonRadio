using System;
using System.Threading;
using System.Threading.Tasks;
using HorizonRadio.Core.Models;

namespace HorizonRadio.Core.Sources;

/// <summary>
/// Audio source contract on the C# side. Each implementation owns a
/// background loop that produces s16-stereo PCM at 44.1 kHz and pushes
/// it through the provided <see cref="IPcmSink"/>. Sources also raise
/// metadata events as tracks change.
///
/// Lifetime: <see cref="StartAsync"/> spins up the background loop;
/// <see cref="StopAsync"/> tears it down. Restarting after Stop must be
/// supported (the source switcher calls Start/Stop as the user picks
/// different sources).
/// </summary>
public interface IAudioSource : IAsyncDisposable
{
    /// <summary>Stable, lowercase id (e.g. "local", "spotify").</summary>
    string Id { get; }

    /// <summary>User-facing label (e.g. "Local Files", "Spotify Connect").</summary>
    string DisplayName { get; }

    /// <summary>Raised when the playing track changes. Fires on whatever
    /// thread the source uses; consumers must marshal to UI as needed.</summary>
    event Action<Track>? TrackChanged;

    /// <summary>Begin producing PCM into <paramref name="sink"/>.</summary>
    Task StartAsync(IPcmSink sink, CancellationToken ct);

    /// <summary>Stop producing. Idempotent.</summary>
    Task StopAsync();
}

/// <summary>
/// Destination for PCM produced by a source. The default implementation
/// is a thin wrapper over <see cref="Ipc.PcmPipeClient"/>, but the
/// abstraction lets us unit-test sources against an in-memory sink and
/// later add an "audio preview" path for the UI itself.
/// </summary>
public interface IPcmSink
{
    /// <summary>Push s16 interleaved stereo samples. Length must be even
    /// (left/right pairs). Returns false if the sink isn't ready (e.g.
    /// pipe not connected); caller should keep pacing but drop the chunk.</summary>
    bool Send(ReadOnlySpan<short> samples);
}
