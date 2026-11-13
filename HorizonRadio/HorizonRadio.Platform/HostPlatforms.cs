using HorizonRadio.Core.Hosting;
using HorizonRadio.Platform.Windows;

namespace HorizonRadio.Platform;

/// <summary>Picks the OS code to run on, at run time: what a front end passes as
/// <see cref="HorizonHostOptions.Platform"/>.</summary>
public static class HostPlatforms
{
    /// <summary>This OS's platform, or null when there's none for it yet (the host then runs on
    /// Core's portable defaults).</summary>
    public static IHostPlatform? Current() => OperatingSystem.IsWindows() ? new WindowsPlatform() : null;
}
