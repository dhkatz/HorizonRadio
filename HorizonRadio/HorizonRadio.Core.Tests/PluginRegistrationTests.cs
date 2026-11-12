using System.Diagnostics;
using HorizonRadio.Core.Hosting;
using HorizonRadio.Core.Settings;
using HorizonRadio.Game.ForzaHorizon6;
using HorizonRadio.Lyrics.Lrclib;
using HorizonRadio.Lyrics.Unison;
using HorizonRadio.Metadata.Apple;
using HorizonRadio.Metadata.MusicBrainz;
using HorizonRadio.Metadata.Spotify;
using HorizonRadio.Metadata.VocaDb;
using HorizonRadio.Sdk;
using HorizonRadio.Sdk.Lyrics;
using HorizonRadio.Sdk.Metadata;
using HorizonRadio.Sdk.Sources;
using HorizonRadio.Sdk.Sources.Config;
using HorizonRadio.Source.Local;
using HorizonRadio.Source.Radio;
using HorizonRadio.Source.Spotify;
using HorizonRadio.Source.Test;
using HorizonRadio.Source.YouTube;
using Microsoft.Extensions.DependencyInjection;

namespace HorizonRadio.Core.Tests;

/// <summary>
/// Plugins in the host's container: plugins listed, their contributions collected and built by the
/// container (taking services in their constructors), and modules adding, replacing and wrapping
/// services.
/// </summary>
public class PluginRegistrationTests
{
    [Fact]
    public void A_contribution_takes_host_services_in_its_constructor()
    {
        var log = new RecordingLog();
        var provider = Build(s => s.AddSingleton<IPluginLog>(log), typeof(LoggingPlugin));

        var lyrics = Assert.IsType<LoggingLyrics>(Assert.Single(provider.GetServices<ILyricsProvider>()));
        Assert.Same(log, lyrics.Log);
    }

    [Fact]
    public void A_plugin_that_is_its_own_module_is_listed_once()
    {
        var provider = Build(s => s.AddSingleton<IPluginLog>(new RecordingLog()), typeof(LoggingPlugin));

        Assert.IsType<LoggingPlugin>(Assert.Single(provider.GetServices<IPlugin>()));
    }

    [Fact]
    public void A_plugin_contributes_to_several_kinds()
    {
        var provider = Build(_ => { }, typeof(SourceAndLyricsPlugin));

        Assert.IsType<SilentSource>(Assert.Single(provider.GetServices<IAudioSourceFactory>()));
        Assert.IsType<NoLyrics>(Assert.Single(provider.GetServices<ILyricsProvider>()));
    }

    [Fact]
    public void A_modules_service_reaches_a_contributions_constructor()
    {
        var provider = Build(_ => { }, typeof(ClientPlugin), typeof(ClientModule));

        var lyrics = Assert.IsType<ClientLyrics>(Assert.Single(provider.GetServices<ILyricsProvider>()));
        Assert.NotNull(lyrics.Client);
    }

    [Fact]
    public void A_game_plugin_is_built_by_the_container_and_listed_as_a_plugin_too()
    {
        var log = new RecordingLog();
        var provider = Build(s => s.AddSingleton<IPluginLog>(log), typeof(LoggingGamePlugin));

        var game = Assert.IsType<LoggingGamePlugin>(Assert.Single(provider.GetServices<IGamePlugin>()));
        Assert.Same(log, game.Log);
        Assert.Same(game, Assert.Single(provider.GetServices<IPlugin>()));
    }

    [Fact]
    public void A_module_can_replace_a_host_service_and_the_replacement_is_logged()
    {
        var lines = new List<string>();
        var provider = Build(s => s.AddSingleton<IProcesses, HostLikeProcesses>(), lines, typeof(ReplacingModule));

        Assert.IsType<ReplacementProcesses>(provider.GetRequiredService<IProcesses>());
        Assert.Contains(lines, l => l.EndsWith("replaces IProcesses", StringComparison.Ordinal));
    }

