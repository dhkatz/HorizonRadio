using HorizonRadio.Core.Fingerprints;
using HorizonRadio.Sdk.Audio;
using HorizonRadio.Sdk.Models;
using HorizonRadio.Sdk.Sources;
using HorizonRadio.Sdk.Sources.Config;

namespace HorizonRadio.Core.Tests;

/// <summary>
/// Synthetic songs to fingerprint: a sequence of chords (two or three notes, a new chord every quarter
/// second) picked from a seed, so the same seed is the same song at any sample rate.
/// </summary>
internal static class SyntheticSong
{
    private const double ChordLength = 0.25;

    public static double At(int seed, double t)
    {
        if (t < 0) return 0;
        int chord = (int)(t / ChordLength);
        var rng = new Random(HashCode.Combine(seed, chord));
        int notes = 2 + rng.Next(2);
        double sum = 0;
        for (int n = 0; n < notes; n++)
        {
            double freq = 440 * Math.Pow(2, (rng.Next(48, 84) - 69) / 12.0);
            sum += Math.Sin(2 * Math.PI * freq * t);
        }
        // A soft attack and release on each chord, as an instrument would have.
        double into = t - chord * ChordLength;
        double envelope = Math.Min(1, into / 0.02) * Math.Min(1, (ChordLength - into) / 0.02);
        return sum / notes * envelope;
    }

    /// <summary>Mono at Chromaprint's rate: a recording of the song.</summary>
    public static short[] Recording(int seed, double seconds, Func<double, double>? songTimeAt = null)
    {
        var samples = new short[(int)(seconds * Chromaprint.SampleRate)];
        for (int i = 0; i < samples.Length; i++)
        {
            double t = (double)i / Chromaprint.SampleRate;
            samples[i] = (short)(At(seed, songTimeAt?.Invoke(t) ?? t) * 12000);
        }
        return samples;
    }

    /// <summary>A station's audio in the engine's format: <paramref name="songAt"/> says which song (seed)
    /// plays at each stream time and how far into it.</summary>
    public static short[] Stream(double seconds, Func<double, (int Seed, double SongTime)> songAt, double gain = 0.5)
    {
        int frames = (int)(seconds * AudioFormat.SampleRate);
        var pcm = new short[frames * 2];
        for (int i = 0; i < frames; i++)
        {
            var (seed, songTime) = songAt((double)i / AudioFormat.SampleRate);
            pcm[2 * i] = pcm[2 * i + 1] = (short)(At(seed, songTime) * 30000 * gain);
        }
        return pcm;
    }

    public static LiveAudioBuffer Heard(short[] stream)
    {
        var buffer = new LiveAudioBuffer(TimeSpan.FromSeconds(90));
        for (int at = 0; at < stream.Length; at += 4096)
            buffer.Write(stream.AsSpan(at, Math.Min(4096, stream.Length - at)));
        return buffer;
    }
}

public class FingerprintAlignerTests
{
    private static uint[] Random(int seed, int length)
    {
        var rng = new Random(seed);
        var words = new uint[length];
        for (int i = 0; i < length; i++) words[i] = (uint)rng.NextInt64(0, 1L << 32);
        return words;
    }

    // Flip each bit with the given chance, as re-encoding the audio would.
    private static uint[] Noisy(uint[] words, double chance, int seed)
    {
        var rng = new Random(seed);
        var noisy = (uint[])words.Clone();
        for (int i = 0; i < noisy.Length; i++)
            for (int b = 0; b < 32; b++)
                if (rng.NextDouble() < chance) noisy[i] ^= 1u << b;
        return noisy;
    }

    [Fact]
    public void Finds_where_a_stretch_of_the_recording_is()
    {
        var reference = Random(1, 2000);
        var live = Noisy(reference[700..860], 0.05, 2);

        var match = FingerprintAligner.Align(reference, live, 60)!.Value;

        Assert.Equal(700, match.Offset);
        Assert.InRange(match.ErrorRate, 0.02, 0.08);
        Assert.True(match.Margin > 0.3);
    }

