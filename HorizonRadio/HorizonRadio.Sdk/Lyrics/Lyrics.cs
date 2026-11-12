namespace HorizonRadio.Sdk.Lyrics;

/// <summary>One sung word (or syllable) of a word-synced line.</summary>
/// <param name="Time">When the word starts, from the start of the song.</param>
/// <param name="End">When it stops being sung; null means "until the next word".</param>
/// <param name="Text">The raw text including any trailing space, so concatenating a line's
/// words rebuilds the line.</param>
public sealed record LyricWord(TimeSpan Time, TimeSpan? End, string Text);

/// <summary>One line of lyrics.</summary>
/// <param name="Time">When the line starts, from the start of the song. Zero for every line of
/// unsynced lyrics.</param>
/// <param name="End">When the line ends; null means "until the next line starts".</param>
/// <param name="Words">Word-level timing, when the source has it (enhanced LRC, TTML).</param>
/// <param name="Voice">Which singer, for duets: lines of different voices may overlap.</param>
/// <param name="Background">Backing vocals or ad-libs sung under a main line.</param>
public sealed record LyricLine(
    TimeSpan Time,
    string Text,
    TimeSpan? End = null,
    IReadOnlyList<LyricWord>? Words = null,
    string? Voice = null,
    bool Background = false);

/// <summary>
/// A song's lyrics as a provider found them. <see cref="Synced"/> false means the lines carry no
/// timing (all zero), and they're shown as a sheet rather than following the song.
/// </summary>
/// <param name="ProviderId">The provider that found them.</param>
/// <param name="Credit">Attribution to show with the lyrics ("Lyrics from LRCLIB").</param>
/// <param name="Variants">Other versions of the same lyrics the listener can switch to: a
/// romanization of the original, translations. <see cref="Lines"/> is the original.</param>
public sealed record SongLyrics(
    string ProviderId,
    bool Synced,
    IReadOnlyList<LyricLine> Lines,
    string? Credit = null,
    IReadOnlyList<LyricsVariant>? Variants = null);

/// <summary>Another version of a song's lyrics: a romanization or a translation.</summary>
/// <param name="Label">What the listener picks it by: "Romaji", "English".</param>
/// <param name="Lines">Its lines, timed like the original's when the original is synced.</param>
public sealed record LyricsVariant(string Label, IReadOnlyList<LyricLine> Lines);

/// <summary>
/// What a lyrics provider looks a song up by.
/// </summary>
/// <param name="Title">The song's title, as the metadata pipeline resolved it.</param>
/// <param name="Artist">The song's artist (may list several).</param>
/// <param name="Album">The album, when known.</param>
/// <param name="Duration">The length of the audio actually playing, when the source knows it. Synced
/// lyrics are only trusted when they were timed to a recording of this length: a file timed to the
/// radio edit runs ahead of the album cut. Null for live radio, where only the text is used.</param>
/// <param name="YouTubeVideoId">The YouTube video id of the audio, when it's playing from YouTube
/// (some lyrics are keyed to the exact video they were timed against).</param>
public sealed record LyricsQuery(
    string Title,
    string Artist,
    string? Album = null,
    TimeSpan? Duration = null,
    string? YouTubeVideoId = null);
