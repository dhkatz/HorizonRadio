using System.Collections.Generic;

namespace HorizonRadio.Core.Sources.Profiles;

/// <summary>
/// A saved source preset: a named (source + content config) bundle the user can
/// switch to in one step. <see cref="Content"/> holds only the source's content
/// fields (playlist URL, folder, normalization, …) — environment fields like tool
/// paths stay in the global per-source config so they aren't frozen per profile.
/// </summary>
public sealed record SourceProfile(
    string Id,
    string Name,
    string SourceId,
    IReadOnlyDictionary<string, object?> Content);
