using System.Diagnostics;
using HorizonRadio.Platform.Windows;
using Xunit;

namespace HorizonRadio.Core.Tests;

public sealed class ChildProcessesTests
{
    [Fact]
    public void A_started_tool_is_tied_to_the_app()
    {
        if (!OperatingSystem.IsWindows()) return;

        using var child = Process.Start(new ProcessStartInfo("cmd.exe", "/c ping -n 30 127.0.0.1 >nul")
        {
            CreateNoWindow = true,
            UseShellExecute = false,
        })!;
        try
        {
            // The job is set up (kill on close) and the process is in it.
            Assert.True(ChildProcesses.TieToApp(child));
        }
        finally
        {
            child.Kill(entireProcessTree: true);
        }
    }
}
