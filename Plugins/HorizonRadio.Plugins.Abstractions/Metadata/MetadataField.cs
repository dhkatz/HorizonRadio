namespace HorizonRadio.Core.Metadata;

/// <summary>
/// The individual pieces of track metadata the pipeline resolves independently.
/// Each field can be drawn from a different contributor (per the user's
/// <see cref="MetadataPolicy"/>) — e.g. title from the source but art always from
/// Spotify — so resolution is per-field, not whole-track.
/// </summary>
public enum MetadataField
{
    Title,
    Artist,
    Album,
    Art,
    Year,
}
