using System.Collections.Generic;
using HorizonRadio.Plugins.Abstractions;

namespace HorizonRadio.Core.Sources.Radio;

/// <summary>The internet-radio source plugin.</summary>
public sealed class RadioSourcePlugin : ISourcePlugin
{
    public string Id => "radio";
    public string DisplayName => "Internet Radio";
    public IReadOnlyList<IAudioSourceFactory> Sources { get; } = [new RadioSourceFactory()];
}
