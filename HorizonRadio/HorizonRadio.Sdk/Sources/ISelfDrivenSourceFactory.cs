using HorizonRadio.Sdk.Sources.Config;

namespace HorizonRadio.Sdk.Sources;

/// <summary>
/// A source that runs on its own rather than playing a locator: Spotify Connect
/// (whatever the user casts to it) or the test tone. The host starts it directly as
/// the active source; it can't be queued or put in a mix. Content-addressable
/// sources implement <see cref="IContentSourceFactory"/> instead and always play
/// through the queue.
/// </summary>
public interface ISelfDrivenSourceFactory : IAudioSourceFactory
{
    /// <summary>Construct a configured source. Implementations may
    /// throw if the values are invalid (e.g. a tool path is missing);
    /// the UI surfaces the message in the Start button feedback.</summary>
    IAudioSource Create(ConfigValues values);
}
