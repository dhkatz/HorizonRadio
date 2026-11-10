using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;
using HorizonRadio.Core.IO;
using HorizonRadio.Core.Sources;

namespace HorizonRadio.Tools.Peer;

/// <summary>
/// The peer-to-peer helper, <c>horizon-peer serve</c>, running for the app: peers find each other
/// by topic (<see cref="AdvertiseAsync"/>, <see cref="FindAsync"/>), connect (directly, or through
/// a relay until a hole punch succeeds) and exchange JSON messages, in order per peer and at most
/// once each. The helper's own reports (reachability, addresses, hole punches) go to the log.
///
/// The process is tied to the app (<see cref="ChildProcesses.TieToApp"/>) and exits when its stdin
/// closes, so it never outlives it. Events are raised on the reading thread: keep handlers short.
/// </summary>
public sealed class PeerHelper : IAsyncDisposable
{
    public const string ExeName = "horizon-peer.exe";

    private static readonly JsonSerializerOptions CommandJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly Process? _process;
    private readonly TextWriter _input;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly ConcurrentDictionary<string, TaskCompletionSource<JsonElement>> _pending = new();
    private readonly ConcurrentDictionary<string, Channel<FoundPeer>> _finds = new();
    private readonly TaskCompletionSource<JsonElement> _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Task _reading;
    private long _nextId;
    private int _disposed;

    /// <summary>Where the app keeps this profile's peer identity: its peer ID is derived from it.</summary>
    public static string DefaultKeyPath => AppPaths.In("peer", "identity.key");

    /// <summary>The bundled helper next to the app, or null when it wasn't built.</summary>
    public static string? FindExe()
    {
        var path = Path.Combine(AppContext.BaseDirectory, ExeName);
        return File.Exists(path) ? path : null;
    }

    internal PeerHelper(TextReader output, TextWriter input, Process? process = null)
    {
        _input = input;
        _process = process;
        _reading = Task.Run(() => ReadAsync(output));
    }

    /// <summary>This peer's ID (from its identity key): how other peers address it.</summary>
    public string PeerId { get; private set; } = "";

    /// <summary>The addresses it listens on, as the helper reported when it started.</summary>
    public IReadOnlyList<string> Addresses { get; private set; } = [];

    /// <summary>A message arrived.</summary>
    public event Action<PeerMessage>? MessageReceived;

    /// <summary>A peer the app connected to or heard from changed path.</summary>
    public event Action<PeerPathChange>? PathChanged;

    /// <summary>The helper stopped (it crashed, or was disposed).</summary>
    public event Action? Exited;

    /// <summary>Start the helper and wait until it's ready for commands.</summary>
    /// <exception cref="FileNotFoundException">The helper isn't there (not built).</exception>
    public static async Task<PeerHelper> StartAsync(string keyPath, PeerDiscovery discovery, string? exePath = null, CancellationToken ct = default)
    {
        exePath ??= FindExe() ?? throw new FileNotFoundException($"{ExeName} isn't next to the app (build it: Plugins/Tools/HorizonRadio.Tools.Peer/Helper/README.md).", ExeName);
        if (!File.Exists(exePath)) throw new FileNotFoundException($"The peer helper isn't at {exePath}.", exePath);
        var psi = new ProcessStartInfo(exePath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            StandardInputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
        };
        foreach (var arg in new[] { "serve", "-key", keyPath, "-discovery", DiscoveryArgument(discovery) }) psi.ArgumentList.Add(arg);

        var process = Process.Start(psi) ?? throw new PeerHelperException($"couldn't start {exePath}");
        ChildProcesses.TieToApp(process);
        process.ErrorDataReceived += (_, e) =>
        {
            if (!string.IsNullOrWhiteSpace(e.Data)) Log(e.Data);
        };
        process.BeginErrorReadLine();

        var helper = new PeerHelper(process.StandardOutput, process.StandardInput, process);
        try
        {
            await helper.WaitReadyAsync(TimeSpan.FromSeconds(30), ct).ConfigureAwait(false);
        }
        catch
        {
            await helper.DisposeAsync().ConfigureAwait(false);
            throw;
        }
        return helper;
    }

