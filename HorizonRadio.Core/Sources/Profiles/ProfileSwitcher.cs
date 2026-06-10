using System;
using System.Threading.Tasks;
using HorizonRadio.Core.Sources.Config;

namespace HorizonRadio.Core.Sources.Profiles;

/// <summary>
/// The single place profiles are launched from. Owns the "current profile"
/// notion (so Next/Previous cycle correctly regardless of how the last switch
/// happened) and centralizes the resolve → validate → start sequence that the
/// Profiles tab, the Now Playing quick-switch, and bound controls/game-events
/// all go through.
/// </summary>
public sealed class ProfileSwitcher
{
    private readonly SourceProfileStore _profiles;
    private readonly SourceConfigStore _config;
    private readonly SourceRunner _runner;

    /// <summary>Id of the profile currently driving the runner, or null.</summary>
    public string? CurrentProfileId { get; private set; }

    public ProfileSwitcher(SourceProfileStore profiles, SourceConfigStore config, SourceRunner runner)
    {
        _profiles = profiles;
        _config = config;
        _runner = runner;
    }

    /// <summary>Switch to a specific profile. Throws <see cref="InvalidOperationException"/>
    /// with a user-facing message on a known failure (unknown profile/source, or an unset
    /// global environment field such as a tool path); propagates start exceptions.</summary>
    public async Task SwitchToAsync(string profileId)
    {
        var profile = _profiles.Get(profileId)
            ?? throw new InvalidOperationException("That profile no longer exists.");

        var resolved = ProfileLauncher.Resolve(profile, _config)
            ?? throw new InvalidOperationException($"'{profile.Name}': its source ({profile.SourceId}) is unavailable.");

        var (factory, values) = resolved;
        var unset = ProfileLauncher.FirstUnsetEnvironmentField(factory, values);
        if (unset != null)
            throw new InvalidOperationException($"'{profile.Name}': set the {unset} in the Sources tab first.");

        await _runner.StartAsync(factory, values).ConfigureAwait(false);
        CurrentProfileId = profileId;
    }

    public Task NextAsync() => CycleAsync(+1);
    public Task PreviousAsync() => CycleAsync(-1);

    private Task CycleAsync(int direction)
    {
        var all = _profiles.All;
        if (all.Count == 0) throw new InvalidOperationException("No profiles to switch to.");

        // Index of the current profile, or -1 if none/unknown. Wrap around so a
        // missing current lands on the first (next) or last (previous) profile.
        var current = -1;
        for (var i = 0; i < all.Count; i++)
            if (all[i].Id == CurrentProfileId) { current = i; break; }

        var next = ((current + direction) % all.Count + all.Count) % all.Count;
        return SwitchToAsync(all[next].Id);
    }
}
