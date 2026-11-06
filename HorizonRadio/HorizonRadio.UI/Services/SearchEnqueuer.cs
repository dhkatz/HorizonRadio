using System;
using System.Threading.Tasks;
using HorizonRadio.Core.Diagnostics;
using HorizonRadio.Core.Sources;
using HorizonRadio.Core.Sources.Queue;
using ShadUI;

namespace HorizonRadio.UI.Services;

/// <summary>
/// Turns a <see cref="SearchResult"/> into a queue action — the one place the two
/// search surfaces (top-bar dropdown, search page) share. Resolves the result's
/// <see cref="SearchResult.SourceId"/> back to its catalog factory and hands the
/// locator to <see cref="QueuePlayback.EnqueueLocatorAsync"/>, which already knows how
/// to resolve and play any content source — so search adds no playback code.
/// </summary>
public sealed class SearchEnqueuer
{
    private readonly QueuePlayback _queue;
    private readonly ToastManager? _toasts;

    public SearchEnqueuer(QueuePlayback queue, ToastManager? toasts = null)
    {
        _queue = queue;
        _toasts = toasts;
    }

    /// <summary>Add the result to the queue. <paramref name="playNow"/> jumps to it so
    /// it starts immediately (the row's Play action) rather than appending (Add).</summary>
    public async Task EnqueueAsync(SearchResult result, bool playNow)
    {
        if (SourceCatalog.Find(result.SourceId) is not { } factory)
        {
            ProcessConsole.Append("search", $"no factory for source '{result.SourceId}'");
            return;
        }

        try
        {
            await _queue.EnqueueLocatorAsync(factory, result.Locator, playNow);
            ProcessConsole.Append("search",
                $"{(playNow ? "play" : "add")} ok: {result.Title} ({result.Locator})");
        }
        catch (Exception ex)
        {
            // Surface it — a swallowed failure here is exactly why "Play does nothing"
            // was invisible. Console line for diagnosis + a toast for the user.
            ProcessConsole.Append("search", $"enqueue FAILED for {result.Locator}: {ex.Message}");
            _toasts?.CreateToast("Couldn't play that")
                .WithContent(ex.Message)
                .WithDelay(8)
                .DismissOnClick()
                .ShowError();
        }
    }
}
