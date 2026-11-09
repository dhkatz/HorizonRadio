using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;

namespace HorizonRadio.Core.Diagnostics;

/// <summary>
/// Writes everything the Console tab shows (<see cref="ProcessConsole"/>) plus the app's own
/// <c>Trace</c> lines (the queue engine, sources) to a file per session under
/// <c>%LOCALAPPDATA%\HorizonRadio\logs</c>, so a failure can be read after the fact. The newest
/// <see cref="KeepFiles"/> sessions are kept.
/// </summary>
public static class LogFile
{
    /// <summary>How many session logs to keep, newest first.</summary>
    public const int KeepFiles = 10;

    private const string Prefix = "horizon-radio-";

    private static readonly object Gate = new();
    private static StreamWriter? _writer;
    private static Listener? _listener;

    public static string DefaultDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HorizonRadio", "logs");

    /// <summary>The file this session writes to, or null when not started.</summary>
    public static string? CurrentPath { get; private set; }

    /// <summary>Open this session's log, copy in what the Console already holds, and capture from
    /// here on. Failing to open the file only means no log; it never stops the app.</summary>
    public static void Start(string? directory = null)
    {
        lock (Gate)
        {
            if (_writer != null) return;
            var dir = directory ?? DefaultDirectory;
            try
            {
                Directory.CreateDirectory(dir);
                Prune(dir, KeepFiles - 1);
                var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
                var path = Path.Combine(dir, $"{Prefix}{stamp}.log");
                _writer = new StreamWriter(new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite))
                {
                    AutoFlush = true,
                };
                CurrentPath = path;
                foreach (var line in ProcessConsole.Snapshot()) Write(line.TimestampUtc, line.Tool, line.Text);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _writer = null;
                CurrentPath = null;
                return;
            }
        }

        ProcessConsole.LineAppended += OnLine;
        _listener = new Listener();
        Trace.Listeners.Add(_listener);
    }

    /// <summary>Stop capturing and close the file.</summary>
    public static void Stop()
    {
        ProcessConsole.LineAppended -= OnLine;
        if (_listener != null) Trace.Listeners.Remove(_listener);
        _listener = null;
        lock (Gate)
        {
            try { _writer?.Dispose(); } catch (IOException) { }
            _writer = null;
            CurrentPath = null;
        }
    }

    private static void OnLine(ConsoleLine line)
    {
        lock (Gate) Write(line.TimestampUtc, line.Tool, line.Text);
    }

    // Under Gate.
    private static void Write(DateTime utc, string tool, string text)
    {
        if (_writer == null) return;
        try
        {
            _writer.Write(utc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture));
            _writer.Write(" [");
            _writer.Write(tool);
            _writer.Write("] ");
            _writer.WriteLine(text);
        }
        catch (IOException) { }
        catch (ObjectDisposedException) { }
    }

    // Keep the newest `keep` session logs (the one about to open makes it KeepFiles).
    private static void Prune(string dir, int keep)
    {
        var old = new DirectoryInfo(dir).GetFiles($"{Prefix}*.log")
            .OrderByDescending(f => f.Name, StringComparer.Ordinal)
            .Skip(keep);
        foreach (var file in old)
        {
            try { file.Delete(); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    // A line may arrive in pieces (Write, then WriteLine), so pieces are joined per thread until
    // the line ends.
    [ThreadStatic] private static System.Text.StringBuilder? _pending;

    private static void Capture(string? message, bool endsLine)
    {
        var pending = _pending ??= new System.Text.StringBuilder();
        pending.Append(message);
        if (!endsLine) return;
        var text = pending.ToString().TrimEnd('\r', '\n');
        pending.Clear();
        if (text.Length == 0) return;
        lock (Gate) Write(DateTime.UtcNow, "app", text);
    }

    // Trace.Write/WriteLine (the app's log helpers; Debug.WriteLine doesn't reach listeners).
    private sealed class Listener : TraceListener
    {
        public override void Write(string? message) => Capture(message, endsLine: false);
        public override void WriteLine(string? message) => Capture(message, endsLine: true);
    }
}
