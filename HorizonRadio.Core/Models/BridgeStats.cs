namespace HorizonRadio.Core.Models;

/// <summary>
/// Per-tick snapshot of the FMOD bridge + normalizer state inside the
/// DLL. Published over IPC roughly every 500 ms while a session is
/// active. Frame counters are cumulative; rate-per-second is derived
/// downstream from the diff between adjacent snapshots.
/// </summary>
public sealed record BridgeStats(
    bool Installed,
    ulong FramesIn,
    ulong FramesOut,
    ulong Underruns,
    float NormalizerGain,
    float LimiterGain);
