using System.Threading.Tasks;
using HorizonRadio.Core.Playback;
using HorizonRadio.Core.Sources;
using HorizonRadio.Core.Sources.Config;
using HorizonRadio.Core.Sources.Mixes;
using HorizonRadio.Sdk;
using HorizonRadio.Sdk.Sources;

namespace HorizonRadio.Core.Events;

/// <summary>
/// Runs a single <see cref="EventAction"/> against the active transport
/// (play/pause/next/prev), the playback router (switch source), or the bridge
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
    private readonly PlaybackRouter _playback;
    private readonly SourceRunner _runner;
    private readonly SourceConfigStore _configStore;
    private readonly MixSwitcher? _mixes;
    private readonly Func<float, bool>? _setGain;
    private readonly IPluginLog _log;
    private readonly string _logChannel;

    public ActionDispatcher(
        PlaybackRouter playback,
        SourceConfigStore configStore,
        IPluginLog log,
        MixSwitcher? mixes = null,
        Func<float, bool>? setGain = null,
        string logChannel = "events")
    {
        _playback = playback;
        _runner = playback.Runner;
        _configStore = configStore;
        _mixes = mixes;
        _setGain = setGain;
        _log = log;
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
                    await Transport(t => t.SetPausedAsync(true), t => t.CanPause).ConfigureAwait(false);
                    break;
                case EventActionType.Resume:
                    await Transport(t => t.SetPausedAsync(false), t => t.CanPause).ConfigureAwait(false);
                    break;
                case EventActionType.TogglePause:
                    await Transport(t => t.TogglePauseAsync(), t => t.CanPause).ConfigureAwait(false);
                    break;
                case EventActionType.SwitchSource:
                    await SwitchSourceAsync(action.Param).ConfigureAwait(false);
                    break;
                case EventActionType.SwitchMix:
                    if (_mixes != null && !string.IsNullOrEmpty(action.Param))
                        await _mixes.SwitchToAsync(action.Param).ConfigureAwait(false);
                    break;
                case EventActionType.NextMix:
                    if (_mixes != null) await _mixes.NextAsync().ConfigureAwait(false);
                    break;
                case EventActionType.PreviousMix:
                    if (_mixes != null) await _mixes.PreviousAsync().ConfigureAwait(false);
                    break;
                case EventActionType.SetVolume:
                    SetVolume(action.Param);
                    break;
            }
        }
        catch (Exception ex)
        {
            _log.Write(_logChannel, $"action failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private async Task Transport(Func<ITransportControls, Task> act, Func<ITransportControls, bool> can)
    {
        if (_runner.ActiveSource is not ITransportControls tc)
        {
            _log.Write(_logChannel, "transport action ignored: active source has no transport controls");
            return;
        }
        if (!can(tc))
        {
            _log.Write(_logChannel, "transport action ignored: not available for this source");
            return;
        }
        await act(tc).ConfigureAwait(false);
    }

    private async Task SwitchSourceAsync(string? sourceId)
    {
        if (string.IsNullOrEmpty(sourceId)) return;
        var factory = _playback.Sources.Find(sourceId);
        if (factory == null)
        {
            _log.Write(_logChannel, $"switch source: unknown source '{sourceId}'");
            return;
        }
        if (factory is not ISelfDrivenSourceFactory selfDriven)
        {
            // A content source needs a locator and plays through the queue; the rule
            // editor never offers one, so only a hand-edited rule lands here.
            _log.Write(_logChannel,
                $"switch source: '{sourceId}' plays through the queue — switch to a mix instead");
            return;
        }
        if (_runner.ActiveFactory?.Id == sourceId) return; // already on it
        var values = _configStore.Load(sourceId, factory.Schema);
        await _playback.StartSourceAsync(selfDriven, values).ConfigureAwait(false);
    }

    private void SetVolume(string? param)
    {
        if (_setGain == null)
        {
            _log.Write(_logChannel, "set volume: no bridge volume path wired");
            return;
        }
        var level = (float)Math.Clamp(EventRuleStore.ParseVolume(param), 0.0, 1.0);
        _setGain(level);
    }
}
