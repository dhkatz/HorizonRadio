using System.Collections.Generic;
using HorizonRadio.Sdk;
using HorizonRadio.Sdk.Metadata;
using HorizonRadio.Sdk.Sources.Config;

namespace HorizonRadio.Metadata.Apple;

public sealed class ItunesProviderFactory(IMetadataCache cache, IMetadataTrace trace) : IMetadataProviderFactory
{
    public const string KeyCountry = "country";

    public string Id => "itunes";
    public string DisplayName => "Apple Music (iTunes)";
    public string? Description => "Free / no-credentials lookup against Apple's iTunes catalog. Strong coverage and cover art, including Japanese / Vocaloid music.";

    public IReadOnlyList<ConfigField> Schema { get; } =
    [
        new TextField(
            Key:         KeyCountry,
            Label:       "Preferred store country (optional)",
            Default:     "",
            Placeholder: "us, jp, gb…",
            Description: "Two-letter Apple storefront to search first. Leave blank for US. Japan and US are always tried as fallbacks, so Japanese releases are found regardless."),
    ];

    public IMetadataProvider Create(ConfigValues values)
        => new ItunesProvider(cache, trace, country: values.GetString(KeyCountry));
}
