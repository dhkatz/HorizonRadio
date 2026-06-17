using System.Threading;

namespace HorizonRadio.UI.ViewModels;

/// <summary>
/// One process-wide cap on concurrent background metadata enrichment (canonical-name resolves and
/// the playable-source searches that spawn yt-dlp/Spotify work). Shared by the queue sidebar and
/// the History tab so the limit is global — two view models with their own gates would allow twice
/// the intended concurrency when both are active at once.
/// </summary>
internal static class EnrichmentThrottle
{
    public static readonly SemaphoreSlim Gate = new(3, 3);
}
