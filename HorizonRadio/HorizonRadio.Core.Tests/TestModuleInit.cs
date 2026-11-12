using System.Runtime.CompilerServices;
using HorizonRadio.Core.Hosting;
using HorizonRadio.Core.Settings;
using HorizonRadio.Core.Sources;
using HorizonRadio.Core.Tools;
using HorizonRadio.Sdk.Sources;
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
/// Populates the source catalog once for the whole test assembly, mirroring what the composition
/// root does at startup: the sources come from a container with Core's services and the plugins'. The queue/mix engine resolves content via <see cref="SourceCatalog"/>, so
/// tests that exercise it need the catalog populated; without this it'd be empty (only the app's
/// <c>App</c> calls <see cref="SourceCatalog.Initialize"/>).
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
        SourceCatalog.Initialize(provider.GetServices<IAudioSourceFactory>().InDisplayOrder());
    }
}
