using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;
using Avalonia.Media;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HorizonRadio.Core.Diagnostics;

namespace HorizonRadio.UI.ViewModels;

/// <summary>
/// Backs the Console tab: a live, filterable view of the output from the
/// external tools we spawn (librespot, ffmpeg, yt-dlp, installers).
///
/// Lines arrive on background threads via <see cref="ProcessConsole"/>;
/// we enqueue them lock-free and flush to the bound collection on a UI
/// timer so a chatty tool can't thrash the render thread one line at a
/// time. The full backlog is kept in <c>_all</c>; <see cref="Lines"/> is
/// the filtered subset actually shown.
/// </summary>
public sealed partial class ConsoleViewModel : ViewModelBase, IDisposable
{
    public const string AllTools = "All";

    private const int MaxLines = ProcessConsole.Capacity;

    private readonly List<ConsoleLineViewModel> _all = new();
    private readonly ConcurrentQueue<ConsoleLine> _pending = new();
    private readonly DispatcherTimer _flushTimer;

    /// <summary>The currently displayed (filtered) lines.</summary>
    public ObservableCollection<ConsoleLineViewModel> Lines { get; } = new();

    /// <summary>Tool names for the filter dropdown; "All" plus whatever
    /// tools have actually produced output this session.</summary>
    public ObservableCollection<string> Tools { get; } = new() { AllTools };

    [ObservableProperty] private string selectedTool = AllTools;
    [ObservableProperty] private bool autoScroll = true;

    /// <summary>Raised after a flush appends lines, so the view can scroll
    /// to the end when <see cref="AutoScroll"/> is on.</summary>
    public event Action? LinesAppended;

    public ConsoleViewModel()
    {
        foreach (var line in ProcessConsole.Snapshot())
            Ingest(line);

        ProcessConsole.LineAppended += OnLineAppended;

        _flushTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        _flushTimer.Tick += (_, _) => Flush();
        _flushTimer.Start();
    }

    private void OnLineAppended(ConsoleLine line) => _pending.Enqueue(line);

    private void Flush()
    {
        var appended = false;
        while (_pending.TryDequeue(out var line))
        {
            Ingest(line);
            appended = true;
        }

        if (appended && AutoScroll)
            LinesAppended?.Invoke();
    }

    /// <summary>Add one line to the master list (+ filtered view if it
    /// matches), maintaining the bounded line cap.</summary>
    private void Ingest(ConsoleLine line)
    {
        var vm = new ConsoleLineViewModel(line);

        _all.Add(vm);
        if (_all.Count > MaxLines) _all.RemoveAt(0);

        if (!Tools.Contains(vm.Tool)) Tools.Add(vm.Tool);

        if (Matches(vm))
        {
            Lines.Add(vm);
            while (Lines.Count > MaxLines) Lines.RemoveAt(0);
        }
    }

    private bool Matches(ConsoleLineViewModel vm) =>
        SelectedTool == AllTools || vm.Tool == SelectedTool;

    partial void OnSelectedToolChanged(string value) => Rebuild();

    private void Rebuild()
    {
        Lines.Clear();
        foreach (var vm in _all)
            if (Matches(vm))
                Lines.Add(vm);
        if (AutoScroll) LinesAppended?.Invoke();
    }

    [RelayCommand]
    private void Clear()
    {
        ProcessConsole.Clear();
        _all.Clear();
        Lines.Clear();
    }

    /// <summary>The visible lines as a single copyable block.</summary>
    public string BuildCopyText()
    {
        var sb = new StringBuilder();
        foreach (var line in Lines)
            sb.AppendLine(line.Display);
        return sb.ToString();
    }

    public void Dispose()
    {
        ProcessConsole.LineAppended -= OnLineAppended;
        _flushTimer.Stop();
    }
}

/// <summary>One rendered console line: timestamp + tool tag + text.</summary>
public sealed class ConsoleLineViewModel
{
    public ConsoleLineViewModel(ConsoleLine line)
    {
        Tool = line.Tool;
        Text = line.Text;
        Time = line.TimestampUtc.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture);
        ToolBrush = BrushFor(line.Tool);
    }

    public string Tool { get; }
    public string Text { get; }
    public string Time { get; }
    public IBrush ToolBrush { get; }

    /// <summary>Plain-text form used for clipboard copy.</summary>
    public string Display => $"{Time}  {Tool,-9} {Text}";

    // Stable, distinct-ish colors for the tools we know about; the rest
    // fall back to a neutral gray. Brushes are shared/immutable so we're
    // not allocating one per line.
    private static readonly IBrush Librespot = new SolidColorBrush(Color.Parse("#1db954"));
    private static readonly IBrush Ffmpeg = new SolidColorBrush(Color.Parse("#5b8def"));
    private static readonly IBrush YtDlp = new SolidColorBrush(Color.Parse("#ef4444"));
    private static readonly IBrush Other = new SolidColorBrush(Color.Parse("#9ca3af"));

    private static IBrush BrushFor(string tool) => tool switch
    {
        "librespot" => Librespot,
        "ffmpeg" => Ffmpeg,
        "yt-dlp" => YtDlp,
        _ => Other,
    };
}
