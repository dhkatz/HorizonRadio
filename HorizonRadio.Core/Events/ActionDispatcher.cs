using System;
using System.Threading.Tasks;
using HorizonRadio.Core.Diagnostics;
using HorizonRadio.Core.Sources;
using HorizonRadio.Core.Sources.Config;

namespace HorizonRadio.Core.Events;

/// <summary>
/// Runs a single <see cref="EventAction"/> against the active transport
/// (play/pause/next/prev), the source runner (switch source), or the bridge
/// (volume).
/// </summary>
public interface IActionDispatcher
{
    Task RunAsync(EventAction action);
}

/// <summary>
/// The one place that turns an <see cref="EventAction"/> into a transport /
/// source / volume call. Extracted out of <see cref="EventActionExecutor"/>
/// so that both producers of actions — game-event rules (Events tab) and
/// input bindings (Controls tab) — share identical capability checks and
/// error handling instead of duplicating the dispatch switch.
/// </summary>
public sealed class ActionDispatcher : IActionDispatcher
{
    private readonly SourceRunner _runner;
    private readonly SourceConfigStore _configStore;
    private readonly Func<float, bool>? _setGain;
    private readonly string _logChannel;

    public ActionDispatcher(
        SourceRunner runner,
        SourceConfigStore configStore,
        Func<float, bool>? setGain = null,
        string logChannel = "events")
    {
        _runner = runner;
        _configStore = configStore;
        _setGain = setGain;
        _logChannel = logChannel;
    }

    public async Task RunAsync(EventAction action)
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
                case EventActionType.TogglePause:
                    await Transport(t => t.TogglePauseAsync(), t => t.CanPause).ConfigureAwait(false);
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
            ProcessConsole.Append(_logChannel, $"action failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private async Task Transport(Func<ITransportControls, Task> act, Func<ITransportControls, bool> can)
    {
        if (_runner.ActiveSource is not ITransportControls tc)
        {
            ProcessConsole.Append(_logChannel, "transport action ignored: active source has no transport controls");
            return;
        }
        if (!can(tc))
        {
            ProcessConsole.Append(_logChannel, "transport action ignored: not available for this source");
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
            ProcessConsole.Append(_logChannel, $"switch source: unknown source '{sourceId}'");
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
            ProcessConsole.Append(_logChannel, "set volume: no bridge volume path wired");
            return;
        }
        var level = (float)Math.Clamp(EventRuleStore.ParseVolume(param), 0.0, 1.0);
        _setGain(level);
    }
}