    [Fact]
    public void A_stretch_that_starts_before_the_recording_counts_only_where_they_overlap()
    {
        var reference = Random(1, 1000);
        // 40 words of something else (the previous song), then the recording's start.
        var live = Random(9, 40).Concat(reference[..120]).ToArray();

        var match = FingerprintAligner.Align(reference, live, 60)!.Value;

        Assert.Equal(-40, match.Offset);
        Assert.Equal(0, match.ErrorRate);
    }

    [Fact]
    public void Unrelated_audio_differs_in_about_half_its_bits()
    {
        var match = FingerprintAligner.Align(Random(1, 2000), Random(2, 160), 60)!.Value;

        Assert.True(match.ErrorRate > 0.35);
    }

    [Fact]
    public void A_section_that_repeats_fits_twice()
    {
        var verse = Random(1, 300);
        var chorus = Random(2, 200);
        var reference = verse.Concat(chorus).Concat(Random(3, 300)).Concat(chorus).ToArray();

        var match = FingerprintAligner.Align(reference, Noisy(chorus[20..120], 0.05, 4), 60)!.Value;

        Assert.True(match.Margin < 0.02);
    }
}

public class LiveAudioBufferTests
{
    private static short[] Stereo(int frames, short value) => Enumerable.Repeat(value, frames * 2).ToArray();

    [Fact]
    public void Keeps_the_stream_mono_at_a_quarter_of_the_rate_with_its_place_in_time()
    {
        var buffer = new LiveAudioBuffer(TimeSpan.FromSeconds(10));
        buffer.Write(Stereo(AudioFormat.SampleRate * 2, 1000)); // 2 s

        Assert.Equal(TimeSpan.FromSeconds(2), buffer.Written);
        var (samples, start) = buffer.Read(TimeSpan.FromSeconds(0.5), TimeSpan.FromSeconds(60))!.Value;
        // The first sample at or after 0.5 s.
        Assert.InRange(start.TotalSeconds, 0.5, 0.5 + 1.0 / Chromaprint.SampleRate);
        Assert.InRange(samples.Length, (int)(1.5 * Chromaprint.SampleRate) - 1, (int)(1.5 * Chromaprint.SampleRate));
        Assert.All(samples[100..], s => Assert.InRange(s, 990, 1010)); // past the filter's warm-up
    }

    [Fact]
    public void Reads_at_most_the_span_asked_for_and_what_it_still_keeps()
    {
        var buffer = new LiveAudioBuffer(TimeSpan.FromSeconds(10));
        buffer.Write(Stereo(AudioFormat.SampleRate * 25, 0));

        var recent = buffer.Read(TimeSpan.Zero, TimeSpan.FromSeconds(4))!.Value;
        Assert.Equal(TimeSpan.FromSeconds(21), recent.Start);

        var kept = buffer.Read(TimeSpan.Zero, TimeSpan.FromSeconds(60))!.Value;
        Assert.Equal(TimeSpan.FromSeconds(15), kept.Start); // only the last 10 s are kept
    }

    [Fact]
    public void Nothing_from_after_the_end_or_after_a_reset()
    {
        var buffer = new LiveAudioBuffer(TimeSpan.FromSeconds(10));
        buffer.Write(Stereo(AudioFormat.SampleRate, 0));
        Assert.Null(buffer.Read(TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5)));

        buffer.Reset();
        Assert.Equal(TimeSpan.Zero, buffer.Written);
        Assert.Null(buffer.Read(TimeSpan.Zero, TimeSpan.FromSeconds(5)));
    }
}

public class RecordingMatchTests
{
    private static RecordingFit Locate(uint[] reference, LiveAudioBuffer heard, TimeSpan from, double seconds)
    {
        var (samples, start) = heard.Read(from, TimeSpan.FromSeconds(seconds))!.Value;
        return RecordingMatch.Locate(reference, samples, start);
    }

