using HorizonRadio.Core.Settings;

namespace HorizonRadio.Core.Lyrics;

/// <summary>How lyrics show on Now Playing.</summary>
public static class LyricsSettings
{
    /// <summary>Synced lyrics fill in as they're sung (word by word where they're word-timed); off
    /// highlights the whole line at once.</summary>
    public static readonly Setting<bool> Karaoke = new("lyrics.karaoke", SettingScope.Synced, true);

    /// <summary>The lyrics version last picked ("Romaji", "English"), shown whenever a song has it;
    /// null is the original.</summary>
    public static readonly Setting<string?> Version = new("lyrics.version", SettingScope.Synced, null);

    /// <summary>How much earlier radio lyrics show than the station's title change suggests, in
    /// seconds (negative: later).</summary>
    public static readonly Setting<double> RadioOffset = new("lyrics.radioOffset", SettingScope.Synced, 0.0);

    /// <summary>Find a radio song's place by lining the station's audio up against a recording of
    /// the song (downloaded once per song), so its synced lyrics follow along.</summary>
    public static readonly Setting<bool> AudioSync = new("lyrics.audioSync", SettingScope.Synced, true);
}
