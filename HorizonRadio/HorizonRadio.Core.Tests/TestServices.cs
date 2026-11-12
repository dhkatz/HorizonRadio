using System.Diagnostics;
using HorizonRadio.Sdk;

namespace HorizonRadio.Core.Tests;

/// <summary>Process launching that refuses: for code that mustn't start a process in a test.</summary>
public sealed class NoProcesses : IProcesses
{
    public static NoProcesses Instance { get; } = new();

    public Process Start(ProcessStartInfo info) =>
        throw new InvalidOperationException($"The test started {info.FileName}.");
}

/// <summary>Starts processes for real (and doesn't tie them to anything): for tests that run a tool.</summary>
public sealed class RealProcesses : IProcesses
{
    public static RealProcesses Instance { get; } = new();

    public Process Start(ProcessStartInfo info) =>
        Process.Start(info) ?? throw new InvalidOperationException($"Couldn't start {info.FileName}.");
}

/// <summary>A log that drops everything.</summary>
public sealed class NoLog : IPluginLog
{
    public static NoLog Instance { get; } = new();

    public void Write(string tag, string text) { }

    public void Trace(string tag, string text) { }
}

/// <summary>No title model installed.</summary>
public sealed class NoTitleModel : Sdk.Metadata.ITitleModel
{
    public Sdk.Metadata.ITitleExtractor? Extractor => null;

    public Sdk.Metadata.TitleModelMode Mode => Sdk.Metadata.TitleModelMode.Escalate;
}

/// <summary>The shipped sources, as the test container built them (<see cref="TestModuleInit"/>).</summary>
public static class TestSources
{
    /// <summary>The shipped sources, built by the test container (<see cref="TestModuleInit"/>).</summary>
    public static Sources.SourceCatalog Catalog { get; internal set; } = Sources.SourceCatalog.Empty;

    public static Source.Spotify.SpotifyContentSourceFactory Spotify =>
        (Source.Spotify.SpotifyContentSourceFactory)Catalog.Find(Source.Spotify.SpotifyContentSourceFactory.SourceId)!;

    public static Source.Radio.RadioSourceFactory Radio =>
        (Source.Radio.RadioSourceFactory)Catalog.Find(Source.Radio.RadioSourceFactory.SourceId)!;

    public static Source.YouTube.YouTubeSourceFactory YouTube =>
        (Source.YouTube.YouTubeSourceFactory)Catalog.Find(Source.YouTube.YouTubeSourceFactory.SourceId)!;
}
