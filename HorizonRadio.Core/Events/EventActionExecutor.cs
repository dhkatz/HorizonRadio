using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading.Tasks;
using HorizonRadio.Core.Diagnostics;
using HorizonRadio.Core.Sources;
using HorizonRadio.Core.Sources.Config;

namespace HorizonRadio.Core.Events;

/// <summary>
/// Subscribes to one or more <see cref="IGameEventSource"/>s and runs the
/// user-configured <see cref="EventAction"/> for each event, against the
/// active source (transport), the source runner (switch source), or the
/// bridge (volume). Debounces so the same event arriving from two producers
/// (e.g. memory poll + telemetry) only acts once.
/// </summary>
public sealed class EventActionExecutor : IDisposable
{
    private readonly SourceRunner _runner;
    private readonly SourceConfigStore _configStore;
    private readonly EventRuleStore _rules;
    private readonly Func<float, bool>? _setGain;
    private readonly IReadOnlyList<IGameEventSource> _sources;

    private readonly ConcurrentDictionary<string, long> _lastFired = new();
    private const long DebounceMs = 750;

    /// <summary>Raised after an event is handled (action may be None), for
    /// the Events tab's recent-activity list. Fires on a background thread.</summary>
    public event Action<GameEvent, EventAction>? Handled;

    public EventActionExecutor(
        IEnumerable<IGameEventSource> sources,
        SourceRunner runner,
        SourceConfigStore configStore,
        EventRuleStore rules,
        Func<float, bool>? setGain = null)
    {
        _runner = runner;
        _configStore = configStore;
        _rules = rules;
        _setGain = setGain;

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
        // Debounce per kind: collapse duplicate emissions from overlapping
        // producers (DLL memory poll + telemetry both report race state).
        var now = Environment.TickCount64;
        if (_lastFired.TryGetValue(e.Kind, out var prev) && now - prev < DebounceMs)
            return;
        _lastFired[e.Kind] = now;

        var action = _rules.GetAction(e.Kind);
        ProcessConsole.Append("events",
            $"{e.Kind} -> {Describe(action)}");
        Handled?.Invoke(e, action);

        if (action.Type == EventActionType.None) return;
        _ = Task.Run(() => ExecuteAsync(action));
    }

    private async Task ExecuteAsync(EventAction action)
    {
        try
        {
            switch (action.Type)
            {
                case EventActionType.NextTrack:
                    await Transport(t => t.NextAsync(), t => t.CanSkipNext).ConfigureAwait(false);
                    break;
                case EventActionType.PreviousTrack:
                    await Transport(t => t.PreviousAsync(), t => t.CanSkipPrevious).ConfigureAwait(false);
                    break;
                case EventActionType.RestartTrack:
                    await Transport(t => t.RestartAsync(), _ => true).ConfigureAwait(false);
                    break;
                case EventActionType.Pause:
                    await Transport(t => t.IsPaused ? Task.CompletedTask : t.TogglePauseAsync(),
                        t => t.CanPause).ConfigureAwait(false);
                    break;
                case EventActionType.Resume:
                    await Transport(t => t.IsPaused ? t.TogglePauseAsync() : Task.CompletedTask,
                        t => t.CanPause).ConfigureAwait(false);
                    break;
                case EventActionType.SwitchSource:
                    await SwitchSourceAsync(action.Param).ConfigureAwait(false);
                    break;
                case EventActionType.SetVolume:
                    SetVolume(action.Param);
                    break;
            }
        }
        catch (Exception ex)
        {
            ProcessConsole.Append("events", $"action failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private async Task Transport(Func<ITransportControls, Task> act, Func<ITransportControls, bool> can)
    {
        if (_runner.ActiveSource is not ITransportControls tc)
        {
            ProcessConsole.Append("events", "transport action ignored: active source has no transport controls");
            return;
        }
        if (!can(tc))
        {
            ProcessConsole.Append("events", "transport action ignored: not available for this source");
            return;
        }
        await act(tc).ConfigureAwait(false);
    }

    private async Task SwitchSourceAsync(string? sourceId)
    {
        if (string.IsNullOrEmpty(sourceId)) return;
        var factory = SourceCatalog.Find(sourceId);
        if (factory == null)
        {
            ProcessConsole.Append("events", $"switch source: unknown source '{sourceId}'");
            return;
        }
        if (_runner.ActiveFactory?.Id == sourceId) return; // already on it
        var values = _configStore.Load(sourceId, factory.Schema);
        await _runner.StartAsync(factory, values).ConfigureAwait(false);
    }

    private void SetVolume(string? param)
    {
        if (_setGain == null)
        {
            ProcessConsole.Append("events", "set volume: no bridge volume path wired");
            return;
        }
        var level = (float)Math.Clamp(EventRuleStore.ParseVolume(param), 0.0, 1.0);
        _setGain(level);
    }

    private static string Describe(EventAction a) => a.Type switch
    {
        EventActionType.None => "(no action)",
        EventActionType.SwitchSource => $"switch source: {a.Param}",
        EventActionType.SetVolume => $"set volume: {a.Param}",
        _ => a.Type.ToString(),
    };

    public void Dispose()
    {
        foreach (var s in _sources) s.GameEventReceived -= OnGameEvent;
    }
}
