using HorizonRadio.Core.Sources;
using HorizonRadio.Sdk.Games;
using HorizonRadio.Sdk.Models;
using HorizonRadio.Sdk.Sources;

namespace HorizonRadio.Core.Audio;

/// <summary>Where the audio was going when it could no longer be heard (<see cref="OutputGuard"/>).</summary>
public enum OutputLoss
{
    /// <summary>The local speaker failed to open, or was lost (unplugged, disabled).</summary>
    Speaker,

    /// <summary>The game isn't connected (not running, or without the mod).</summary>
    Game,
}

/// <summary>
/// Pauses what's playing when where it's going can't be heard: the local speaker failed or went,
/// or the game it plays into isn't connected. It pauses once each time that happens (keeping the
/// track and its place), as soon as the source can pause (some only can after their first track
/// loads), and never plays again by itself: the user presses play once they've picked an output
/// that works, so nothing starts blaring. A play or pause by the user meanwhile
/// (<see cref="UserTookControl"/>) cancels a pause still waiting. <see cref="OutputLost"/> tells the
/// front end, to say so.
/// </summary>
public sealed class OutputGuard : IDisposable
{
    private readonly SourceRunner _runner;
    private readonly PreviewController _preview;
    private readonly IGameIntegration _game;
    private readonly Lock _lock = new();
    private bool _available = true;
    private bool _pausePending;

    public OutputGuard(SourceRunner runner, PreviewController preview, IGameIntegration game)
    {
        _runner = runner;
        _preview = preview;
        _game = game;
        _runner.ActiveSourceChanged += OnActiveSourceChanged;
        _runner.TrackChanged += OnTrackChanged;
        _preview.StateChanged += Reevaluate;
        _game.StateChanged += OnGameStateChanged;
    }

    /// <summary>Raised when what's playing can no longer be heard, once each time. On the thread
    /// that noticed (a device's, the game's, the runner's).</summary>
    public event Action<OutputLoss>? OutputLost;

    /// <summary>Whether the audio can be heard where it's going now: the speaker when local
    /// monitoring is on, else the game.</summary>
    public bool IsOutputAvailable =>
        _preview.Enabled ? _preview.IsSpeakerActive : _game.Output != null && _game.State == ConnectionState.Connected;

    /// <summary>The user played or paused: a pause still waiting for the source is dropped, so
    /// their choice stands.</summary>
    public void UserTookControl()
    {
        lock (_lock) _pausePending = false;
    }

    private void OnActiveSourceChanged(IAudioSourceFactory? factory)
    {
        if (_runner.ActiveSource is null)
        {
            // Nothing playing: the next start is judged afresh.
            lock (_lock)
            {
                _available = true;
                _pausePending = false;
            }
            return;
        }
        Reevaluate();
    }

    // A source may only be able to pause once its first track is in.
    private void OnTrackChanged(Track track) => Reevaluate();

    private void OnGameStateChanged(ConnectionState state) => Reevaluate();

    private void Reevaluate()
    {
        if (!_runner.IsRunning) return;
        var available = IsOutputAvailable;
        OutputLoss? lost = null;
        ITransportControls? pause = null;
        lock (_lock)
        {
            if (available != _available)
            {
                _available = available;
                _pausePending = !available;
                if (!available) lost = _preview.Enabled ? OutputLoss.Speaker : OutputLoss.Game;
            }
            // Once the source can pause: at most once, so the user playing again while it's still
            // unreachable isn't overruled.
            if (_pausePending && _runner.ActiveSource is ITransportControls { CanPause: true, IsPaused: false } transport)
            {
                _pausePending = false;
                pause = transport;
            }
        }
        if (lost is { } loss) OutputLost?.Invoke(loss);
        if (pause != null) _ = PauseAsync(pause);
    }

    // Pause, not toggle: a play or pause that lands in between can't turn this into a resume.
    private static async Task PauseAsync(ITransportControls transport)
    {
        try { await transport.SetPausedAsync(true).ConfigureAwait(false); }
        catch (Exception ex) { System.Diagnostics.Trace.WriteLine($"[hzn-output] pause: {ex.Message}"); }
    }

    public void Dispose()
    {
        _runner.ActiveSourceChanged -= OnActiveSourceChanged;
        _runner.TrackChanged -= OnTrackChanged;
        _preview.StateChanged -= Reevaluate;
        _game.StateChanged -= OnGameStateChanged;
    }
}
