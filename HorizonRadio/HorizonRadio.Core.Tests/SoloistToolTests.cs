using HorizonRadio.Core.IO;
using HorizonRadio.Core.Sources.Config;
using HorizonRadio.Tools.Soloist;
using Xunit;

namespace HorizonRadio.Core.Tests;

public sealed class SoloistToolTests
{
    [Fact]
    public void Reads_the_build_and_its_expiry_from_the_version_line()
    {
        var build = Soloist.ParseVersion(
            "soloist 1.3.10.52 build 1794182509 (20261109) (g19412df6ea) (windows/x86_64)\r\n");

        Assert.NotNull(build);
        Assert.Equal("1.3.10.52", build.Version);
        Assert.Equal(new DateOnly(2026, 11, 9), build.Built);
        Assert.Equal(new DateOnly(2027, 2, 7), build.Expires); // 90 days
    }

    [Theory]
    [InlineData("")]
    [InlineData("librespot 0.8.0 33bf3a7 (Built on 2026-06-11)")]
    [InlineData("soloist 1.3.10.52 build 1794182509 (2026110) (g19412df6ea)")]
    public void Ignores_output_that_isnt_a_soloist_version(string output)
        => Assert.Null(Soloist.ParseVersion(output));

    [Fact]
    public void Starts_soloist_with_the_key_its_own_data_and_full_volume()
    {
        var args = Soloist.BuildArgs(new SoloistOptions
        {
            ExecutablePath = @"C:\tools\soloist.exe",
            ApiKey = "k3y",
            DataDirectory = @"C:\data\soloist",
        });

        Assert.Equal(
        [
            "--device-name", Soloist.DefaultDeviceName,
            "--api-key", "k3y",
            "--data-dir", @"C:\data\soloist",
            "--cache-dir", @"C:\data\soloist\cache",
            "--ws", "127.0.0.1:0",
            "--initial-volume", "100",
        ], args);
    }

    [Theory]
    [InlineData("\"003a656316c7b3e6a593ac200d5301ef\"", "003a656316c7b3e6a593ac200d5301ef")]
    [InlineData("\"003A656316C7B3E6A593AC200D5301EF\"", "003a656316c7b3e6a593ac200d5301ef")]
    [InlineData("W/\"003a656316c7b3e6a593ac200d5301ef\"", "003a656316c7b3e6a593ac200d5301ef")]
    [InlineData("\"003a656316c7b3e6a593ac200d5301ef-4\"", null)] // a multipart upload's ETag isn't an MD5
    [InlineData(null, null)]
    public void Takes_the_md5_from_the_cdns_etag(string? etag, string? md5)
        => Assert.Equal(md5, SoloistInstaller.Md5FromEtag(etag));
}

public sealed class SecretSettingTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"hzn-sources-{Guid.NewGuid():N}.json");

    public void Dispose()
    {
        File.Delete(_path);
        GC.SuppressFinalize(this);
    }

    private static readonly ConfigField[] Schema =
    [
        new TextField("soloistKey", "Key", IsSecret: true),
        new TextField("deviceName", "Device name"),
    ];

    [Fact]
    public void A_secret_setting_is_stored_encrypted_and_read_back()
    {
        if (!OperatingSystem.IsWindows()) return;
        var store = new SourceConfigStore();
        var values = store.Load("spotify-driven", Schema);
        values.Set("soloistKey", "my-secret-key");
        values.Set("deviceName", "Horizon Radio");
        store.Save("spotify-driven", values);
        store.SaveToDisk(_path);

        var onDisk = File.ReadAllText(_path);
        Assert.DoesNotContain("my-secret-key", onDisk, StringComparison.Ordinal);
        Assert.Contains(SecretText.Prefix, onDisk, StringComparison.Ordinal);
        Assert.Contains("Horizon Radio", onDisk, StringComparison.Ordinal); // the rest stays plain

        var reloaded = SourceConfigStore.LoadFromDisk(_path).Load("spotify-driven", Schema);
        Assert.Equal("my-secret-key", reloaded.GetString("soloistKey"));
    }

    [Fact]
    public void A_secret_stays_encrypted_when_saved_before_its_settings_are_shown()
    {
        if (!OperatingSystem.IsWindows()) return;
        var first = new SourceConfigStore();
        var values = first.Load("spotify-driven", Schema);
        values.Set("soloistKey", "my-secret-key");
        first.Save("spotify-driven", values);
        first.SaveToDisk(_path);

        // Next run: saved again before anything loads the Spotify settings.
        SourceConfigStore.LoadFromDisk(_path).SaveToDisk(_path);

        Assert.DoesNotContain("my-secret-key", File.ReadAllText(_path), StringComparison.Ordinal);
    }
}
