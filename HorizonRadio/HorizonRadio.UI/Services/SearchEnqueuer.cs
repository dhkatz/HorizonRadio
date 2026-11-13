using System;
using System.Threading.Tasks;
using HorizonRadio.Core.Sources.Queue;
using HorizonRadio.Sdk.Sources;
using ShadUI;

namespace HorizonRadio.UI.Services;

/// <summary>
/// Turns a <see cref="SearchResult"/> into a queue action — the one place the two
/// search surfaces (top-bar dropdown, search page) share. The queue does the work
/// (<see cref="QueuePlayback.EnqueueAsync"/>, which also tells the Console); this only
/// tells the user when it fails.
/// </summary>
public sealed class SearchEnqueuer
{
    private readonly QueuePlayback _queue;
    private readonly ToastManager? _toasts;

    /// <summary>Adds a result to a mix (its rows' Add To Mix), when the shell has one.</summary>
    public ViewModels.SongActions? Songs { get; init; }

    public SearchEnqueuer(QueuePlayback queue, ToastManager? toasts = null)
    {
        _queue = queue;
        _toasts = toasts;
    }

    /// <summary>Add the result to the queue. <paramref name="playNow"/> jumps to it so
    /// it starts immediately (the row's Play action) rather than appending (Add).</summary>
    public async Task EnqueueAsync(SearchResult result, bool playNow)
    {
        try
        {
            await _queue.EnqueueAsync(new ContentRef(result.SourceId, result.Locator, result.Title), playNow);
        }
        catch (Exception ex)
        {
            // Surface it: a swallowed failure here is exactly why "Play does nothing" was
            // invisible. The queue has told the Console; this tells the user.
            _toasts?.CreateToast("Couldn't play that")
                .WithContent(ex.Message)
                .WithDelay(8)
                .DismissOnClick()
                .ShowError();
        }
    }
}
