using HorizonRadio.Core.Sources.Spotify;
using Xunit;

namespace HorizonRadio.Core.Tests;

public sealed class SpotifyPlayerChoiceTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "hzn-player-choice-" + Guid.NewGuid().ToString("N"));
    private string PathFor => Path.Combine(_dir, "spotify-player.json");
    private DateTimeOffset _now = new(2026, 11, 8, 20, 0, 0, TimeSpan.Zero);

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    private SpotifyPlayerChoice Open() => new(PathFor, () => _now);

    [Fact]
    public void Librespot_goes_first_until_Spotify_refuses_it()
    {
        var choice = Open();
        Assert.False(choice.PreferSoloist("build-1"));

        choice.LibrespotRefused("build-1");
        Assert.True(choice.PreferSoloist("build-1"));
    }

    [Fact]
    public void A_refusal_is_remembered_across_sessions()
    {
        Open().LibrespotRefused("build-1");
        _now += TimeSpan.FromHours(3);
        Assert.True(Open().PreferSoloist("build-1"));
    }

    [Fact]
    public void Librespot_gets_another_try_once_the_recheck_is_due()
    {
        var choice = Open();
        choice.LibrespotRefused("build-1");
        _now += SpotifyPlayerChoice.RecheckAfter;
        Assert.False(choice.PreferSoloist("build-1"));

        // Refused again: Soloist goes first for another stretch.
        choice.LibrespotRefused("build-1");
        Assert.True(choice.PreferSoloist("build-1"));
    }

    [Fact]
    public void An_updated_librespot_gets_tried_at_once()
    {
        var choice = Open();
        choice.LibrespotRefused("build-1");
        Assert.False(choice.PreferSoloist("build-2"));
    }

    [Fact]
    public void A_play_librespot_delivers_puts_it_back_first()
    {
        var choice = Open();
        choice.LibrespotRefused("build-1");
        choice.LibrespotPlayed();

        Assert.False(choice.PreferSoloist("build-1"));
        Assert.Null(Open().LibrespotRefusedAt);
        Assert.False(File.Exists(PathFor));
    }

    [Fact]
    public void A_damaged_file_means_no_preference()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(PathFor, "{ not json");
        Assert.False(Open().PreferSoloist("build-1"));
    }

    [Fact]
    public void A_build_is_its_size_and_write_time()
    {
        Directory.CreateDirectory(_dir);
        var exe = Path.Combine(_dir, "librespot.exe");
        File.WriteAllBytes(exe, [1, 2, 3]);
        var before = SpotifyPlayerChoice.BuildOf(exe);
        Assert.StartsWith("3:", before, StringComparison.Ordinal);

        File.WriteAllBytes(exe, [1, 2, 3, 4]);
        Assert.NotEqual(before, SpotifyPlayerChoice.BuildOf(exe));
        Assert.Null(SpotifyPlayerChoice.BuildOf(Path.Combine(_dir, "missing.exe")));
        Assert.Null(SpotifyPlayerChoice.BuildOf(null));
    }
}
