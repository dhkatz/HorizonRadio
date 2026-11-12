using System.Runtime.CompilerServices;
using HorizonRadio.Core.Hosting;
using HorizonRadio.Core.Settings;
using HorizonRadio.Core.Sources;
using HorizonRadio.Core.Tools;
using HorizonRadio.Sdk.Tools;
using HorizonRadio.Source.Local;
using HorizonRadio.Source.Radio;
using HorizonRadio.Source.Spotify;
using HorizonRadio.Source.Test;
using HorizonRadio.Source.YouTube;
using HorizonRadio.Tools.YtDlp;
using Microsoft.Extensions.DependencyInjection;

namespace HorizonRadio.Core.Tests;

/// <summary>
/// Builds the shipped sources once for the whole test assembly, the way the app does at startup:
/// a container with Core's services and the plugins', whose <see cref="SourceCatalog"/> becomes
/// <see cref="TestSources.Catalog"/>. Tests that build the queue or mix engine hand it that.
/// </summary>
internal static class TestModuleInit
{
    [ModuleInitializer]
    internal static void Init()
    {
        // Seed the tool catalog first, mirroring the host (App seeds it before the source plugins).
        // Some source factories probe for their tool (Spotify → librespot) in their ctor, so the
        // catalog must be populated before the source factories are constructed.
        ToolCatalog.Initialize(
        [
            new ToolDescriptor(ToolKind.YtDlp, "yt-dlp.exe"),
            new ToolDescriptor(ToolKind.Ffmpeg, "ffmpeg.exe"),
            new ToolDescriptor(ToolKind.Librespot, "librespot.exe"),
            new ToolDescriptor(ToolKind.Soloist, "soloist.exe"),
            new ToolDescriptor(ToolKind.TitleModel, "title-model.gguf", IsData: true),
        ]);

        Type[] plugins = [typeof(LocalSourcePlugin), typeof(SpotifySourcePlugin), typeof(YouTubeSourcePlugin),
            typeof(RadioSourcePlugin), typeof(TestToneSourcePlugin), typeof(YtDlpToolPlugin)];
        var services = new ServiceCollection()
            .AddHorizonCore(SettingsStore.CreateInMemory())
            .AddPlugins([.. plugins.SelectMany(p => p.Assembly.GetExportedTypes())]);
        var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true });
        TestSources.Catalog = provider.GetRequiredService<SourceCatalog>();
    }
}