    [Fact]
    public void Two_replacements_of_one_service_warn_and_the_later_module_wins()
    {
        var lines = new List<string>();
        var provider = Build(s => s.AddSingleton<IProcesses, HostLikeProcesses>(), lines,
            typeof(LateReplacingModule), typeof(ReplacingModule));

        // LateReplacingModule has the higher Order, so it runs last whatever the type order.
        Assert.IsType<LateReplacementProcesses>(provider.GetRequiredService<IProcesses>());
        Assert.Contains(lines, l => l.StartsWith("warning:", StringComparison.Ordinal) && l.Contains("IProcesses", StringComparison.Ordinal));
    }

    [Fact]
    public void A_module_adding_a_service_the_host_doesnt_have_isnt_reported()
    {
        var lines = new List<string>();
        Build(s => s.AddSingleton<IProcesses, HostLikeProcesses>(), lines, typeof(ClientModule));

        Assert.Empty(lines);
    }

    [Fact]
    public async Task Decorate_wraps_the_service_and_the_wrapped_one_is_still_disposed()
    {
        var lines = new List<string>();
        var provider = Build(s => s.AddSingleton<IProcesses, HostLikeProcesses>(), lines, typeof(WrappingModule));

        var outer = Assert.IsType<CountingProcesses>(provider.GetRequiredService<IProcesses>());
        var inner = Assert.IsType<HostLikeProcesses>(outer.Inner);
        Assert.Contains(lines, l => l.EndsWith("wraps IProcesses", StringComparison.Ordinal));

        await provider.DisposeAsync();
        Assert.True(inner.Disposed);
    }

    [Fact]
    public void Decorate_without_a_registration_throws()
    {
        var services = new ServiceCollection();
        Assert.Throws<InvalidOperationException>(() =>
            services.Decorate<IProcesses>((inner, _) => new CountingProcesses(inner)));
    }

    [Fact]
    public void A_module_without_a_parameterless_constructor_is_refused()
    {
        var services = new ServiceCollection();
        var ex = Assert.Throws<InvalidOperationException>(() => services.AddPlugins([typeof(ModuleWithArguments)]));
        Assert.Contains(nameof(ModuleWithArguments), ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_contribution_asking_for_a_service_nobody_registers_fails_at_build()
    {
        var services = new ServiceCollection().AddPlugins([typeof(LoggingPlugin)]);

        Assert.Throws<AggregateException>(() =>
            services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true }));
    }

    [Fact]
    public void The_shipped_plugins_build_from_the_hosts_services()
    {
        Type[] assemblies = [typeof(YouTubeSourcePlugin), typeof(TestToneSourcePlugin), typeof(LocalSourcePlugin),
            typeof(SpotifySourcePlugin), typeof(RadioSourcePlugin), typeof(LrclibLyricsPlugin), typeof(UnisonLyricsPlugin),
            typeof(VocaDbMetadataPlugin), typeof(ItunesMetadataPlugin), typeof(MusicBrainzMetadataPlugin),
            typeof(SpotifyMetadataPlugin), typeof(Fh6GamePlugin)];
        using var settings = SettingsStore.CreateInMemory();
        var provider = Build(s => s.AddHorizonCore(settings), [.. assemblies.SelectMany(a => a.Assembly.GetExportedTypes())]);

        Assert.Contains(provider.GetServices<IPlugin>(), p => p is YouTubeSourcePlugin);
        // The order each list had when it came from the plugins' own SortOrder.
        Assert.Equal(["local", "spotify", SpotifyContentSourceFactory.SourceId, "youtube", "radio", "testtone"],
            provider.GetServices<IAudioSourceFactory>().InDisplayOrder().Select(s => s.Id));
        Assert.Equal(["unison", "lrclib", "vocadb"],
            provider.GetServices<ILyricsProvider>().InDisplayOrder().Select(l => l.Id));
        Assert.Equal(["spotify", "itunes", "musicbrainz", "vocadb"],
            provider.GetServices<IMetadataProviderFactory>().InDisplayOrder().Select(f => f.Id));
        Assert.Contains(provider.GetServices<IGamePlugin>(), p => p is Fh6GamePlugin);
    }

    [Fact]
    public void Plugins_are_shown_in_sort_order_then_by_id()
    {
        IPlugin[] plugins = [new Named("b", 2), new Named("c", 1), new Named("a", 2)];

        Assert.Equal(["c", "a", "b"], plugins.InDisplayOrder().Select(p => p.Id));
    }

    private static ServiceProvider Build(Action<IServiceCollection> host, params Type[] types) =>
        Build(host, [], types);

