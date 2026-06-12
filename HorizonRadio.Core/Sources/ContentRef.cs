namespace HorizonRadio.Core.Sources;

/// <summary>
/// A source-agnostic pointer to playable content: which content-addressable
/// source can play it (<see cref="SourceId"/>) and the locator that source
/// understands (a URL, a folder path, an M3U, or a single file).
///
/// Decoupled from the engine that plays it (<see cref="IContentPlayer"/>) so
/// that one engine can play many refs in sequence and a mix can hold refs
/// across different sources. A YouTube ref whose locator is a playlist URL
/// expands to many tracks inside the opened source — i.e. a single ref can
/// stand for a whole collection.
/// </summary>
public sealed record ContentRef(string SourceId, string Locator, string? DisplayName = null);
