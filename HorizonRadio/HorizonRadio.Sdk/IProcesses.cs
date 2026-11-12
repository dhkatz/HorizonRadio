using System.Diagnostics;

namespace HorizonRadio.Sdk;

/// <summary>
/// Starts the tool processes a plugin runs (yt-dlp, ffmpeg, librespot). A process started here
/// belongs to the app: it ends when the app does, even if the app crashes, so a stray tool can't
/// outlive it (on Windows, through a kill-on-close job object the process joins as it starts).
/// </summary>
public interface IProcesses
{
    /// <summary>Starts <paramref name="info"/>. Throws when the process can't be started; the
    /// caller owns the returned <see cref="Process"/> (and disposes it).</summary>
    Process Start(ProcessStartInfo info);
}
