using System.Collections.Generic;
using HorizonRadio.Core.Sources.Config;

namespace HorizonRadio.Core.Sources;

/// <summary>
/// Describes one kind of audio source and constructs instances from
/// user-supplied configuration. The factory is the registration unit:
/// <see cref="SourceCatalog"/> lists them, the UI renders a config
/// form from <see cref="Schema"/>, and <see cref="Create"/> turns the
/// completed config into a runnable <see cref="IAudioSource"/>.
///
/// Factories must be cheap to construct (no I/O, no thread spinup) —
/// they're enumerated at app startup just to populate the source
/// picker. All heavy lifting happens inside <see cref="Create"/> or
/// the resulting source's StartAsync.
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

    /// <summary>Construct a configured source. Implementations may
    /// throw if the values are invalid (e.g. directory doesn't exist);
    /// the UI surfaces the message in the Start button feedback.</summary>
    IAudioSource Create(ConfigValues values);
}
