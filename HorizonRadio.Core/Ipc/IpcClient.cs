using System;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using HorizonRadio.Core.Models;

namespace HorizonRadio.Core.Ipc;

/// <summary>
/// Client for the named-pipe IPC the DLL exposes. Auto-reconnects on
/// startup and on disconnect; raises typed events the UI (or any other
/// consumer in Core) subscribes to. Lives in Core so audio-pipeline
/// services can use the same connection later — e.g. the source layer
/// will send PCM through a sibling pipe but track which session this
/// IpcClient represents.
///
/// Wire format mirrors `IpcServer` on the C++ side: newline-delimited
/// UTF-8 JSON, one event per line.
/// </summary>
public sealed class IpcClient : IAsyncDisposable
{
    public const string DefaultPipeName = "HorizonRadio";

    private readonly string _pipeName;
    private readonly CancellationTokenSource _cts = new();
    private Task? _runLoop;

    // Reference to the active pipe stream, set in the connect loop and
    // cleared on disconnect. SendCommand reaches in to write outbound
    // JSON lines. Guarded by _writeLock so writes from the UI thread
    // serialize cleanly even if multiple commands stack up.
    private NamedPipeClientStream? _activePipe;
    private readonly object _writeLock = new();

    public IpcClient(string pipeName = DefaultPipeName) { _pipeName = pipeName; }

    private static void Log(string msg) => Debug.WriteLine($"[hzn-core] {msg}");

    public event Action? Connected;
    public event Action? Disconnected;
    public event Action<Track>? TrackChanged;
    public event Action<BridgeStats>? StatsUpdated;
    public event Action<SourceInfo>? SourceChanged;

    /// <summary>Push a track change to the DLL so the metadata
    /// injector can write it into the game's HUD. Best-effort; returns
    /// false when the pipe isn't connected (the DLL isn't running or
    /// FH6 hasn't loaded yet) — the caller doesn't need to retry, the
    /// next track change will reattempt.
    /// </summary>
    public bool SendTrack(Track t)
    {
        var json = new System.Text.StringBuilder(256);
        json.Append("{\"cmd\":\"set_track\"");
        AppendJsonField(json, "title",          t.Title);
        AppendJsonField(json, "artist",         t.Artist);
        AppendJsonField(json, "album",          t.Album ?? "");
        AppendJsonField(json, "source_id",      t.SourceId);
        AppendJsonField(json, "source_display", t.SourceDisplay);
        AppendJsonField(json, "external_id",    t.ExternalId ?? "");
        json.Append("}\n");
        return SendRaw(json.ToString());
    }

    private bool SendRaw(string line)
    {
        NamedPipeClientStream? pipe;
        lock (_writeLock) { pipe = _activePipe; }
        if (pipe == null || !pipe.IsConnected) return false;

        var bytes = Encoding.UTF8.GetBytes(line);
        try
        {
            lock (_writeLock) { pipe.Write(bytes, 0, bytes.Length); }
            return true;
        }
        catch (Exception ex)
        {
            Log($"send failed: {ex.Message}");
            return false;
        }
    }

    private static void AppendJsonField(System.Text.StringBuilder sb, string key, string value)
    {
        sb.Append(",\"").Append(key).Append("\":\"");
        foreach (var c in value)
        {
            switch (c)
            {
                case '"':  sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\b': sb.Append("\\b");  break;
                case '\f': sb.Append("\\f");  break;
                case '\n': sb.Append("\\n");  break;
                case '\r': sb.Append("\\r");  break;
                case '\t': sb.Append("\\t");  break;
                default:
                    if (c < 0x20) sb.Append($"\\u{(int)c:x4}");
                    else          sb.Append(c);
                    break;
            }
        }
        sb.Append('"');
    }

    /// <summary>Start the reconnect-and-read loop. Idempotent.</summary>
    public void Start()
    {
        if (_runLoop != null) return;
        _runLoop = Task.Run(() => RunAsync(_cts.Token));
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        if (_runLoop != null) {
            try { await _runLoop.ConfigureAwait(false); }
            catch { /* shutdown swallows */ }
        }
        _cts.Dispose();
    }

