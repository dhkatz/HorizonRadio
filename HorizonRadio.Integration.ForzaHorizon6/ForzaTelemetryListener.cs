using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using HorizonRadio.Core.Diagnostics;

namespace HorizonRadio.Core.Events;

/// <summary>
/// Listens for Forza "Data Out" UDP telemetry and turns it into
/// <see cref="GameEvent"/>s. Forza Horizon/Motorsport can stream a fixed
/// binary struct to a UDP host the player configures in
/// Settings → HUD/Gameplay → Data Out (point it at 127.0.0.1 and the port
/// below). This is a richer, hook-free event source that complements the
/// DLL's memory polling.
///
/// v1 uses the one field whose position is stable across every Data Out
/// format — <c>IsRaceOn</c> (s32 at offset 0): 1 while gameplay is live,
/// 0 in menus/pause/replay. We map its edges to Resumed/Paused. The packet
/// also carries speed/position/lap once you're past the 232-byte "sled"
/// header; those offsets shift between titles, so we only attach speed as
/// event data (behind a clearly-marked constant) rather than acting on it —
/// future threshold rules (e.g. "speed &gt; X") can build on that.
/// </summary>
public sealed class ForzaTelemetryListener : IGameEventSource, IDisposable
{
    /// <summary>Default UDP port. Tell users to set Data Out to 127.0.0.1:this.</summary>
    public const int DefaultPort = 9967;

    // Forza Data Out layout (little-endian):
    //   offset 0 : s32 IsRaceOn
    //   offset 4 : u32 TimestampMS
    //   ... 232-byte "sled" physics header ...
    //   then car/race "dash" fields whose offsets vary by title.
    private const int IsRaceOnOffset = 0;
    private const int SledSize = 232;
    // Speed (m/s, f32) in the dash segment. FH5-era offset; verify for FH6.
    private const int SpeedOffsetDash = 244;

    private readonly int _port;
    private UdpClient? _udp;
    private CancellationTokenSource? _cts;
    private Task? _loop;
    private int _lastRaceOn = -1; // -1 = unknown

    public event Action<GameEvent>? GameEventReceived;

    public ForzaTelemetryListener(int port = DefaultPort) => _port = port;

    public void Start()
    {
        if (_loop != null) return;
        try
        {
            _udp = new UdpClient(new IPEndPoint(IPAddress.Any, _port));
        }
        catch (Exception ex)
        {
            ProcessConsole.Append("telemetry", $"could not bind UDP {_port}: {ex.Message}");
            return;
        }
        _cts = new CancellationTokenSource();
        _loop = Task.Run(() => ReceiveLoopAsync(_cts.Token));
        ProcessConsole.Append("telemetry", $"listening for Forza Data Out on 127.0.0.1:{_port}");
    }

    private async Task ReceiveLoopAsync(CancellationToken ct)
    {
        var udp = _udp!;
        while (!ct.IsCancellationRequested)
        {
            UdpReceiveResult res;
            try
            {
                res = await udp.ReceiveAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex)
            {
                ProcessConsole.Append("telemetry", $"receive error: {ex.Message}");
                return;
            }

            try { Parse(res.Buffer); }
            catch (Exception ex) { ProcessConsole.Append("telemetry", $"parse error: {ex.Message}"); }
        }
    }

    private void Parse(byte[] buf)
    {
        if (buf.Length < IsRaceOnOffset + 4) return;
        var span = buf.AsSpan();

        int raceOn = BinaryPrimitives.ReadInt32LittleEndian(span.Slice(IsRaceOnOffset, 4));
        if (raceOn != _lastRaceOn)
        {
            bool first = _lastRaceOn < 0;
            _lastRaceOn = raceOn;
            // Don't fire on the very first packet (we're just learning the
            // current state, not observing a transition).
            if (!first)
            {
                var data = TryReadSpeed(span, out var speed)
                    ? new Dictionary<string, string> { ["speed_mps"] = speed.ToString("0.0", CultureInfo.InvariantCulture) }
                    : null;
                GameEventReceived?.Invoke(new GameEvent(
                    raceOn != 0 ? GameEventKinds.Resumed : GameEventKinds.Paused, data));
            }
        }
    }

    private static bool TryReadSpeed(ReadOnlySpan<byte> span, out float speed)
    {
        speed = 0f;
        if (span.Length < SledSize || span.Length < SpeedOffsetDash + 4) return false;
        speed = BinaryPrimitives.ReadSingleLittleEndian(span.Slice(SpeedOffsetDash, 4));
        return speed is >= 0f and < 200f; // sanity (m/s); reject garbage offset
    }

    public void Stop()
    {
        try { _cts?.Cancel(); } catch { }
        try { _udp?.Dispose(); } catch { }
        _udp = null;
        if (_loop != null) { try { _loop.Wait(1000); } catch { } _loop = null; }
        _cts?.Dispose();
        _cts = null;
    }

    public void Dispose() => Stop();
}
