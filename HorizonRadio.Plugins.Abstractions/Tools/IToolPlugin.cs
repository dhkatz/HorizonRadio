namespace HorizonRadio.Core.Tools;

/// <summary>
/// A tool plugin: bundles a tool's identity/on-disk shape (<see cref="Descriptor"/>) with how to
/// provision it (<see cref="Installer"/>). Each external tool ships as its own plugin assembly
/// (<c>HorizonRadio.Tools.*</c>), so the host provisions any tool generically and never names
/// yt-dlp / ffmpeg / librespot itself. The composition root aggregates these into the tool catalog
/// (path resolution + the install registry) and the Tools tab; consumers (sources, metadata
/// providers) just declare a dependency on a tool by id via a <c>ToolField</c>.
/// </summary>
public interface IToolPlugin
{
    /// <summary>The tool's id + on-disk file name + data-vs-exe flag (drives path resolution).</summary>
    ToolDescriptor Descriptor { get; }

    /// <summary>How to download, verify, and install the tool, and report its freshness.</summary>
    IToolInstaller Installer { get; }
}
