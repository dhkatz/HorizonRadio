using System.Diagnostics;
using HorizonRadio.Core.Diagnostics;
using Xunit;

namespace HorizonRadio.Core.Tests;

// LogFile is process-wide; keep its tests off the parallel runners.
[Collection(nameof(LogFileTests))]
[CollectionDefinition(nameof(LogFileTests), DisableParallelization = true)]
public sealed class LogFileTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "hzn-logs-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        LogFile.Stop();
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
        GC.SuppressFinalize(this);
    }

    [Fact]
    public void Writes_console_lines_and_debug_lines_to_the_session_file()
    {
        var marker = Guid.NewGuid().ToString("N");
        ProcessConsole.Append("test", $"before {marker}");

        LogFile.Start(_dir);
        var path = LogFile.CurrentPath!;
        ProcessConsole.Append("spotify", $"console {marker}");
        Trace.Write("[hzn-queue] ");
        Trace.WriteLine($"item failed {marker}");
        LogFile.Stop();

        var lines = File.ReadAllLines(path);
        Assert.Contains(lines, l => l.EndsWith($"[test] before {marker}", StringComparison.Ordinal));
        Assert.Contains(lines, l => l.EndsWith($"[spotify] console {marker}", StringComparison.Ordinal));
        Assert.Contains(lines, l => l.EndsWith($"[app] [hzn-queue] item failed {marker}", StringComparison.Ordinal));
    }

    [Fact]
    public void Keeps_only_the_newest_sessions()
    {
        Directory.CreateDirectory(_dir);
        for (var i = 0; i < LogFile.KeepFiles + 3; i++)
            File.WriteAllText(Path.Combine(_dir, $"horizon-radio-20260101-0000{i:00}.log"), "");

        LogFile.Start(_dir);
        var current = Path.GetFileName(LogFile.CurrentPath!);
        LogFile.Stop();

        var kept = Directory.GetFiles(_dir, "horizon-radio-*.log").Select(Path.GetFileName).ToList();
        Assert.Equal(LogFile.KeepFiles, kept.Count);
        Assert.Contains(current, kept);
        Assert.DoesNotContain("horizon-radio-20260101-000000.log", kept);
    }
}
