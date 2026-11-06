using System;
using System.Threading;

namespace HorizonRadio.Core.Metadata;

/// <summary>
/// Tracks whether every request in one provider lookup succeeded, so a lookup that only
/// "found nothing" because a request failed (MusicBrainz throttling with a 503, a timeout, a
/// Cover Art Archive 500) isn't cached as if the catalog had answered. A cached miss is kept
/// for weeks; a failure should be retried the next time the song plays.
///
/// A provider opens a scope around its lookup; anything it calls (its request helper,
/// <see cref="ImageDownload"/>) reports failures into the scope it runs under, without the
/// failure being threaded through every return value. "Not found" (a 404) is an answer, not
/// a failure.
/// </summary>
public sealed class CatalogLookup : IDisposable
{
    private static readonly AsyncLocal<CatalogLookup?> Ambient = new();

    private readonly CatalogLookup? _outer;
    private string? _failure;

    private CatalogLookup(CatalogLookup? outer) => _outer = outer;

    /// <summary>Start tracking a lookup in the current async flow.</summary>
    public static CatalogLookup Begin()
    {
        var scope = new CatalogLookup(Ambient.Value);
        Ambient.Value = scope;
        return scope;
    }

    /// <summary>Record that a request in the current lookup failed. Harmless outside one.</summary>
    public static void ReportFailure(string reason)
    {
        if (Ambient.Value is { } scope) Interlocked.CompareExchange(ref scope._failure, reason, null);
    }

    /// <summary>True when any request in this lookup failed: its result (match or miss) isn't the
    /// catalog's full answer and must not be cached.</summary>
    public bool Failed => Volatile.Read(ref _failure) != null;

    /// <summary>The first failure reported, for logs and the trace.</summary>
    public string? Failure => Volatile.Read(ref _failure);

    /// <summary>The exception for a lookup that found nothing because a request failed.</summary>
    public CatalogUnavailableException Unavailable(string providerId) =>
        new(providerId, Failure ?? "a request failed");

    public void Dispose() => Ambient.Value = _outer;
}

/// <summary>A provider couldn't get the catalog's answer (HTTP error, timeout, not signed in).
/// The resolver treats it like any provider error: nothing contributed, nothing cached, logged.</summary>
public sealed class CatalogUnavailableException : Exception
{
    public CatalogUnavailableException(string providerId, string reason)
        : base($"{providerId} unavailable: {reason}") => ProviderId = providerId;

    public CatalogUnavailableException() { ProviderId = ""; }

    public CatalogUnavailableException(string message) : base(message) { ProviderId = ""; }

    public CatalogUnavailableException(string message, Exception innerException)
        : base(message, innerException) { ProviderId = ""; }

    public string ProviderId { get; }
}
