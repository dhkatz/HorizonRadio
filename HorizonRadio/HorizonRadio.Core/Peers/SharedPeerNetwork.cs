using HorizonRadio.Sdk.Peers;

namespace HorizonRadio.Core.Peers;

/// <summary>
/// The one peer network the app's features share (your devices, your friends): started when the
/// first of them needs it, and again after it stops. The features each subscribe to the network
/// they're handed and ignore the messages that aren't theirs. Disposing it stops the network.
/// </summary>
public sealed class SharedPeerNetwork : IAsyncDisposable
{
    private readonly Func<CancellationToken, Task<IPeerNetwork>> _start;
    private readonly SemaphoreSlim _starting = new(1, 1);
    private IPeerNetwork? _network;

    public SharedPeerNetwork(Func<CancellationToken, Task<IPeerNetwork>> start) => _start = start;

    /// <summary>The running network, started if it isn't.</summary>
    public async Task<IPeerNetwork> GetAsync(CancellationToken ct = default)
    {
        if (Volatile.Read(ref _network) is { } running) return running;
        await _starting.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_network is { } started) return started;
            var network = await _start(ct).ConfigureAwait(false);
            network.Exited += () => Interlocked.CompareExchange(ref _network, null, network);
            Volatile.Write(ref _network, network);
            return network;
        }
        finally
        {
            _starting.Release();
        }
    }

    /// <summary>Tell the network to stop now without waiting (the app is exiting), when it can.</summary>
    public void Stop() => (Volatile.Read(ref _network) as IStoppable)?.StopNow();

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _network, null) is IAsyncDisposable network) await network.DisposeAsync().ConfigureAwait(false);
        _starting.Dispose();
    }
}
