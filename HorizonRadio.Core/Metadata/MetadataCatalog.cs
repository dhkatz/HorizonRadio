using HorizonRadio.Core.Metadata.MusicBrainz;
using HorizonRadio.Core.Metadata.Spotify;

namespace HorizonRadio.Core.Metadata;

public static class MetadataCatalog
{
    public const string NoneId = "none";

    public static IReadOnlyList<IMetadataProviderFactory> All { get; } =
    [
        new SpotifyProviderFactory(),
        new MusicBrainzProviderFactory(),
    ];

    public static IMetadataProviderFactory? Find(string id) =>
        All.FirstOrDefault(f => f.Id == id);
}
