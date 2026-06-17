namespace HorizonRadio.Core.Metadata;

/// <summary>
/// What a single contributor (the source itself, MusicBrainz, Spotify, the local
/// model, …) knows about a track — every field nullable, because a contributor
/// supplies only what it can. The <see cref="MetadataResolver"/> collects one of
/// these per contributor and merges them per the user's <see cref="MetadataPolicy"/>.
/// </summary>
public sealed record MetadataContribution(
    string? Title = null,
    string? Artist = null,
    string? Album = null,
    byte[]? Art = null,
    int? Year = null,
    IReadOnlyList<PlayableRef>? Playables = null)
{
    public static readonly MetadataContribution Empty = new();

    // Playables are descriptive extras (where the track can be played), not a merged metadata field,
    // so they don't count toward emptiness — a contribution that ONLY knows PV links still says
    // nothing about title/artist/art and shouldn't read as a metadata match.
    public bool IsEmpty =>
        string.IsNullOrEmpty(Title) &&
        string.IsNullOrEmpty(Artist) &&
        string.IsNullOrEmpty(Album) &&
        Art is not { Length: > 0 } &&
        Year is null;

    /// <summary>Whether this contribution actually carries the given field.</summary>
    public bool Has(MetadataField field) => field switch
    {
        MetadataField.Title => !string.IsNullOrEmpty(Title),
        MetadataField.Artist => !string.IsNullOrEmpty(Artist),
        MetadataField.Album => !string.IsNullOrEmpty(Album),
        MetadataField.Art => Art is { Length: > 0 },
        MetadataField.Year => Year is not null,
        _ => false,
    };
}
