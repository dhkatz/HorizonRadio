using System.Diagnostics;
using HorizonRadio.Core.Hosting;
using HorizonRadio.Core.Settings;
using HorizonRadio.Core.Sources.Config;
using HorizonRadio.Sdk;
using HorizonRadio.Sdk.Sources;
using HorizonRadio.Sdk.Sources.Config;
using HorizonRadio.Source.YouTube;
using HorizonRadio.Tools.YtDlp;

namespace HorizonRadio.Core.Tests;

/// <summary>
/// The host's services for plugins (process runs, audio caches, a source's saved settings), and the
/// YouTube source finding its tools through them.
/// </summary>
public sealed class HostServicesTests : IDisposable
{
    private readonly TempDir _dir = TempDir.Create();

    public void Dispose() => _dir.Dispose();

    [Fact]
    public async Task A_run_to_its_end_returns_the_output_and_logs_the_command()
    {
        var log = new Lines();
        var info = new ProcessStartInfo { FileName = "dotnet" };
        info.ArgumentList.Add("--version");

        var run = await new PlainProcesses().RunToEndAsync(info, log, "dotnet", CancellationToken.None);

        Assert.Equal(0, run.ExitCode);
        Assert.False(string.IsNullOrWhiteSpace(run.Output));
        Assert.Contains(log.Written, l => l == "dotnet: $ dotnet --version");
    }

    [Fact]
    public void A_source_gets_one_cache_in_its_own_folder()
    {
        using var caches = new HostAudioCaches(new Folders(_dir.Path));

        var youtube = caches.ForSource("youtube");

        Assert.Same(youtube, caches.ForSource("YouTube"));
        Assert.Equal(Path.Combine(_dir.Path, "cache", "audio", "youtube"), youtube.Directory);
        Assert.NotSame(youtube, caches.ForSource("radio"));
    }

    [Fact]
    public void A_sources_settings_are_read_as_saved()
    {
        using var settings = SettingsStore.CreateInMemory();
        var store = new SourceConfigStore(settings);
        var youtube = TestSources.YouTube;
        var values = store.Load(youtube.Id, youtube.Schema);
        values.Set(YouTubeSourceFactory.KeyYtDlp, @"C:\tools\yt-dlp.exe");
        store.Save(youtube.Id, values);

        var read = new HostSourceSettings(store).Load(youtube);

        Assert.Equal(@"C:\tools\yt-dlp.exe", read.GetString(YouTubeSourceFactory.KeyYtDlp));
    }

    [Fact]
    public async Task YouTube_search_without_yt_dlp_finds_nothing_and_starts_nothing()
    {
        var youtube = YouTube(ytDlp: null);

        Assert.Empty(await youtube.SearchAsync("melt", 5));
        Assert.Null(await youtube.FetchAsync("https://youtu.be/abc", 11025, TimeSpan.FromMinutes(10), CancellationToken.None));
        var ex = Assert.Throws<InvalidOperationException>(() => youtube.CreatePlayer(new ConfigValues()));
        Assert.Contains("yt-dlp", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task YouTube_search_uses_the_installed_yt_dlp_when_none_is_set()
    {
        // The settings name no yt-dlp; the tool locator has the Tools tab's copy, which search runs
        // (and the test's process launching refuses, which shows it tried).
        var youtube = YouTube(ytDlp: @"C:\tools\yt-dlp\yt-dlp.exe");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => youtube.SearchAsync("melt", 5));
        Assert.Contains(@"C:\tools\yt-dlp\yt-dlp.exe", ex.Message, StringComparison.Ordinal);
    }

    private YouTubeSourceFactory YouTube(string? ytDlp) => new(
        new NoSettings(), new Tools(ytDlp), new YtDlpClient(NoProcesses.Instance, NoLog.Instance),
        NoProcesses.Instance, NoLog.Instance, new HostAudioCaches(new Folders(_dir.Path)));

    private sealed class PlainProcesses : IProcesses
    {
        public Process Start(ProcessStartInfo info) => Process.Start(info)!;
    }

    private sealed class Lines : IPluginLog
    {
        public List<string> Written { get; } = [];
        public void Write(string tag, string text) => Written.Add($"{tag}: {text}");
        public void Trace(string tag, string text) { }
    }

    private sealed class Folders(string root) : IAppFolders
    {
        public string? Profile => null;
        public string Data(params string[] parts) => Path.Combine([root, .. parts]);
        public string Label(string name) => name;
    }

    private sealed class NoSettings : ISourceSettings
    {
        public ConfigValues Load(IAudioSourceFactory source) => new();
    }

    // Finds yt-dlp at the given path (whatever's configured), and no ffmpeg.
    private sealed class Tools(string? ytDlp) : IToolLocator
    {
        public string? Find(string kind, string? configuredPath = null) => kind == "yt-dlp" ? ytDlp : null;
        public string InstallPath(string kind) => throw new NotSupportedException();
    }
}
