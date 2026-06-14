using System.Collections.Generic;
using System.Linq;
using HorizonRadio.Core.Sources.Local;
using HorizonRadio.Core.Sources.Radio;
using HorizonRadio.Core.Sources.Spotify;
using HorizonRadio.Core.Sources.Test;
using HorizonRadio.Core.Sources.YouTube;

namespace HorizonRadio.Core.Sources;

/// <summary>
/// Static registry of every <see cref="IAudioSourceFactory"/> the app
/// knows about. The UI's source picker is bound to <see cref="All"/>;
/// adding a new source means dropping a new factory here.
///
/// Kept dead simple on purpose — no DI container, no plugin discovery.
/// If we later want runtime-loadable sources we can swap the static
/// list for a scan, but the call sites won't change.
/// </summary>
public static class SourceCatalog
{
    public static IReadOnlyList<IAudioSourceFactory> All { get; } =
    [
        new LocalFileSourceFactory(),
        // Two Spotify entries, by design: the zero-setup Connect receiver (cast from
        // your phone, no developer app) and the driven, mixable source (links, queue,
        // mixes; needs your own Client ID). See SpotifyContentSourceFactory.SourceId.
        new SpotifySourceFactory(),
        new SpotifyContentSourceFactory(),
        new YouTubeSourceFactory(),
        new RadioSourceFactory(),
        new TestToneSourceFactory()
    ];

    public static IAudioSourceFactory? Find(string id) =>
        All.FirstOrDefault(f => f.Id == id);
}
