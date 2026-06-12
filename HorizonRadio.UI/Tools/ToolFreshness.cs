namespace HorizonRadio.UI.Tools;

/// <summary>
/// Provisioning-freshness of an installed tool: does the user have what
/// this build of the app expects? This is deliberately NOT upstream-drift
/// detection ("is our pin behind upstream") — that's the maintainer's CI
/// job. For latest-policy tools (yt-dlp, ffmpeg) "what we expect" is the
/// upstream-current build; for the pinned tool (librespot) it's the hash
/// baked into the embedded manifest.
/// </summary>
public enum ToolFreshness
{
    /// <summary>Couldn't determine — offline, no published hash, no
    /// installed sidecar to compare, or an empty manifest pin. Never
    /// reported as stale.</summary>
    Unknown,

    /// <summary>Not installed.</summary>
    Missing,

    /// <summary>Installed and matches the expected hash.</summary>
    UpToDate,

    /// <summary>Installed but the expected hash differs — a newer build
    /// is available (latest tools) or the app's pin moved (librespot).</summary>
    UpdateAvailable,
}
