using HorizonRadio.Core.Tools;
using HorizonRadio.Sdk.Tools;

namespace HorizonRadio.Core.Tests;

/// <summary>
/// Where tools are found: a source's configured path when it exists, else a copy shipped next to the
/// app, else the one installed from the Tools tab, so a tool installed once is usable from every source.
/// </summary>
public sealed class ToolCatalogTests : IDisposable
{
    private readonly TempDir _dir = TempDir.Create();
    private readonly ToolCatalog _tools;

    public ToolCatalogTests() => _tools = new ToolCatalog(
        [new Tool(ToolKind.Ffmpeg, "ffmpeg.exe", 20), new Tool(ToolKind.YtDlp, "yt-dlp.exe", 10),
            new Tool(ToolKind.TitleModel, "title-model.gguf", 30, IsData: true)],
        Path.Combine(_dir.Path, "installed"), Path.Combine(_dir.Path, "bundled"));

    public void Dispose() => _dir.Dispose();

    [Fact]
    public void Tools_are_listed_in_their_plugins_order()
    {
        Assert.Equal([ToolKind.YtDlp, ToolKind.Ffmpeg, ToolKind.TitleModel], _tools.All.Select(t => t.Id));
    }

    [Fact]
    public void A_tool_installs_into_a_folder_of_its_own()
    {
        Assert.Equal(Path.Combine(_dir.Path, "installed", "ffmpeg", "ffmpeg.exe"), _tools.PathFor(ToolKind.Ffmpeg));
        Assert.Equal(new ToolInstallTarget(Path.Combine(_dir.Path, "installed", "ffmpeg"),
            Path.Combine(_dir.Path, "installed", "ffmpeg", "ffmpeg.exe")), _tools.TargetFor(ToolKind.Ffmpeg));
        Assert.True(_tools.IsModel(ToolKind.TitleModel));
        Assert.False(_tools.IsModel(ToolKind.Ffmpeg));
    }

    [Fact]
    public void Configured_path_is_used_when_it_exists()
    {
        var exe = _dir.Touch("ffmpeg.exe");
        Touch(_tools.PathFor(ToolKind.Ffmpeg));

        Assert.Equal(exe, _tools.Resolve(exe, ToolKind.Ffmpeg));
    }

    [Fact]
    public void Missing_configured_path_falls_back_to_the_installed_copy()
    {
        var installed = Touch(_tools.PathFor(ToolKind.Ffmpeg));

        Assert.Equal(installed, _tools.Resolve(Path.Combine(_dir.Path, "nope", "ffmpeg.exe"), ToolKind.Ffmpeg));
    }

    [Fact]
    public void A_copy_next_to_the_app_comes_before_the_installed_one()
    {
        Touch(_tools.PathFor(ToolKind.Ffmpeg));
        var bundled = Touch(Path.Combine(_dir.Path, "bundled", "ffmpeg.exe"));

        Assert.Equal(bundled, _tools.Discover(ToolKind.Ffmpeg));
    }

    [Fact]
    public void Nothing_configured_or_installed_finds_nothing()
    {
        Assert.Null(_tools.Resolve(null, ToolKind.Ffmpeg));
        Assert.Null(_tools.Discover("not-a-tool"));
    }

    private static string Touch(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "");
        return path;
    }

    private sealed record Tool(string Kind, string FileName, int SortOrder, bool IsData = false) : IToolPlugin
    {
        public ToolDescriptor Descriptor => new(Kind, FileName, IsData);
        public IToolInstaller Installer => throw new NotSupportedException();
    }
}
