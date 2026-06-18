namespace HorizonRadio.Core.Metadata;

/// <summary>
/// A playable link a metadata provider learned about a track — e.g. VocaDB's promotion-video (PV)
/// list, which points at the official/reprint uploads on YouTube, Niconico, Bilibili, SoundCloud, …
/// <see cref="Service"/> is the human label of where it lives ("YouTube", "Niconico"); <see cref="Url"/>
/// is a page URL a yt-dlp-backed source can play. This is descriptive — it says "this track is
/// available here" — and is carried alongside <see cref="MetadataContribution"/> rather than being a
/// merged metadata field. The decision of how to play it (which queueable source) belongs to the
/// consumer (play history), so this stays free of any <c>Sources</c>-layer concept.
/// </summary>
public sealed record PlayableRef(string Service, string Url);
