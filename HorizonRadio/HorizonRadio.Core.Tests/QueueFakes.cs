using System.Threading.Channels;
using HorizonRadio.Core.Models;
using HorizonRadio.Core.Sources;
using HorizonRadio.Core.Sources.Queue;

namespace HorizonRadio.Core.Tests;

// Fakes for driving the queue engine step by step: items and a context whose timing the test
// controls, instead of real files playing in real time.

internal sealed class NullPcmSink : IPcmSink
{
    public bool Send(ReadOnlySpan<short> samples) => true;
}

/// <summary>An item that plays until it's cancelled or the test ends it, recording each play.</summary>
internal sealed class FakeItem : PlayableItem
{
    private readonly List<TimeSpan> _startPositions = new();
    private TaskCompletionSource _end = NewSignal();
    private int _plays;

    public FakeItem(string title, TimeSpan? duration = null)
    {
        Metadata = new Track(title, "", null, null, "test", "Test");
        Duration = duration;
    }

    public static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>How many times a play began.</summary>
    public int Plays => Volatile.Read(ref _plays);

    /// <summary>Where each play that got going was told to start.</summary>
    public IReadOnlyList<TimeSpan> StartPositions
    {
        get { lock (_startPositions) return [.. _startPositions]; }
    }

    /// <summary>The pump context of the latest play.</summary>
    public PumpContext? Context { get; private set; }

    /// <summary>What <see cref="Position"/> reports.</summary>
    public TimeSpan At { get; set; }

    public override TimeSpan Position => At;
    public override bool CanSeek => true;

    /// <summary>When set, a play waits for it before it starts (like an item still resolving).</summary>
    public TaskCompletionSource? HoldStart { get; set; }

    /// <summary>When set, a cancelled play waits for it before giving up, so a test can land
    /// several requests before the engine reacts to the first.</summary>
    public TaskCompletionSource? HoldCancel { get; set; }

    /// <summary>End the current play as if the track ran out.</summary>
    public void End() => Volatile.Read(ref _end).TrySetResult();

    public override async Task PlayAsync(PumpContext ctx, CancellationToken ct)
    {
        var end = NewSignal();
        Volatile.Write(ref _end, end);
        Context = ctx;
        Interlocked.Increment(ref _plays);
        try
        {
            if (HoldStart is { } hold) await hold.Task.WaitAsync(ct);
            var start = ctx.StartPosition?.Invoke(this) ?? TimeSpan.Zero;
            lock (_startPositions) _startPositions.Add(start);
            ctx.OnStarted?.Invoke(this);
            await end.Task.WaitAsync(ct);
        }
        catch (OperationCanceledException) when (HoldCancel is { } release)
        {
            await release.Task;
            throw;
        }
    }
}

/// <summary>A context that hands out only what the test feeds it, so its "resolve" takes as
/// long as the test likes. Like a mix, it returns null when cancelled.</summary>
internal sealed class FakeContext(string name = "Fake") : IQueueContext
{
    private readonly Channel<PlayableItem> _feed = Channel.CreateUnbounded<PlayableItem>();
    private int _calls;
    private int _cancelled;

    public string DisplayName => name;
    public bool HasEntries => true;

    /// <summary>How many times the engine asked for a track.</summary>
    public int Calls => Volatile.Read(ref _calls);

    /// <summary>How many of those asks it gave up on.</summary>
    public int Cancelled => Volatile.Read(ref _cancelled);

    public void Feed(PlayableItem item) => _feed.Writer.TryWrite(item);

    public async Task<PlayableItem?> NextAsync(CancellationToken ct)
    {
        Interlocked.Increment(ref _calls);
        try
        {
            return await _feed.Reader.ReadAsync(ct);
        }
        catch (OperationCanceledException)
        {
            Interlocked.Increment(ref _cancelled);
            return null;
        }
    }

    public IReadOnlyList<QueuePreview> Peek(int count) => [];

    public void SetShuffle(bool enabled) { }
}

internal static class Eventually
{
    /// <summary>Wait until <paramref name="condition"/> holds, failing the test after a while.</summary>
    public static async Task Until(Func<bool> condition, string what, int timeoutMs = 5000)
    {
        var start = Environment.TickCount64;
        while (!condition())
        {
            if (Environment.TickCount64 - start > timeoutMs) Assert.Fail($"timed out waiting for {what}");
            await Task.Delay(10);
        }
    }
}