    private static ServiceProvider Build(Action<IServiceCollection> host, List<string> log, params Type[] types)
    {
        var services = new ServiceCollection();
        host(services);
        services.AddPlugins(types, log.Add);
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true });
    }

    public sealed class RecordingLog : IPluginLog
    {
        public void Write(string tag, string text) { }
        public void Trace(string tag, string text) { }
    }

    public class NoLyrics : ILyricsProvider
    {
        public virtual string Id => "none";
        public Task<SongLyrics?> FindAsync(LyricsQuery query, CancellationToken ct) => Task.FromResult<SongLyrics?>(null);
        public ValueTask DisposeAsync()
        {
            GC.SuppressFinalize(this);
            return ValueTask.CompletedTask;
        }
    }

    public sealed class LoggingLyrics(IPluginLog log) : NoLyrics
    {
        public IPluginLog Log { get; } = log;
    }

    public sealed class LoggingPlugin : IPlugin, IPluginModule
    {
        public string Id => "logging";
        public string DisplayName => "Logging";
        public void ConfigureServices(IServiceCollection services) => services.AddSingleton<ILyricsProvider, LoggingLyrics>();
    }

    public sealed class SilentSource : IAudioSourceFactory
    {
        public string Id => "silent";
        public string DisplayName => "Silent";
        public string? Description => null;
        public IReadOnlyList<ConfigField> Schema => [];
    }

    public sealed class SourceAndLyricsPlugin : IPlugin, IPluginModule
    {
        public string Id => "both";
        public string DisplayName => "Both";
        public void ConfigureServices(IServiceCollection services)
        {
            services.AddSingleton<IAudioSourceFactory, SilentSource>();
            services.AddSingleton<ILyricsProvider, NoLyrics>();
        }
    }

    public sealed class Client;

    public sealed class ClientModule : IPluginModule
    {
        public void ConfigureServices(IServiceCollection services) => services.AddSingleton<Client>();
    }

    public sealed class ClientLyrics(Client client) : NoLyrics
    {
        public Client Client { get; } = client;
    }

    public sealed class ClientPlugin : IPlugin, IPluginModule
    {
        public string Id => "client";
        public string DisplayName => "Client";
        public void ConfigureServices(IServiceCollection services) => services.AddSingleton<ILyricsProvider, ClientLyrics>();
    }

    public sealed class LoggingGamePlugin(IPluginLog log) : IGamePlugin
    {
        public IPluginLog Log { get; } = log;
        public string Id => "game";
        public string DisplayName => "Game";
        public Sdk.Games.IGameIntegration Create(IPluginContext context) => throw new NotSupportedException();
    }

    public class HostLikeProcesses : IProcesses, IDisposable
    {
        public bool Disposed { get; private set; }
        public Process Start(ProcessStartInfo info) => throw new NotSupportedException();
        public void Dispose()
        {
            Disposed = true;
            GC.SuppressFinalize(this);
        }
    }

    public sealed class ReplacementProcesses : HostLikeProcesses;

    public sealed class LateReplacementProcesses : HostLikeProcesses;

    public sealed class ReplacingModule : IPluginModule
    {
        public void ConfigureServices(IServiceCollection services) => services.AddSingleton<IProcesses, ReplacementProcesses>();
    }

    public sealed class LateReplacingModule : IPluginModule
    {
        public int Order => 1;
        public void ConfigureServices(IServiceCollection services) => services.AddSingleton<IProcesses, LateReplacementProcesses>();
    }

    public sealed class CountingProcesses(IProcesses inner) : IProcesses
    {
        public IProcesses Inner { get; } = inner;
        public Process Start(ProcessStartInfo info) => Inner.Start(info);
    }

    public sealed class WrappingModule : IPluginModule
    {
        public void ConfigureServices(IServiceCollection services) =>
            services.Decorate<IProcesses>((inner, _) => new CountingProcesses(inner));
    }

    public sealed class ModuleWithArguments(IPluginLog log) : IPluginModule
    {
        public void ConfigureServices(IServiceCollection services) => log.Trace("test", "unused");
    }

    private sealed record Named(string Id, int SortOrder) : IPlugin
    {
        public string DisplayName => Id;
    }
}
