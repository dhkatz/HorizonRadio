using HorizonRadio.Sdk;

namespace HorizonRadio.Core.Hosting;

/// <summary>
/// The host's process launching and log for plugin code the container doesn't build yet (the
/// Radio and Spotify sources' audio processes). They bypass a plugin module's replacement of
/// <see cref="IProcesses"/> or <see cref="IPluginLog"/>, so this goes as those sources take the
/// services in their constructors (docs/modular-architecture.md, step 5).
/// </summary>
public static class StaticHostServices
{
    public static IProcesses Processes { get; } = new HostProcesses();

    public static IPluginLog Log { get; } = new HostPluginLog();
}
