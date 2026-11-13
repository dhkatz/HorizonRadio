using System.Runtime.Versioning;
using HorizonRadio.Core.Audio;
using HorizonRadio.Core.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace HorizonRadio.Platform.Windows;

/// <summary>Windows: the speakers through WASAPI.</summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsPlatform : IHostPlatform
{
    public string Name => "Windows";

    public void ConfigureServices(IServiceCollection services)
    {
        services.Replace(ServiceDescriptor.Singleton<ISpeakers, WasapiSpeakers>());
    }
}
