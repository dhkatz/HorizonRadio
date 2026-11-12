namespace HorizonRadio.Sdk.Sources;

/// <summary>
/// The host's audio caches: one per source, in the app's data folder (<c>cache\audio\&lt;source&gt;</c>),
/// sharing one limit on downloads at once across every source.
/// </summary>
public interface IAudioCaches
{
    /// <summary>The cache for <paramref name="sourceId"/>'s downloads; the same one every call.</summary>
    AudioCache ForSource(string sourceId);
}
