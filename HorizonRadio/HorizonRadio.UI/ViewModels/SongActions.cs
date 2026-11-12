using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using CommunityToolkit.Mvvm.Input;
using HorizonRadio.Core.History;
using HorizonRadio.Core.Metadata;
using HorizonRadio.Core.Sources;
using HorizonRadio.Core.Sources.Mixes;
using HorizonRadio.Core.Sources.Queue;
using HorizonRadio.Sdk.Models;
using HorizonRadio.Sdk.Sources;
using ShadUI;

namespace HorizonRadio.UI.ViewModels;

/// <summary>
/// Something shown as a song (or a playlist, album, folder or station) that can be added to the
/// queue or a mix: what it's called, and the entry that plays it. A song with no entry of its own
/// (a radio song) carries its <see cref="Seed"/> instead, to be found on a service when it's added.
/// <see cref="Known"/> is what a mix can show for it without looking it up.
/// </summary>
public sealed record Song(string Title, string? Subtitle, ContentRef? Origin, Track? Seed = null, MixEntryInfo? Known = null)
{
    /// <summary>A played or queued track. <paramref name="resolved"/> says its metadata is final
    /// (the song playing now), so a mix can show it as is; otherwise a mix looks it up. Null for
    /// something that isn't a song (a radio station before its first title).</summary>
    public static Song? FromTrack(Track track, bool resolved = false)
    {
        if (string.IsNullOrWhiteSpace(track.Title)) return null;
        var origin = HistoryReplay.OriginOf(track);
        if (origin is null && !track.Resolvable) return null;

        var title = track.Title.Trim();
        var artist = string.IsNullOrWhiteSpace(track.Artist) ? null : track.Artist.Trim();
        var known = resolved && origin != null && track.AlbumArt is { Length: > 0 } art
            ? new MixEntryInfo(title, artist, ContentKind.Track, 1, art)
            : null;
        return new Song(title, artist, origin, track, known);
    }

    /// <summary>A mix entry, a search result, or a preview row: an entry that's already known.</summary>
    public static Song FromEntry(ContentRef entry, string title, string? subtitle = null, MixEntryInfo? known = null) =>
        new(title, subtitle, entry with { DisplayName = title }, Known: known);
}

/// <summary>A <see cref="Song"/> bound to the actions that add it somewhere: what a song's right-click
/// menu, Add To Queue button, and Add To Mix button bind to.</summary>
public sealed class SongTarget
{
    public SongActions Actions { get; }
    public Song Song { get; }
    public ICommand AddToQueueCommand { get; }

    public SongTarget(SongActions actions, Song song)
    {
        Actions = actions;
        Song = song;
        AddToQueueCommand = new AsyncRelayCommand(() => actions.AddToQueueAsync(song));
    }
}

/// <summary>Whether adding an entry to a mix did anything.</summary>
public enum MixAddResult { Added, AlreadyInMix, NoSuchMix }

/// <summary>
/// Adds songs to the queue or a mix from anywhere they show (History, search, the queue, the song
/// playing now, a mix's entries). A song with an entry of its own adds that; a radio song is first
/// identified and found on a service (<see cref="SongSources"/>), and the first service found is
/// what's added. Changes to mixes go through the Mixes tab (<see cref="Editor"/>), so a mix that's
/// open there shows the new entry too. Toasts say what happened. UI thread only.
/// </summary>
public sealed class SongActions
{
    private readonly QueuePlayback? _queue;
    private readonly MixStore? _mixes;
    private readonly MetadataResolver? _resolver;
    private readonly ToastManager? _toasts;

    // Radio songs already found on a service, by artist and title, so adding one again is instant.
    private readonly Dictionary<string, ContentRef> _found = new(StringComparer.OrdinalIgnoreCase);

    public SongActions(QueuePlayback? queue, MixStore? mixes, MetadataResolver? resolver, ToastManager? toasts)
    {
        _queue = queue;
        _mixes = mixes;
        _resolver = resolver;
        _toasts = toasts;
    }