    private async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await using var pipe = new NamedPipeClientStream(
                    serverName: ".",
                    pipeName:   _pipeName,
                    direction:  PipeDirection.InOut,
                    options:    PipeOptions.Asynchronous);

                await pipe.ConnectAsync(timeout: 2000, ct).ConfigureAwait(false);
                Log("connected to pipe");
                lock (_writeLock) { _activePipe = pipe; }
                Connected?.Invoke();

                using var reader = new StreamReader(pipe, Encoding.UTF8, false, 4096, leaveOpen: true);
                while (!ct.IsCancellationRequested)
                {
                    var line = await reader.ReadLineAsync(ct).ConfigureAwait(false);
                    if (line == null) break;
                    HandleLine(line);
                }
            }
            catch (TimeoutException)             { Log("connect timeout (DLL not running)"); }
            catch (OperationCanceledException)   { Log("cancelled"); return; }
            catch (IOException ex)               { Log($"io exception: {ex.Message}"); }
            catch (Exception ex)                 { Log($"unexpected: {ex.GetType().Name}: {ex.Message}"); }
            finally
            {
                lock (_writeLock) { _activePipe = null; }
                Log("disconnected, will retry");
                Disconnected?.Invoke();
            }

            try { await Task.Delay(1000, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
        }
    }

    private void HandleLine(string line)
    {
        line = line.Trim();
        if (line.Length == 0) return;

        try
        {
            using var doc = JsonDocument.Parse(line);
            if (!doc.RootElement.TryGetProperty("event", out var ev)) return;
            switch (ev.GetString())
            {
                case "hello":         break;
                case "track":         DispatchTrack(doc.RootElement);          break;
                case "stats":         DispatchStats(doc.RootElement);          break;
                case "source_changed": DispatchSourceChanged(doc.RootElement); break;
            }
        }
        catch (JsonException ex)
        {
            Log($"parse error: {ex.Message} for line: {line}");
        }
    }

    private void DispatchTrack(JsonElement el)
    {
        TrackChanged?.Invoke(new Track(
            Title:         GetString(el, "title")          ?? "",
            Artist:        GetString(el, "artist")         ?? "",
            Album:         GetString(el, "album"),
            AlbumArt:      GetBase64(el, "art_b64"),
            SourceId:      GetString(el, "source_id")      ?? "",
            SourceDisplay: GetString(el, "source_display") ?? "",
            ExternalId:    GetString(el, "external_id")));
    }

    private void DispatchStats(JsonElement el)
    {
        StatsUpdated?.Invoke(new BridgeStats(
            Installed:      el.TryGetProperty("installed", out var i) && i.GetBoolean(),
            FramesIn:       el.TryGetProperty("frames_in",  out var fi) ? fi.GetUInt64() : 0,
            FramesOut:      el.TryGetProperty("frames_out", out var fo) ? fo.GetUInt64() : 0,
            Underruns:      el.TryGetProperty("underruns",  out var un) ? un.GetUInt64() : 0,
            NormalizerGain: el.TryGetProperty("normalizer_gain", out var ng) ? ng.GetSingle() : 1.0f,
            LimiterGain:    el.TryGetProperty("limiter_gain",    out var lg) ? lg.GetSingle() : 1.0f));
    }

    private void DispatchSourceChanged(JsonElement el)
    {
        SourceChanged?.Invoke(new SourceInfo(
            Id:          GetString(el, "id")      ?? "",
            DisplayName: GetString(el, "display") ?? "",
            IsActive:    true));
    }

    private static string? GetString(JsonElement el, string name) =>
        el.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String
            ? p.GetString()
            : null;

    private static byte[]? GetBase64(JsonElement el, string name)
    {
        var s = GetString(el, name);
        if (string.IsNullOrEmpty(s)) return null;
        try { return Convert.FromBase64String(s); }
        catch { return null; }
    }
}
