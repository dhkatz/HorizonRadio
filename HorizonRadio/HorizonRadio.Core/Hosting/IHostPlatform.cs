using HorizonRadio.Sdk;
using Microsoft.Extensions.DependencyInjection;

namespace HorizonRadio.Core.Hosting;

/// <summary>
/// The OS a host runs on: the code that reaches it (<c>HorizonRadio.Platform</c>; on Windows the
/// speakers through WASAPI). Core declares what it needs from the OS as interfaces and registers a
/// portable default for each (no speakers, say); the platform registers its own over them, like a
/// plugin module, after Core's and before the front end's and the plugins', so either can still
/// replace one. A host with no platform (<see cref="HorizonHostOptions.Platform"/> null, as in a
/// test) runs on the defaults.
/// </summary>
public interface IHostPlatform
{
    /// <summary>The OS, for the log ("Windows").</summary>
    string Name { get; }

    /// <summary>What secrets are protected with. The host needs it before the container (the
    /// settings open first, and their secret settings are protected on disk); register the same
    /// one in <see cref="ConfigureServices"/>.</summary>
    ISecretProtector Secrets { get; }

    void ConfigureServices(IServiceCollection services);
}
