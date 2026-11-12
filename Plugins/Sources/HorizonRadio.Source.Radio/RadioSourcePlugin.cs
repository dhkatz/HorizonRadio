using System.Collections.Generic;
using HorizonRadio.Sdk;
using HorizonRadio.Sdk.Sources;

namespace HorizonRadio.Source.Radio;

/// <summary>The internet-radio source plugin.</summary>
public sealed class RadioSourcePlugin : ISourcePlugin
{
    public string Id => "radio";
    public string DisplayName => "Internet Radio";
    public int SortOrder => 40;
    public IReadOnlyList<IAudioSourceFactory> Sources { get; } = [new RadioSourceFactory()];
}
