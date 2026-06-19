using HorizonRadio.Core.Tools;

namespace HorizonRadio.Tools.Librespot;

/// <summary>librespot tool plugin — Spotify Connect receiver. Pinned to a tested build.</summary>
public sealed class LibrespotToolPlugin : IToolPlugin
{
    public ToolDescriptor Descriptor { get; } = new(ToolKind.Librespot, "librespot.exe");
    public IToolInstaller Installer { get; } = new LibrespotInstaller();
}
