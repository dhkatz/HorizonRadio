using HorizonRadio.Core.Tools;

namespace HorizonRadio.Tools.YtDlp;

/// <summary>yt-dlp tool plugin — resolves URLs into direct audio streams. Tracks upstream's latest.</summary>
public sealed class YtDlpToolPlugin : IToolPlugin
{
    public ToolDescriptor Descriptor { get; } = new(ToolKind.YtDlp, "yt-dlp.exe");
    public IToolInstaller Installer { get; } = new YtDlpInstaller();
}
