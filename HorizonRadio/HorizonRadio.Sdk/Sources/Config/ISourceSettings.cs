namespace HorizonRadio.Sdk.Sources.Config;

/// <summary>
/// The settings the user saved for a source on the Sources page. The host passes them to
/// <c>CreatePlayer</c>; a source reads them here for work outside a player (search, fetching a
/// recording), so a change applies without a restart.
/// </summary>
public interface ISourceSettings
{
    /// <summary>The saved settings of the source with id <paramref name="sourceId"/> and settings
    /// <paramref name="schema"/>, read now (the schema's defaults where unset). For code that needs
    /// a source's settings without the source itself (a service the source is built from).</summary>
    ConfigValues Load(string sourceId, IReadOnlyList<ConfigField> schema);

    /// <summary><paramref name="source"/>'s saved settings, read now (its schema's defaults where unset).</summary>
    ConfigValues Load(IAudioSourceFactory source)
    {
        ArgumentNullException.ThrowIfNull(source);
        return Load(source.Id, source.Schema);
    }
}
