using System.Collections.Generic;
using HorizonRadio.Plugins.Abstractions;

namespace HorizonRadio.Core.Sources.Test;

/// <summary>The diagnostic test-tone source plugin.</summary>
public sealed class TestToneSourcePlugin : ISourcePlugin
{
    public string Id => "testtone";
    public string DisplayName => "Test Tone";
    public int SortOrder => 50;
    public IReadOnlyList<IAudioSourceFactory> Sources { get; } = [new TestToneSourceFactory()];
}
