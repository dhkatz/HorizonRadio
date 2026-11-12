using HorizonRadio.Sdk.Sources.Config;

namespace HorizonRadio.Sdk.Metadata;

/// <summary>
/// Builds a metadata provider from user config. The registration unit: the host lists factories,
/// renders a config form from <see cref="Schema"/>, and calls <see cref="Create"/> with the
/// completed values to produce a runnable <see cref="IMetadataProvider"/>, again whenever the user
/// changes them. A plugin registers it in its <see cref="IPluginModule"/>, so the services a provider
/// needs (the metadata cache, diagnostics) reach the factory's constructor from the container.
/// </summary>
public interface IMetadataProviderFactory : IOrdered
{
    string DisplayName { get; }
    string? Description { get; }
    IReadOnlyList<ConfigField> Schema { get; }

    IMetadataProvider Create(ConfigValues values);
}
