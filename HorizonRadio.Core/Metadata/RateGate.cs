using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace HorizonRadio.Core.Metadata;

/// <summary>
/// Serializes calls and enforces a minimum spacing between them — a polite client-side rate
/// limit. Shared by the metadata providers, which each hit an external API that tolerates
/// bursts poorly (MusicBrainz asks for ~1 req/s; iTunes/VocaDB just shouldn't be hammered).
/// </summary>
internal sealed class RateGate(TimeSpan minInterval) : IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Stopwatch _sinceLast = Stopwatch.StartNew();

    /// <summary>Block until at least <c>minInterval</c> has elapsed since the previous call.</summary>
    public async Task WaitAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var elapsed = _sinceLast.Elapsed;
            if (elapsed < minInterval) await Task.Delay(minInterval - elapsed, ct).ConfigureAwait(false);
            _sinceLast.Restart();
        }
        finally { _gate.Release(); }
    }

    public void Dispose() => _gate.Dispose();
}
