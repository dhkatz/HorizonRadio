using System.Collections.Generic;
using System.Linq;

namespace HorizonRadio.Game.ForzaHorizon6;

/// <summary>
/// The fixed set of Forza Horizon 6 radio stations, captured from the live
/// game. Used by the "replace which station" dropdown. FH6 doesn't add
/// stations post-launch, so a hard-coded list is simpler and fully
/// populated from the start (no need to tune through them to discover names).
/// </summary>
public static class StationCatalog
{
    /// <summary>Dropdown entry meaning "replace whatever station is active."</summary>
    public const string AnyLabel = "Any station";

    /// <summary>The in-game station names, exactly as the game reports them.</summary>
    public static readonly IReadOnlyList<string> Names = new[]
    {
        "Gacha City Radio",
        "Horizon Bass Arena",
        "Horizon Block Party",
        "Horizon Opus",
        "Horizon Pulse",
        "Horizon Wave",
        "Horizon XS",
        "Hospital Records",
        "Streamer Mode",
        "Sub Pop Records",
    };

    /// <summary>Dropdown contents: "Any station" first, then the stations.</summary>
    public static readonly IReadOnlyList<string> All = new[] { AnyLabel }.Concat(Names).ToList();
}
