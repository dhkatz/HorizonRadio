using HorizonRadio.Sdk;
using HorizonRadio.Sdk.Metadata;
using HorizonRadio.Sdk.Tools;
using Microsoft.Extensions.DependencyInjection;

namespace HorizonRadio.TitleModel;

/// <summary>
/// Gives the host's title model a body: the host's <see cref="ITitleModel"/> knows the user's
/// setting but has no model to run, so this wraps it with one that runs the GGUF installed from the
/// Tools tab (<see cref="LocalTitleModel"/>). A host without this assembly parses titles without it.
/// </summary>
public sealed class TitleModelModule : IPluginModule
{
    public void ConfigureServices(IServiceCollection services) =>
        services.Decorate<ITitleModel>((host, sp) => new LocalTitleModel(host, sp.GetRequiredService<IToolLocator>()));
}

/// <summary>
/// The title model installed from the Tools tab, as it is now: installing it, updating it (a new
/// file) or removing it replaces the extractor the next time it's asked for, without a restart.
/// The extractor loads its weights on first use, so nothing loads until a messy title needs it.
/// </summary>
public sealed class LocalTitleModel(ITitleModel host, IToolLocator tools) : ITitleModel, IAsyncDisposable
{
    private readonly Lock _lock = new();
    // The file the extractor was built for (path and last write), so it's rebuilt only when it changes.
    private (string? Path, long WriteTicks) _stamp;
    private LlamaTitleExtractor? _extractor;

    public TitleModelMode Mode => host.Mode;

    public ITitleExtractor? Extractor
    {
        get
        {
            var stamp = Stamp();
            LlamaTitleExtractor? old;
            lock (_lock)
            {
                if (stamp == _stamp) return _extractor;
                _stamp = stamp;
                old = _extractor;
                _extractor = stamp.Path == null ? null : new LlamaTitleExtractor(() => tools.Find(ToolKind.TitleModel));
            }
            // DisposeAsync waits for an inference under way before freeing the weights.
            if (old != null) _ = old.DisposeAsync().AsTask();
            return _extractor;
        }
    }

    private (string? Path, long WriteTicks) Stamp()
    {
        if (tools.Find(ToolKind.TitleModel) is not { } path) return (null, 0);
        try
        {
            return (path, File.GetLastWriteTimeUtc(path).Ticks);
        }
        catch (IOException)
        {
            return (path, 0);
        }
        catch (UnauthorizedAccessException)
        {
            return (path, 0);
        }
    }

    public async ValueTask DisposeAsync()
    {
        LlamaTitleExtractor? extractor;
        lock (_lock)
        {
            extractor = _extractor;
            _extractor = null;
        }
        if (extractor != null) await extractor.DisposeAsync().ConfigureAwait(false);
    }
}
