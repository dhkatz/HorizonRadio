using HorizonRadio.Core.Audio;
using HorizonRadio.Core.Games;
using HorizonRadio.Core.Playback;
using HorizonRadio.Core.Settings;
using HorizonRadio.Core.Sources;
using HorizonRadio.Core.Sources.Config;
using HorizonRadio.Core.Sources.Mixes;
using HorizonRadio.Sdk.Sources;

namespace HorizonRadio.Core.Tests;

/// <summary>
/// The engine pauses what's playing when it can't be heard, once, without the window, and never
/// plays again by itself.
/// </summary>
public sealed class OutputGuardTests
{
    [Fact]
    public async Task A_song_with_no_speaker_to_play_on_is_paused_once_and_the_loss_reported()
    {
        using var settings = SettingsStore.CreateInMemory();
        var config = new SourceConfigStore(settings);
        var tee = new TeePcmSink(null);
        using var preview = new PreviewController(tee, settings, new NoSpeakers());
        preview.SetEnabled(true); // monitoring on, and no speaker opens
        await using var runner = new SourceRunner(tee);
        using var guard = new OutputGuard(runner, preview, new NoGameIntegration());
        var losses = new List<OutputLoss>();
        guard.OutputLost += l => { lock (losses) losses.Add(l); };
        await using var router = new PlaybackRouter(runner, config, new MixStore(),
            new MixContentResolver(config, TestSources.Catalog));
        using var dir = TempDir.Create();
        dir.WriteSilentWav("song.wav", seconds: 10);

        await router.Player.EnqueueAsync(new ContentRef("local", dir.Path));

        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (runner.ActiveSource is not ITransportControls { IsPaused: true } && DateTime.UtcNow < deadline)
            await Task.Delay(20);
        var transport = Assert.IsAssignableFrom<ITransportControls>(runner.ActiveSource);
        Assert.True(transport.IsPaused);
        Assert.False(guard.IsOutputAvailable);
        lock (losses) Assert.Equal([OutputLoss.Speaker], losses);

        // The user plays it anyway: that stands, and isn't paused again while nothing changes.
        guard.UserTookControl();
        await transport.SetPausedAsync(false);
        await Task.Delay(300);
        Assert.False(transport.IsPaused);
    }
}
