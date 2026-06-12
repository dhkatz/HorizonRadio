using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using HorizonRadio.Core.Events;
using HorizonRadio.Core.Sources;

namespace HorizonRadio.UI.ViewModels;

/// <summary>
/// Events tab: binds each known in-game event to an action. Persists via
/// <see cref="EventRuleStore"/> and shows a small recent-activity log fed
/// by the <see cref="EventActionExecutor"/>.
/// </summary>
public sealed partial class EventsViewModel : ViewModelBase, IDisposable
{
    private readonly EventActionExecutor? _executor;

    public ObservableCollection<EventCategoryGroup> Groups { get; } = new();
    public ObservableCollection<string> Activity { get; } = new();

    public int TelemetryPort { get; }
    public string TelemetryHint =>
        $"For richer events, enable Forza “Data Out” in-game and point it at 127.0.0.1:{TelemetryPort}.";

    // Design-time / fallback ctor.
    public EventsViewModel() : this(new EventRuleStore(), null, ForzaTelemetryListener.DefaultPort) { }

    public EventsViewModel(EventRuleStore rules, EventActionExecutor? executor, int telemetryPort)
    {
        _executor = executor;
        TelemetryPort = telemetryPort;

        var options = BuildOptions();
        foreach (var category in GameEventKinds.Catalog.GroupBy(i => i.Category))
        {
            var rows = category.Select(info => new EventRuleRow(info, options, rules)).ToList();
            Groups.Add(new EventCategoryGroup(category.Key, rows));
        }

        if (executor != null) executor.Handled += OnHandled;
    }

    private void OnHandled(GameEvent e, EventAction action)
    {
        var line = string.Create(CultureInfo.InvariantCulture,
            $"{DateTime.Now:HH:mm:ss}  {e.Kind} → {DescribeAction(action)}");
        Dispatcher.UIThread.Post(() =>
        {
            Activity.Insert(0, line);
            while (Activity.Count > 50) Activity.RemoveAt(Activity.Count - 1);
        });
    }

    private static List<EventActionOption> BuildOptions()
    {
        var list = new List<EventActionOption>
        {
            new("Do nothing", EventAction.None),
            new("Next track", new EventAction(EventActionType.NextTrack)),
            new("Previous track", new EventAction(EventActionType.PreviousTrack)),
            new("Restart track", new EventAction(EventActionType.RestartTrack)),
            new("Pause", new EventAction(EventActionType.Pause)),
            new("Resume", new EventAction(EventActionType.Resume)),
            new("Next mix", new EventAction(EventActionType.NextMix)),
            new("Previous mix", new EventAction(EventActionType.PreviousMix)),
        };
        // Only self-driven sources are directly switchable (Spotify Connect, the
        // test tone). Content sources play via mixes, so they aren't offered here.
        foreach (var f in SourceCatalog.All.Where(f => f is not IContentSourceFactory))
            list.Add(new EventActionOption($"Switch to: {f.DisplayName}",
                new EventAction(EventActionType.SwitchSource, f.Id)));
        list.Add(new EventActionOption("Duck volume (30%)",
            new EventAction(EventActionType.SetVolume, EventRuleStore.FormatVolume(0.3))));
        list.Add(new EventActionOption("Full volume (100%)",
            new EventAction(EventActionType.SetVolume, EventRuleStore.FormatVolume(1.0))));
        return list;
    }

    private static string DescribeAction(EventAction a) => a.Type switch
    {
        EventActionType.None => "(no action)",
        EventActionType.SwitchSource => $"switch to {a.Param}",
        EventActionType.NextMix => "next mix",
        EventActionType.PreviousMix => "previous mix",
        EventActionType.SetVolume => $"volume {a.Param}",
        _ => a.Type.ToString(),
    };

    public void Dispose()
    {
        if (_executor != null) _executor.Handled -= OnHandled;
    }
}

/// <summary>One row in the Events tab: an event and its chosen action.</summary>
public sealed partial class EventRuleRow : ViewModelBase
{
    private readonly EventRuleStore _rules;

    public GameEventInfo Info { get; }
    public IReadOnlyList<EventActionOption> Options { get; }
    public string DisplayName => Info.DisplayName;
    public string Description => Info.Description;

    [ObservableProperty] private EventActionOption selectedOption;

    public EventRuleRow(GameEventInfo info, IReadOnlyList<EventActionOption> options, EventRuleStore rules)
    {
        Info = info;
        Options = options;
        _rules = rules;
        // Assign the backing field directly so loading the saved choice
        // doesn't trigger OnSelectedOptionChanged (which would re-save).
        selectedOption = Match(options, rules.GetAction(info.Kind));
    }

    partial void OnSelectedOptionChanged(EventActionOption value)
    {
        _rules.SetAction(Info.Kind, value.Action);
        _rules.SaveToDisk();
    }

    private static EventActionOption Match(IReadOnlyList<EventActionOption> options, EventAction action)
    {
        foreach (var o in options)
            if (o.Action == action) return o;
        return options[0]; // "Do nothing"
    }
}

/// <summary>A selectable action in the per-event dropdown.</summary>
public sealed record EventActionOption(string Label, EventAction Action);

/// <summary>A category of events (e.g. "Racing") and its rule rows, for the
/// grouped Events tab.</summary>
public sealed record EventCategoryGroup(string Category, IReadOnlyList<EventRuleRow> Rows);
