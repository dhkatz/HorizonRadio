using HorizonRadio.Core.Settings;

namespace HorizonRadio.Core.Audio;

/// <summary>The master volume and local monitoring (<see cref="PreviewController"/>): about this
/// machine's speakers, so none of them sync.</summary>
public static class PreviewSettings
{
    /// <summary>Whether the playing audio also comes out of a local speaker ("test playback"),
    /// beside the game.</summary>
    public static readonly Setting<bool> Enabled = new("preview.enabled", SettingScope.Device, false);

    /// <summary>The render endpoint for local monitoring (<c>MMDevice.ID</c>); null follows the
    /// system default.</summary>
    public static readonly Setting<string?> Device = new("preview.device", SettingScope.Device, null);

    /// <summary>
    /// The master volume slider's <em>position</em>, 0..1: tapered to a gain
    /// (<see cref="VolumeTaper"/>) for both the local monitor and the game bridge's pre-amp. The
    /// default (0.75, a gain of about 0.42, -7.5 dBFS) was set by ear to sit level with FH6's own
    /// stations; the game's music fader scales ours and theirs together, so it holds at any in-game
    /// volume.
    /// </summary>
    public static readonly Setting<double> Volume = new("preview.volume", SettingScope.Device, 0.75);
}
