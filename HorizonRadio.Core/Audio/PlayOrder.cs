using System;
using System.Collections.Generic;

namespace HorizonRadio.Core.Audio;

/// <summary>
/// Owns an iteration order over a fixed set of items identified by index
/// <c>[0, count)</c>, plus a cursor into that order. Sequential mode is the
/// identity permutation (0,1,2,…); shuffle mode is a random permutation.
///
/// Both <see cref="Playlist"/> (local files) and the YouTube source share
/// this so the shuffle semantics live in exactly one place:
/// <list type="bullet">
/// <item><see cref="SetShuffle"/> with <c>keepCurrent:true</c> pins whatever
///   is playing to the front and shuffles the rest — the Spotify-style
///   "toggle shuffle mid-track" behavior.</item>
/// <item><c>keepCurrent:false</c> fully randomizes — used when a source starts
///   with shuffle already on (random first track) and when a shuffled order
///   wraps for a fresh pass.</item>
/// </list>
///
/// Not thread-safe: callers mutate it from a single playback loop, matching
/// the existing source threading model.
/// </summary>
internal sealed class PlayOrder
{
    private readonly Random _rng;
    private List<int> _order = new();
    private int _pos = -1;

    public PlayOrder(Random? rng = null) => _rng = rng ?? new Random();

    /// <summary>True when the current order is a random permutation.</summary>
    public bool Shuffled { get; private set; }

    public int Count => _order.Count;

    /// <summary>Item index the cursor points at, or -1 when empty / walked
    /// off the end (see <see cref="Advance"/> with <c>wrap:false</c>).</summary>
    public int CurrentIndex => _pos >= 0 && _pos < _order.Count ? _order[_pos] : -1;

    /// <summary>Rebuild as the identity order over <paramref name="count"/>
    /// items and park the cursor at the start. Clears shuffle.</summary>
    public void Reset(int count)
    {
        _order = new List<int>(count);
        for (int i = 0; i < count; i++) _order.Add(i);
        _pos = count > 0 ? 0 : -1;
        Shuffled = false;
    }

    /// <summary>Append one new item at the end of the order. Only valid while
    /// sequential (the load-time growth path); the appended index is the new
    /// last item. The cursor parks at 0 if the order was previously empty.</summary>
    public void Append()
    {
        _order.Add(_order.Count);
        if (_pos < 0) _pos = 0;
    }

    /// <summary>Advance one step. With <paramref name="wrap"/> the cursor loops
    /// at the end (and, when shuffled, reshuffles for a fresh pass); without it
    /// the cursor walks off the end and <see cref="CurrentIndex"/> goes -1.
    /// Returns the new current index.</summary>
    public int Advance(bool wrap)
    {
        if (_order.Count == 0) return -1;

        if (_pos + 1 < _order.Count)
        {
            _pos++;
        }
        else if (wrap)
        {
            if (Shuffled) Reshuffle(keepCurrent: false); // fresh permutation each pass
            _pos = 0;
        }
        else
        {
            _pos = _order.Count; // off the end; CurrentIndex == -1
        }

        return CurrentIndex;
    }

    /// <summary>Step back one. With <paramref name="wrap"/> the cursor loops to
    /// the end; without it the cursor clamps at the start. Returns the new
    /// current index.</summary>
    public int Retreat(bool wrap)
    {
        if (_order.Count == 0) return -1;

        if (_pos > 0) _pos--;
        else _pos = wrap ? _order.Count - 1 : 0;

        return CurrentIndex;
    }

    /// <summary>Turn shuffle on or off. <paramref name="keepCurrent"/> preserves
    /// the currently-pointed item (pinned to front when enabling, kept at its
    /// natural position when disabling); when false the order is fully
    /// randomized / reset and the cursor returns to the start. No-op if the
    /// requested mode already matches.</summary>
    public void SetShuffle(bool on, bool keepCurrent)
    {
        if (on == Shuffled) return;

        if (on)
        {
            Reshuffle(keepCurrent);
        }
        else
        {
            int cur = CurrentIndex;
            Reset(_order.Count);
            if (keepCurrent && cur >= 0) _pos = cur; // resume natural order here
        }
    }

    private void Reshuffle(bool keepCurrent)
    {
        int cur = CurrentIndex;

        // Fisher–Yates over the whole order.
        for (int i = _order.Count - 1; i > 0; i--)
        {
            int j = _rng.Next(i + 1);
            (_order[i], _order[j]) = (_order[j], _order[i]);
        }

        Shuffled = true;

        if (keepCurrent && cur >= 0)
        {
            int idx = _order.IndexOf(cur);
            (_order[0], _order[idx]) = (_order[idx], _order[0]);
            _pos = 0;
        }
        else
        {
            _pos = _order.Count > 0 ? 0 : -1;
        }
    }
}
