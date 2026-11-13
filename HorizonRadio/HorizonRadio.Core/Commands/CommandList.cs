using System.Globalization;
using HorizonRadio.Core.Events;
using HorizonRadio.Core.Playback;
using HorizonRadio.Core.Sources;
using HorizonRadio.Core.Sources.Mixes;
using HorizonRadio.Sdk;
using HorizonRadio.Sdk.Sources;

namespace HorizonRadio.Core.Commands;

/// <summary>
/// The commands any front end can run by name, and the one place each is carried out: key
/// bindings and event rules run through it (as <see cref="IActionDispatcher"/>), and so will the
/// control endpoint. Each command is a call on the service that does the work (the transport, the
/// router, <see cref="MixSwitcher"/>, the game's volume), checked the same way whoever asks. A
/// command that can't be done (no transport, nothing to skip to, an unknown mix) is reported and
/// logged to the Console, not thrown. Safe from any thread; a command returns once it's done.
/// </summary>
public sealed class CommandList : IActionDispatcher
{
    public const string ConsoleTag = "commands";

    /// <summary>The commands built in, in the order they're offered.</summary>
    public static readonly IReadOnlyList<HorizonCommand> BuiltIn =
    [
        new("playback.toggle-pause", "Play / Pause", "Toggle playback on or off.", EventActionType.TogglePause),
        new("playback.pause", "Pause", "Pause what's playing.", EventActionType.Pause),
        new("playback.resume", "Resume", "Carry on playing what's paused.", EventActionType.Resume),
        new("playback.next", "Next Track", "Skip to the next track.", EventActionType.NextTrack),
        new("playback.previous", "Previous Track", "Go back to the previous track.", EventActionType.PreviousTrack),
        new("playback.restart", "Restart Track", "Restart the current track from the beginning.", EventActionType.RestartTrack),
        new("mix.switch", "Switch Mix", "Switch to this mix.", EventActionType.SwitchMix, CommandArgument.Mix),
        new("mix.next", "Next Mix", "Cycle to the next saved mix.", EventActionType.NextMix),
        new("mix.previous", "Previous Mix", "Cycle to the previous saved mix.", EventActionType.PreviousMix),
        new("source.start", "Switch Source",
            "Play a source that drives itself (Spotify Connect, the test tone) in the player's place.",
            EventActionType.SwitchSource, CommandArgument.Source),
        new("volume.set", "Set Volume", "Set the game's radio volume, from 0 to 1.", EventActionType.SetVolume,
            CommandArgument.Volume),
    ];

    private readonly PlaybackRouter _playback;
    private readonly SourceRunner _runner;
    private readonly MixSwitcher? _switcher;
    private readonly MixStore? _mixes;
    private readonly Func<float, bool>? _setGain;
    private readonly IPluginLog _log;

    private static readonly Dictionary<string, HorizonCommand> ByName =
        BuiltIn.ToDictionary(c => c.Name, StringComparer.OrdinalIgnoreCase);

    /// <param name="switcher">Switches mixes; without it the mix commands aren't done.</param>
    /// <param name="mixes">The saved mixes, to name one in <see cref="Describe"/>.</param>
    /// <param name="setGain">Sets the game's radio volume, if the game has one.</param>
    public CommandList(
        PlaybackRouter playback,
        IPluginLog log,
        MixSwitcher? switcher = null,
        MixStore? mixes = null,
        Func<float, bool>? setGain = null)
    {
        _playback = playback;
        _runner = playback.Runner;
        _switcher = switcher;
        _mixes = mixes;
        _setGain = setGain;
        _log = log;
    }

    /// <summary>The command called <paramref name="name"/> (any case), or null.</summary>
    public static HorizonCommand? Find(string name) => ByName.GetValueOrDefault(name);

    /// <summary>The built-in command a binding or rule saved as <paramref name="type"/> runs.</summary>
    public static HorizonCommand For(EventActionType type) =>
        BuiltIn.FirstOrDefault(c => c.Action == type)
        ?? throw new ArgumentOutOfRangeException(nameof(type), type, "no command for this action");

    /// <summary>Runs the command called <paramref name="name"/>.</summary>
    public Task<CommandResult> RunAsync(string name, string? argument = null)
    {
        var command = Find(name);
        if (command == null) return Task.FromResult(Report(CommandResult.NotDone($"no command '{name}'")));
        return RunAsync(command.ToAction(argument));
    }

