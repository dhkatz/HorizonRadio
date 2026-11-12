using HorizonRadio.Sdk.Models;

namespace HorizonRadio.Sdk.Metadata;

/// <summary>
/// The metadata diagnostics the user can switch on (About → diagnostics): while it's on, the host
/// writes every song and every lookup to a file, so a bad match can be replayed and reported. A
/// provider adds what it searched for and the catalog results it weighed; a source that parses
/// titles adds what it parsed. All of it is a no-op while diagnostics are off.
/// </summary>
public interface IMetadataTrace
{
    /// <summary>Diagnostics that record nothing (tests, a host without them).</summary>
    static IMetadataTrace Off { get; } = new OffTrace();

    /// <summary>True while diagnostics are on. Building what you'd record can wait on this.</summary>
    bool Enabled { get; }

    /// <summary>A list to collect the catalog results a search weighed, or null while diagnostics
    /// are off: <c>var capture = trace.NewCapture(); capture?.Add(...)</c>, then hand it to
    /// <see cref="ProviderSearch"/>.</summary>
    List<CatalogCandidate>? NewCapture() => Enabled ? [] : null;

    /// <summary>What provider <paramref name="providerId"/> asked its catalog for, and the results it
    /// weighed, for the lookup under way. A null <paramref name="considered"/> (diagnostics were off)
    /// is a no-op.</summary>
    void ProviderSearch(string providerId, string query, IReadOnlyList<CatalogCandidate>? considered);

    /// <summary>A song title a source saw (a station's stream title) and how it was parsed.</summary>
    void Song(string station, string raw, string? parsedArtist, string parsedTitle,
        string confidence, IReadOnlyList<TitleCandidate>? candidates, string modelMode, bool modelWillRun);

    /// <summary>What the title model made of a raw title, and how long it took.</summary>
    void Model(string raw, long latencyMs, IReadOnlyList<TitleCandidate> extracted, bool applied);

    private sealed class OffTrace : IMetadataTrace
    {
        public bool Enabled => false;
        public void ProviderSearch(string providerId, string query, IReadOnlyList<CatalogCandidate>? considered) { }
        public void Song(string station, string raw, string? parsedArtist, string parsedTitle,
            string confidence, IReadOnlyList<TitleCandidate>? candidates, string modelMode, bool modelWillRun)
        { }
        public void Model(string raw, long latencyMs, IReadOnlyList<TitleCandidate> extracted, bool applied) { }
    }
}

/// <summary>One catalog result a provider weighed, with the score
/// <see cref="SearchTerms.MatchScore"/> gave it (null = rejected by the guard). Recorded so a
/// diagnostics line can be replayed offline: feed the lookup's title and artist and each candidate
/// back through MatchScore to test a scoring change without the network.</summary>
public readonly record struct CatalogCandidate(string? Title, string? Artist, string? Album, double? Score);
