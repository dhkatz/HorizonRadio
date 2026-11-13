using HorizonRadio.Core.Audio;
using HorizonRadio.Core.IO;
using HorizonRadio.Core.Settings;
using HorizonRadio.Platform;
using HorizonRadio.Platform.Windows;
using Microsoft.Extensions.DependencyInjection;

namespace HorizonRadio.Core.Tests;

/// <summary>
/// Core reaches the OS through interfaces with portable defaults, and the platform
/// (<c>HorizonRadio.Platform</c>) registers the OS's own over them.
/// </summary>
public sealed class PlatformTests : IDisposable
{
    private readonly TempDir _dir = TempDir.Create();

    public void Dispose() => _dir.Dispose();

    [Fact]
    public void Without_a_platform_there_are_no_speakers_and_monitoring_never_opens_one()
    {
        using var settings = SettingsStore.CreateInMemory();
        using var provider = new ServiceCollection()
            .AddHorizonCore(settings, new AppFolders(_dir.Path))
            .BuildServiceProvider();

        var speakers = provider.GetRequiredService<ISpeakers>();
        Assert.IsType<NoSpeakers>(speakers);
        using var preview = new PreviewController(new TeePcmSink(null), settings, speakers);
        Assert.Empty(preview.Devices);
        preview.SetEnabled(true);
        Assert.Equal(SpeakerState.Faulted, preview.State);
    }

    [Fact]
    public void On_Windows_the_platform_brings_its_speakers()
    {
        if (!OperatingSystem.IsWindows()) return;

        var platform = Assert.IsType<WindowsPlatform>(HostPlatforms.Current());
        using var settings = SettingsStore.CreateInMemory();
        var services = new ServiceCollection().AddHorizonCore(settings, new AppFolders(_dir.Path));
        platform.ConfigureServices(services);
        using var provider = services.BuildServiceProvider();

        Assert.IsType<WasapiSpeakers>(provider.GetRequiredService<ISpeakers>());
    }
}
