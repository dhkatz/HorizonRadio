using HorizonRadio.Sdk.Tools;

namespace HorizonRadio.Tools.FFmpeg;

/// <summary>ffmpeg tool plugin — decodes resolved audio streams to PCM. Tracks upstream's latest.</summary>
public sealed class FfmpegToolPlugin : IToolPlugin
{
    public int SortOrder => 20;

    public ToolDescriptor Descriptor { get; } = new(ToolKind.Ffmpeg, "ffmpeg.exe");
    public IToolInstaller Installer { get; } = new FfmpegInstaller();
}
