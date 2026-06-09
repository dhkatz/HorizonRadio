using System;
using System.Collections.Concurrent;

namespace HorizonRadio.Core.Events;

/// <summary>
/// Per-key time gate: <see cref="ShouldFire"/> returns true at most once per
/// <c>windowMs</c> for a given key. Shared by the action producers — game-event
/// rules (debounced per event kind) and input bindings (debounced per physical
/// input) — so the collapse logic lives in one place. Thread-safe; callers fire
/// from event-source / backend threads.
/// </summary>
public sealed class Debouncer
{
    private readonly ConcurrentDictionary<string, long> _last = new();
    private readonly long _windowMs;

    public Debouncer(long windowMs) => _windowMs = windowMs;

    /// <summary>True if <paramref name="key"/> hasn't fired within the window;
    /// records the time only when it returns true.</summary>
    public bool ShouldFire(string key)
    {
        var now = Environment.TickCount64;
        if (_last.TryGetValue(key, out var prev) && now - prev < _windowMs) return false;
        _last[key] = now;
        return true;
    }
}
