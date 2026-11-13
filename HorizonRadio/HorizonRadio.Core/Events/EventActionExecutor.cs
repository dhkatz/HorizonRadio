using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using HorizonRadio.Sdk;
using HorizonRadio.Sdk.Games;

namespace HorizonRadio.Core.Events;

/// <summary>
/// Subscribes to one or more <see cref="IGameEventSource"/>s and runs the
/// user-configured <see cref="EventAction"/> for each event via the shared
/// <see cref="IActionDispatcher"/>. Debounces so the same event arriving from
/// two producers (e.g. memory poll + telemetry) only acts once.
/// </summary>
public sealed class EventActionExecutor : IDisposable
{
    private readonly EventRuleStore _rules;
    private readonly IActionDispatcher _dispatcher;
    private readonly IPluginLog _log;
    private readonly IReadOnlyList<IGameEventSource> _sources;

    // Collapse the same event kind arriving from overlapping producers
    // (DLL memory poll + telemetry both report race state) into one action.
    private readonly Debouncer _debounce = new(750);

    /// <summary>Raised after an event is handled (action may be None), for
    /// the Events tab's recent-activity list. Fires on a background thread.</summary>
    public event Action<GameEvent, EventAction>? Handled;

    public EventActionExecutor(
        IEnumerable<IGameEventSource> sources,
        EventRuleStore rules,
        IActionDispatcher dispatcher,
        IPluginLog log)
    {
        _rules = rules;
        _dispatcher = dispatcher;
        _log = log;

        var list = new List<IGameEventSource>();
        foreach (var s in sources)
        {
            s.GameEventReceived += OnGameEvent;
            list.Add(s);
        }
        _sources = list;
    }

    private void OnGameEvent(GameEvent e)
    {
        if (!_debounce.ShouldFire(e.Kind)) return;

        var action = _rules.GetAction(e.Kind);
        _log.Write("events", $"{e.Kind} -> {_dispatcher.Describe(action)}");
        Handled?.Invoke(e, action);

        if (action.Type == EventActionType.None) return;
        _ = Task.Run(() => _dispatcher.RunAsync(action));
    }

    public void Dispose()
    {
        foreach (var s in _sources) s.GameEventReceived -= OnGameEvent;
    }
}
