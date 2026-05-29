using System.Collections.Generic;
using System.Linq;
using HorizonRadio.Core.Sources.InternetRadio;

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
    public static IReadOnlyList<IAudioSourceFactory> All { get; } = new IAudioSourceFactory[]
    {
        new LocalFileSourceFactory(),
        new SpotifyLibrespotSourceFactory(),
        new InternetRadioSourceFactory(),
        new TestToneSourceFactory(),
    };

    public static IAudioSourceFactory? Find(string id) =>
        All.FirstOrDefault(f => f.Id == id);
}
