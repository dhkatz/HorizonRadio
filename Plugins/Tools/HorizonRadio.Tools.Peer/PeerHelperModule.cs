using HorizonRadio.Sdk;
using HorizonRadio.Sdk.Peers;
using Microsoft.Extensions.DependencyInjection;

namespace HorizonRadio.Tools.Peer;

/// <summary>Provides the peer network: the bundled helper, with this profile's identity, finding
/// peers on the internet and the local network.</summary>
public sealed class PeerHelperModule : IPluginModule
{
    public void ConfigureServices(IServiceCollection services) =>
        services.AddSingleton<IPeerNetworkFactory, PeerHelperNetworks>();
}

/// <summary>Starts <see cref="PeerHelper"/> as the app's peer network.</summary>
public sealed class PeerHelperNetworks(IProcesses processes, IAppFolders folders) : IPeerNetworkFactory
{
    public async Task<IPeerNetwork> StartAsync(CancellationToken ct) =>
        await PeerHelper.StartAsync(processes, PeerHelper.KeyPath(folders), PeerDiscovery.All, ct: ct).ConfigureAwait(false);
}
