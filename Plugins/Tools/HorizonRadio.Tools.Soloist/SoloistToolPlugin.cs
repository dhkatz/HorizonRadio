using HorizonRadio.Core.Tools;

namespace HorizonRadio.Tools.Soloist;

/// <summary>Spotify Soloist tool plugin: Spotify's own Connect client, for the tracks librespot is
/// refused. Tracks Spotify's latest build (builds expire after 90 days).</summary>
public sealed class SoloistToolPlugin : IToolPlugin
{
    public ToolDescriptor Descriptor { get; } = new(ToolKind.Soloist, Soloist.ExeName);
    public IToolInstaller Installer { get; } = new SoloistInstaller();
}
