using HorizonRadio.Core.Settings;

namespace HorizonRadio.Core.Games;

/// <summary>
/// Decides which game to start with: the saved choice, else a sensible default for people who
/// already used Horizon Radio with a game, else ask (first run).
/// </summary>
public static class GameSelection
{
    /// <summary>The game Horizon Radio runs with: a game plugin's id, <see cref="NoGameIntegration.Id"/>
    /// for a plain player, or null before the user has chosen (first run). Read at startup, so a
    /// change takes effect on restart. About what's installed on this machine, so it doesn't sync.</summary>
    public static readonly Setting<string?> Chosen = new("game", SettingScope.Device, null);

    /// <summary>The outcome: a game id to start with (a plugin id or
    /// <see cref="NoGameIntegration.Id"/>), or <see langword="null"/> to ask the user first.</summary>
    /// <param name="savedId">The persisted choice, or <see langword="null"/> if never chosen.</param>
    /// <param name="availableIds">Ids of the game plugins installed now.</param>
    /// <param name="usedGameIds">Ids of game plugins that already have settings of their own (the
    /// user set one up before the picker existed).</param>
    public static string? Resolve(string? savedId, IReadOnlyCollection<string> availableIds,
                                  IReadOnlyCollection<string> usedGameIds)
    {
        if (savedId == NoGameIntegration.Id) return savedId;
        if (savedId != null) return availableIds.Contains(savedId) ? savedId : null; // plugin gone: ask again
        // Never chosen: an existing user who set a game up keeps it; a fresh install asks.
        return usedGameIds.FirstOrDefault(availableIds.Contains);
    }
}
