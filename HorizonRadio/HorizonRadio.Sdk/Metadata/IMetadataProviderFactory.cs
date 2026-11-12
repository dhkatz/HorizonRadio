using HorizonRadio.Sdk.Sources.Config;

namespace HorizonRadio.Sdk.Metadata;

/// <summary>
/// Builds a metadata provider from user config. The registration unit: the host lists factories,
/// renders a config form from <see cref="Schema"/>, and calls <see cref="Create"/> with the
/// completed values to produce a runnable <see cref="IMetadataProvider"/>. The services a provider
/// needs (the metadata cache, diagnostics) reach the factory through its plugin's constructor.
/// </summary>
public interface IMetadataProviderFactory
{
    string Id { get; }
    string DisplayName { get; }
    string? Description { get; }
    IReadOnlyList<ConfigField> Schema { get; }

    IMetadataProvider Create(ConfigValues values);
}
