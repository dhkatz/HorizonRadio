using System.Collections.Generic;
using HorizonRadio.Plugins.Abstractions;

namespace HorizonRadio.Core.Sources.YouTube;

/// <summary>The YouTube source plugin.</summary>
public sealed class YouTubeSourcePlugin : ISourcePlugin
{
    public string Id => "youtube";
    public string DisplayName => "YouTube";
    public int SortOrder => 30;
    public IReadOnlyList<IAudioSourceFactory> Sources { get; } = [new YouTubeSourceFactory()];
}
