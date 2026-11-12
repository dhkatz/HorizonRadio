namespace HorizonRadio.Sdk.Sources.Config;

/// <summary>
/// The settings the user saved for a source on the Sources page. The host passes them to
/// <c>CreatePlayer</c>; a source reads them here for work outside a player (search, fetching a
/// recording), so a change applies without a restart.
/// </summary>
public interface ISourceSettings
{
    /// <summary><paramref name="source"/>'s saved settings, read now (its schema's defaults where unset).</summary>
    ConfigValues Load(IAudioSourceFactory source);
}
