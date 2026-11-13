using HorizonRadio.Core.IO;
using HorizonRadio.Core.Settings;
using HorizonRadio.Core.Sources.Config;
using HorizonRadio.Sdk.Sources.Config;
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
            DeviceName = "Horizon Radio (Soloist)",
            DataDirectory = @"C:\data\soloist",
        });

        Assert.Equal(
        [
            "--device-name", "Horizon Radio (Soloist)",
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
    private readonly TempDir _dir = TempDir.Create();

    private string Path => System.IO.Path.Combine(_dir.Path, SettingsStore.FileName);

    public void Dispose() => _dir.Dispose();

    private static readonly ConfigField[] Schema =
    [
        new TextField("soloistKey", "Key", IsSecret: true),
        new TextField("deviceName", "Device name"),
    ];

    private static void Write(SettingsStore settings, Action<ConfigValues> change)
    {
        var config = new SourceConfigStore(settings);
        var values = config.Load("spotify-driven", Schema);
        change(values);
        config.Save("spotify-driven", values);
        settings.Flush();
    }

    private static ConfigValues Read(string path)
    {
        using var settings = SettingsStore.Open(path, TestSecrets.ThisOs);
        return new SourceConfigStore(settings).Load("spotify-driven", Schema);
    }

    [Fact]
    public void A_secret_setting_is_stored_encrypted_and_read_back()
    {
        if (!OperatingSystem.IsWindows()) return;
        using (var settings = SettingsStore.Open(Path, TestSecrets.ThisOs))
        {
            Write(settings, values =>
            {
                values.Set("soloistKey", "my-secret-key");
                values.Set("deviceName", "Horizon Radio");
            });
        }

        var onDisk = File.ReadAllText(Path);
        Assert.DoesNotContain("my-secret-key", onDisk, StringComparison.Ordinal);
        Assert.Contains("dpapi:", onDisk, StringComparison.Ordinal);
        Assert.Contains("Horizon Radio", onDisk, StringComparison.Ordinal); // the rest stays plain

        Assert.Equal("my-secret-key", Read(Path).GetString("soloistKey"));
    }

    [Fact]
    public void A_secret_this_user_cant_decrypt_is_kept_until_replaced()
    {
        if (!OperatingSystem.IsWindows()) return;
        // As if settings.json came from another Windows account: valid base64, not this user's DPAPI.
        const string foreign = "dpapi:AAECAwQFBgcICQ==";
        File.WriteAllText(Path, $$"""
            { "version": 1, "settings": {
              "source.spotify-driven.soloistKey": { "value": "{{foreign}}", "scope": "secret" },
              "source.spotify-driven.deviceName": { "value": "Horizon Radio", "scope": "device" } } }
            """);

        using var settings = SettingsStore.Open(Path, TestSecrets.ThisOs);
        Assert.Null(new SourceConfigStore(settings).Load("spotify-driven", Schema).GetString("soloistKey"));
        Write(settings, values => values.Set("deviceName", "Renamed"));
        Assert.Contains(foreign, File.ReadAllText(Path), StringComparison.Ordinal);

        Write(settings, values => values.Set("soloistKey", "a-new-key"));
        Assert.DoesNotContain(foreign, File.ReadAllText(Path), StringComparison.Ordinal);
        Assert.Equal("a-new-key", Read(Path).GetString("soloistKey"));
    }

    [Fact]
    public void A_secret_stays_encrypted_when_saved_before_its_settings_are_shown()
    {
        if (!OperatingSystem.IsWindows()) return;
        using (var first = SettingsStore.Open(Path, TestSecrets.ThisOs))
            Write(first, values => values.Set("soloistKey", "my-secret-key"));

        // Next run: saved again, with a source that never loads the Spotify settings.
        using (var next = SettingsStore.Open(Path, TestSecrets.ThisOs))
        {
            var config = new SourceConfigStore(next);
            var values = config.Load("local", []);
            values.Set("path", "C:/Music");
            config.Save("local", values);
        }

        Assert.DoesNotContain("my-secret-key", File.ReadAllText(Path), StringComparison.Ordinal);
        Assert.Equal("my-secret-key", Read(Path).GetString("soloistKey"));
    }
}
