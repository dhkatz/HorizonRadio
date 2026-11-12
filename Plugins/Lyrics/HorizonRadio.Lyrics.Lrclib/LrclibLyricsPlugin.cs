using HorizonRadio.Sdk;
using HorizonRadio.Sdk.Lyrics;

namespace HorizonRadio.Lyrics.Lrclib;

/// <summary>The LRCLIB lyrics plugin. After Unison: LRCLIB is much larger, but its lyrics are
/// timed per line rather than per word.</summary>
public sealed class LrclibLyricsPlugin : ILyricsPlugin
{
    public string Id => "lrclib";
    public string DisplayName => "LRCLIB";
    public int SortOrder => 20;

    public ILyricsProvider CreateLyricsProvider(IPluginContext context) => new LrclibProvider();
}
