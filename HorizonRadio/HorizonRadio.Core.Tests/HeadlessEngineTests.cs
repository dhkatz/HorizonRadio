using HorizonRadio.Core.Games;
using HorizonRadio.Core.Hosting;
using HorizonRadio.Core.Input;
using HorizonRadio.Core.IO;
using HorizonRadio.Core.Playback;
using HorizonRadio.Core.Sources.Mixes;
using HorizonRadio.Core.Tools;
using HorizonRadio.Sdk.Models;
using HorizonRadio.Sdk.Peers;
using HorizonRadio.Sdk.Sources;
using HorizonRadio.Sdk.Sources.Config;
using HorizonRadio.Sdk.Tools;
using HorizonRadio.Source.Test;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace HorizonRadio.Core.Tests;

/// <summary>
/// The whole engine, as a front end starts it (<see cref="HorizonHost"/>, then
/// <see cref="HorizonEngine"/>), with no window and no game, on a temporary folder: it plays,
/// keeps what it played, and finds it again when the next host starts on the same folder.
/// </summary>
public sealed class HeadlessEngineTests : IDisposable
{
    private readonly TempDir _dir = TempDir.Create();

    public void Dispose() => _dir.Dispose();

    // The test tone as the only plugin; no tools, no global input hooks, and a peer network that
    // must not be needed (nothing here pairs or adds a friend).
    private HorizonHost Build() => HorizonHost.Build(new HorizonHostOptions
    {
        Folders = new AppFolders(_dir.Path),
        PluginTypes = typeof(TestToneSourcePlugin).Assembly.GetExportedTypes(),
        WriteLogFile = false,
        ConfigureServices = services =>
        {
            services.RemoveAll<IInputBackend>();
            services.AddSingleton<IPeerNetworkFactory, NoPeerNetwork>();
            services.Replace(ServiceDescriptor.Singleton(sp => new ToolCatalog(sp.GetServices<IToolPlugin>(),
                Path.Combine(_dir.Path, "no-tools"), Path.Combine(_dir.Path, "no-bundled-tools"))));
        },
    });

    [Fact]
    public async Task The_engine_runs_headless_and_keeps_its_files_in_the_hosts_folder()
    {
        await using (var host = Build())
        {
            Assert.Null(host.ChosenGame()); // a fresh folder: the front end would ask
            var engine = host.StartEngine(NoGameIntegration.Id);
            engine.Start();

            var played = new TaskCompletionSource<Track>(TaskCreationOptions.RunContinuationsAsynchronously);
            engine.Runner.TrackChanged += t => played.TrySetResult(t);
            var tone = (ISelfDrivenSourceFactory)engine.Sources.Find("testtone")!;
            await engine.Router.StartSourceAsync(tone, new ConfigValues());
            Assert.Equal("Test Tone", (await played.Task.WaitAsync(TimeSpan.FromSeconds(10))).Title);
            await engine.Router.StopAsync();

            engine.Mixes.AddOrUpdate(new Mix("drive", "Drive", [new ContentRef("local", _dir.Path)]));
            engine.Mixes.SaveToDisk();
            host.Settings.Set(PlaybackSettings.Shuffle, true);
        } // stops the engine (writing the history), then the container, then the settings

        Assert.True(File.Exists(Path.Combine(_dir.Path, "settings.json")));
        Assert.True(File.Exists(Path.Combine(_dir.Path, "history.json")));
        Assert.True(File.Exists(Path.Combine(_dir.Path, "mixes.json")));

        await using var next = Build();
        var again = next.StartEngine(NoGameIntegration.Id);
        Assert.Contains(again.HistoryStore.All, e => e.Title == "Test Tone");
        Assert.NotNull(again.Mixes.Get("drive"));
        Assert.True(next.Settings.Get(PlaybackSettings.Shuffle));
    }

    private sealed class NoPeerNetwork : IPeerNetworkFactory
    {
        public Task<IPeerNetwork> StartAsync(CancellationToken ct) =>
            throw new InvalidOperationException("The headless test started the peer helper.");
    }
}
