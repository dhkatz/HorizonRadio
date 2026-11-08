namespace HorizonRadio.Core.Sources;

/// <summary>
/// A live stream (internet radio) whose place in the current song can be found from its audio. The
/// stream plays many songs as one track and only roughly knows where each began (from when the
/// station changed its title), and not at all for the song playing when it was tuned in. The host
/// lines the stream's recent audio up against a recording of the song and tells the stream where in
/// the song it is. Reached through <see cref="IPlaybackProgress.LiveAudio"/>.
/// </summary>
public interface ILiveSongAudio
{
    /// <summary>Identifies the song playing now. A new song, or a reconnect, gets a new one, so a
    /// position worked out for one song is never applied to the next.</summary>
    long SongId { get; }

    /// <summary>Where the current song's title change falls on the stream's clock: roughly where the
    /// station started the song. Null for the song that was playing when the station was tuned in.</summary>
    TimeSpan? TitleChange { get; }

    /// <summary>The current song's audio heard so far (since its title change), at most the last
    /// <paramref name="span"/> of it, or null while there's none.</summary>
    LiveAudio? RecentAudio(TimeSpan span);

    /// <summary>The stream's audio between <paramref name="from"/> and <paramref name="to"/> on its
    /// clock, as much of it as has been heard and is still kept (about the last minute and a half),
    /// or null when there's none.</summary>
    LiveAudio? AudioBetween(TimeSpan from, TimeSpan to);

    /// <summary>Set where playback is in the song: <paramref name="songTime"/> into song
    /// <paramref name="songId"/> at <paramref name="streamTime"/> on the stream's clock (the clock
    /// <see cref="LiveAudio.Start"/> is on). Returns false, changing nothing, when another song is
    /// playing now.</summary>
    bool SetSongPosition(long songId, TimeSpan streamTime, TimeSpan songTime);
}

/// <summary>A stretch of a live stream's audio: mono 16-bit samples.</summary>
/// <param name="SongId">The song it's from (<see cref="ILiveSongAudio.SongId"/>).</param>
/// <param name="Samples">The audio, mono, at <paramref name="SampleRate"/>.</param>
/// <param name="SampleRate">Samples per second.</param>
/// <param name="Start">Where the first sample plays on the stream's clock.</param>
public sealed record LiveAudio(long SongId, short[] Samples, int SampleRate, TimeSpan Start)
{
    public TimeSpan Length => TimeSpan.FromSeconds((double)Samples.Length / SampleRate);
}
