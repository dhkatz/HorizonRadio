namespace HorizonRadio.Sdk;

/// <summary>
/// Something the host lists in a stable order: plugins, and what they contribute (sources, metadata
/// providers, lyrics providers). Neither the plugin scan nor the container's registrations come in
/// a useful order, so the host sorts by <see cref="SortOrder"/>, then <see cref="Id"/>.
/// </summary>
public interface IOrdered
{
    /// <summary>Stable, lowercase id: what settings and saved choices refer to it by.</summary>
    string Id { get; }

    /// <summary>
    /// Position among its kind, ascending. First-party plugins and contributions set explicit values
    /// to keep their shipped order (and for lyrics, which provider wins a tie); anything that
    /// doesn't goes last.
    /// </summary>
    int SortOrder => int.MaxValue;
}
