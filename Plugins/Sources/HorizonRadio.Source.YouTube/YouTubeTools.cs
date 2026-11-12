using HorizonRadio.Sdk;
using HorizonRadio.Tools.YtDlp;

namespace HorizonRadio.Source.YouTube;

/// <summary>
/// What the YouTube source runs, found for one use (a player, a search, a fetch): yt-dlp, through
/// its client, and ffmpeg, at the paths the source's settings and the Tools tab resolve to, with
/// the host's process launching and log.
/// </summary>
public sealed record YouTubeTools(
    YtDlpClient YtDlp, string YtDlpPath, string FfmpegPath, IProcesses Processes, IPluginLog Log);
