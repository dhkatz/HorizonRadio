using HorizonRadio.Sdk;
using HorizonRadio.Sdk.Tools;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace HorizonRadio.Tools.YtDlp;

/// <summary>yt-dlp tool plugin — resolves URLs into direct audio streams. Tracks upstream's latest.</summary>
public sealed class YtDlpToolPlugin : IToolPlugin, IPluginModule
{
    public ToolDescriptor Descriptor { get; } = new(ToolKind.YtDlp, "yt-dlp.exe");
    public IToolInstaller Installer { get; } = new YtDlpInstaller();

    /// <summary>The yt-dlp client sources run it through.</summary>
    public void ConfigureServices(IServiceCollection services) => services.TryAddSingleton<YtDlpClient>();
}
