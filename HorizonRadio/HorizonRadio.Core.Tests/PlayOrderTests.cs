using HorizonRadio.Core.Audio;

namespace HorizonRadio.Core.Tests;

/// <summary>
/// The shared iteration order: sequential walk, shuffle toggles that keep the current
/// item (and, optionally, what already played), and a fresh permutation on each wrap.
/// </summary>
public class PlayOrderTests
{
    private static PlayOrder Order(int count, int seed = 1)
    {
        var order = new PlayOrder(new Random(seed));
        order.Reset(count);
        return order;
    }

    // The current item, then each one the cursor visits until it walks off the end.
    private static List<int> Walk(PlayOrder order)
    {
        var seen = new List<int>();
        for (var i = order.CurrentIndex; i >= 0; i = order.Advance(wrap: false)) seen.Add(i);
        return seen;
    }

    [Fact]
    public void Sequential_walks_in_order_then_off_the_end()
    {
        var order = Order(4);
        Assert.Equal([0, 1, 2, 3], Walk(order));
        Assert.Equal(-1, order.CurrentIndex);
        Assert.Empty(order.RemainingIndices());
    }

    [Fact]
    public void Shuffle_on_with_keepCurrent_pins_the_current_item_first()
    {
        var order = Order(8);
        order.Advance(wrap: false);
        order.Advance(wrap: false); // on item 2

        order.SetShuffle(true, keepCurrent: true);

        Assert.True(order.Shuffled);
        Assert.Equal(2, order.CurrentIndex);
        Assert.Equal(Enumerable.Range(0, 8), order.RemainingIndices().Order());
    }

    [Fact]
    public void Shuffle_on_with_keepPlayed_reorders_only_what_comes_after_the_current_item()
    {
        for (var seed = 0; seed < 20; seed++)
        {
            var order = Order(8, seed);
            order.Advance(wrap: false);
            order.Advance(wrap: false); // on item 2; 0 and 1 played

            order.SetShuffle(true, keepCurrent: true, keepPlayed: true);

            Assert.True(order.Shuffled);
            var remaining = order.RemainingIndices();
            Assert.Equal(2, remaining[0]);
            Assert.Equal([3, 4, 5, 6, 7], remaining.Skip(1).Order());
        }
    }

    [Fact]
    public void Shuffle_off_with_keepCurrent_resumes_natural_order_after_the_current_item()
    {
        var order = Order(6);
        order.SetShuffle(true, keepCurrent: false);
        order.Advance(wrap: false);
        var current = order.CurrentIndex;

        order.SetShuffle(false, keepCurrent: true, keepPlayed: true);

        Assert.False(order.Shuffled);
        Assert.Equal(current, order.CurrentIndex);
        Assert.Equal(Enumerable.Range(current, 6 - current), order.RemainingIndices());
    }

    [Fact]
    public void KeepCurrent_with_the_cursor_off_the_end_restarts_from_the_top()
    {
        // Nothing is current to pin, so the order starts over: a caller whose cursor
        // walks off the end must not toggle there if the last item is still playing.
        var order = Order(3);
        Walk(order);
        Assert.Equal(-1, order.CurrentIndex);

        order.SetShuffle(true, keepCurrent: true, keepPlayed: true);

        Assert.Equal(Enumerable.Range(0, 3), order.RemainingIndices().Order());
        Assert.True(order.CurrentIndex >= 0);
    }

    [Fact]
    public void Shuffled_order_reshuffles_on_wrap_and_visits_every_item_each_pass()
    {
        var order = Order(10, seed: 7);
        order.SetShuffle(true, keepCurrent: false);

        var first = new List<int>();
        var second = new List<int>();
        for (var i = 0; i < 10; i++) { first.Add(order.CurrentIndex); order.Advance(wrap: true); }
        for (var i = 0; i < 10; i++) { second.Add(order.CurrentIndex); order.Advance(wrap: true); }

        Assert.Equal(Enumerable.Range(0, 10), first.Order());
        Assert.Equal(Enumerable.Range(0, 10), second.Order());
        Assert.NotEqual(first, second); // a fresh permutation (fixed seed, so deterministic)
    }

    [Fact]
    public void SetShuffle_to_the_current_mode_is_a_no_op()
    {
        var order = Order(5);
        order.Advance(wrap: false);
        order.SetShuffle(false, keepCurrent: true);
        Assert.Equal([1, 2, 3, 4], order.RemainingIndices());
    }
}
