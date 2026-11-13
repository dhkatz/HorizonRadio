using System.Runtime.Versioning;
using HorizonRadio.Core.Audio;
using HorizonRadio.Core.Hosting;
using HorizonRadio.Sdk;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace HorizonRadio.Platform.Windows;

/// <summary>Windows: the speakers through WASAPI, and the tools the app starts tied to it (a job
/// object).</summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsPlatform : IHostPlatform
{
    public string Name => "Windows";

    public void ConfigureServices(IServiceCollection services)
    {
        services.Replace(ServiceDescriptor.Singleton<ISpeakers, WasapiSpeakers>());
        services.Decorate<IProcesses>((inner, _) => new AppTiedProcesses(inner));
    }
}
