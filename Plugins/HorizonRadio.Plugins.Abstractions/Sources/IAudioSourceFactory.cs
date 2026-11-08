using System.Collections.Generic;
using HorizonRadio.Core.Sources.Config;

namespace HorizonRadio.Core.Sources;

/// <summary>
/// Describes one kind of audio source. The factory is the registration unit:
/// the host's source catalog lists them and the UI renders a config form from
/// <see cref="Schema"/>. A factory is one of two kinds, and implements the
/// matching sub-interface: <see cref="IContentSourceFactory"/> (it plays a
/// locator — a URL, folder, link — through the queue) or
/// <see cref="ISelfDrivenSourceFactory"/> (it runs on its own, like Spotify
/// Connect or the test tone, and the host starts it directly).
///
/// Factories must be cheap to construct (no I/O, no thread spinup) —
/// they're enumerated at app startup just to populate the source
/// picker. All heavy lifting happens when a source or item actually plays.
/// </summary>
public interface IAudioSourceFactory
{
    /// <summary>Stable lowercase id used as the persistence key and
    /// as the source's runtime <see cref="IAudioSource.Id"/>.</summary>
    string Id { get; }

    /// <summary>User-facing label, e.g. "Local Files".</summary>
    string DisplayName { get; }

    /// <summary>Optional one-line tagline shown under the picker.</summary>
    string? Description { get; }

    /// <summary>Ordered list of configurable fields. May be empty for
    /// sources with no settings (like the test tone).</summary>
    IReadOnlyList<ConfigField> Schema { get; }
}
