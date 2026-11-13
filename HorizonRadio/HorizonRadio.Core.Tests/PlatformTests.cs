using System.Security.Cryptography;
using System.Text;
using HorizonRadio.Core.Audio;
using HorizonRadio.Core.Hosting;
using HorizonRadio.Core.IO;
using HorizonRadio.Core.Settings;
using HorizonRadio.Platform;
using HorizonRadio.Platform.Windows;
using HorizonRadio.Sdk;
using HorizonRadio.Source.Spotify;
using Microsoft.Extensions.DependencyInjection;

namespace HorizonRadio.Core.Tests;

/// <summary>
/// Core reaches the OS through interfaces with portable defaults, and the platform
/// (<c>HorizonRadio.Platform</c>) registers the OS's own over them.
/// </summary>
public sealed class PlatformTests : IDisposable
{
    private readonly TempDir _dir = TempDir.Create();

    public void Dispose() => _dir.Dispose();

    [Fact]
    public void Without_a_platform_there_are_no_speakers_and_monitoring_never_opens_one()
    {
        using var settings = SettingsStore.CreateInMemory();
        using var provider = new ServiceCollection()
            .AddHorizonCore(settings, new AppFolders(_dir.Path))
            .BuildServiceProvider();

        var speakers = provider.GetRequiredService<ISpeakers>();
        Assert.IsType<NoSpeakers>(speakers);
        using var preview = new PreviewController(new TeePcmSink(null), settings, speakers);
        Assert.Empty(preview.Devices);
        preview.SetEnabled(true);
        Assert.Equal(SpeakerState.Faulted, preview.State);
    }

    [Fact]
    public void Without_a_platform_a_process_is_only_started()
    {
        using var settings = SettingsStore.CreateInMemory();
        using var provider = new ServiceCollection()
            .AddHorizonCore(settings, new AppFolders(_dir.Path))
            .BuildServiceProvider();

        // Core's own, which ties nothing to the app: that's the platform's.
        Assert.Same(typeof(HorizonHost).Assembly, provider.GetRequiredService<IProcesses>().GetType().Assembly);
    }

    [Fact]
    public void Without_a_platform_secrets_are_kept_as_written_and_a_protected_one_is_kept_unread()
    {
        var path = Path.Combine(_dir.Path, SettingsStore.FileName);
        const string protectedElsewhere = "dpapi:AAECAwQFBgcICQ==";
        File.WriteAllText(path, $$"""
            { "version": 1, "settings": {
              "copied": { "value": "{{protectedElsewhere}}", "scope": "secret" } } }
            """);
        var copied = new Setting<string?>("copied", SettingScope.Secret, null);
        var typed = new Setting<string?>("typed", SettingScope.Secret, null);

        using (var settings = SettingsStore.Open(path))
        {
            Assert.Null(settings.Get(copied));
            settings.Set(typed, "hunter2");
        }

        var onDisk = File.ReadAllText(path);
        Assert.Contains(protectedElsewhere, onDisk, StringComparison.Ordinal);
        Assert.Contains("\"hunter2\"", onDisk, StringComparison.Ordinal);

        // And Spotify keeps no login it can't protect: the user signs in again next time.
        var spotify = new SpotifyAuthStore(Path.Combine(_dir.Path, "spotify-auth.dat"), ISecretProtector.None);
        spotify.Save("refresh-token");
        Assert.Null(spotify.Load());
        Assert.False(File.Exists(Path.Combine(_dir.Path, "spotify-auth.dat")));
    }

    [Fact]
    public void On_Windows_secrets_kept_before_the_platform_existed_still_read()
    {
        if (!OperatingSystem.IsWindows()) return;

        // As SecretText and SpotifyAuthStore wrote them: DPAPI for this user, their own entropy.
        var setting = "dpapi:" + Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes("my-soloist-key"),
            Encoding.UTF8.GetBytes("HorizonRadio.SecretSetting.v1"), DataProtectionScope.CurrentUser));
        var token = Path.Combine(_dir.Path, "spotify-auth.dat");
        File.WriteAllBytes(token, ProtectedData.Protect(Encoding.UTF8.GetBytes("refresh-token"),
            Encoding.UTF8.GetBytes("HorizonRadio.Spotify.RefreshToken.v1"), DataProtectionScope.CurrentUser));

        var secrets = Assert.IsType<WindowsPlatform>(HostPlatforms.Current()).Secrets;
        Assert.Equal("my-soloist-key", new SecretText(secrets).Unprotect(setting));
        Assert.Equal("refresh-token", new SpotifyAuthStore(token, secrets).Load());
    }

    [Fact]
    public void On_Windows_the_platform_brings_its_speakers_and_ties_tools_to_the_app()
    {
        if (!OperatingSystem.IsWindows()) return;

        var platform = Assert.IsType<WindowsPlatform>(HostPlatforms.Current());
        using var settings = SettingsStore.CreateInMemory();
        var services = new ServiceCollection().AddHorizonCore(settings, new AppFolders(_dir.Path));
        platform.ConfigureServices(services);
        using var provider = services.BuildServiceProvider();

        Assert.IsType<WasapiSpeakers>(provider.GetRequiredService<ISpeakers>());
        Assert.IsType<AppTiedProcesses>(provider.GetRequiredService<IProcesses>());
        Assert.Same(platform.Secrets, provider.GetRequiredService<ISecretProtector>());
    }
}
