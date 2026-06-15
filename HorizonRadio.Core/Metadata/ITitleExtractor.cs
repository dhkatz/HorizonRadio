using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using HorizonRadio.Core.Models;

namespace HorizonRadio.Core.Metadata;

/// <summary>
/// Extracts (artist, title) interpretations from a freeform stream/track title. An optional
/// upgrade over the deterministic <see cref="TitleArtistParser"/>: a local language model can
/// split formats heuristics can't (no separators, reversed order, mixed-language). The result
/// feeds the same <see cref="Models.TitleCandidate"/> seam the resolver already validates
/// against the catalogs, so a wrong extraction can't reach the UI.
/// </summary>
public interface ITitleExtractor : System.IAsyncDisposable
{
    /// <summary>Best (artist, title) hypotheses for <paramref name="rawTitle"/>, best first.
    /// Empty when nothing usable was produced. Never throws for ordinary failures.</summary>
    Task<IReadOnlyList<TitleCandidate>> ExtractAsync(string rawTitle, CancellationToken ct);
}

/// <summary>When the title-extraction model runs.</summary>
public enum TitleModelMode
{
    /// <summary>Never run the model (deterministic parsing only).</summary>
    Off,

    /// <summary>Run only when the deterministic parse is Low/Medium confidence; the model's
    /// hypotheses are appended as fallback candidates. The conservative default.</summary>
    Escalate,

    /// <summary>Run on every title and make the model's extraction the primary interpretation,
    /// so every catalog query starts from a clean (artist, title). Best quality if the model is
    /// fast enough.</summary>
    Always,
}
