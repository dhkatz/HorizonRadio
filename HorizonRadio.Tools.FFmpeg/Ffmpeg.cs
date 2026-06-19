using System.Collections.Generic;
using System.Globalization;
using HorizonRadio.Core.Audio;

namespace HorizonRadio.Tools.FFmpeg;

/// <summary>
/// The ffmpeg CLI contract: builds the command lines that decode an input into the canonical
/// s16/44.1k/stereo PCM the engine consumes (written to <c>pipe:1</c>). Centralizes the
/// ffmpeg-specific flags so the sources don't each carry a copy — the YouTube source decodes a
/// (flaky) network URL; the internet-radio source strips ICY metadata then pipes the clean stream
/// into ffmpeg's stdin. The actual process is run through the generic Core
/// <c>SubprocessPcmSource</c> — ffmpeg is just the executable plus these args.
///
/// Mirrors the librespot tool plugin's <c>Librespot.BuildArgs</c>: the tool owns its own CLI
/// contract, the host owns the generic subprocess runner.
/// </summary>
public static class Ffmpeg
{
    /// <summary>
    /// Decode a network URL to canonical PCM. Includes reconnect flags for flaky CDN streams and
    /// optional EBU R128 loudness normalisation. Used by the YouTube source.
    /// </summary>
    public static string[] BuildUrlDecodeArgs(string url, bool normalise)
    {
        var args = new List<string>
        {
            "-hide_banner",
            "-loglevel", "error",
            "-reconnect", "1",
            "-reconnect_streamed", "1",
            "-reconnect_delay_max", "5",
            "-i", url,
        };
        AppendPcmOutput(args, normalise);
        return args.ToArray();
    }

    /// <summary>
    /// Decode audio fed into ffmpeg's stdin (<c>pipe:0</c>) to canonical PCM. Used by the
    /// internet-radio source, which pipes its ICY-stripped stream in.
    /// </summary>
    public static string[] BuildStdinDecodeArgs()
    {
        var args = new List<string>
        {
            "-hide_banner",
            "-loglevel", "error",
            "-i", "pipe:0",
        };
        AppendPcmOutput(args, normalise: false);
        return args.ToArray();
    }

    // Drop video, emit raw s16-LE PCM at the canonical channels/rate to stdout (pipe:1), with
    // optional loudnorm. This is the shared tail of every decode command.
    private static void AppendPcmOutput(List<string> args, bool normalise)
    {
        args.Add("-vn");
        args.Add("-f");
        args.Add("s16le");
        args.Add("-ac");
        args.Add(AudioFormat.Channels.ToString(CultureInfo.InvariantCulture));
        args.Add("-ar");
        args.Add(AudioFormat.SampleRate.ToString(CultureInfo.InvariantCulture));
        if (normalise)
        {
            args.Add("-af");
            args.Add("loudnorm=I=-16:TP=-1.5:LRA=11");
        }
        args.Add("pipe:1");
    }
}
