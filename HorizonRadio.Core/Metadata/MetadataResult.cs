using System.Collections.Generic;
using HorizonRadio.Core.Models;

namespace HorizonRadio.Core.Metadata;

/// <summary>
/// The full outcome of <see cref="MetadataResolver.ResolveDetailedAsync"/>: the merged
/// <see cref="Track"/>, whether a catalog actually confirmed it (<see cref="Matched"/>), and any
/// playable links contributors learned about it (<see cref="Playables"/> — e.g. VocaDB's PV list).
/// <see cref="MetadataResolver.ResolveAsync"/> returns just the <see cref="Track"/>; only callers
/// that need the verdict or the PV links (play history) use this richer result.
/// </summary>
public sealed record MetadataResult(Track Track, bool Matched, IReadOnlyList<PlayableRef> Playables);
