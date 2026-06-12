using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using HorizonRadio.Core.Sources.Queue;

namespace HorizonRadio.Core.Sources.Mixes;

/// <summary>
/// The single place mixes are launched from — the mix-era successor to the old
/// profile switcher, now a thin façade over <see cref="QueuePlayback"/>. Owns the
/// "current mix" notion (so Next/Previous cycle the library correctly however the
/// last switch happened) and centralizes the resolve → start sequence the Mixes
/// tab, the player-bar quick-switch, and bound controls/game-events all go through.
///
/// Starting a mix sets it as the queue's context (the infinite tail). "Current mix"
/// is derived from the queue model and gated on the queue engine actually being the
/// active source, so a self-driven source (Spotify Connect) taking over reports no
/// current mix — keeping station targeting relative to what's really playing.
///
/// Switches are serialized so concurrent triggers (a hotkey on a backend thread plus
/// a UI click, or rapid Next presses) can't overlap on <see cref="CurrentMixId"/>.
/// </summary>
public sealed class MixSwitcher : IDisposable
{
    private readonly MixStore _mixes;
    private readonly QueuePlayback _queue;
    private readonly SourceRunner _runner;
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>Id of the mix currently driving the queue's tail, or null when the
    /// active source isn't the queue (e.g. Spotify Connect took over) or no mix is
    /// set. Derived, so it can't drift from what's actually playing.</summary>
    public string? CurrentMixId => _queue.IsActive ? _queue.Model.ContextMixId : null;

    /// <summary>Raised after a successful switch, carrying the mix now playing. The
    /// app uses it to push the mix's effective target station to the DLL (the mix's
    /// override, else the global default).</summary>
    public event Action<Mix>? Switched;

    public MixSwitcher(MixStore mixes, QueuePlayback queue, SourceRunner runner)
    {
        _mixes = mixes;
        _queue = queue;
        _runner = runner;
    }

    /// <summary>Switch to a specific mix (replacing the queue's context). Throws
    /// <see cref="InvalidOperationException"/> with a user-facing message on a known
    /// failure (unknown mix, empty mix), and propagates a
    /// <see cref="MissingToolException"/> when an entry's source needs a tool that
    /// isn't installed.</summary>
    public async Task SwitchToAsync(string mixId)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try { await SwitchToCoreAsync(mixId).ConfigureAwait(false); }
        finally { _gate.Release(); }
    }

    /// <summary>True when the queue already has content (explicit items or a
    /// context) — the UI uses it to decide whether to prompt "replace or add" when
    /// the user starts another mix.</summary>
    public bool QueueHasContent => _queue.Model.HasWork;

    /// <summary>Add a mix to the queue as one-time content (one lap of its tracks)
    /// without changing the context the queue's tail draws from. Unlike
    /// <see cref="SwitchToAsync"/> this leaves the "current mix" unchanged, so it
    /// raises no <see cref="Switched"/> (station targeting stays put).</summary>
    public async Task AddToQueueAsync(string mixId)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            var mix = _mixes.Get(mixId)
                ?? throw new InvalidOperationException("That mix no longer exists.");
            await _queue.PlayMixAsync(mix, QueueAddMode.Add).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    public Task NextAsync() => CycleAsync(+1);
    public Task PreviousAsync() => CycleAsync(-1);

    private async Task CycleAsync(int direction)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            var all = _mixes.All;
            if (all.Count == 0) throw new InvalidOperationException("No mixes to switch to.");

            var current = IndexOf(all, CurrentMixId);
            var next = current < 0
                ? (direction > 0 ? 0 : all.Count - 1)
                : ((current + direction) % all.Count + all.Count) % all.Count;

            if (all[next].Id == CurrentMixId) return; // cycling onto the current mix is a no-op
            await SwitchToCoreAsync(all[next].Id).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    // Assumes the gate is held.
    private async Task SwitchToCoreAsync(string mixId)
    {
        var mix = _mixes.Get(mixId)
            ?? throw new InvalidOperationException("That mix no longer exists.");

        await _queue.PlayMixAsync(mix, QueueAddMode.Replace).ConfigureAwait(false);
        Switched?.Invoke(mix);
    }

    private static int IndexOf(IReadOnlyList<Mix> all, string? id)
    {
        if (id == null) return -1;
        for (var i = 0; i < all.Count; i++)
            if (all[i].Id == id) return i;
        return -1;
    }

    public void Dispose() => _gate.Dispose();
}
