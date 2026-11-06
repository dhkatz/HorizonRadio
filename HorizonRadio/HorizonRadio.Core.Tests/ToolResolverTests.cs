using HorizonRadio.Core.Tools;

namespace HorizonRadio.Core.Tests;

/// <summary>Serializes the tests that mutate <see cref="ToolResolver.DiscoverOverride"/>
/// (a shared static seam) so they can't clobber each other under parallel runs.</summary>
[CollectionDefinition("tool-resolver", DisableParallelization = true)]
public sealed class ToolResolverCollection { }

/// <summary>
/// A source's configured tool path wins when it exists; otherwise resolution falls back
/// to a managed/bundled copy so a tool installed once is usable from every source.
/// </summary>
[Collection("tool-resolver")]
public class ToolResolverTests
{
    [Fact]
    public void Configured_path_is_used_when_it_exists()
    {
        using var dir = TempDir.Create();
        var exe = dir.Touch("ffmpeg.exe");

        // A present configured path short-circuits discovery.
        Assert.Equal(exe, ToolResolver.Resolve(exe, ToolKind.Ffmpeg));
    }

    [Fact]
    public void Missing_configured_path_falls_back_to_discovery()
    {
        using var dir = TempDir.Create();
        var discovered = dir.Touch("ffmpeg.exe");
        var bogus = Path.Combine(dir.Path, "nope", "ffmpeg.exe");

        ToolResolver.DiscoverOverride = _ => discovered;
        try
        {
            Assert.Equal(discovered, ToolResolver.Resolve(bogus, ToolKind.Ffmpeg));
        }
        finally { ToolResolver.DiscoverOverride = null; }
    }

    [Fact]
    public void Returns_null_when_nothing_configured_or_discoverable()
    {
        ToolResolver.DiscoverOverride = _ => null;
        try
        {
            Assert.Null(ToolResolver.Resolve(null, ToolKind.Ffmpeg));
        }
        finally { ToolResolver.DiscoverOverride = null; }
    }
}
