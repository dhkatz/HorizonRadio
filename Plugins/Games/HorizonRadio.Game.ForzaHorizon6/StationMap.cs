using HorizonRadio.Sdk;
using HorizonRadio.Sdk.Games;

namespace HorizonRadio.Game.ForzaHorizon6;

/// <summary>What one in-game station plays.</summary>
public enum StationAssignmentKind
{
    /// <summary>The game's own music (we don't touch the station).</summary>
    Game,

    /// <summary>Horizon Radio's player: the queue, one-offs, whatever the user started.</summary>
    Player,

    /// <summary>One of the user's mixes.</summary>
    Mix,
}

/// <summary>A station's assignment.</summary>
public readonly record struct StationAssignment(StationAssignmentKind Kind, string? MixId = null)
{
    public static StationAssignment Game => new(StationAssignmentKind.Game);

    public static StationAssignment Player => new(StationAssignmentKind.Player);

    public static StationAssignment ForMix(string mixId) => new(StationAssignmentKind.Mix, mixId);
}

/// <summary>
/// FH6's Stations setting: which station plays what, and how mix stations behave while you're
/// tuned elsewhere. Persisted in the plugin's settings as <c>"station:&lt;name&gt;"</c> →
/// <c>"player"</c> | <c>"mix:&lt;id&gt;"</c> (absent = the game's own music) and
/// <c>"switchMode"</c>, both synced between the user's PCs (<see cref="IsSynced"/>), so a change made
/// on another PC is taken in as it arrives. At most one station plays the player, so "where does my
/// queue play?" always has one answer. Thread-safe.
/// </summary>
public sealed class StationMap
{
    // Keep in sync with HorizonRadio.Core's LegacySettingsMigration, which writes these keys.
    internal const string StationKeyPrefix = "station:";
    internal const string PlayerValue = "player";
    internal const string MixValuePrefix = "mix:";
    internal const string SwitchModeKey = "switchMode";

    private readonly IPluginSettings? _settings;
    private readonly Lock _lock = new();
    private readonly Dictionary<string, StationAssignment> _assignments = new(StringComparer.Ordinal);
    private StationSwitchMode _mode;

    /// <param name="settings">Where assignments persist; <see langword="null"/> keeps them in
    /// memory only (tests).</param>
    public StationMap(IPluginSettings? settings)
    {
        _settings = settings;
        Load();
        // Written here, or on another of the user's PCs: take the settings in again. Off the
        // writer's thread, since a write here happens under _lock.
        if (settings != null) settings.Changed += _ => ThreadPool.QueueUserWorkItem(_ => Reload());
    }

    /// <summary>Which of the plugin's settings follow the user to their other PCs: all of them,
    /// the assignments and the mode, as preferences.</summary>
    public static bool IsSynced(string key) => key.StartsWith(StationKeyPrefix, StringComparison.Ordinal) || key == SwitchModeKey;

    // Read the settings into the map (caller holds _lock, or nobody else can see the map yet).
    private void Load()
    {
        _assignments.Clear();
        var hasPlayer = false;
        foreach (var station in StationCatalog.Names)
        {
            if (Parse(_settings?.GetValue(StationKeyPrefix + station)) is not { } assignment) continue;
            // Two PCs may each have put the player on a station before they met: keep one.
            if (assignment.Kind == StationAssignmentKind.Player && hasPlayer) continue;
            hasPlayer |= assignment.Kind == StationAssignmentKind.Player;
            _assignments[station] = assignment;
        }
        _mode = Enum.TryParse<StationSwitchMode>(_settings?.GetValue(SwitchModeKey), ignoreCase: true, out var mode)
            ? mode
            : StationSwitchMode.Resume;
    }

    private void Reload()
    {
        bool changed;
        lock (_lock)
        {
            var before = (_assignments.ToDictionary(), _mode);
            Load();
            changed = before._mode != _mode || before.Item1.Count != _assignments.Count
                || before.Item1.Any(a => !_assignments.TryGetValue(a.Key, out var now) || now != a.Value);
        }
        if (changed) Changed?.Invoke();
    }

