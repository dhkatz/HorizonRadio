using System.Collections.Generic;
using HorizonRadio.Sdk;
using HorizonRadio.Sdk.Sources;

namespace HorizonRadio.Source.YouTube;

/// <summary>The YouTube source plugin.</summary>
public sealed class YouTubeSourcePlugin : ISourcePlugin
{
    public string Id => "youtube";
    public string DisplayName => "YouTube";
    public int SortOrder => 30;
    public IReadOnlyList<IAudioSourceFactory> Sources { get; } = [new YouTubeSourceFactory()];
}
