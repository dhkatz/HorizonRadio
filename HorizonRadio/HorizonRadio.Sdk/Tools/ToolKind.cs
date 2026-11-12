namespace HorizonRadio.Sdk.Tools;

/// <summary>
/// Tool-kind identifiers shared across the Core/UI boundary. Source
/// factories tag their <see cref="HorizonRadio.Sdk.Sources.Config.ToolField"/>
/// entries with one of these; the UI's tool registry and per-kind
/// installers key install state and downloaders off the same strings.
///
/// Lowercase, hyphen-separated. Don't rename loosely — the strings appear
/// in serialized factory schemas and on-disk tool paths
/// (a tool installs into a folder named for its kind).
/// </summary>
public static class ToolKind
{
    public const string YtDlp = "yt-dlp";
    public const string Ffmpeg = "ffmpeg";
    public const string Librespot = "librespot";

    /// <summary>Spotify Soloist, Spotify's own Connect client: plays the Spotify tracks librespot is
    /// refused, through the Spotify plugin's audio tap.</summary>
    public const string Soloist = "soloist";

    /// <summary>The optional local title-extraction model — a single GGUF file (not an exe),
    /// downloaded via the Tools tab. Its descriptor marks it as data (<see cref="ToolDescriptor.IsData"/>).</summary>
    public const string TitleModel = "title-model";
}