    /// <summary>The Mixes tab, which owns changes to mixes (set once it exists).</summary>
    public MixesViewModel? Editor { get; set; }

    /// <summary>Opens the Mixes tab, for New Mix….</summary>
    public Action? ShowMixes { get; set; }

    /// <summary>The mixes a song can be added to, in library order.</summary>
    public IReadOnlyList<Mix> Mixes => _mixes?.All ?? [];

    public SongTarget? For(Song? song) => song is null ? null : new SongTarget(this, song);

    /// <summary>True when the mix already has this song's entry (a radio song can't tell until
    /// it's found, so it never is).</summary>
    public static bool IsIn(Song song, Mix mix) =>
        song.Origin is { } o && mix.Entries.Any(e => e.SourceId == o.SourceId && e.Locator == o.Locator);

    public async Task AddToQueueAsync(Song song)
    {
        if (_queue is null) return;
        var entry = await EntryForAsync(song);
        if (entry is null) return;
        if (_queue.Sources.Find(entry.SourceId) is not { } factory)
        {
            Toast("Couldn't add that to the queue", $"{entry.SourceId} isn't available.").ShowError();
            return;
        }

        try
        {
            await _queue.EnqueueLocatorAsync(factory, entry.Locator);
            Toast("Added to the queue", song.Title).ShowSuccess();
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[hzn-songs] queue {entry.SourceId} {entry.Locator}: {ex.Message}");
            Toast($"Couldn't add “{song.Title}” to the queue", ex.Message).ShowError();
        }
    }

    public async Task AddToMixAsync(Song song, Mix mix)
    {
        if (Editor is null) return;
        var entry = await EntryForAsync(song);
        if (entry is null) return;

        switch (Editor.AddToMix(mix.Id, entry, KnownFor(song)))
        {
            case MixAddResult.Added:
                Toast($"Added to “{mix.Name}”", song.Title).ShowSuccess();
                break;
            case MixAddResult.AlreadyInMix:
                Toast($"Already in “{mix.Name}”", song.Title).ShowInfo();
                break;
            default:
                Toast("Couldn't add that", $"“{mix.Name}” was deleted.").ShowWarning();
                break;
        }
    }

    /// <summary>Open a new mix in the Mixes tab with this song as its first entry.</summary>
    public async Task NewMixAsync(Song song)
    {
        if (Editor is null) return;
        var entry = await EntryForAsync(song);
        if (entry is null) return;
        Editor.NewMixWith(entry, KnownFor(song));
        ShowMixes?.Invoke();
    }

    // What a mix can show for the song without a lookup. Only for the song's own entry: a radio
    // song's details describe the station's stream, not the service's track that was found.
    private static MixEntryInfo? KnownFor(Song song) => song.Origin is null ? null : song.Known;

    // The entry that plays the song: its own, else (a radio song) the first service that has it.
    private async Task<ContentRef?> EntryForAsync(Song song)
    {
        if (song.Origin is { } origin) return origin;
        if (song.Seed is not { } seed)
        {
            NotFound(song);
            return null;
        }

        var key = $"{seed.Artist}\n{seed.Title}";
        if (_found.TryGetValue(key, out var found)) return found;

        Toast($"Looking for “{song.Title}”…", "Finding it on your music services.").ShowInfo();
        try
        {
            var sources = await SongSources.FindAsync(_queue?.Sources ?? SourceCatalog.Empty, _resolver, seed, CancellationToken.None);
            if (sources.Count == 0)
            {
                NotFound(song);
                return null;
            }
            found = new ContentRef(sources[0].SourceId, sources[0].Locator, song.Title);
            _found[key] = found;
            return found;
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[hzn-songs] find {seed.Artist} - {seed.Title}: {ex.Message}");
            NotFound(song);
            return null;
        }
    }

    private void NotFound(Song song) =>
        Toast($"Couldn't find “{song.Title}”", "None of your music services has this song, or it couldn't be identified.")
            .ShowWarning();

    private ToastBuilder Toast(string title, string content) =>
        (_toasts ?? new ToastManager()).CreateToast(title).WithContent(content).WithDelay(4).DismissOnClick();
}
