using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using HorizonRadio.Core.Sources.Config;
using HorizonRadio.Core.Sources.Mixes;

namespace HorizonRadio.Core.Sources.Queue;

/// <summary>
/// The single entry point everything that feeds the queue goes through — quick-play
/// one-offs, "play a mix", and the mix-switch hotkeys (via <see cref="MixSwitcher"/>).
/// Owns the long-lived <see cref="QueueModel"/> and is responsible for making the
/// <see cref="QueueSource"/> the active source on the runner whenever there's content
/// to play, recreating it after a self-driven source (Spotify Connect) took over.
///
/// Resolving (a yt-dlp enumerate) happens off the gate; only the model mutation and
/// the runner hand-off are serialized, so concurrent triggers can't race on the
/// runner or double-start the engine.
/// </summary>
public sealed class QueuePlayback : IDisposable
{
    private readonly SourceRunner _runner;
    private readonly SourceConfigStore _config;
    private readonly MixContentResolver _resolver;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public QueueModel Model { get; } = new();

    public QueuePlayback(SourceRunner runner, SourceConfigStore config, MixContentResolver resolver)
    {
        _runner = runner;
        _config = config;
        _resolver = resolver;
    }

    /// <summary>True when the queue engine is the source currently on the runner
    /// (as opposed to a self-driven source like Spotify, or nothing).</summary>
    public bool IsActive => _runner.ActiveSource is QueueSource;

    /// <summary>Ensure the queue engine is the active source, recreating it if a
    /// self-driven source had taken over. The model (and so the queue's contents
    /// and context) is preserved across the swap.</summary>
    public async Task EnsureActiveAsync()
    {
        if (_runner.ActiveSource is QueueSource) return;
        await _runner.StartSourceAsync(new QueueSource(Model)).ConfigureAwait(false);
    }

    /// <summary>Resolve a one-off locator and append it to the explicit queue,
    /// starting the engine if it isn't already playing. Mirrors the old quick-play
    /// pre-flight (tool check) so a missing tool still surfaces a friendly error.</summary>
    public async Task EnqueueLocatorAsync(IAudioSourceFactory factory, string locator, CancellationToken ct = default)
    {
        if (factory is not IContentSourceFactory)
            throw new InvalidOperationException($"{factory.DisplayName} can't be added to the queue.");
        if (string.IsNullOrWhiteSpace(locator)) return;

        var values = _config.Load(factory.Id, factory.Schema);
        SourceRequirements.EnsureToolsAvailable(factory, values);

        var items = await _resolver.EnumerateAsync(new ContentRef(factory.Id, locator.Trim()), ct)
            .ConfigureAwait(false);
        if (items.Count == 0)
            throw new InvalidOperationException("That didn't resolve to anything playable.");

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            Model.AppendExplicit(items);
            await EnsureActiveAsync().ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    /// <summary>Play a mix into the queue. <see cref="QueueAddMode.Replace"/> makes
    /// it the queue's context (the infinite tail) and switches to it now;
    /// <see cref="QueueAddMode.Add"/> snapshots one lap of its tracks as explicit
    /// items ahead of the existing context. Pre-flights every entry's tools first so
    /// a missing tool reports up front instead of silently skipping mid-mix.</summary>
    public async Task PlayMixAsync(Mix mix, QueueAddMode mode, CancellationToken ct = default)
    {
        if (mix.Entries.Count == 0)
            throw new InvalidOperationException($"'{mix.Name}' has no entries to play.");
        EnsureMixToolsAvailable(mix);

        if (mode == QueueAddMode.Replace)
        {
            var provider = new MixContextProvider(mix, _resolver, _runner.Shuffle);
            await _gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                Model.SetContext(provider, mix.Id, replaceNow: true);
                await EnsureActiveAsync().ConfigureAwait(false);
            }
            finally { _gate.Release(); }
            return;
        }

        // Add: one lap of the mix, in order, resolved off the gate.
        var all = new List<PlayableItem>();
        foreach (var entry in mix.Entries)
        {
            try { all.AddRange(await _resolver.EnumerateAsync(entry, ct).ConfigureAwait(false)); }
            catch (OperationCanceledException) { throw; }
            catch { /* skip an unresolvable entry, like the mix engine does */ }
        }
        if (all.Count == 0)
            throw new InvalidOperationException($"'{mix.Name}' produced nothing to queue.");

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            Model.AppendExplicit(all);
            await EnsureActiveAsync().ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    private void EnsureMixToolsAvailable(Mix mix)
    {
        foreach (var sourceId in mix.Entries.Select(e => e.SourceId).Distinct())
        {
            if (SourceCatalog.Find(sourceId) is not IContentSourceFactory factory) continue;
            var values = _config.Load(factory.Id, factory.Schema);
            SourceRequirements.EnsureToolsAvailable(factory, values);
        }
    }

    public void Dispose() => _gate.Dispose();
}
