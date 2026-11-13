using System.Diagnostics;
using System.Runtime.Versioning;
using HorizonRadio.Sdk;

namespace HorizonRadio.Platform.Windows;

/// <summary>The host's <see cref="IProcesses"/>, with each process it starts tied to the app
/// (<see cref="ChildProcesses.TieToApp"/>), so no tool outlives it.</summary>
[SupportedOSPlatform("windows")]
public sealed class AppTiedProcesses(IProcesses inner) : IProcesses
{
    public Process Start(ProcessStartInfo info)
    {
        var process = inner.Start(info);
        ChildProcesses.TieToApp(process);
        return process;
    }
}
