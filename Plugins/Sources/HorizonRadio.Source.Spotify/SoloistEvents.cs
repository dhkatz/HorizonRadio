using System.Text.Json;

namespace HorizonRadio.Core.Sources.Spotify;

/// <summary>One message from Soloist's WebSocket API, with the fields playback needs.</summary>
internal sealed record SoloistEvent(
    string Type,
    string? Uri = null,
    long? DurationMs = null,
    string? Status = null,
    long? PositionMs = null,
    long? TimestampMs = null,
    double? Speed = null,
    bool? LoggedIn = null,
    bool? IsActive = null,
    string? Message = null,
    string? Command = null)
{
    /// <summary>Read one text frame. Null when it isn't JSON or has no type.</summary>
    public static SoloistEvent? Parse(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("type", out var type)) return null;

            string? uri = null;
            long? duration = null;
            if (root.TryGetProperty("item", out var item) && item.ValueKind == JsonValueKind.Object)
            {
                uri = Str(item, "uri");
                if (item.TryGetProperty("decorations", out var deco)
                    && deco.TryGetProperty("playback", out var pb)
                    && pb.TryGetProperty("duration_ms", out var d) && d.TryGetInt64(out var ms))
                    duration = ms;
            }

            long? position = null, stamp = null;
            double? speed = null;
            if (root.TryGetProperty("position", out var pos) && pos.ValueKind == JsonValueKind.Object)
            {
                if (pos.TryGetProperty("position_ms", out var p) && p.TryGetInt64(out var pv)) position = pv;
                if (pos.TryGetProperty("timestamp_ms", out var t) && t.TryGetInt64(out var tv)) stamp = tv;
                if (pos.TryGetProperty("speed", out var s) && s.TryGetDouble(out var sv)) speed = sv;
            }

            return new SoloistEvent(
                Type: type.GetString() ?? "",
                Uri: uri,
                DurationMs: duration,
                Status: Str(root, "status"),
                PositionMs: position,
                TimestampMs: stamp,
                Speed: speed,
                LoggedIn: Bool(root, "logged_in"),
                IsActive: Bool(root, "is_active"),
                Message: Str(root, "message"),
                Command: Str(root, "command"));
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static bool? Bool(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False ? v.GetBoolean() : null;
}

/// <summary>What a Soloist event means for the track being played.</summary>
internal enum SoloistTrackStep
{
    None,
    /// <summary>Soloist switched to the track asked for (its URI may differ: Spotify relinks).</summary>
    Started,
    /// <summary>Its audio began.</summary>
    Playing,
    /// <summary>It's over: Soloist moved on (it autoplays a related track after a single one), or
    /// stopped at the end.</summary>
    Ended,
}

/// <summary>
/// Follows one track through Soloist's events. Soloist has no "finished" event, and after a single
/// track it autoplays a related one (measured on 1.3.10: <c>buffering</c> at the end, then
/// <c>track_changed</c> to another URI about 1.4 s later). Becoming the active device also resumes
/// whatever Soloist last had, possibly this very track at an old position. So this track starts at
/// the first <c>track_changed</c> after Soloist acknowledged the play (its URI can differ: Spotify
/// relinks); a later one, or an idle/paused stop at the end, is the end.
/// </summary>
internal sealed class SoloistTrack
{
    private static readonly TimeSpan EndSlack = TimeSpan.FromSeconds(2);

    private readonly Func<bool> _userPaused;
    private long _positionMs, _stampMs;
    private double _speed;

    private readonly string _requested;
    private bool _acknowledged;

    public SoloistTrack(string requestedUri, TimeSpan? duration, Func<bool> userPaused)
    {
        _requested = requestedUri;
        Duration = duration;
        _userPaused = userPaused;
    }

    public string? Uri { get; private set; }
    public TimeSpan? Duration { get; private set; }
    public bool Started => Uri != null;
    public bool IsPlaying { get; private set; }

    /// <summary>Soloist says this track is paused (by anyone: the user here, the Spotify app, a
    /// device handover).</summary>
    public bool IsPaused { get; private set; }
    public bool Ended { get; private set; }

    /// <summary>Where the track is, from Soloist's last position anchor.</summary>
    public TimeSpan PositionAt(long nowUnixMs)
    {
        var ms = _positionMs + (long)Math.Max(0, (nowUnixMs - _stampMs) * _speed);
        if (Duration is { } d) ms = Math.Min(ms, (long)d.TotalMilliseconds);
        return TimeSpan.FromMilliseconds(Math.Max(0, ms));
    }

    public SoloistTrackStep Apply(SoloistEvent e)
    {
        ArgumentNullException.ThrowIfNull(e);
        if (Ended) return SoloistTrackStep.None;

        if (e.PositionMs is { } p && e.TimestampMs is { } t && Started)
        {
            _positionMs = p;
            _stampMs = t;
            _speed = e.Speed ?? 0;
        }

        switch (e.Type)
        {
            case "command_result" when e.Command == "play":
                _acknowledged = true;
                return SoloistTrackStep.None;

            case "track_changed" when e.Uri is { Length: > 0 } uri:
                if (!Started)
                {
                    // Before the play is acknowledged it's what activation resumed, even this URI.
                    if (!_acknowledged) return SoloistTrackStep.None;
                    Uri = uri;
                    if (e.DurationMs is { } ms) Duration = TimeSpan.FromMilliseconds(ms);
                    _positionMs = 0;
                    _speed = 0;
                    return SoloistTrackStep.Started;
                }
                if (uri != Uri) return End();
                return SoloistTrackStep.None;

            // Replaying the track Soloist already has loaded may not send track_changed: the
            // snapshot naming it is the start then.
            case "playback_state" when !Started && _acknowledged && e.Uri == _requested:
                Uri = _requested;
                if (e.DurationMs is { } ms2) Duration = TimeSpan.FromMilliseconds(ms2);
                var status = OnStatus(e.Status);
                return status == SoloistTrackStep.Playing ? SoloistTrackStep.Playing : SoloistTrackStep.Started;

            case "playback_changed" or "playback_state":
                return OnStatus(e.Status);
        }
        return SoloistTrackStep.None;
    }

    private SoloistTrackStep OnStatus(string? status)
    {
        if (!Started) return SoloistTrackStep.None;
        if (status is "playing" or "paused") IsPaused = status == "paused";
        switch (status)
        {
            case "playing" when !IsPlaying:
                IsPlaying = true;
                return SoloistTrackStep.Playing;
            case "idle":
                return End();
            case "paused" when !_userPaused() && NearEnd():
                return End(); // stopped at the end (autoplay off on the account)
        }
        return SoloistTrackStep.None;
    }

    private bool NearEnd() =>
        Duration is { } d && TimeSpan.FromMilliseconds(_positionMs) >= d - EndSlack;

    private SoloistTrackStep End()
    {
        Ended = true;
        return SoloistTrackStep.Ended;
    }
}
