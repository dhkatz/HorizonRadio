using System;
using System.Diagnostics;
using System.IO.Pipes;
using System.Threading;
using System.Threading.Tasks;

namespace HorizonRadio.Core.Ipc;

/// <summary>
/// Writes raw s16 interleaved stereo PCM at 44.1 kHz into the DLL's
/// ingress pipe (`\\.\pipe\HorizonRadio.pcm`). One-way (client→server);
/// the DLL doesn't send anything back on this channel — the control
/// pipe (`HorizonRadio`) carries events + future commands.
///
/// Separate from <see cref="IpcClient"/> on purpose. Mixing raw binary
/// into the JSON line protocol would force every consumer to peek for
/// magic bytes; two pipes is cleaner and the OS handles them
/// independently.
/// </summary>
public sealed class PcmPipeClient(string pipeName = PcmPipeClient.DefaultPipeName) : IAsyncDisposable
{
    public const string DefaultPipeName = "HorizonRadio.pcm";

    private readonly CancellationTokenSource _cts = new();
    private NamedPipeClientStream? _pipe;
    private Task? _connectLoop;

    private static void Log(string msg) => Debug.WriteLine($"[hzn-core-pcm] {msg}");

    public bool IsConnected => _pipe?.IsConnected == true;

    /// <summary>
    /// Spin up the connect-and-reconnect loop in the background. The
    /// loop sits idle until the DLL's pipe server appears, then keeps
    /// _pipe live until it disconnects, then reconnects. Send() is a
    /// no-op while disconnected, so a source can pump PCM without
    /// caring about pipe state.
    /// </summary>
    public void Start()
    {
        if (_connectLoop != null) return;
        _connectLoop = Task.Run(() => ConnectLoopAsync(_cts.Token));
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        if (_connectLoop != null)
        {
            try { await _connectLoop.ConfigureAwait(false); }
            catch { }
        }
        _pipe?.Dispose();
        _cts.Dispose();
    }

    /// <summary>
    /// Write a chunk of interleaved s16 stereo samples. `samples.Length`
    /// must be a multiple of 2 (left/right pairs). Best-effort: returns
    /// false if disconnected or the write fails (the connect loop will
    /// reconnect; the caller should drop the chunk and move on, since
    /// queueing PCM during a disconnect would just produce a desync
    /// burst when the pipe comes back).
    /// </summary>
    public bool Send(ReadOnlySpan<short> samples)
    {
        var pipe = _pipe;
        if (pipe is not { IsConnected: true }) return false;

        // Reinterpret the s16 span as bytes for WriteFile.
        var bytes = System.Runtime.InteropServices.MemoryMarshal.AsBytes(samples);
        try
        {
            pipe.Write(bytes);
            return true;
        }
        catch (Exception ex)
        {
            Log($"send failed, will reconnect: {ex.Message}");
            // Forcibly close the pipe so the loop notices and reconnects.
            try { pipe.Dispose(); } catch { }
            _pipe = null;
            return false;
        }
    }

    private async Task ConnectLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            NamedPipeClientStream? pipe = null;
            try
            {
                pipe = new NamedPipeClientStream(
                    serverName: ".",
                    pipeName: pipeName,
                    direction: PipeDirection.Out,
                    options: PipeOptions.Asynchronous);

                await pipe.ConnectAsync(timeout: 2000, ct).ConfigureAwait(false);
                Log("connected to pcm pipe");
                _pipe = pipe;

                // Park until the pipe disconnects or we're cancelled. The
                // pipe stream itself doesn't raise an event on disconnect;
                // we just poll IsConnected at a low rate. Send() catches
                // mid-write failures synchronously.
                while (!ct.IsCancellationRequested && pipe.IsConnected)
                {
                    await Task.Delay(500, ct).ConfigureAwait(false);
                }
            }
            catch (TimeoutException) { /* DLL not up yet */ }
            catch (OperationCanceledException) { return; }
            catch (Exception ex) { Log($"connect loop: {ex.Message}"); }
            finally
            {
                _pipe = null;
                try { pipe?.Dispose(); } catch { }
            }

            try { await Task.Delay(1000, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
        }
    }
}
