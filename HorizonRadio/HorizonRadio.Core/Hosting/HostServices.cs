using System.Diagnostics;
using HorizonRadio.Core.Diagnostics;
using HorizonRadio.Core.IO;
using HorizonRadio.Core.Sources;
using HorizonRadio.Core.Tools;
using HorizonRadio.Sdk;

namespace HorizonRadio.Core.Hosting;

// The host's own implementations of the services plugins take (registered by AddHorizonCore). Each
// is the app's existing code for the job; a plugin module can replace or wrap any of them.

/// <summary>The Console tab (<see cref="ProcessConsole"/>) and the session log.</summary>
internal sealed class HostPluginLog : IPluginLog
{
    public void Write(string tag, string text) => ProcessConsole.AppendBlock(tag, text);

    public void Trace(string tag, string text) => System.Diagnostics.Trace.WriteLine($"[hzn-{tag}] {text}");
}

/// <summary>Starts a process and ties it to the app (<see cref="ChildProcesses.TieToApp"/>).</summary>
internal sealed class HostProcesses : IProcesses
{
    public Process Start(ProcessStartInfo info)
    {
        var process = Process.Start(info) ?? throw new InvalidOperationException($"Couldn't start {info.FileName}.");
        ChildProcesses.TieToApp(process);
        return process;
    }
}

/// <summary>The profile's folders (<see cref="AppPaths"/>).</summary>
internal sealed class HostAppFolders : IAppFolders
{
    public string? Profile => AppPaths.Profile;

    public string Data(params string[] parts) => AppPaths.In(parts);

    public string Label(string name) => AppPaths.Label(name);
}

/// <summary>The configured, installed or bundled tool (<see cref="ToolResolver"/>, <see cref="ToolsPaths"/>).</summary>
internal sealed class HostToolLocator : IToolLocator
{
    public string? Find(string kind, string? configuredPath = null) => ToolResolver.Resolve(configuredPath, kind);

    public string InstallPath(string kind) => ToolsPaths.PathFor(kind);
}
