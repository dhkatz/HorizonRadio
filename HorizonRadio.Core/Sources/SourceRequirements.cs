using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using HorizonRadio.Core.Sources.Config;

namespace HorizonRadio.Core.Sources;

/// <summary>
/// Raised when a source can't be launched because one or more of the
/// external tools it depends on (yt-dlp, ffmpeg, librespot) aren't
/// available. Carries the tool kinds so the UI can react specifically —
/// the message is already user-facing and points at the Tools tab.
/// </summary>
public sealed class MissingToolException : Exception
{
    public IReadOnlyList<string> ToolKinds { get; }

    public MissingToolException(string message, IReadOnlyList<string> toolKinds)
        : base(message) => ToolKinds = toolKinds;
}

/// <summary>
/// Pre-flight checks shared by every source-launch path. Lives next to
/// the runner so the picker, profile switcher, game events, and input
/// bindings all get the same answer to "can this source even run?".
/// </summary>
public static class SourceRequirements
{
    /// <summary>
    /// The distinct tool kinds the source needs but can't resolve — the
    /// configured path is empty or points at a file that no longer exists.
    /// In schema order. Empty for sources with no <see cref="ToolField"/>.
    /// </summary>
    public static IReadOnlyList<string> MissingToolKinds(
        IAudioSourceFactory factory, ConfigValues values)
    {
        var missing = new List<string>();
        foreach (var field in factory.Schema)
        {
            if (field is not ToolField tool) continue;
            var path = values.GetString(tool.Key);
            if ((string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                && !missing.Contains(tool.ToolKind))
                missing.Add(tool.ToolKind);
        }
        return missing;
    }

    /// <summary>
    /// Throw a friendly <see cref="MissingToolException"/> if the source
    /// can't run for lack of its tools. Called at the single launch
    /// chokepoint (<c>SourceRunner.StartAsync</c>) so every entry point
    /// reports the same thing and the user is pointed at the Tools tab.
    /// </summary>
    public static void EnsureToolsAvailable(IAudioSourceFactory factory, ConfigValues values)
    {
        var missing = MissingToolKinds(factory, values);
        if (missing.Count == 0) return;

        var names = HumanJoin(missing);
        var plural = missing.Count > 1;
        throw new MissingToolException(
            $"{factory.DisplayName} needs {names}, but {(plural ? "they couldn't" : "it couldn't")} be found. " +
            $"Install {(plural ? "them" : "it")} from the Tools tab, or set the path in the source's settings.",
            missing);
    }

    /// <summary>"a", "a and b", or "a, b, and c".</summary>
    private static string HumanJoin(IReadOnlyList<string> items) => items.Count switch
    {
        1 => items[0],
        2 => $"{items[0]} and {items[1]}",
        _ => string.Join(", ", items.Take(items.Count - 1)) + ", and " + items[^1],
    };
}
