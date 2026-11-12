using HorizonRadio.Sdk;
using HorizonRadio.Sdk.Lyrics;

namespace HorizonRadio.Lyrics.Unison;

/// <summary>The Unison lyrics plugin. First: its lyrics are often synced word by word.</summary>
public sealed class UnisonLyricsPlugin : ILyricsPlugin
{
    public string Id => "unison";
    public string DisplayName => "Unison";
    public int SortOrder => 10;

    public ILyricsProvider CreateLyricsProvider() => new UnisonProvider();
}
