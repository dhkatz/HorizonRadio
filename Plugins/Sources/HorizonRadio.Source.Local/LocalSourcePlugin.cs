using System.Collections.Generic;
using HorizonRadio.Sdk;
using HorizonRadio.Sdk.Sources;

namespace HorizonRadio.Source.Local;

/// <summary>The local-files source plugin.</summary>
public sealed class LocalSourcePlugin : ISourcePlugin
{
    public string Id => "local";
    public string DisplayName => "Local Files";
    public int SortOrder => 10;
    public IReadOnlyList<IAudioSourceFactory> Sources { get; } = [new LocalFileSourceFactory()];
}
