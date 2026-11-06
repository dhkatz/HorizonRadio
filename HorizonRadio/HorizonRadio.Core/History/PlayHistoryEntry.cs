using System;
using System.Collections.Generic;
using HorizonRadio.Core.Models;

namespace HorizonRadio.Core.History;

/// <summary>How confidently we know what a played song actually is — drives the
/// "couldn't identify this" warning in the history list.</summary>
public enum HistoryMatchState
{
    /// <summary>Not evaluated yet (a freeform song whose catalog lookup hasn't finished, or
    /// ran with no providers configured). No warning shown — we make no claim either way.</summary>
    Unknown,

    /// <summary>Confidently identified: a real source identity (Spotify/YouTube/local) or a
    /// freeform (radio) song a catalog confirmed. Directly comparable, no warning.</summary>
    Matched,

    /// <summary>A freeform (radio) title no catalog could confirm — the metadata shown is the
    /// stream's own best guess. Flagged so the user can report a metadata gap.</summary>
    Unmatched,
}

/// <summary>One alternative interpretation of a freeform stream title that was carried on the
/// played track — persisted only to enrich a "report this" bug draft (the parser's ambiguity is
/// the useful repro for a mis-identification).</summary>
public sealed record HistoryCandidate(string? Artist, string Title);

/// <summary>One way to play a history entry again: a queueable source + a
/// <see cref="Sources.ContentRef"/>-ready locator. A song matched on multiple services keeps one
/// per service, so the user can pick where to play (the same multi-source model as search).</summary>
/// <param name="SourceId">Queueable source id (e.g. "youtube", "spotify-driven").</param>
/// <param name="SourceDisplay">Human label for the picker ("YouTube", "Spotify").</param>
/// <param name="Locator">The locator to enqueue against the source.</param>
public sealed record ReplaySource(string SourceId, string SourceDisplay, string Locator);

/// <summary>
/// One song the app played, as remembered for the History tab. Holds the displayed identity
/// (what the user saw), a timestamp, and — crucially — enough to play it again:
///
///   • <see cref="Sources"/> — one or more queueable (source, locator) pairs. A song played from a
///     re-addressable source (Spotify/YouTube/local) gets its origin as the single source at record
///     time. A freeform song (radio) has no playable origin, so its sources are filled in lazily by
///     searching the services for the catalog-canonical name (far better matches than the raw stream
///     title); a song found on several services keeps one per service for a play-from picker.
///
/// Album art is deliberately NOT stored: the list re-enriches art on demand (cheap, cached),
/// keeping the on-disk history small and the art fresh as catalogs improve.
/// Identity fields are immutable; <see cref="Sources"/> and <see cref="MatchState"/> are filled in
/// once the lookup resolves (and may improve on a later view as catalogs change).
/// </summary>
public sealed class PlayHistoryEntry
{
    public required string Id { get; init; }
    public required DateTimeOffset PlayedAt { get; init; }

    public required string Title { get; init; }
    public required string Artist { get; init; }
    public string? Album { get; init; }
    public int? Year { get; init; }

    /// <summary>Origin source id as played (e.g. "radio", "youtube", "spotify").</summary>
    public required string SourceId { get; init; }

    /// <summary>Human label for the origin source ("Internet Radio", "YouTube", …).</summary>
    public required string SourceDisplay { get; init; }

    /// <summary>Ways to play this song again (one per service). Empty until resolved for a freeform
    /// song; settable because radio sources are filled in lazily after a search. May replay through
    /// a different source than <see cref="SourceId"/> — a Spotify song played via the zero-config
    /// receiver replays through the driven "spotify-driven" factory.</summary>
    public IReadOnlyList<ReplaySource> Sources { get; set; } = [];

    /// <summary>Alternative title parses carried from a freeform source, for the report draft.</summary>
    public IReadOnlyList<HistoryCandidate> Candidates { get; init; } = [];

    public HistoryMatchState MatchState { get; set; } = HistoryMatchState.Unknown;

    /// <summary>True when at least one playable source is known (one-click replay, no re-search).</summary>
    public bool IsReplayable => Sources.Count > 0;
}
