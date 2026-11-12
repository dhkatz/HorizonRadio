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
using Microsoft.Extensions.DependencyInjection.Extensions;

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
        Type[] plugins = [typeof(LocalSourcePlugin), typeof(SpotifySourcePlugin), typeof(YouTubeSourcePlugin),
            typeof(RadioSourcePlugin), typeof(TestToneSourcePlugin), typeof(YtDlpToolPlugin),
            typeof(HorizonRadio.Tools.FFmpeg.FfmpegToolPlugin), typeof(HorizonRadio.Tools.Librespot.LibrespotToolPlugin),
            typeof(HorizonRadio.Tools.Soloist.SoloistToolPlugin), typeof(HorizonRadio.Tools.TitleModel.TitleModelToolPlugin)];
        var services = new ServiceCollection()
            .AddHorizonCore(SettingsStore.CreateInMemory())
            .AddPlugins([.. plugins.SelectMany(p => p.Assembly.GetExportedTypes())]);
        // Tools are looked for in empty folders, so no test depends on (or finds) this PC's.
        var noTools = Directory.CreateTempSubdirectory("hzr-test-tools-").FullName;
        services.Replace(ServiceDescriptor.Singleton(sp =>
            new ToolCatalog(sp.GetServices<IToolPlugin>(), Path.Combine(noTools, "installed"), Path.Combine(noTools, "bundled"))));
        var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true });
        TestSources.Catalog = provider.GetRequiredService<SourceCatalog>();
    }
}