    /// <summary>Runs a saved action (a binding's, a rule's). <see cref="EventActionType.None"/> does
    /// nothing.</summary>
    public async Task<CommandResult> RunAsync(EventAction action)
    {
        ArgumentNullException.ThrowIfNull(action);
        CommandResult result;
        try
        {
            result = action.Type switch
            {
                EventActionType.None => CommandResult.Ok,
                EventActionType.NextTrack =>
                    await TransportAsync(t => t.NextAsync(), t => t.CanSkipNext).ConfigureAwait(false),
                EventActionType.PreviousTrack =>
                    await TransportAsync(t => t.PreviousAsync(), t => t.CanSkipPrevious).ConfigureAwait(false),
                EventActionType.RestartTrack =>
                    await TransportAsync(t => t.RestartAsync(), _ => true).ConfigureAwait(false),
                EventActionType.Pause =>
                    await TransportAsync(t => t.SetPausedAsync(true), t => t.CanPause).ConfigureAwait(false),
                EventActionType.Resume =>
                    await TransportAsync(t => t.SetPausedAsync(false), t => t.CanPause).ConfigureAwait(false),
                EventActionType.TogglePause =>
                    await TransportAsync(t => t.TogglePauseAsync(), t => t.CanPause).ConfigureAwait(false),
                EventActionType.SwitchSource => await StartSourceAsync(action.Param).ConfigureAwait(false),
                EventActionType.SwitchMix => await SwitchMixAsync(action.Param).ConfigureAwait(false),
                EventActionType.NextMix => await CycleMixAsync(s => s.NextAsync()).ConfigureAwait(false),
                EventActionType.PreviousMix => await CycleMixAsync(s => s.PreviousAsync()).ConfigureAwait(false),
                EventActionType.SetVolume => SetVolume(action.Param),
                _ => CommandResult.NotDone($"no command for {action.Type}"),
            };
        }
        catch (Exception ex)
        {
            result = CommandResult.NotDone($"{For(action.Type).Name} failed: {ex.GetType().Name}: {ex.Message}");
        }
        return Report(result);
    }

    Task IActionDispatcher.RunAsync(EventAction action) => RunAsync(action);

    /// <summary>A saved action in words, its argument named ("Mix: Drive", "Volume 30%").</summary>
    public string Describe(EventAction action)
    {
        ArgumentNullException.ThrowIfNull(action);
        return action.Type switch
        {
            EventActionType.None => "Do Nothing",
            EventActionType.SwitchMix =>
                _mixes?.Get(action.Param ?? "") is { } mix ? $"Mix: {mix.Name}" : For(action.Type).Title,
            EventActionType.SwitchSource =>
                $"Switch to {_playback.Sources.Find(action.Param ?? "")?.DisplayName ?? action.Param}",
            EventActionType.SetVolume => string.Create(CultureInfo.InvariantCulture,
                $"Volume {Math.Round(EventRuleStore.ParseVolume(action.Param) * 100)}%"),
            _ => BuiltIn.FirstOrDefault(c => c.Action == action.Type)?.Title ?? action.Type.ToString(),
        };
    }

    private CommandResult Report(CommandResult result)
    {
        if (!result.Done && result.Message != null) _log.Write(ConsoleTag, result.Message);
        return result;
    }

    private async Task<CommandResult> TransportAsync(
        Func<ITransportControls, Task> act, Func<ITransportControls, bool> can)
    {
        if (_runner.ActiveSource is not ITransportControls tc)
            return CommandResult.NotDone("transport action ignored: active source has no transport controls");
        if (!can(tc)) return CommandResult.NotDone("transport action ignored: not available for this source");
        await act(tc).ConfigureAwait(false);
        return CommandResult.Ok;
    }

    private async Task<CommandResult> StartSourceAsync(string? sourceId)
    {
        if (string.IsNullOrEmpty(sourceId)) return CommandResult.NotDone("switch source: no source given");
        var factory = _playback.Sources.Find(sourceId);
        if (factory == null) return CommandResult.NotDone($"switch source: unknown source '{sourceId}'");
        // A content source needs a locator and plays through the queue; the rule editor never
        // offers one, so only a hand-edited rule (or a typed command) lands here.
        if (factory is not ISelfDrivenSourceFactory)
            return CommandResult.NotDone(
                $"switch source: '{sourceId}' plays through the queue — switch to a mix instead");
        if (_runner.ActiveFactory?.Id == sourceId) return CommandResult.Ok; // already on it
        await _playback.StartSourceAsync(sourceId).ConfigureAwait(false);
        return CommandResult.Ok;
    }

    private async Task<CommandResult> SwitchMixAsync(string? mixId)
    {
        if (_switcher == null) return CommandResult.NotDone("switch mix: no mixes here");
        if (string.IsNullOrEmpty(mixId)) return CommandResult.NotDone("switch mix: no mix given");
        if (_mixes != null && _mixes.Get(mixId) == null)
            return CommandResult.NotDone($"switch mix: unknown mix '{mixId}'");
        await _switcher.SwitchToAsync(mixId).ConfigureAwait(false);
        return CommandResult.Ok;
    }

    private async Task<CommandResult> CycleMixAsync(Func<MixSwitcher, Task> cycle)
    {
        if (_switcher == null) return CommandResult.NotDone("switch mix: no mixes here");
        await cycle(_switcher).ConfigureAwait(false);
        return CommandResult.Ok;
    }

    private CommandResult SetVolume(string? level)
    {
        if (_setGain == null) return CommandResult.NotDone("set volume: the game has no volume to set");
        _setGain((float)Math.Clamp(EventRuleStore.ParseVolume(level), 0.0, 1.0));
        return CommandResult.Ok;
    }
}
