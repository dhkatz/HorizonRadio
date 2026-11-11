using HorizonRadio.Core.Sources;

namespace HorizonRadio.Core.Tests;

/// <summary>Songs downloaded ahead: a song is here only once its download is whole, a download is
/// shared by everyone who asks for it, and past the limit the songs used longest ago go first.</summary>
public sealed class AudioCacheTests : IDisposable
{
    private readonly TempDir _dir = TempDir.Create();

    public void Dispose() => _dir.Dispose();

    private static Func<string, CancellationToken, Task> Writes(int bytes, Task? until = null, Action? onRun = null) =>
        async (folder, _) =>
        {
            onRun?.Invoke();
            if (until != null) await until;
            await File.WriteAllBytesAsync(Path.Combine(folder, "audio.webm"), new byte[bytes], CancellationToken.None);
        };

    [Fact]
    public async Task A_song_is_here_once_its_download_is_whole_and_a_download_is_shared()
    {
        var cache = new AudioCache(_dir.Path);
        var release = new TaskCompletionSource();
        var runs = 0;
        var download = Writes(10, release.Task, () => Interlocked.Increment(ref runs));

        var first = cache.FetchAsync("abc", download, CancellationToken.None);
        var second = cache.FetchAsync("abc", download, CancellationToken.None);
        await Eventually.Until(() => Volatile.Read(ref runs) == 1, "the download to start");
        Assert.Null(cache.Find("abc")); // not while it's downloading
        Assert.True(cache.IsFetching("abc"));

        release.SetResult();
        var folder = await first;
        Assert.Equal(folder, await second);
        Assert.Equal(1, runs);
        Assert.Equal(folder, cache.Find("abc"));
        Assert.True(File.Exists(Path.Combine(folder!, "audio.webm")));
        Assert.False(cache.IsFetching("abc"));
    }

    [Fact]
    public async Task Giving_up_waiting_doesnt_stop_the_download()
    {
        var cache = new AudioCache(_dir.Path);
        var release = new TaskCompletionSource();
        using var cts = new CancellationTokenSource();
        var waiting = cache.FetchAsync("abc", Writes(10, release.Task), cts.Token);
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);

        release.SetResult();
        await Eventually.Until(() => cache.Find("abc") != null, "the download to finish anyway");
    }

    [Fact]
    public async Task A_failed_download_leaves_nothing()
    {
        var cache = new AudioCache(_dir.Path);
        var folder = await cache.FetchAsync("abc", async (f, _) =>
        {
            await File.WriteAllBytesAsync(Path.Combine(f, "audio.webm.part"), new byte[10], CancellationToken.None);
            throw new InvalidOperationException("yt-dlp exited 1");
        }, CancellationToken.None);

        Assert.Null(folder);
        Assert.Null(cache.Find("abc"));
        Assert.Equal(0, cache.Size);
    }

    [Fact]
    public async Task Past_the_limit_the_songs_used_longest_ago_go_first()
    {
        var cache = new AudioCache(_dir.Path) { MaxBytes = 250 };
        foreach (var key in new[] { "a", "b" }) await cache.FetchAsync(key, Writes(100), CancellationToken.None);
        Directory.SetLastWriteTimeUtc(Path.Combine(_dir.Path, "a"), DateTime.UtcNow.AddHours(-2));
        Directory.SetLastWriteTimeUtc(Path.Combine(_dir.Path, "b"), DateTime.UtcNow.AddHours(-1));
        Assert.NotNull(cache.Find("a")); // played again: now the most recent

        await cache.FetchAsync("c", Writes(100), CancellationToken.None);
        Assert.NotNull(cache.Find("a"));
        Assert.Null(cache.Find("b"));
        Assert.NotNull(cache.Find("c"));
        Assert.Equal(200, cache.Size);
    }

    [Fact]
    public async Task A_song_being_played_isnt_removed_half_way()
    {
        var cache = new AudioCache(_dir.Path);
        var folder = await cache.FetchAsync("a", async (f, _) =>
        {
            await File.WriteAllBytesAsync(Path.Combine(f, "audio.webm"), new byte[10], CancellationToken.None);
            await File.WriteAllTextAsync(Path.Combine(f, "info.json"), "{}", CancellationToken.None);
        }, CancellationToken.None);

        using (File.OpenRead(Path.Combine(folder!, "audio.webm"))) // ffmpeg has it open
        {
            cache.Clear();
            Assert.True(File.Exists(Path.Combine(folder!, "info.json")));
        }
        cache.Clear();
        Assert.Null(cache.Find("a"));
    }

    [Fact]
    public void A_download_left_from_last_time_is_cleared_away()
    {
        var leftover = Directory.CreateDirectory(Path.Combine(_dir.Path, ".incoming", "abc-123")).FullName;
        File.WriteAllBytes(Path.Combine(leftover, "audio.webm.part"), new byte[10]);
        var cache = new AudioCache(_dir.Path);
        Assert.False(Directory.Exists(leftover));
        Assert.Equal(0, cache.Size);
    }
}