    [Fact]
    public void Places_a_song_that_began_mid_stream_to_within_a_few_hundredths_of_a_second()
    {
        // The station plays another song, then ours from 5.3 s.
        var reference = Chromaprint.Compute(SyntheticSong.Recording(1, 90));
        var heard = SyntheticSong.Heard(SyntheticSong.Stream(45, t => t < 5.3 ? (2, t + 60) : (1, t - 5.3)));

        var fit = Locate(reference, heard, TimeSpan.FromSeconds(5), 20);

        Assert.Equal(MatchVerdict.Matched, fit.Verdict);
        Assert.InRange((fit.StreamTime - fit.SongTime).TotalSeconds, 5.3 - 0.03, 5.3 + 0.03);
        Assert.True(fit.ErrorRate < RecordingMatch.ExactErrorRate);
    }

    [Fact]
    public void Places_the_song_that_was_playing_when_the_station_was_tuned_in()
    {
        var reference = Chromaprint.Compute(SyntheticSong.Recording(1, 90));
        var heard = SyntheticSong.Heard(SyntheticSong.Stream(30, t => (1, t + 41.7)));

        var fit = Locate(reference, heard, TimeSpan.Zero, 20);

        Assert.Equal(MatchVerdict.Matched, fit.Verdict);
        Assert.InRange((fit.SongTime - fit.StreamTime).TotalSeconds, 41.7 - 0.03, 41.7 + 0.03);
    }

    [Fact]
    public void Another_song_is_different()
    {
        var reference = Chromaprint.Compute(SyntheticSong.Recording(1, 90));
        var heard = SyntheticSong.Heard(SyntheticSong.Stream(30, t => (3, t)));

        Assert.Equal(MatchVerdict.Different, Locate(reference, heard, TimeSpan.Zero, 20).Verdict);
        // Over too little audio to be sure, it isn't called different yet.
        Assert.Equal(MatchVerdict.Unsure, Locate(reference, heard, TimeSpan.Zero, 10).Verdict);
    }

    [Fact]
    public void A_stretch_inside_a_repeated_section_is_ambiguous()
    {
        // The song's first 20 s, played three times over.
        var reference = Chromaprint.Compute(SyntheticSong.Recording(1, 60, t => t % 20));
        var heard = SyntheticSong.Heard(SyntheticSong.Stream(30, t => (1, (t + 3) % 20)));

        Assert.Equal(MatchVerdict.Ambiguous, Locate(reference, heard, TimeSpan.Zero, 12).Verdict);
    }
}

public class ReferenceStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "hzn-fp-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
        GC.SuppressFinalize(this);
    }

    private static StoredReference Ref(string locator, double? rate) =>
        new(locator, TimeSpan.FromSeconds(200), TimeSpan.FromSeconds(1.5), TimeSpan.FromSeconds(1.5), [1, 2, 0xFFFFFFFF], rate,
            rate is null ? null : TimeSpan.FromSeconds(2.25));

    [Fact]
    public void Keeps_fingerprints_per_song_closest_match_first()
    {
        var store = new ReferenceStore(_dir);
        var song = ReferenceStore.SongKey("ばらっげ feat. IA", "Blurry Star");
        store.Save(song, Ref("https://a", null));
        store.Save(song, Ref("https://b", 0.08));
        store.Save(song, Ref("https://c", 0.02));

        var stored = new ReferenceStore(_dir).For(ReferenceStore.SongKey(" ばらっげ FEAT. IA ", "blurry star"));

        Assert.Equal(["https://c", "https://b", "https://a"], stored.Select(r => r.Locator));
        Assert.Equal([1u, 2u, 0xFFFFFFFFu], stored[0].Fingerprint);
        Assert.Equal(TimeSpan.FromSeconds(200), stored[0].Duration);
        Assert.Equal(TimeSpan.FromSeconds(1.5), stored[0].LeadIn);
        // The song starts where a station started it, else at the recording's first sound.
        Assert.Equal(TimeSpan.FromSeconds(2.25), stored[0].SongStart);
        Assert.Equal(TimeSpan.FromSeconds(196.25), stored[0].SongLength);
        Assert.Equal(TimeSpan.FromSeconds(1.5), stored[2].SongStart);
    }

    [Fact]
    public void Saving_a_recording_again_replaces_it()
    {
        var store = new ReferenceStore(_dir);
        store.Save("song", Ref("https://a", null));
        store.Save("song", Ref("https://a", 0.03));

        Assert.Equal(0.03, Assert.Single(store.For("song")).MatchRate);
        Assert.Empty(store.For("another song"));
    }
}

