namespace HorizonRadio.Core.Tools;

/// <summary>
/// Tool-kind identifiers shared across the Core/UI boundary. Source
/// factories tag their <see cref="HorizonRadio.Core.Sources.Config.ToolField"/>
/// entries with one of these; the UI's tool registry and per-kind
/// installers key install state and downloaders off the same strings.
///
/// Lowercase, hyphen-separated. Don't rename loosely — the strings appear
/// in serialized factory schemas and on-disk tool paths
/// (see <see cref="ToolsPaths"/>).
/// </summary>
public static class ToolKind
{
    public const string YtDlp = "yt-dlp";
    public const string Ffmpeg = "ffmpeg";
    public const string Librespot = "librespot";
}
