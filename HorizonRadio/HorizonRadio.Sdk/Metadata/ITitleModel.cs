namespace HorizonRadio.Sdk.Metadata;

/// <summary>
/// The optional local title model (Tools tab), which splits a messy stream title into artist and
/// title. Installing, updating or removing it replaces the extractor, so read it each time it's
/// needed rather than keeping it.
/// </summary>
public interface ITitleModel
{
    /// <summary>The model as installed now, or null when it isn't.</summary>
    ITitleExtractor? Extractor { get; }

    /// <summary>When to ask it, as the user set it (Metadata page).</summary>
    TitleModelMode Mode { get; }
}
