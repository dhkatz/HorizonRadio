using System;
using System.Linq;
using System.Reflection;

namespace HorizonRadio.Core;

/// <summary>The distribution channel this build was stamped for.</summary>
public enum ReleaseChannel
{
    /// <summary>Local/unstamped build — no update checks run.</summary>
    Dev,

    /// <summary>Tagged release (vX.Y.Z). Checks GitHub releases/latest.</summary>
    Stable,

    /// <summary>Rolling nightly prerelease. Checks the `nightly` release.</summary>
    Nightly,
}

/// <summary>
/// Build-time identity of the running app, resolved once at startup from
/// the entry assembly's attributes. <see cref="Version"/> and
/// <see cref="CommitSha"/> come from the publish-time
/// <c>-p:Version=</c> (baked into <c>AssemblyInformationalVersion</c> as
/// <c>"&lt;version&gt;+&lt;sha&gt;"</c>); <see cref="Channel"/> comes from an
/// <c>[AssemblyMetadata("Channel", …)]</c> the csproj/workflows stamp. A
/// local <c>dotnet build</c> (no Version/Channel) reports the dev defaults,
/// and the self-updater treats <see cref="ReleaseChannel.Dev"/> as
/// "never check".
/// </summary>
public sealed record BuildInfo(string Version, ReleaseChannel Channel, string? CommitSha)
{
    public static BuildInfo Current { get; } = Load();

    public bool IsDev => Channel == ReleaseChannel.Dev;

    private static BuildInfo Load()
    {
        var asm = Assembly.GetEntryAssembly() ?? typeof(BuildInfo).Assembly;

        // InformationalVersion is "<version>+<sha>" (SourceLink appends the
        // commit) or just "<version>". For nightly the version segment is
        // itself "X.Y.Z-nightly.<date>", which we keep intact.
        var info = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "";
        var plus = info.IndexOf('+');
        var version = plus >= 0 ? info[..plus] : info;
        var commit = plus >= 0 ? info[(plus + 1)..] : null;
        if (string.IsNullOrWhiteSpace(version))
            version = "0.0.0";

        var channelValue = asm.GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(a => string.Equals(a.Key, "Channel", StringComparison.OrdinalIgnoreCase))
            ?.Value;

        var channel = channelValue?.Trim().ToLowerInvariant() switch
        {
            "stable" => ReleaseChannel.Stable,
            "nightly" => ReleaseChannel.Nightly,
            _ => ReleaseChannel.Dev,
        };

        return new BuildInfo(version, channel, string.IsNullOrWhiteSpace(commit) ? null : commit);
    }
}
