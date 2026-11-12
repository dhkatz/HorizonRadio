using System.Diagnostics;
using HorizonRadio.Sdk;
using HorizonRadio.Sdk.Models;
using HorizonRadio.Sdk.Sources;

namespace HorizonRadio.Source.Spotify;

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
    private readonly SoloistPlaybackService? _soloist;
    private readonly SpotifyPlayerChoice? _choice;
    // This play went to Soloist: position and seek follow it.
    private volatile bool _viaSoloist;
    private byte[]? _art;
    private bool _artFetched;

    private readonly IPluginLog _log;

    /// <param name="soloist">Plays what Spotify refuses librespot; null to only use librespot.</param>
    /// <param name="choice">Which player goes first, kept across sessions; null to always try librespot first.</param>
    public SpotifyPlayableItem(SpotifyTrackInfo info, SpotifyPlaybackService playback,
                               SoloistPlaybackService? soloist, SpotifyPlayerChoice? choice, IPluginLog log)
    {
        _info = info;
        _playback = playback;
        _soloist = soloist;
        _choice = choice;
        _log = log;
        Duration = info.Duration;
        Metadata = BuildTrack(art: null);
    }

    // Position/seek come from the playback service (valid while this is the active
    // item — the engine plays one item at a time, so the shared service's state is
    // this track's state).
    public override TimeSpan Position => _viaSoloist && _soloist != null ? _soloist.Position : _playback.Position;
    public override bool CanSeek => true;

    public override void Seek(TimeSpan position) =>
        _ = _viaSoloist && _soloist != null ? _soloist.SeekAsync(position) : _playback.SeekAsync(position);

    private static void Log(string msg) => Trace.WriteLine($"[hzn-spotify-item] {msg}");

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

        // OnStarted fires when the audio actually begins (timed to real audio, not the laggy play
        // command), publishing the now-final metadata.
        void OnPlaying()
        {
            ctx.OnStarted?.Invoke(this);
            // Resuming mid-track: seek once this track is streaming.
            if (ctx.StartPosition?.Invoke(this) is { Ticks: > 0 } start) Seek(start);
        }

        // Spotify refuses librespot per account: once it has (and Soloist played instead), don't
        // wait to be refused again until it's time to give librespot another try.
        var soloistReady = _soloist is { IsConfigured: true };
        var librespot = _playback.LibrespotBuild;
        var soloistFailed = false;
        if (soloistReady && (_choice?.PreferSoloist(librespot) ?? _playback.AudioKeyRefused))
        {
            if (_choice?.LibrespotRefusedAt is { } at)
            {
                _log.Write("spotify", "playing through Soloist: Spotify refused librespot " +
                    $"{at.ToLocalTime():g}; librespot gets another try after {(at + SpotifyPlayerChoice.RecheckAfter).ToLocalTime():g}");
            }
            var soloistPlayed = false;
            try
            {
                await PlayWithSoloistAsync(ctx, () =>
                {
                    soloistPlayed = true;
                    OnPlaying();
                }, ct).ConfigureAwait(false);
                return;
            }
            catch (Exception ex) when (ex is not OperationCanceledException && !soloistPlayed)
            {
                // Soloist broke (an expired build, its audio tap, pairing lost): librespot plays what
                // Spotify lets it rather than every track failing until the recheck.
                _log.Write("spotify", $"Soloist couldn't play the track ({ex.Message}); trying librespot");
                soloistFailed = true;
            }
        }
        else if (soloistReady && _choice?.LibrespotRefusedAt is { } refusedAt)
        {
            _log.Write("spotify", "giving librespot another try (Spotify refused it " +
                $"{refusedAt.ToLocalTime():g}); Soloist plays the track if it's refused again");
        }

        try
        {
            _viaSoloist = false;
            await _playback.PlayTrackAsync(_info.Uri, _info.Duration, ctx, () =>
            {
                OnPlaying();
                _choice?.LibrespotPlayed();
            }, ct).ConfigureAwait(false);
        }
        catch (SpotifyTrackUnavailableException ex) when (ex.AudioKeyRefused)
        {
            if (!soloistReady || soloistFailed)
            {
                _log.Write("spotify", "Spotify refused librespot the audio; no Soloist fallback: "
                    + (soloistFailed ? "Soloist failed on this track too"
                                     : _soloist?.NotReadyReason ?? "Soloist isn't available in this build"));
                throw;
            }
            _log.Write("spotify", "Spotify refused librespot the audio; playing it through Soloist");
            await PlayWithSoloistAsync(ctx, () =>
            {
                OnPlaying();
                // Soloist works where librespot doesn't: it goes first from now on.
                _choice?.LibrespotRefused(librespot);
            }, ct).ConfigureAwait(false);
        }
    }

    private async Task PlayWithSoloistAsync(PumpContext ctx, Action onPlaying, CancellationToken ct)
    {
        _viaSoloist = true;
        await _soloist!.PlayTrackAsync(_info.Uri, _info.Duration, ctx, onPlaying, ct).ConfigureAwait(false);
    }
}
