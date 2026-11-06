namespace HorizonRadio.Core.Games;

/// <summary>
/// Decides which game to start with: the saved choice, else a sensible default for people who
/// already used Horizon Radio with a game, else ask (first run).
/// </summary>
public static class GameSelection
{
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
