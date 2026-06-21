namespace HorizonRadio.UI.Tools;

/// <summary>
/// One installed tool entry surfaced to the UI — typically populated
/// by <see cref="ToolRegistry"/> after a directory scan. Version is
/// best-effort (parsed from the exe's metadata); may be null if we
/// can't determine it. <see cref="Sha256"/> comes from the install-time
/// sidecar written by <see cref="HashVerification"/> and is null when
/// no sidecar exists (older install, or upstream sums file was
/// missing at install time).
/// </summary>
public sealed record InstalledTool(
    string Kind,
    string Path,
    string? Version = null,
    string? Sha256 = null);
