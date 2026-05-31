using System.Collections.Generic;

namespace HorizonRadio.Core.Events;

/// <summary>
/// A detected in-game event. <see cref="Kind"/> is one of the constants
/// in <see cref="GameEventKinds"/>; <see cref="Data"/> carries optional
/// event-specific fields (e.g. telemetry speed) for future rule params.
/// </summary>
public sealed record GameEvent(string Kind, IReadOnlyDictionary<string, string>? Data = null);

/// <summary>UI-facing description of an event kind.</summary>
public sealed record GameEventInfo(string Kind, string DisplayName, string Description);

/// <summary>
/// Canonical event-kind strings, shared by every producer (the DLL's
/// memory poller over IPC, the Forza Data Out telemetry listener) and the
/// rule store. Strings (not an enum) so a new producer can introduce a
/// kind without a breaking change to persisted config.
/// </summary>
public static class GameEventKinds
{
    public const string RaceStart = "race_start";
    public const string RaceFinish = "race_finish";
    public const string RaceRestart = "race_restart";
    public const string StationChanged = "station_changed";
    public const string RadioOn = "radio_on";
    public const string RadioOff = "radio_off";
    public const string Paused = "paused";
    public const string Resumed = "resumed";

    /// <summary>Every event the Events tab lets the user bind, in display order.</summary>
    public static readonly IReadOnlyList<GameEventInfo> Catalog = new[]
    {
        new GameEventInfo(RaceStart, "Race start", "A race begins."),
        new GameEventInfo(RaceFinish, "Race finish", "A race ends (you cross the line or bail out)."),
        new GameEventInfo(RaceRestart, "Race restart", "You restart the current race."),
        new GameEventInfo(StationChanged, "Station changed", "You switch the in-game radio station."),
        new GameEventInfo(RadioOn, "Radio on", "The in-game radio is turned on."),
        new GameEventInfo(RadioOff, "Radio off", "The in-game radio is turned off."),
        new GameEventInfo(Paused, "Game paused / menu", "Gameplay pauses — menu, cutscene, or alt-tab."),
        new GameEventInfo(Resumed, "Game resumed", "Gameplay resumes after a pause."),
    };
}
