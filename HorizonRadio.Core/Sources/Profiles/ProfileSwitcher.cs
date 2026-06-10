using System;
using System.Threading;
using System.Threading.Tasks;
using HorizonRadio.Core.Sources.Config;

namespace HorizonRadio.Core.Sources.Profiles;

/// <summary>
/// The single place profiles are launched from. Owns the "current profile"
/// notion (so Next/Previous cycle correctly regardless of how the last switch
/// happened) and centralizes the resolve → validate → start sequence that the
/// Profiles tab, the Now Playing quick-switch, and bound controls/game-events
/// all go through.
///
/// Switches are serialized so concurrent triggers (a hotkey on a backend thread
/// + a UI click, or rapid Next presses) can't overlap on the runner or on
/// <see cref="CurrentProfileId"/>. A source started outside this switcher (the
/// Sources tab, the Now Playing source dropdown, a SwitchSource action) clears
/// <see cref="CurrentProfileId"/> via the runner's change event, so cycling is
/// always relative to what's actually playing.
/// </summary>
public sealed class ProfileSwitcher : IDisposable
{
    private readonly SourceProfileStore _profiles;
    private readonly SourceConfigStore _config;
    private readonly SourceRunner _runner;
    private readonly SemaphoreSlim _gate = new(1, 1);

    // True only while this switcher is driving a StartAsync, so the runner's
    // ActiveSourceChanged (which fires during that start) doesn't clear the id.
    private volatile bool _switching;

    /// <summary>Id of the profile currently driving the runner, or null if the
    /// active source wasn't started from a profile.</summary>
    public string? CurrentProfileId { get; private set; }

    public ProfileSwitcher(SourceProfileStore profiles, SourceConfigStore config, SourceRunner runner)
    {
        _profiles = profiles;
        _config = config;
        _runner = runner;
        _runner.ActiveSourceChanged += OnActiveSourceChanged;
    }

    private void OnActiveSourceChanged(IAudioSourceFactory? factory)
    {
        // A source we didn't start (raw source switch, or a stop) means no
        // profile is current anymore.
        if (!_switching) CurrentProfileId = null;
    }

    /// <summary>Switch to a specific profile. Throws <see cref="InvalidOperationException"/>
    /// with a user-facing message on a known failure (unknown profile/source, or an unset
    /// global environment field such as a tool path); propagates start exceptions.</summary>
    public async Task SwitchToAsync(string profileId)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try { await SwitchToCoreAsync(profileId).ConfigureAwait(false); }
        finally { _gate.Release(); }
    }

    public Task NextAsync() => CycleAsync(+1);
    public Task PreviousAsync() => CycleAsync(-1);

    private async Task CycleAsync(int direction)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            var all = _profiles.All;
            if (all.Count == 0) throw new InvalidOperationException("No profiles to switch to.");

            var current = IndexOf(all, CurrentProfileId);
            // No current profile: step to the first (next) or last (previous).
            var next = current < 0
                ? (direction > 0 ? 0 : all.Count - 1)
                : ((current + direction) % all.Count + all.Count) % all.Count;

            // Cycling onto the already-current profile is a no-op (don't restart).
            if (all[next].Id == CurrentProfileId) return;
            await SwitchToCoreAsync(all[next].Id).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    // Assumes the gate is held.
    private async Task SwitchToCoreAsync(string profileId)
    {
        var profile = _profiles.Get(profileId)
            ?? throw new InvalidOperationException("That profile no longer exists.");

        var resolved = ProfileLauncher.Resolve(profile, _config)
            ?? throw new InvalidOperationException($"'{profile.Name}': its source ({profile.SourceId}) is unavailable.");

        var (factory, values) = resolved;
        var unset = ProfileLauncher.FirstUnsetEnvironmentField(factory, values);
        if (unset != null)
            throw new InvalidOperationException($"'{profile.Name}': set the {unset} in the Sources tab first.");

        _switching = true;
        try
        {
            await _runner.StartAsync(factory, values).ConfigureAwait(false);
            CurrentProfileId = profileId;
        }
        finally
        {
            _switching = false;
        }
    }

    private static int IndexOf(System.Collections.Generic.IReadOnlyList<SourceProfile> all, string? id)
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
