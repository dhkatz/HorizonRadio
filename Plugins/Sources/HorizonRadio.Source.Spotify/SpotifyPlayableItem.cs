using System.Diagnostics;
using HorizonRadio.Core.Models;

namespace HorizonRadio.Core.Sources.Spotify;

/// <summary>
/// Rich metadata for one Spotify track, fetched up front from the Web API when a
/// track/playlist/album is enumerated — so the queue and mix lists show real
/// titles/artists/art immediately, with no enrichment pass needed for Spotify's
/// own catalog. Cover art is the only deferred bit (a URL we download lazily).
/// </summary>
public sealed record SpotifyTrackInfo(
    string Uri, string Title, string Artist, string? Album, string? ArtUrl, TimeSpan? Duration, int? Year);

/// <summary>
/// One Spotify track as a <see cref="PlayableItem"/>. Metadata is already known
/// (from the enumerate-time Web API call), so the only work is downloading cover
/// art before playback; <see cref="PlayAsync"/> then hands the URI to the shared
/// <see cref="SpotifyPlaybackService"/>, which drives librespot via the Web API
/// and pumps the resulting PCM. Seek/position ride that same service, so Spotify
/// items get scrubbing the legacy Connect receiver never had.
/// </summary>
public sealed class SpotifyPlayableItem : PlayableItem
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(8) };

    private readonly SpotifyTrackInfo _info;
    private readonly SpotifyPlaybackService _playback;
    private byte[]? _art;
    private bool _artFetched;

    public SpotifyPlayableItem(SpotifyTrackInfo info, SpotifyPlaybackService playback)
    {
        _info = info;
        _playback = playback;
        Duration = info.Duration;
        Metadata = BuildTrack(art: null);
    }

    // Position/seek come from the playback service (valid while this is the active
    // item — the engine plays one item at a time, so the shared service's state is
    // this track's state).
    public override TimeSpan Position => _playback.Position;
    public override bool CanSeek => true;
    public override void Seek(TimeSpan position) => _ = _playback.SeekAsync(position);

    private static void Log(string msg) => Debug.WriteLine($"[hzn-spotify-item] {msg}");

    private Track BuildTrack(byte[]? art) => new(
        Title: _info.Title,
        Artist: _info.Artist,
        Album: _info.Album,
        AlbumArt: art,
        SourceId: SpotifyContentSourceFactory.SourceId,
        SourceDisplay: "Spotify",
        ExternalId: _info.Uri,
        Year: _info.Year);

    public override Task PrepareAsync(CancellationToken ct) => EnsureArtAsync(ct);

    public override async Task<Track?> TryGetMetadataAsync(CancellationToken ct)
    {
        await EnsureArtAsync(ct).ConfigureAwait(false);
        return Metadata;
    }

    private async Task EnsureArtAsync(CancellationToken ct)
    {
        if (_artFetched) return;
        if (string.IsNullOrEmpty(_info.ArtUrl)) { _artFetched = true; return; }
        try
        {
            _art = await Http.GetByteArrayAsync(_info.ArtUrl, ct).ConfigureAwait(false);
            Metadata = BuildTrack(_art);
            _artFetched = true; // mark fetched only on success, so a transient failure retries later
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { Log($"art fetch failed: {ex.Message}"); }
    }

    public override async Task PlayAsync(PumpContext ctx, CancellationToken ct)
    {
        await PrepareAsync(ct).ConfigureAwait(false);

        // OnStarted fires when librespot actually begins streaming (timed to real
        // audio, not the laggy play command), publishing the now-final metadata.
        await _playback.PlayTrackAsync(
            _info.Uri, _info.Duration, ctx,
            onPlaying: () => ctx.OnStarted?.Invoke(this), ct).ConfigureAwait(false);
    }
}
