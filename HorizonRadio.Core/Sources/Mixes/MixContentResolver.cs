using HorizonRadio.Core.Sources.Config;

namespace HorizonRadio.Core.Sources.Mixes;

/// <summary>
/// Resolves a <see cref="ContentRef"/> to the engine that can play it, tying
/// together the source registry and the global per-source config: find the
/// content-addressable factory by id, build its <see cref="IContentPlayer"/>
/// from the saved environment/behavior values, and enumerate the ref's items.
///
/// This is the one place the mix engine reaches into source configuration — a
/// mix entry only carries a (source id, locator); the tool paths and behavior
/// come from the same global config the Sources tab edits.
/// </summary>
public sealed class MixContentResolver(SourceConfigStore configStore)
{
    /// <summary>Build the player for <paramref name="content"/>'s source. Throws
    /// <see cref="InvalidOperationException"/> if the source id is unknown or not
    /// content-addressable (self-driven sources can't be mix entries), or if a
    /// required tool path is unset (propagated from <c>CreatePlayer</c>).</summary>
    public IContentPlayer ResolvePlayer(ContentRef content)
    {
        if (SourceCatalog.Find(content.SourceId) is not IContentSourceFactory factory)
            throw new InvalidOperationException(
                $"'{content.SourceId}' can't be played in a mix (unknown or self-driven source).");

        var values = configStore.Load(factory.Id, factory.Schema);
        return factory.CreatePlayer(values);
    }

    /// <summary>Expand a ref into its ordered playable items via its player.</summary>
    public Task<IReadOnlyList<PlayableItem>> EnumerateAsync(ContentRef content, CancellationToken ct)
        => ResolvePlayer(content).EnumerateAsync(content, ct);
}
