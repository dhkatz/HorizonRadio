using System.Diagnostics;
using System.Text.Json;
using HorizonRadio.Core.IO;

namespace HorizonRadio.Core.Sources.Spotify;

/// <summary>
/// Which player a Spotify track goes to first, kept across sessions. Spotify refuses librespot
/// per account, so once it has (and Soloist played the track instead) Soloist goes first and
/// tracks skip the second or so librespot takes to be refused. librespot gets another try after
/// <see cref="RecheckAfter"/>, or as soon as its build changes (an update from the Tools tab is
/// the likeliest fix); a play it delivers puts it back first.
/// </summary>
public sealed class SpotifyPlayerChoice
{
    public static readonly TimeSpan RecheckAfter = TimeSpan.FromHours(24);

    public static string DefaultPath => AppPaths.In("spotify-player.json");

    private readonly string _path;
    private readonly Func<DateTimeOffset> _now;
    private readonly object _lock = new();
    private Refusal? _refusal;

    private sealed record Refusal(DateTimeOffset At, string? Librespot);

    public SpotifyPlayerChoice(string path, Func<DateTimeOffset>? now = null)
    {
        _path = path;
        _now = now ?? (() => DateTimeOffset.UtcNow);
        _refusal = Load(path);
    }

    /// <summary>True when Soloist should play without trying librespot: Spotify refused this
    /// librespot build less than <see cref="RecheckAfter"/> ago.</summary>
    /// <param name="librespotBuild">The librespot that would play (<see cref="BuildOf"/>).</param>
    public bool PreferSoloist(string? librespotBuild)
    {
        lock (_lock)
        {
            if (_refusal is not { } r || r.Librespot != librespotBuild) return false;
            // A refusal from the future (the clock was ahead) is due a recheck, not a day's wait.
            var age = _now() - r.At;
            return age >= TimeSpan.Zero && age < RecheckAfter;
        }
    }

    /// <summary>When Spotify last refused librespot (and Soloist played instead), or null.</summary>
    public DateTimeOffset? LibrespotRefusedAt
    {
        get { lock (_lock) return _refusal?.At; }
    }

    /// <summary>Spotify refused <paramref name="librespotBuild"/> and Soloist played the track.</summary>
    public void LibrespotRefused(string? librespotBuild)
    {
        lock (_lock)
        {
            _refusal = new Refusal(_now(), librespotBuild);
            Save();
        }
    }

    /// <summary>librespot played a track: it goes first again.</summary>
    public void LibrespotPlayed()
    {
        lock (_lock)
        {
            if (_refusal == null) return;
            _refusal = null;
            Save();
        }
    }

    /// <summary>Tells librespot builds apart: its size and write time, or null when it's missing.</summary>
    public static string? BuildOf(string? librespotPath)
    {
        if (string.IsNullOrEmpty(librespotPath)) return null;
        var file = new FileInfo(librespotPath);
        return file.Exists
            ? $"{file.Length}:{file.LastWriteTimeUtc.Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture)}"
            : null;
    }

    private static Refusal? Load(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var root = doc.RootElement;
            if (!root.TryGetProperty("librespotRefusedAt", out var at) || !at.TryGetDateTimeOffset(out var when)) return null;
            var build = root.TryGetProperty("librespotBuild", out var b) && b.ValueKind == JsonValueKind.String ? b.GetString() : null;
            return new Refusal(when, build);
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            Trace.WriteLine($"[hzn-spotify-choice] couldn't read {path}: {ex.Message}");
            return null;
        }
    }

    private void Save()
    {
        try
        {
            if (_refusal == null)
            {
                File.Delete(_path);
                return;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            using var stream = File.Create(_path);
            using var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true });
            writer.WriteStartObject();
            writer.WriteString("librespotRefusedAt", _refusal.At);
            writer.WriteString("librespotBuild", _refusal.Librespot);
            writer.WriteEndObject();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Trace.WriteLine($"[hzn-spotify-choice] couldn't save {_path}: {ex.Message}");
        }
    }
}
