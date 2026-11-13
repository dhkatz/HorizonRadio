using HorizonRadio.Sdk.Metadata;
using HorizonRadio.Sdk.Sources.Config;

namespace HorizonRadio.Core.Metadata;

/// <summary>
/// Keeps the resolver on the user's metadata settings: built from them at start, and again when
/// the provider order or a field's chosen provider changes, here or on another of your PCs (they
/// sync), or when the Metadata page saves (<see cref="Save"/>).
/// </summary>
public sealed class MetadataPipeline : IDisposable
{
    private readonly MetadataConfigStore _config;
    private readonly MetadataResolver _resolver;
    private readonly IDisposable _orderWatch;
    private readonly IDisposable _forcedWatch;
    private int _saving;

    public MetadataPipeline(MetadataConfigStore config, MetadataResolver resolver)
    {
        _config = config;
        _resolver = resolver;
        Apply();
        _orderWatch = config.Settings.Watch(MetadataSettings.Order, _ => OnChanged());
        _forcedWatch = config.Settings.Watch(MetadataSettings.Forced, _ => OnChanged());
    }

    /// <summary>Save the metadata settings and rebuild the resolver from them, once.</summary>
    /// <param name="providers">Each provider's settings (kept for a disabled one too, so its
    /// credentials stay).</param>
    /// <param name="order">The enabled providers, first asked first.</param>
    /// <param name="forced">The provider each field is taken from, when not the first that has it.</param>
    public void Save(IEnumerable<(string Id, ConfigValues Values)> providers, IReadOnlyList<string> order,
        IReadOnlyDictionary<MetadataField, string> forced)
    {
        ArgumentNullException.ThrowIfNull(providers);
        Interlocked.Increment(ref _saving);
        try
        {
            foreach (var (id, values) in providers) _config.Save(id, values);
            _config.Order = order;
            _config.Forced = forced;
        }
        finally { Interlocked.Decrement(ref _saving); }
        Apply();
    }

    private void OnChanged()
    {
        if (Volatile.Read(ref _saving) == 0) Apply();
    }

    private void Apply()
    {
        var (contributors, policy) = _config.Catalog.BuildPipeline(_config);
        _resolver.Configure(contributors, policy);
    }

    public void Dispose()
    {
        _orderWatch.Dispose();
        _forcedWatch.Dispose();
    }
}
