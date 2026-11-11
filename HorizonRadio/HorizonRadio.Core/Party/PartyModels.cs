using System.Text.Json;
using System.Text.Json.Serialization;

namespace HorizonRadio.Core.Party;

/// <summary>Where a song can be played: a source and the locator it plays (a Spotify uri, a YouTube
/// URL, a station).</summary>
public sealed record PartySource(string SourceId, string Locator);

/// <summary>What the host is playing, as guests are told it. <paramref name="Key"/> tells one play from
/// the next (the same song played again is a new key). A <paramref name="Live"/> one is a station:
/// guests tune into it rather than follow a position, and its song changes don't change the key.
/// <paramref name="Sources"/>: where the host plays it, best first; a guest without them finds it by
/// title and artist.</summary>
public sealed record PartySong(string Key, string Title, string Artist, long? DurationMs, bool Live, IReadOnlyList<PartySource> Sources);

/// <summary>The host's player now: the song, where in it (null for a station, or before it has
/// started), and whether it's paused.</summary>
public sealed record PartyNow(PartySong Song, TimeSpan? Position, bool Paused);

/// <summary>The host's side: what its player is doing.</summary>
public interface IPartyHostSource
{
    /// <summary>What's playing, or null when nothing is.</summary>
    PartyNow? Now { get; }

    /// <summary>The song, the pause or the position changed (a seek). Optional: the party also
    /// looks every second.</summary>
    event Action? Changed;
}

/// <summary>What a guest's player is doing with the party's song.</summary>
/// <param name="Key">The song the party put on.</param>
/// <param name="Position">Where in it the player is.</param>
/// <param name="Started">The player is playing it (its position means something).</param>
public sealed record PartyPlayerState(string Key, TimeSpan Position, bool Started, bool Paused, bool CanSeek);

/// <summary>A guest's side: its player, which plays the host's songs.</summary>
public interface IPartyPlayer
{
    /// <summary>Play <paramref name="song"/> now, starting where <paramref name="startAt"/> says the
    /// moment it starts (so the time spent finding it isn't lost). False when no source here has it.
    /// Throws <see cref="PartyException"/>, saying why for the page, when a source here has it but
    /// can't play it (its tools aren't installed, say).</summary>
    Task<bool> PlayAsync(PartySong song, Func<TimeSpan> startAt, CancellationToken ct);

    /// <summary>The party's song, while it's what the player is on; else null.</summary>
    PartyPlayerState? Current { get; }

    Task SeekAsync(TimeSpan position);

    Task SetPausedAsync(bool paused);
}

public enum PartyRole
{
    None,
    Hosting,
    Joining,
    Guest,
}

/// <summary>The party as the Friends page shows it.</summary>
/// <param name="HostName">Whose party you're in (as a guest).</param>
/// <param name="Guests">Who's listening along (as the host).</param>
/// <param name="Song">What the party's playing.</param>
/// <param name="Finding">A guest is finding the song to play it.</param>
/// <param name="Problem">What went wrong last ("Couldn't find …").</param>
public sealed record PartyView(PartyRole Role, string? HostName, IReadOnlyList<string> Guests, PartySong? Song, bool Finding, string? Problem)
{
    public static readonly PartyView None = new(PartyRole.None, null, [], null, false, null);
}

/// <summary>Joining a party, or playing its song, failed; the message is for the page.</summary>
public sealed class PartyException(string message) : Exception(message);

// What a party's host and guests say to each other, over the friends' verified connections:
//   party.join     guest → host                  let me listen along
//   party.welcome  host → guest                  you're in (the state follows)
//   party.closed   host → guest                  not hosting
//   party.state    host → guests                 { seq, song?, positionMs?, at, paused }: at, the
//                                                host's clock (Unix ms) when positionMs was read
//   party.ping     guest → host                  { t0 }: the guest's clock
//   party.pong     host → guest                  { t0, at }: the host's clock when it answered
//   party.leave    guest → host
//   party.end      host → guests                 the host stopped hosting
internal sealed record PartyMessage
{
    public required string Type { get; init; }
    public long Seq { get; init; }
    public PartySong? Song { get; init; }
    public long? PositionMs { get; init; }
    public long At { get; init; }
    public bool Paused { get; init; }
    public long T0 { get; init; }

    public const string Join = "party.join";
    public const string Welcome = "party.welcome";
    public const string Closed = "party.closed";
    public const string State = "party.state";
    public const string Ping = "party.ping";
    public const string Pong = "party.pong";
    public const string Leave = "party.leave";
    public const string End = "party.end";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public JsonElement ToJson() => JsonSerializer.SerializeToElement(this, Json);

    public static PartyMessage? From(JsonElement json)
    {
        try
        {
            var message = json.Deserialize<PartyMessage>(Json);
            return message?.Type?.StartsWith("party.", StringComparison.Ordinal) == true ? message : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
