using HorizonRadio.Core.Models;

namespace HorizonRadio.Core.Sources.Queue;

/// <summary>
/// A read-only "what's coming up" row for the queue sidebar's
/// "Next from: &lt;Mix&gt;" section. The mix context loops forever and a remote
/// playlist can be huge, so we never expand it eagerly — instead the provider
/// returns a rolling window of these: the already-resolved items of the current
/// entry (exact title/artist) followed by one <see cref="IsPlaceholder"/> row per
/// upcoming entry (a folder / playlist that hasn't been resolved yet).
/// </summary>
public sealed record QueuePreview(string Title, string Subtitle, bool IsPlaceholder)
{
    public static QueuePreview ForTrack(Track t) => new(
        string.IsNullOrWhiteSpace(t.Title) ? "Unknown track" : t.Title,
        string.IsNullOrWhiteSpace(t.Artist) ? "" : t.Artist,
        IsPlaceholder: false);

    public static QueuePreview ForEntry(ContentRef entry)
    {
        var source = SourceCatalog.Find(entry.SourceId)?.DisplayName ?? entry.SourceId;
        return new(entry.DisplayName ?? entry.Locator, source, IsPlaceholder: true);
    }
}
