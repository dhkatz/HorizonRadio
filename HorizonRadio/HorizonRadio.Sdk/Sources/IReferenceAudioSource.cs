namespace HorizonRadio.Sdk.Sources;

/// <summary>
/// Optional capability for an <see cref="IAudioSourceFactory"/>: fetch a recording's audio whole and
/// as fast as it downloads, rather than played out in real time, so the host can analyse it. The host
/// uses it to get a reference recording of the song a radio station is playing (a video the song's
/// catalog entry links, or a search hit) and line the station's audio up against it.
/// </summary>
public interface IReferenceAudioSource
{
    /// <summary>Whether <paramref name="locator"/> (a page URL) is something this source can fetch.</summary>
    bool CanFetch(string locator);

    /// <summary>
    /// The recording at <paramref name="locator"/> as mono 16-bit samples at
    /// <paramref name="sampleRate"/>, or null when the source isn't usable right now (a tool isn't
    /// installed). Recordings longer than <paramref name="maxLength"/> aren't fetched (null): a long
    /// mix or stream isn't a song. Throws when the fetch itself failed.
    /// </summary>
    Task<ReferenceAudio?> FetchAsync(string locator, int sampleRate, TimeSpan maxLength, CancellationToken ct);
}

/// <summary>A recording fetched for analysis.</summary>
/// <param name="Locator">The page it came from.</param>
/// <param name="Samples">The audio, mono 16-bit, at <paramref name="SampleRate"/>.</param>
/// <param name="SampleRate">Samples per second.</param>
public sealed record ReferenceAudio(string Locator, short[] Samples, int SampleRate)
{
    public TimeSpan Duration => TimeSpan.FromSeconds((double)Samples.Length / SampleRate);
}
