using HorizonRadio.Sdk.Metadata;

namespace HorizonRadio.Core.Metadata;

/// <summary>
/// App-level holder for the optional title-extraction model, set once at startup so the
/// parameterless, catalog-constructed sources (internet radio) can reach it without a DI seam —
/// what the host's <see cref="HorizonRadio.Sdk.Metadata.ITitleModel"/> reads, which is how plugins see it.
///
/// <see cref="Current"/> is null until a model is installed and initialized, in which case
/// sources fall back to deterministic parsing only (no behavior change). <see cref="Mode"/>
/// is the run policy from config; re-call <see cref="Initialize"/> when the model or the
/// setting changes.
/// </summary>
public static class TitleExtractorRuntime
{
    public static ITitleExtractor? Current { get; private set; }

    public static TitleModelMode Mode { get; private set; } = TitleModelMode.Escalate;

    public static void Initialize(ITitleExtractor? extractor, TitleModelMode mode)
    {
        Current = extractor;
        Mode = mode;
    }
}
