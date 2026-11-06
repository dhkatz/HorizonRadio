namespace HorizonRadio.Core.Events;

/// <summary>
/// A detected in-game event. <see cref="Kind"/> is one of the constants
/// in <see cref="GameEventKinds"/>; <see cref="Data"/> carries optional
/// event-specific fields (e.g. telemetry speed) for future rule params.
/// </summary>
public sealed record GameEvent(string Kind, IReadOnlyDictionary<string, string>? Data = null);

/// <summary>UI-facing description of an event kind, grouped by category.</summary>
public sealed record GameEventInfo(string Kind, string Category, string DisplayName, string Description);

/// <summary>
/// Canonical event-kind strings, shared by every producer (the DLL's
/// memory poller over IPC, the Forza Data Out telemetry listener) and the
/// rule store. Strings (not an enum) so a new producer can introduce a
/// kind without a breaking change to persisted config.
///
/// Only events we can actually detect reliably are listed. Radio power
/// on/off and race restart were investigated but proved undetectable
/// (see git history) and are intentionally absent.
/// </summary>
public static class GameEventKinds
{
    public const string RaceStart = "race_start";
    public const string RaceFinish = "race_finish";
    public const string StationChanged = "station_changed";
    public const string Paused = "paused";
    public const string Resumed = "resumed";

    /// <summary>Every event in the Events tab lets the user bind, grouped by
    /// category in display order.</summary>
    public static readonly IReadOnlyList<GameEventInfo> Catalog = new[]
    {
        new GameEventInfo(RaceStart, "Racing", "Race Start", "A race begins."),
        new GameEventInfo(RaceFinish, "Racing", "Race Finish", "A race ends — you cross the line or bail out."),
        new GameEventInfo(Paused, "Menu", "Game Paused", "Gameplay pauses — a menu, the map, or a cutscene."),
        new GameEventInfo(Resumed, "Menu", "Game Resumed", "Gameplay resumes after a pause."),
        new GameEventInfo(StationChanged, "Radio", "Station Changed", "You switch to a different in-game radio station."),
    };
}
