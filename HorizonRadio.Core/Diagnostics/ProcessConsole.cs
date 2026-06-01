using System;
using System.Collections.Generic;

namespace HorizonRadio.Core.Diagnostics;

/// <summary>One captured line of subprocess output.</summary>
/// <param name="TimestampUtc">When the line was captured.</param>
/// <param name="Tool">Logical tool name, e.g. "librespot", "ffmpeg", "yt-dlp".</param>
/// <param name="Text">The raw line, sans trailing newline.</param>
public readonly record struct ConsoleLine(DateTime TimestampUtc, string Tool, string Text);

/// <summary>
/// Process-wide capture point for the stdout/stderr of the external
/// tools we spawn (librespot, ffmpeg, yt-dlp, …). Sources and installers
/// scattered across Core push lines here; the UI's Console tab subscribes
/// to <see cref="LineAppended"/> and seeds its backlog from
/// <see cref="Snapshot"/>.
///
/// Deliberately static: the producers are created deep inside source
/// factories that don't take a logging dependency, and the existing code
/// already logs via static <c>Debug.WriteLine</c> helpers — this mirrors
/// that shape while making the output user-visible. A bounded ring buffer
/// keeps memory flat during long sessions.
/// </summary>
public static class ProcessConsole
{
    /// <summary>Max retained lines. Oldest are dropped past this.</summary>
    public const int Capacity = 5000;

    private static readonly object Gate = new();
    private static readonly Queue<ConsoleLine> Buffer = new(Capacity);

    /// <summary>
    /// Raised for every appended line. Fires on whatever thread produced
    /// the output (typically a background stderr-drain task), so handlers
    /// must marshal to their own thread before touching UI state.
    /// </summary>
    public static event Action<ConsoleLine>? LineAppended;

    /// <summary>Append a single line. Empty/null text is ignored.</summary>
    public static void Append(string tool, string text)
    {
        if (string.IsNullOrEmpty(text)) return;

        var line = new ConsoleLine(DateTime.UtcNow, tool, text);
        lock (Gate)
        {
            if (Buffer.Count >= Capacity) Buffer.Dequeue();
            Buffer.Enqueue(line);
        }

        LineAppended?.Invoke(line);
    }

    /// <summary>
    /// Append a block of text, splitting on newlines. Convenience for the
    /// capture-to-end callers (e.g. yt-dlp) that hold a whole stderr dump
    /// rather than a per-line stream.
    /// </summary>
    public static void AppendBlock(string tool, string? block)
    {
        if (string.IsNullOrEmpty(block)) return;
        foreach (var raw in block.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.Length > 0) Append(tool, line);
        }
    }

    /// <summary>Point-in-time copy of the retained backlog, oldest first.</summary>
    public static IReadOnlyList<ConsoleLine> Snapshot()
    {
        lock (Gate) return Buffer.ToArray();
    }

    /// <summary>Drop the retained backlog. Does not raise events.</summary>
    public static void Clear()
    {
        lock (Gate) Buffer.Clear();
    }
}
