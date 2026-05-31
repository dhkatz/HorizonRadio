using System.Collections.Generic;
using HorizonRadio.Core.Sources.Config;

namespace HorizonRadio.Core.Metadata.MusicBrainz;

public sealed class MusicBrainzProviderFactory : IMetadataProviderFactory
{
    public const string KeyContact = "contact";

    public string Id => "musicbrainz";
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

    public IMetadataProvider Create(ConfigValues values, MetadataCache cache)
    {
        var contact = values.GetString(KeyContact);
        return new MusicBrainzProvider(cache, contact: contact);
    }
}
