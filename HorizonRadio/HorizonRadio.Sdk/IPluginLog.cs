namespace HorizonRadio.Sdk;

/// <summary>
/// Where a plugin writes what it's doing. The host shows <see cref="Write"/> lines in the Console
/// tab and keeps both kinds in the session's log file.
/// </summary>
public interface IPluginLog
{
    /// <summary>Lines for the Console tab, under <paramref name="tag"/>: usually the tool that said
    /// them ("yt-dlp", "ffmpeg") or the activity ("search", "sync"). <paramref name="text"/> may
    /// hold several lines (a tool's whole stderr); empty lines are dropped.</summary>
    void Write(string tag, string text);

    /// <summary>A line for the log file only: detail for whoever reads the log after a failure,
    /// not for the Console tab. Written as <c>[hzn-&lt;tag&gt;] text</c>.</summary>
    void Trace(string tag, string text);
}
