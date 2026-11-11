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
/// Owns the long-lived <see cref="QueueModel"/> and the player's queue engine, and asks
/// the playback router to play the queue whenever there's content: the router is the
/// only one that puts the engine on the runner (or parks it, or recreates it after a
/// self-driven source like Spotify Connect took over), so a change of station and an
/// addition to the queue can't race for it. The router creates this (its player channel).
///
/// Resolving (a yt-dlp enumerate) happens off the gate; only the model mutations are
/// serialized, so concurrent triggers can't interleave a multi-step change.
/// </summary>
public sealed class QueuePlayback : IDisposable
{
    private readonly SourceRunner _runner;
    private readonly SourceConfigStore _config;
    private readonly MixContentResolver _resolver;
    private readonly Func<Task> _requestPlay;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public QueueModel Model { get; } = new();

    // The player's engine. Long-lived so the router can park it (tuning to a mix station) and
    // resume it later with its history and place intact; recreated only after it was disposed
    // (e.g. a self-driven source like Spotify Connect took the runner). Only the router's loop
    // creates, starts, parks or disposes it.
    private volatile QueueSource? _source;

    /// <param name="requestPlay">Asks the router to play the queue: it starts the engine if the
    /// player has the runner (not while a game routes playback to a mix station or silence:
    /// additions then wait in the queue until the game routes back to the player).</param>
    internal QueuePlayback(SourceRunner runner, SourceConfigStore config, MixContentResolver resolver,
                           Func<Task> requestPlay)
    {
        _runner = runner;
        _config = config;
        _resolver = resolver;
        _requestPlay = requestPlay;
    }

    /// <summary>True when the queue engine is the source currently on the runner
    /// (as opposed to a self-driven source like Spotify, or nothing).</summary>
    public bool IsActive => _source is { } source && ReferenceEquals(_runner.ActiveSource, source);

    /// <summary>Ask for the queue to play, and wait until the router has acted on it: it starts
    /// the engine (recreating it if a self-driven source had taken over) when the player has the
    /// runner, and leaves the queue waiting otherwise. The model (and so the queue's contents and
    /// context) is preserved across engine swaps.</summary>
    public Task EnsureActiveAsync() => _requestPlay();

    /// <summary>The player's engine, for the router to start or park; null if never created.</summary>
    internal QueueSource? Source => _source;

    /// <summary>A fresh engine over the queue, replacing a disposed one (router only).</summary>
    internal QueueSource NewEngine() => _source = new QueueSource(Model);

    /// <summary>Resolve a one-off locator and append it to the explicit queue, and ask for
    /// the queue to play (<see cref="EnsureActiveAsync"/>). Mirrors the old quick-play
    /// pre-flight (tool check) so a missing tool still surfaces a friendly error.
    /// When <paramref name="playNow"/> is set, jumps to the first resolved item so it
    /// starts immediately (the search "Play" action), dropping anything queued ahead
    /// of it exactly like the per-row play-now.</summary>
    public async Task EnqueueLocatorAsync(IAudioSourceFactory factory, string locator, bool playNow = false, CancellationToken ct = default)
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
            Model.AppendExplicit(items, playNow);
        }
        finally { _gate.Release(); }
        await EnsureActiveAsync().ConfigureAwait(false);
    }

    /// <summary>The first thing a locator plays, without queueing it.</summary>
    /// <exception cref="InvalidOperationException">It can't be played here (a tool missing, nothing
    /// playable).</exception>
    public async Task<PlayableItem> ResolveAsync(IAudioSourceFactory factory, string locator, CancellationToken ct = default)
    {
        if (factory is not IContentSourceFactory)
            throw new InvalidOperationException($"{factory.DisplayName} can't be added to the queue.");
        var values = _config.Load(factory.Id, factory.Schema);
        SourceRequirements.EnsureToolsAvailable(factory, values);

        var items = await _resolver.EnumerateAsync(new ContentRef(factory.Id, locator.Trim()), ct).ConfigureAwait(false);
        return items.Count > 0 ? items[0] : throw new InvalidOperationException("That didn't resolve to anything playable.");
    }

    /// <summary>Play <paramref name="item"/> now, ahead of the queue, which keeps the rest (unlike
    /// <see cref="EnqueueLocatorAsync"/>'s play-now), as <paramref name="id"/> when given.
    /// <paramref name="joinAt"/>, when given, says where to start, worked out the moment it starts.
    /// Returns the queued item.</summary>
    public async Task<QueueItem> PlayNowAsync(PlayableItem item, Func<TimeSpan>? joinAt = null, string? id = null,
        CancellationToken ct = default)
    {
        QueueItem queued;
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            queued = Model.PlayNext(item, joinAt, id);
        }
        finally { _gate.Release(); }
        await EnsureActiveAsync().ConfigureAwait(false);
        return queued;
    }

    /// <summary>The engine on the runner, or null when the queue isn't what's playing.</summary>
    public QueueSource? ActiveEngine => _source is { } source && ReferenceEquals(_runner.ActiveSource, source) ? source : null;

    /// <summary>Play a mix into the queue. <see cref="QueueAddMode.Replace"/> makes
    /// it the queue's context (the infinite tail) and switches to it now;
    /// <see cref="QueueAddMode.Add"/> snapshots one lap of its tracks as explicit
    /// items ahead of the existing context. Pre-flights every entry's tools first so
    /// a missing tool reports up front instead of silently skipping mid-mix. While the queue is a
    /// listening party's, a mix is always added to it (the host's mix stays the party's).</summary>
    public async Task PlayMixAsync(Mix mix, QueueAddMode mode, CancellationToken ct = default)
    {
        if (mix.Entries.Count == 0)
            throw new InvalidOperationException($"'{mix.Name}' has no entries to play.");
        EnsureMixToolsAvailable(mix);

        if (mode == QueueAddMode.Replace && Model.Owner == null)
        {
            var provider = new MixContextProvider(mix, _resolver, _runner.Shuffle);
            await _gate.WaitAsync(ct).ConfigureAwait(false);
            try { Model.SetContext(provider, mix.Id, replaceNow: true); }
            finally { _gate.Release(); }
            await EnsureActiveAsync().ConfigureAwait(false);
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
        try { Model.AppendExplicit(all); }
        finally { _gate.Release(); }
        await EnsureActiveAsync().ConfigureAwait(false);
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