    internal static string DiscoveryArgument(PeerDiscovery discovery)
    {
        var names = new List<string>();
        if (discovery.HasFlag(PeerDiscovery.Internet)) names.Add("amino");
        if (discovery.HasFlag(PeerDiscovery.Lan)) names.Add("lan");
        return names.Count == 0 ? "none" : string.Join(',', names);
    }

    internal async Task WaitReadyAsync(TimeSpan timeout, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        var ready = await _ready.Task.WaitAsync(cts.Token).ConfigureAwait(false);
        PeerId = ready.GetProperty("peer_id").GetString() ?? "";
        Addresses = ready.TryGetProperty("addrs", out var addrs) && addrs.ValueKind == JsonValueKind.Array
            ? [.. addrs.EnumerateArray().Select(a => a.GetString()).OfType<string>()]
            : [];
    }

    /// <summary>Be findable under <paramref name="topic"/> until <see cref="UnadvertiseAsync"/>.</summary>
    public Task AdvertiseAsync(string topic, CancellationToken ct = default) =>
        CommandAsync(new Command("advertise") { Topic = topic }, ct);

    public Task UnadvertiseAsync(string topic, CancellationToken ct = default) =>
        CommandAsync(new Command("unadvertise") { Topic = topic }, ct);

    /// <summary>Each peer found under <paramref name="topic"/>, once, as it's found, until
    /// <paramref name="timeout"/> or the caller stops enumerating.</summary>
    public async IAsyncEnumerable<FoundPeer> FindAsync(string topic, TimeSpan timeout, [EnumeratorCancellation] CancellationToken ct = default)
    {
        var id = NextId();
        var found = Channel.CreateUnbounded<FoundPeer>(new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });
        _finds[id] = found;
        try
        {
            await WriteAsync(new Command("find") { Id = id, Topic = topic, TimeoutSeconds = timeout.TotalSeconds }, ct).ConfigureAwait(false);
            await foreach (var peer in found.Reader.ReadAllAsync(ct).ConfigureAwait(false)) yield return peer;
        }
        finally
        {
            _finds.TryRemove(id, out _);
            if (!found.Reader.Completion.IsCompleted)
            {
                try { await WriteAsync(new Command("stop") { Id = id }, CancellationToken.None).ConfigureAwait(false); }
                catch (Exception ex) when (ex is IOException or ObjectDisposedException or PeerHelperException) { }
            }
        }
    }

    /// <summary>Connect to a peer, by <paramref name="addresses"/> (from a find), those already
    /// known, or (with Internet discovery) a lookup of its ID. Returns the path it's on.</summary>
    public async Task<PeerPath> ConnectAsync(string peerId, IReadOnlyList<string>? addresses = null, CancellationToken ct = default)
    {
        var reply = await CommandAsync(new Command("connect") { Peer = peerId, Addrs = addresses }, ct).ConfigureAwait(false);
        return ParsePath(reply.TryGetProperty("path", out var path) ? path.GetString() : null);
    }

    /// <summary>Send <paramref name="message"/> (any JSON, up to 1 MiB) to a connected peer.
    /// Messages to a peer arrive in the order sent.</summary>
    public Task SendAsync(string peerId, JsonElement message, CancellationToken ct = default) =>
        CommandAsync(new Command("send") { Peer = peerId, Message = message }, ct);

    public Task DisconnectAsync(string peerId, CancellationToken ct = default) =>
        CommandAsync(new Command("disconnect") { Peer = peerId }, ct);

    private async Task<JsonElement> CommandAsync(Command command, CancellationToken ct)
    {
        var id = NextId();
        var reply = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = reply;
        try
        {
            await WriteAsync(command with { Id = id }, ct).ConfigureAwait(false);
            return await reply.Task.WaitAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _pending.TryRemove(id, out _);
        }
    }

    private string NextId() => Interlocked.Increment(ref _nextId).ToString(System.Globalization.CultureInfo.InvariantCulture);

    private async Task WriteAsync(Command command, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (_reading.IsCompleted) throw new PeerHelperException("the peer helper has stopped");
        var line = JsonSerializer.Serialize(command, CommandJson);
        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await _input.WriteLineAsync(line.AsMemory(), ct).ConfigureAwait(false);
            await _input.FlushAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private async Task ReadAsync(TextReader output)
    {
        try
        {
            while (await output.ReadLineAsync().ConfigureAwait(false) is { } line)
            {
                try
                {
                    Handle(line);
                }
                catch (Exception ex) when (ex is JsonException or InvalidOperationException or KeyNotFoundException)
                {
                    Log($"unreadable event ({ex.Message}): {line}");
                }
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
        }
        finally
        {
            var stopped = new PeerHelperException("the peer helper has stopped");
            _ready.TrySetException(stopped);
            foreach (var (_, reply) in _pending) reply.TrySetException(stopped);
            foreach (var (_, found) in _finds) found.Writer.TryComplete(stopped);
            Raise(Exited, "exited");
        }
    }

    private void Handle(string line)
    {
        using var doc = JsonDocument.Parse(line);
        var ev = doc.RootElement;
        var name = ev.GetProperty("event").GetString();
        var id = ev.TryGetProperty("id", out var idProp) ? idProp.GetString() : null;
        switch (name)
        {
            case "ready":
                _ready.TrySetResult(ev.Clone());
                break;
            case "ok" or "connected" when id != null && _pending.TryGetValue(id, out var reply):
                reply.TrySetResult(ev.Clone());
                break;
            case "error" when id != null && _pending.TryGetValue(id, out var failed):
                failed.TrySetException(new PeerHelperException(ev.GetProperty("error").GetString() ?? "failed"));
                break;
            case "error" when id != null && _finds.TryGetValue(id, out var badFind):
                badFind.Writer.TryComplete(new PeerHelperException(ev.GetProperty("error").GetString() ?? "failed"));
                break;
            case "found" when id != null && _finds.TryGetValue(id, out var find):
                find.Writer.TryWrite(new FoundPeer(
                    ev.GetProperty("peer").GetString() ?? "",
                    [.. ev.GetProperty("addrs").EnumerateArray().Select(a => a.GetString()).OfType<string>()]));
                break;
            case "find_done" when id != null && _finds.TryGetValue(id, out var done):
                done.Writer.TryComplete();
                break;
            case "message":
                var message = new PeerMessage(ev.GetProperty("peer").GetString() ?? "", ev.GetProperty("message").Clone());
                Raise(MessageReceived, message);
                break;
            case "peer":
                var change = new PeerPathChange(ev.GetProperty("peer").GetString() ?? "", ParsePath(ev.GetProperty("path").GetString()));
                Log($"{change.PeerId}: {change.Path}");
                Raise(PathChanged, change);
                break;
            case "ok" or "find_done":
                break; // a reply nobody waits for any more (a stopped find)
            default:
                Log(line);
                break;
        }
    }

    private static PeerPath ParsePath(string? path) => path switch
    {
        "direct" => PeerPath.Direct,
        "relayed" => PeerPath.Relayed,
        _ => PeerPath.None,
    };

    private static void Raise<T>(Action<T>? handler, T arg)
    {
        try
        {
            handler?.Invoke(arg);
        }
        catch (Exception ex)
        {
            Log($"a {typeof(T).Name} handler failed: {ex}");
        }
    }

    private static void Raise(Action? handler, string what)
    {
        try
        {
            handler?.Invoke();
        }
        catch (Exception ex)
        {
            Log($"an {what} handler failed: {ex}");
        }
    }

    private static void Log(string message) => Trace.WriteLine($"[hzn-peer] {message}");

    /// <summary>Ask the helper to stop, and end it if it doesn't within a few seconds.</summary>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        try
        {
            await _writeLock.WaitAsync().ConfigureAwait(false);
            try
            {
                await _input.WriteLineAsync("""{"cmd":"shutdown"}""").ConfigureAwait(false);
                await _input.FlushAsync().ConfigureAwait(false);
                _input.Close();
            }
            finally
            {
                _writeLock.Release();
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
        }

        if (_process != null)
        {
            using var wait = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            try
            {
                await _process.WaitForExitAsync(wait.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                try { _process.Kill(); }
                catch (InvalidOperationException) { }
            }
            _process.Dispose();
        }
        await _reading.ConfigureAwait(false);
        _writeLock.Dispose();
    }

    private sealed record Command(string Cmd)
    {
        public string? Id { get; init; }
        public string? Topic { get; init; }
        public string? Peer { get; init; }
        public IReadOnlyList<string>? Addrs { get; init; }
        public JsonElement? Message { get; init; }
        public double? TimeoutSeconds { get; init; }
    }
}
