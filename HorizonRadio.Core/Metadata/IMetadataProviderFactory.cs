using HorizonRadio.Core.Sources.Config;

namespace HorizonRadio.Core.Metadata;

public interface IMetadataProviderFactory
{
    string Id { get; }
    string DisplayName { get; }
    string? Description { get; }
    IReadOnlyList<ConfigField> Schema { get; }

    IMetadataProvider Create(ConfigValues values, MetadataCache cache);
}
