using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using HorizonRadio.Core.Commands;
using HorizonRadio.Core.Events;
using HorizonRadio.Core.Sources;
using HorizonRadio.Sdk.Games;
using HorizonRadio.Sdk.Sources;

namespace HorizonRadio.UI.ViewModels;

/// <summary>
/// Events tab: binds each known in-game event to an action. Persists via
/// <see cref="EventRuleStore"/> and shows a small recent-activity log fed
/// by the <see cref="EventActionExecutor"/>.
/// </summary>
public sealed partial class EventsViewModel : ViewModelBase, IDisposable
{
    private readonly EventActionExecutor? _executor;
    private readonly CommandList? _commands;

    public ObservableCollection<EventCategoryGroup> Groups { get; } = new();
    public ObservableCollection<string> Activity { get; } = new();

    /// <summary>Setup advice from the active game (e.g. enabling its telemetry feed), or null.</summary>
    public string? SetupHint { get; }
    public bool HasSetupHint => !string.IsNullOrEmpty(SetupHint);

    // Design-time / fallback ctor.
    public EventsViewModel() : this(new EventRuleStore(), null, null, GameEventKinds.Catalog, null) { }

    /// <param name="commands">Names the actions in the activity list.</param>
    /// <param name="catalog">The events the active game reports (its own list).</param>
    /// <param name="sources">The sources, whose self-driven ones an event can switch to.</param>
    public EventsViewModel(EventRuleStore rules, EventActionExecutor? executor, CommandList? commands,
                           IReadOnlyList<GameEventInfo> catalog, string? setupHint, SourceCatalog? sources = null)
    {
        _executor = executor;
        _commands = commands;
        SetupHint = setupHint;

        var options = BuildOptions(sources ?? SourceCatalog.Empty);
        foreach (var category in catalog.GroupBy(i => i.Category))
        {
            var rows = category.Select(info => new EventRuleRow(info, options, rules)).ToList();
            Groups.Add(new EventCategoryGroup(category.Key, rows));
        }

        if (executor != null) executor.Handled += OnHandled;
        // A rule changed on another of your devices (Devices) shows here too.
        rules.Changed += kind => Dispatcher.UIThread.Post(() =>
        {
            foreach (var row in Groups.SelectMany(g => g.Rows).Where(r => r.Info.Kind == kind)) row.Reload();
        });
    }

    private void OnHandled(GameEvent e, EventAction action)
    {
        var line = string.Create(CultureInfo.InvariantCulture,
            $"{DateTime.Now:HH:mm:ss}  {e.Kind} → {_commands?.Describe(action) ?? action.Type.ToString()}");
        Dispatcher.UIThread.Post(() =>
        {
            Activity.Insert(0, line);
            while (Activity.Count > 50) Activity.RemoveAt(Activity.Count - 1);
        });
    }

    private static List<EventActionOption> BuildOptions(SourceCatalog sources)
    {
        var list = new List<EventActionOption> { new("Do Nothing", EventAction.None) };
        foreach (var type in Offered)
            list.Add(new EventActionOption(CommandList.For(type).Title, new EventAction(type)));
        // Only self-driven sources are directly switchable (Spotify Connect, the
        // test tone). Content sources play via mixes, so they aren't offered here.
        var start = CommandList.For(EventActionType.SwitchSource);
        foreach (var f in sources.All.Where(f => f is ISelfDrivenSourceFactory))
            list.Add(new EventActionOption($"Switch to {f.DisplayName}", start.ToAction(f.Id)));
        var volume = CommandList.For(EventActionType.SetVolume);
        list.Add(new EventActionOption("Duck Volume (30%)", volume.ToAction(EventRuleStore.FormatVolume(0.3))));
        list.Add(new EventActionOption("Full Volume (100%)", volume.ToAction(EventRuleStore.FormatVolume(1.0))));
        return list;
    }

    // The commands an event can run that take no argument, in the order offered.
    private static readonly EventActionType[] Offered =
    [
        EventActionType.NextTrack, EventActionType.PreviousTrack, EventActionType.RestartTrack,
        EventActionType.Pause, EventActionType.Resume, EventActionType.NextMix, EventActionType.PreviousMix,
    ];

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
    }

    /// <summary>Show the saved choice again (it changed elsewhere).</summary>
    public void Reload() => SelectedOption = Match(Options, _rules.GetAction(Info.Kind));

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
