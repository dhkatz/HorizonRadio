namespace HorizonRadio.UI.Tools;

/// <summary>
/// Tool-kind identifiers. Sources reference these via
/// <see cref="HorizonRadio.Core.Sources.Config.ToolField.ToolKind"/>;
/// the UI's <see cref="ToolRegistry"/> and the per-kind
/// <see cref="IToolInstaller"/> map keys to install state and downloaders.
///
/// Lowercase, hyphen-separated strings. Don't rename loosely — the
/// strings appear in serialized factory schemas referenced from Core.
/// </summary>
public static class ToolKind
{
    public const string YtDlp = "yt-dlp";
    public const string Ffmpeg = "ffmpeg";
    public const string Librespot = "librespot";
}
