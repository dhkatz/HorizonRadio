using System.Net.Http;
using HorizonRadio.Core.Tools;
using HorizonRadio.Sdk.Tools;

namespace HorizonRadio.Core.Tests;

/// <summary>
/// Installing and removing a tool through the host, as the Tools page does: it lands where the
/// catalog says, the registry finds it at once, and the Console says how it went.
/// </summary>
public sealed class ToolInstallsTests : IDisposable
{
    private readonly TempDir _dir = TempDir.Create();
    private readonly LinesLog _log = new();
    private readonly FakeInstaller _installer = new();
    private readonly ToolRegistry _registry;
    private readonly ToolInstalls _installs;

    public ToolInstallsTests()
    {
        _registry = new ToolRegistry(new ToolCatalog([new Tool(_installer)],
            Path.Combine(_dir.Path, "tools"), Path.Combine(_dir.Path, "bundled")));
        _installs = new ToolInstalls(_registry, _log);
    }

    public void Dispose() => _dir.Dispose();

    [Fact]
    public async Task An_installed_tool_is_found_at_once_and_removed_again()
    {
        await _installs.InstallAsync("fake");

        Assert.True(_registry.IsInstalled("fake"));
        Assert.Equal(["fake: install: starting…", "fake: install: Downloading", "fake: install: done"], _log.Lines);

        Assert.True(_installs.Uninstall("fake"));
        Assert.False(_registry.IsInstalled("fake"));
        Assert.False(_installs.Uninstall("fake"));
        Assert.Contains("fake: uninstalled", _log.Lines);
    }

    [Fact]
    public async Task A_failed_install_says_why_on_the_Console()
    {
        _installer.Fail = true;

        var ex = await Assert.ThrowsAsync<IOException>(() => _installs.InstallAsync("fake"));
        Assert.Contains($"fake: install failed: {ex.Message}", _log.Lines);
        Assert.False(_registry.IsInstalled("fake"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => _installs.InstallAsync("nothing"));
    }

    [Fact]
    public async Task The_update_check_compares_what_is_installed_with_what_is_expected()
    {
        Assert.Equal(ToolFreshness.Missing, (await _installs.CheckAsync())["fake"]);

        await _installs.InstallAsync("fake");
        Assert.Equal(ToolFreshness.UpToDate, (await _installs.CheckAsync())["fake"]);

        _installer.Expected = "newer";
        Assert.Equal(ToolFreshness.UpdateAvailable, (await _installs.CheckAsync())["fake"]);
        Assert.Contains("tools: updates available: fake", _log.Lines);
    }

    private sealed record Tool(IToolInstaller Installer) : IToolPlugin
    {
        public int SortOrder => 0;

        public ToolDescriptor Descriptor => new("fake", "fake.exe");
    }

    // Writes its "exe" where it's told, the way a real installer does.
    private sealed class FakeInstaller : IToolInstaller
    {
        public bool Fail { get; set; }

        public string Expected { get; set; } = "built";

        public string Kind => "fake";

        public string DisplayName => "Fake";

        public string Description => "A tool for tests.";

        public Task InstallAsync(ToolInstallTarget target, IProgress<ToolInstallProgress>? progress, CancellationToken ct)
        {
            progress?.Report(new ToolInstallProgress("Downloading", 0.5));
            progress?.Report(new ToolInstallProgress("Downloading", 1.0));
            if (Fail) throw new IOException("The download was cut off.");
            Directory.CreateDirectory(target.Directory);
            File.WriteAllText(target.Path, "built");
            return Task.CompletedTask;
        }

        public Task<string?> GetExpectedHashAsync(HttpClient http, CancellationToken ct) => Task.FromResult<string?>(Expected);

        public Task<string?> GetInstalledHashAsync(InstalledTool installed, CancellationToken ct) =>
            Task.FromResult<string?>(File.ReadAllText(installed.Path));
    }
}
