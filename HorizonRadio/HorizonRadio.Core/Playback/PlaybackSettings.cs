using HorizonRadio.Core.Settings;

namespace HorizonRadio.Core.Playback;

/// <summary>The player's own settings.</summary>
public static class PlaybackSettings
{
    /// <summary>Shuffle, for whichever channel plays (the player's queue and every mix).</summary>
    public static readonly Setting<bool> Shuffle = new("playback.shuffle", SettingScope.Synced, false);
}
