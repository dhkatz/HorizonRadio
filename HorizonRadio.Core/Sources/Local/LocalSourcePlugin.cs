using System.Collections.Generic;
using HorizonRadio.Plugins.Abstractions;

namespace HorizonRadio.Core.Sources.Local;

/// <summary>The local-files source plugin.</summary>
public sealed class LocalSourcePlugin : ISourcePlugin
{
    public string Id => "local";
    public string DisplayName => "Local Files";
    public IReadOnlyList<IAudioSourceFactory> Sources { get; } = [new LocalFileSourceFactory()];
}
