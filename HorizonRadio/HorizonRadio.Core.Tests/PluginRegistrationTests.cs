using System.Diagnostics;
using HorizonRadio.Core.Hosting;
using HorizonRadio.Core.Settings;
using HorizonRadio.Game.ForzaHorizon6;
using HorizonRadio.Lyrics.Lrclib;
using HorizonRadio.Metadata.Apple;
using HorizonRadio.Metadata.MusicBrainz;
using HorizonRadio.Metadata.Spotify;
using HorizonRadio.Metadata.VocaDb;
using HorizonRadio.Sdk;
using HorizonRadio.Sdk.Sources;
using HorizonRadio.Source.Test;
using HorizonRadio.Source.YouTube;
using Microsoft.Extensions.DependencyInjection;

namespace HorizonRadio.Core.Tests;

/// <summary>
/// Plugins in the host's container: plugin classes built by it (taking services in their
/// constructors), and modules adding, replacing and wrapping services.
/// </summary>
public class PluginRegistrationTests
{
    [Fact]
    public void A_plugin_takes_host_services_in_its_constructor()
    {
        var log = new RecordingLog();
        var provider = Build(s => s.AddSingleton<IPluginLog>(log), typeof(LoggingSourcePlugin));

        var plugin = Assert.IsType<LoggingSourcePlugin>(Assert.Single(provider.GetServices<ISourcePlugin>()));
        Assert.Same(log, plugin.Log);
    }

    [Fact]
    public void A_plugin_of_two_kinds_is_one_instance_under_both()
    {
        var provider = Build(_ => { }, typeof(MetadataAndLyricsPlugin));

        Assert.Same(
            Assert.Single(provider.GetServices<IMetadataPlugin>()),
            Assert.Single(provider.GetServices<ILyricsPlugin>()));
    }

    [Fact]
    public void A_modules_service_reaches_a_plugins_constructor()
    {
        var provider = Build(_ => { }, typeof(ClientSourcePlugin), typeof(ClientModule));

        var plugin = Assert.IsType<ClientSourcePlugin>(Assert.Single(provider.GetServices<ISourcePlugin>()));
        Assert.NotNull(plugin.Client);
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
    public void A_plugin_asking_for_a_service_nobody_registers_fails_at_build()
    {
        var services = new ServiceCollection().AddPlugins([typeof(LoggingSourcePlugin)]);

        Assert.Throws<AggregateException>(() =>
            services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true }));
    }

    [Fact]
    public void The_shipped_plugins_build_from_the_hosts_services()
    {
        Type[] assemblies = [typeof(YouTubeSourcePlugin), typeof(TestToneSourcePlugin), typeof(LrclibLyricsPlugin),
            typeof(VocaDbMetadataPlugin), typeof(ItunesMetadataPlugin), typeof(MusicBrainzMetadataPlugin),
            typeof(SpotifyMetadataPlugin), typeof(Fh6GamePlugin)];
        using var settings = SettingsStore.CreateInMemory();
        var provider = Build(s => s.AddHorizonCore(settings), [.. assemblies.SelectMany(a => a.Assembly.GetExportedTypes())]);

        Assert.Contains(provider.GetServices<ISourcePlugin>(), p => p is YouTubeSourcePlugin);
        Assert.Contains(provider.GetServices<ILyricsPlugin>(), p => p is VocaDbMetadataPlugin);
        Assert.Equal(4, provider.GetServices<IMetadataPlugin>().Count());
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

    public sealed class LoggingSourcePlugin(IPluginLog log) : ISourcePlugin
    {
        public IPluginLog Log { get; } = log;
        public string Id => "logging";
        public string DisplayName => "Logging";
        public IReadOnlyList<IAudioSourceFactory> Sources => [];
    }

    public sealed class MetadataAndLyricsPlugin : IMetadataPlugin, ILyricsPlugin
    {
        public string Id => "both";
        public string DisplayName => "Both";
        public IReadOnlyList<Sdk.Metadata.IMetadataProviderFactory> Providers => [];
        public Sdk.Lyrics.ILyricsProvider CreateLyricsProvider() => throw new NotSupportedException();
    }

    public sealed class Client;

    public sealed class ClientModule : IPluginModule
    {
        public void ConfigureServices(IServiceCollection services) => services.AddSingleton<Client>();
    }

    public sealed class ClientSourcePlugin(Client client) : ISourcePlugin
    {
        public Client Client { get; } = client;
        public string Id => "client";
        public string DisplayName => "Client";
        public IReadOnlyList<IAudioSourceFactory> Sources => [];
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
