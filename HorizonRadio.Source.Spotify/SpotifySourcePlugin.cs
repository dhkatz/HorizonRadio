using System.Collections.Generic;
using HorizonRadio.Plugins.Abstractions;

namespace HorizonRadio.Core.Sources.Spotify;

/// <summary>
/// The Spotify source plugin. Ships two factories by design: the zero-config Connect receiver
/// (<see cref="SpotifySourceFactory"/>) and the driven, mixable source
/// (<see cref="SpotifyContentSourceFactory"/>) — both drive one librespot device.
/// </summary>
public sealed class SpotifySourcePlugin : ISourcePlugin
{
    public string Id => "spotify";
    public string DisplayName => "Spotify";
    public IReadOnlyList<IAudioSourceFactory> Sources { get; } =
        [new SpotifySourceFactory(), new SpotifyContentSourceFactory()];
}
