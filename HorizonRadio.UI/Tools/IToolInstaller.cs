using System;
using System.Threading;
using System.Threading.Tasks;

namespace HorizonRadio.UI.Tools;

/// <summary>
/// One installer per tool kind. Implementations download, extract (if
/// needed), and atomically place the binary at
/// <see cref="ToolsPaths.ExeFor"/>. They are stateless — the registry
/// owns "what's installed", installers only know "how to install".
/// </summary>
public interface IToolInstaller
{
    /// <summary>Tool kind this installer handles (see <see cref="ToolKind"/>).</summary>
    string Kind { get; }

    /// <summary>Human-readable label for the Tools tab card.</summary>
    string DisplayName { get; }

    /// <summary>Short one-liner about what the tool does.</summary>
    string Description { get; }

    /// <summary>Download + install. Reports progress through
    /// <paramref name="progress"/> (null is fine). Throws on failure
    /// rather than returning a status — the caller wraps in try/catch
    /// and surfaces the message; that way installers can let HttpClient
    /// and ZipArchive exceptions propagate without translation.</summary>
    Task InstallAsync(IProgress<ToolInstallProgress>? progress, CancellationToken ct);
}

/// <summary>
/// Progress snapshot emitted during an install. <see cref="Fraction"/>
/// is null when the work is indeterminate (e.g. "Extracting…"); the UI
/// shows a spinner instead of a progress bar in that case.
/// </summary>
public sealed record ToolInstallProgress(string Status, double? Fraction = null);
