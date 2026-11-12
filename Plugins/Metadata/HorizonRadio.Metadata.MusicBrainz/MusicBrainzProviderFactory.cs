using HorizonRadio.Sdk;
using HorizonRadio.Sdk.Metadata;
using HorizonRadio.Sdk.Sources.Config;

namespace HorizonRadio.Metadata.MusicBrainz;

public sealed class MusicBrainzProviderFactory(IMetadataCache cache, IMetadataTrace trace) : IMetadataProviderFactory
{
    public const string KeyContact = "contact";

    public string Id => "musicbrainz";
    public int SortOrder => 30;
    public string DisplayName => "MusicBrainz";
    public string? Description => "Free / no-credentials lookup against MusicBrainz + Cover Art Archive. Works for any track that's been catalogued there.";

    public IReadOnlyList<ConfigField> Schema { get; } =
    [
        new TextField(
            Key:         KeyContact,
            Label:       "Contact (optional)",
            Default:     "",
            Placeholder: "you@example.com",
            Description: "Email or URL included in the User-Agent. MB's ToS asks for one; it isn't checked, but it's polite."),
    ];

    public IMetadataProvider Create(ConfigValues values)
    {
        var contact = values.GetString(KeyContact);
        return new MusicBrainzProvider(cache, trace, contact: contact);
    }
}
