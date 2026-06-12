using System.Linq;
using HorizonRadio.Core.Sources.Config;

namespace HorizonRadio.Core.Sources.Mixes;

/// <summary>
/// The single place mixes are launched from — the mix-era successor to the old
/// profile switcher. Owns the "current mix" notion (so Next/Previous cycle the
/// library correctly however the last switch happened) and centralizes the
/// resolve → pre-flight → start sequence the Mixes tab, the player-bar quick-
/// switch, and bound controls/game-events all go through.
///
/// Switches are serialized so concurrent triggers (a hotkey on a backend thread
/// plus a UI click, or rapid Next presses) can't overlap on the runner or on
/// <see cref="CurrentMixId"/>. A source started outside this switcher (a direct
/// self-driven source, a SwitchSource action) clears <see cref="CurrentMixId"/>
/// via the runner's change event, so cycling stays relative to what's playing.
/// </summary>
public sealed class MixSwitcher : IDisposable
{
    private readonly MixStore _mixes;
    private readonly SourceConfigStore _config;
    private readonly SourceRunner _runner;
    private readonly MixContentResolver _resolver;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private volatile bool _switching;

    /// <summary>Id of the mix currently driving the runner, or null if the active
    /// source wasn't started from a mix.</summary>
    public string? CurrentMixId { get; private set; }

    public MixSwitcher(MixStore mixes, SourceConfigStore config, SourceRunner runner)
    {
        _mixes = mixes;
        _config = config;
        _runner = runner;
        _resolver = new MixContentResolver(config);
        _runner.ActiveSourceChanged += OnActiveSourceChanged;
    }

    private void OnActiveSourceChanged(IAudioSourceFactory? factory)
    {
        if (!_switching) CurrentMixId = null;
    }

    /// <summary>Switch to a specific mix. Throws <see cref="InvalidOperationException"/>
    /// with a user-facing message on a known failure (unknown mix, empty mix), and
    /// propagates a <see cref="MissingToolException"/> when an entry's source needs
    /// a tool that isn't installed.</summary>
    public async Task SwitchToAsync(string mixId)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try { await SwitchToCoreAsync(mixId).ConfigureAwait(false); }
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
        if (mix.Entries.Count == 0)
            throw new InvalidOperationException($"'{mix.Name}' has no entries to play.");

        // Pre-flight every distinct source the mix uses before tearing down what's
        // playing, so a mix that needs a missing tool reports it up front (and the
        // current source keeps playing) instead of silently skipping mid-mix.
        EnsureMixToolsAvailable(mix);

        var source = new MixSource(mix, _resolver);

        _switching = true;
        try
        {
            await _runner.StartSourceAsync(source).ConfigureAwait(false);
            CurrentMixId = mixId;
        }
        finally
        {
            _switching = false;
        }
    }

    private void EnsureMixToolsAvailable(Mix mix)
    {
        foreach (var sourceId in mix.Entries.Select(e => e.SourceId).Distinct())
        {
            if (SourceCatalog.Find(sourceId) is not IContentSourceFactory factory) continue;
            var values = _config.Load(factory.Id, factory.Schema);
            SourceRequirements.EnsureToolsAvailable(factory, values);
        }
    }

    private static int IndexOf(System.Collections.Generic.IReadOnlyList<Mix> all, string? id)
    {
        if (id == null) return -1;
        for (var i = 0; i < all.Count; i++)
            if (all[i].Id == id) return i;
        return -1;
    }

    public void Dispose()
    {
        _runner.ActiveSourceChanged -= OnActiveSourceChanged;
        _gate.Dispose();
    }
}