    /// <summary>Raised after any assignment or the mode changes. Fires on the caller's thread.</summary>
    public event Action? Changed;

    public StationAssignment Get(string station)
    {
        lock (_lock) return _assignments.GetValueOrDefault(station, StationAssignment.Game);
    }

    /// <summary>Assign <paramref name="station"/>. Making a station the player's moves the
    /// player off whichever station had it.</summary>
    public void Set(string station, StationAssignment assignment)
    {
        if (!StationCatalog.Names.Contains(station)) return;
        lock (_lock)
        {
            if (_assignments.GetValueOrDefault(station, StationAssignment.Game) == assignment) return;
            if (assignment.Kind == StationAssignmentKind.Player)
            {
                foreach (var other in _assignments.Where(a => a.Value.Kind == StationAssignmentKind.Player).ToList())
                    Store(other.Key, StationAssignment.Game);
            }
            Store(station, assignment);
        }
        Changed?.Invoke();
    }

    /// <summary>How mix stations behave while you're tuned elsewhere.</summary>
    public StationSwitchMode Mode
    {
        get { lock (_lock) return _mode; }
        set
        {
            lock (_lock)
            {
                if (_mode == value) return;
                _mode = value;
                _settings?.SetValue(SwitchModeKey, value.ToString());
            }
            Changed?.Invoke();
        }
    }

    /// <summary>True if any station plays something of ours.</summary>
    public bool HasAssignments
    {
        get { lock (_lock) return _assignments.Count > 0; }
    }

    /// <summary>The stations the DLL should replace (everything not left to the game).</summary>
    public IReadOnlyList<string> TargetStations
    {
        get { lock (_lock) return [.. _assignments.Keys.Order(StringComparer.Ordinal)]; }
    }

    /// <summary>Stations playing a mix that no longer exists go back to the game's music.</summary>
    public void ForgetMissingMixes(IReadOnlyCollection<string> existingMixIds)
    {
        var changed = false;
        lock (_lock)
        {
            foreach (var (station, assignment) in _assignments.ToList())
            {
                if (assignment.Kind == StationAssignmentKind.Mix && !existingMixIds.Contains(assignment.MixId!))
                {
                    Store(station, StationAssignment.Game);
                    changed = true;
                }
            }
        }
        if (changed) Changed?.Invoke();
    }

    /// <summary>
    /// What the app should play with <paramref name="station"/> tuned in. With no station known
    /// (the game isn't connected yet, or isn't running) the app just plays its player, as it would
    /// without a game.
    /// </summary>
    public PlaybackRoute RouteFor(string? station)
    {
        if (station == null) return PlaybackRoute.Player;
        var assignment = Get(station);
        return assignment.Kind switch
        {
            StationAssignmentKind.Player => PlaybackRoute.Player,
            StationAssignmentKind.Mix => PlaybackRoute.Mix(assignment.MixId!, Mode),
            _ => PlaybackRoute.Silent,
        };
    }

    // Caller holds _lock.
    private void Store(string station, StationAssignment assignment)
    {
        if (assignment.Kind == StationAssignmentKind.Game) _assignments.Remove(station);
        else _assignments[station] = assignment;
        _settings?.SetValue(StationKeyPrefix + station, Format(assignment));
    }

    private static string? Format(StationAssignment assignment) => assignment.Kind switch
    {
        StationAssignmentKind.Player => PlayerValue,
        StationAssignmentKind.Mix => MixValuePrefix + assignment.MixId,
        _ => null,
    };

    private static StationAssignment? Parse(string? value) => value switch
    {
        PlayerValue => StationAssignment.Player,
        _ when value != null && value.StartsWith(MixValuePrefix, StringComparison.Ordinal) && value.Length > MixValuePrefix.Length
            => StationAssignment.ForMix(value[MixValuePrefix.Length..]),
        _ => null,
    };
}