public class LiveSongSyncTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "hzn-fp-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
        GC.SuppressFinalize(this);
    }

    // A station that's 40 s into a stream. Our song began 6.25 s in, after the end of another, with
    // its title change a quarter second early; or, tuned in mid-song, it's been playing from 30 s in.
    private sealed class Station(bool tunedIn = false) : ILiveSongAudio
    {
        private readonly LiveAudioBuffer _heard = SyntheticSong.Heard(SyntheticSong.Stream(40,
            t => tunedIn ? (1, t + 30) : t < 6.25 ? (2, t + 80) : (1, t - 6.25)));

        public long SongId => 7;
        public TimeSpan? TitleChange => tunedIn ? null : TimeSpan.FromSeconds(6);
        public List<(TimeSpan Stream, TimeSpan Song)> Positions { get; } = [];

        public LiveAudio? RecentAudio(TimeSpan span) => Tag(_heard.Read(TitleChange ?? TimeSpan.Zero, span));

        public LiveAudio? AudioBetween(TimeSpan startTime, TimeSpan endTime) => Tag(_heard.ReadBetween(startTime, endTime));

        private static LiveAudio? Tag((short[] Samples, TimeSpan Start)? a) =>
            a is { } h ? new LiveAudio(7, h.Samples, Chromaprint.SampleRate, h.Start) : null;

        public bool SetSongPosition(long songId, TimeSpan streamTime, TimeSpan songTime)
        {
            if (songId != 7) return false;
            Positions.Add((streamTime, songTime));
            return true;
        }
    }

    // Finds two videos for any search, the first of another song; fetches them as synthetic audio. The
    // song's video opens with a prelude: 3.5 s of silence (a title card), or a scene with sound.
    private sealed class Videos(double prelude = 3.5, bool silentPrelude = true) : IAudioSourceFactory, IReferenceAudioSource, ISearchSource
    {
        public string Id => "videos";
        public string DisplayName => "Videos";
        public string? Description => null;
        public IReadOnlyList<ConfigField> Schema => [];
        public List<string> Fetched { get; } = [];

        public Task<IReadOnlyList<SearchResult>> SearchAsync(string query, int limit, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<SearchResult>>(
            [
                new(Id, SearchResultKind.Track, "Another song", "", null, "https://video/other"),
                new(Id, SearchResultKind.Track, "The song", "", null, "https://video/song"),
            ]);

        public bool CanFetch(string locator) => locator.StartsWith("https://video/", StringComparison.Ordinal);

        public Task<ReferenceAudio?> FetchAsync(string locator, int sampleRate, TimeSpan maxLength, CancellationToken ct)
        {
            Fetched.Add(locator);
            var song = locator.EndsWith("/song", StringComparison.Ordinal);
            var audio = song
                ? Video(t => t < prelude ? (silentPrelude ? (1, -1) : (4, t)) : (1, t - prelude))
                : Video(t => (3, t));
            return Task.FromResult<ReferenceAudio?>(new ReferenceAudio(locator, audio, sampleRate));
        }

        private static short[] Video(Func<double, (int Seed, double Time)> at)
        {
            var samples = new short[120 * Chromaprint.SampleRate];
            for (int i = 0; i < samples.Length; i++)
            {
                var (seed, time) = at((double)i / Chromaprint.SampleRate);
                samples[i] = (short)(SyntheticSong.At(seed, time) * 12000);
            }
            return samples;
        }
    }

    private static readonly Track Song = new("Blurry Star", "ばらっげ feat. IA", null, null, "radio", "Internet Radio");

    // Runs the sync until it settles in to keep the song in step (its first long wait).
    private async Task<List<SongSync>> RunAsync(Station station, Videos videos)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var settled = new TaskCompletionSource();
        var sync = new LiveSongSync(() => [videos], new ReferenceStore(_dir), resolver: null)
        {
            Delay = (wait, ct) =>
            {
                if (wait < TimeSpan.FromSeconds(10)) return Task.CompletedTask;
                settled.TrySetResult();
                return Task.Delay(Timeout.Infinite, ct);
            },
        };
        var synced = new List<SongSync>();
        var run = sync.RunAsync(Song, station, synced.Add, cts.Token);
        await Task.WhenAny(settled.Task, run).WaitAsync(cts.Token);
        await cts.CancelAsync();
        await run;
        return synced;
    }

    [Fact]
    public async Task Lines_the_song_up_against_the_recording_that_is_this_song()
    {
        var station = new Station();
        var videos = new Videos();

        var synced = await RunAsync(station, videos);

        Assert.Equal("https://video/song", Assert.Single(synced).Locator);
        // The song's time counts from the music, not from the video's silent title card.
        var (stream, song) = station.Positions[^1];
        Assert.InRange((stream - song).TotalSeconds, 6.25 - 0.03, 6.25 + 0.03);
        Assert.InRange(synced[0].SongLength.TotalSeconds, 116, 117);

        // Both fingerprints are kept; the one that matched is tried first next time. A silent title card
        // is no prelude: the song starts at the first sound.
        var stored = new ReferenceStore(_dir).For(ReferenceStore.SongKey(Song.Artist, Song.Title));
        Assert.Equal(["https://video/song", "https://video/other"], stored.Select(r => r.Locator));
        Assert.NotNull(stored[0].MatchRate);
        Assert.Null(stored[0].StationStart);
    }

    [Fact]
    public async Task A_video_that_opens_with_a_scene_counts_the_song_from_where_the_station_starts_it()
    {
        // A music video with 20 s of a scene, with sound, before the song.
        var station = new Station();

        var synced = await RunAsync(station, new Videos(prelude: 20, silentPrelude: false));

        // The song starts where the station changed its title, a quarter second early here.
        var (stream, song) = station.Positions[^1];
        Assert.InRange((stream - song).TotalSeconds, 6.0 - 0.05, 6.0 + 0.05);
        Assert.InRange(synced[^1].SongLength.TotalSeconds, 99.5, 100.5);
        var stored = new ReferenceStore(_dir).For(ReferenceStore.SongKey(Song.Artist, Song.Title));
        Assert.InRange(stored[0].StationStart!.Value.TotalSeconds, 19.75 - 0.05, 19.75 + 0.05);

        // Tuned in mid-song the next time, the song is still counted from there.
        var tunedIn = new Station(tunedIn: true);
        var videos = new Videos(prelude: 20, silentPrelude: false);
        await RunAsync(tunedIn, videos);

        Assert.Empty(videos.Fetched);
        var (later, at) = tunedIn.Positions[^1];
        Assert.InRange((at - later).TotalSeconds, 30.25 - 0.05, 30.25 + 0.05);
    }

    [Fact]
    public async Task The_next_time_the_song_plays_nothing_is_downloaded()
    {
        await RunAsync(new Station(), new Videos());
        var videos = new Videos();
        var station = new Station();

        var synced = await RunAsync(station, videos);

        Assert.Empty(videos.Fetched);
        Assert.Equal("https://video/song", Assert.Single(synced).Locator);
        Assert.NotEmpty(station.Positions);
    }
}

public class SilenceTests
{
    [Fact]
    public void Finds_the_silence_around_the_music()
    {
        // 2 s of near-silence (tape hiss), 10 s of music, 1 s of silence.
        var audio = SyntheticSong.Recording(1, 13, t => t is >= 2 and < 12 ? t - 2 : -1);
        var rng = new Random(5);
        for (int i = 0; i < audio.Length; i++) audio[i] += (short)rng.Next(-20, 21);

        Assert.InRange(Silence.Before(audio, Chromaprint.SampleRate).TotalSeconds, 1.99, 2.01); // to the 10 ms
        Assert.InRange(Silence.After(audio, Chromaprint.SampleRate).TotalSeconds, 0.99, 1.01);
    }
}
