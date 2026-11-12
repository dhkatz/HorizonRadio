using System.Diagnostics;
using HorizonRadio.Core.Diagnostics;
using HorizonRadio.Sdk.Sources;
using HorizonRadio.Tools.FFmpeg;
using HorizonRadio.Tools.YtDlp;

namespace HorizonRadio.Core.Sources.YouTube;

/// <summary>
/// Fetches a video's audio whole, as fast as it downloads, for analysis: yt-dlp downloads the best
/// audio to its stdout, which is piped into ffmpeg, which decodes it to mono PCM at the rate asked
/// for. Works for whatever yt-dlp supports (YouTube, Niconico, Bilibili, SoundCloud, …).
/// </summary>
internal static class YouTubeReferenceAudio
{
    public static async Task<ReferenceAudio?> FetchAsync(
        string ytDlp, string ffmpeg, string url, int sampleRate, TimeSpan maxLength, CancellationToken ct)
    {
        var started = Stopwatch.StartNew();
        using var download = Start(ytDlp, YtDlpClient.BuildAudioToStdoutArgs(url, maxLength), stdin: false);
        using var decode = Start(ffmpeg, Ffmpeg.BuildStdinAnalysisArgs(sampleRate), stdin: true);
        using var kill = ct.Register(() =>
        {
            Kill(download);
            Kill(decode);
        });

        var downloadErrors = download.StandardError.ReadToEndAsync(CancellationToken.None);
        var decodeErrors = decode.StandardError.ReadToEndAsync(CancellationToken.None);

        // Download → decoder, closing the decoder's input when the download ends so it finishes.
        var pipe = Task.Run(async () =>
        {
            try { await download.StandardOutput.BaseStream.CopyToAsync(decode.StandardInput.BaseStream, ct).ConfigureAwait(false); }
            catch (IOException) { /* the decoder stopped reading: it failed, or it was killed */ }
            finally
            {
                try { decode.StandardInput.Close(); } catch (IOException) { }
            }
        }, CancellationToken.None);

        // Read the decoded audio, giving up on anything longer than a song (the length filter can't
        // see a video whose length the site doesn't report).
        long maxBytes = (long)(maxLength.TotalSeconds * sampleRate) * sizeof(short);
        using var pcm = new MemoryStream();
        var buffer = new byte[64 * 1024];
        var output = decode.StandardOutput.BaseStream;
        int read;
        while ((read = await output.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
        {
            pcm.Write(buffer, 0, read);
            if (pcm.Length > maxBytes)
            {
                Kill(download);
                Kill(decode);
                ProcessConsole.Append("sync", $"skipped {url}: longer than {maxLength.TotalMinutes:0} min");
                return null;
            }
        }

        await pipe.ConfigureAwait(false);
        await download.WaitForExitAsync(ct).ConfigureAwait(false);
        await decode.WaitForExitAsync(ct).ConfigureAwait(false);
        ProcessConsole.AppendBlock("yt-dlp", await downloadErrors.ConfigureAwait(false));
        ProcessConsole.AppendBlock("ffmpeg", await decodeErrors.ConfigureAwait(false));

        if (download.ExitCode != 0)
            throw new InvalidOperationException($"yt-dlp exited {download.ExitCode} downloading {url}");
        if (pcm.Length < sizeof(short)) return null; // skipped by the length filter

        var samples = new short[pcm.Length / sizeof(short)];
        Buffer.BlockCopy(pcm.GetBuffer(), 0, samples, 0, samples.Length * sizeof(short));
        ProcessConsole.Append("sync",
            $"fetched {url}: {samples.Length / (double)sampleRate:0.0} s of audio in {started.Elapsed.TotalSeconds:0.0} s");
        return new ReferenceAudio(url, samples, sampleRate);
    }

    private static Process Start(string exe, string[] args, bool stdin)
    {
        var psi = new ProcessStartInfo
        {
            FileName = exe,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = stdin,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardErrorEncoding = System.Text.Encoding.UTF8,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        var process = Process.Start(psi) ?? throw new InvalidOperationException($"failed to start {exe}");
        ChildProcesses.TieToApp(process);
        return process;
    }

    private static void Kill(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        catch (InvalidOperationException) { /* already gone */ }
        catch (System.ComponentModel.Win32Exception) { /* can't be killed; it's exiting */ }
    }
}
