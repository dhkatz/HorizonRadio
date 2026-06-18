using HorizonRadio.Core.Sources.Config;
using HorizonRadio.Plugins.Abstractions;

namespace HorizonRadio.Core.Metadata;

/// <summary>
/// Builds a metadata provider from user config. The registration unit: the host lists factories,
/// renders a config form from <see cref="Schema"/>, and calls <see cref="Create"/> with the
/// completed values plus an <see cref="IPluginContext"/> (which carries the host's metadata cache
/// and other shared services) to produce a runnable <see cref="IMetadataProvider"/>.
/// </summary>
public interface IMetadataProviderFactory
{
    string Id { get; }
    string DisplayName { get; }
    string? Description { get; }
    IReadOnlyList<ConfigField> Schema { get; }

    IMetadataProvider Create(ConfigValues values, IPluginContext context);
}
