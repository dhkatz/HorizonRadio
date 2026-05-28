namespace HorizonRadio.Core.Models;

/// <summary>
/// A registered audio source the user can switch to. Mirrors the DLL's
/// SourceRegistry entries (will eventually mirror the C# SourceRegistry
/// once sources move to this side).
/// </summary>
public sealed record SourceInfo(
    string Id,
    string DisplayName,
    bool   IsActive);
