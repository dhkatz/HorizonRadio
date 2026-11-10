using HorizonRadio.Core.Audio;
using HorizonRadio.Core.Settings;
using HorizonRadio.Core.Sources;

namespace HorizonRadio.Core.Tests;

/// <summary>
/// The local-speaker state machine: Off / On / Faulted / Disposed, the tee route each
/// state implies, and the device events (loss, default change, device back) that move
/// it — with a fake speaker and watcher instead of WASAPI.
/// </summary>
public class PreviewControllerTests
{
    private const string Speakers = "speakers";
    private const string Headphones = "headphones";
    private const string Hdmi = "hdmi";

    /// <summary>A render "device set": opens the requested device if present, else the
    /// default (like the real fallback); can be told to fail or to lose its device.</summary>
    private sealed class FakeSpeaker : ISpeakerOutput
    {
        public HashSet<string> Present { get; } = [Speakers, Headphones];
        public string? Default { get; set; } = Speakers;
        public bool FailOpens { get; set; }
        public int Opens { get; private set; }
        public bool Disposed { get; private set; }

        public bool IsOpen => OpenedDeviceId != null;
        public string? OpenedDeviceId { get; private set; }
        public float Volume { get; set; }
        public event Action? Lost;

        public bool Open(string? deviceId)
        {
            Opens++;
            OpenedDeviceId = null;
            var target = deviceId != null && Present.Contains(deviceId) ? deviceId : Default;
            if (FailOpens || target == null || !Present.Contains(target)) return false;
            OpenedDeviceId = target;
            return true;
        }

        public void Close() => OpenedDeviceId = null;

        /// <summary>The device dies under us (the render thread stops with an error).</summary>
        public void Lose(bool unplug = true)
        {
            if (unplug && OpenedDeviceId != null) Present.Remove(OpenedDeviceId);
            OpenedDeviceId = null;
            Lost?.Invoke();
        }

        public bool Send(ReadOnlySpan<short> samples) => IsOpen;

        public void Dispose()
        {
            Close();
            Disposed = true;
        }
    }

    private sealed class FakeWatcher : IAudioDeviceWatcher
    {
        public bool Disposed { get; private set; }
        public event Action<string?>? DefaultDeviceChanged;
        public event Action<string, bool>? DeviceAvailabilityChanged;

        public void RaiseDefault(string? id) => DefaultDeviceChanged?.Invoke(id);
        public void RaiseAvailability(string id, bool available) => DeviceAvailabilityChanged?.Invoke(id, available);
        public void Dispose() => Disposed = true;
    }

    private sealed class RecordingSink : IPcmSink
    {
        public int Chunks { get; private set; }

        public bool Send(ReadOnlySpan<short> samples)
        {
            Chunks++;
            return true;
        }
    }

    /// <summary>The controller plus its fakes. Device callbacks are posted to a queue
    /// (the "UI thread") that <see cref="Drain"/> runs.</summary>
    private sealed class Harness
    {
        private readonly Queue<Action> _posted = new();

        public Harness(bool enabled = false, string? deviceId = null)
            : this(Preset(enabled, deviceId))
        {
        }

        public Harness(SettingsStore settings)
        {
            Settings = settings;
            Settings.Changed += _ => SettingChanges++;
            Controller = new PreviewController(Tee, settings, _posted.Enqueue, Speaker, Watcher);
            Controller.StateChanged += () => StateChanges++;
        }

        private static SettingsStore Preset(bool enabled, string? deviceId)
        {
            var settings = SettingsStore.CreateInMemory();
            settings.Set(PreviewSettings.Enabled, enabled);
            settings.Set(PreviewSettings.Device, deviceId);
            return settings;
        }

        public FakeSpeaker Speaker { get; } = new();
        public FakeWatcher Watcher { get; } = new();
        public RecordingSink Game { get; } = new();
        public TeePcmSink Tee => _tee ??= new TeePcmSink(Game);
        private TeePcmSink? _tee;
        public PreviewController Controller { get; }
        public SettingsStore Settings { get; }
        public int StateChanges { get; set; }
        public int SettingChanges { get; private set; }
        public int Posted => _posted.Count;

        public void Drain()
        {
            while (_posted.TryDequeue(out var action)) action();
        }
    }

    private static readonly short[] Chunk = [1, 2, 3, 4];

    private static void AssertRoutedTo(Harness h, SpeakerState state)
    {
        Assert.Equal(state, h.Controller.State);
        var route = h.Tee.Route;
        switch (state)
        {
            case SpeakerState.On:
                Assert.False(route.ToPrimary);
                Assert.Same(h.Speaker, route.Preview);
                break;
            case SpeakerState.Faulted:
                Assert.Equal(TeeRoute.Nowhere, route); // the game stays off: no leak while the picker says speakers
                break;
            default:
                Assert.Equal(TeeRoute.PrimaryOnly, route);
                break;
        }
    }

    [Fact]
    public void Starts_off_routed_to_the_game()
    {
        var h = new Harness();

        AssertRoutedTo(h, SpeakerState.Off);
        Assert.Equal(0, h.Speaker.Opens);
        h.Tee.Send(Chunk);
        Assert.Equal(1, h.Game.Chunks);
    }

    [Fact]
    public void Starts_on_when_monitoring_was_saved_on()
    {
        var h = new Harness(enabled: true, deviceId: Headphones);

        AssertRoutedTo(h, SpeakerState.On);
        Assert.Equal(Headphones, h.Speaker.OpenedDeviceId);
    }

    // (pre-existing state, how the user turns it on, open fails?) → resulting state
    [Theory]
    [InlineData("enable", false, SpeakerState.On)]
    [InlineData("pick", false, SpeakerState.On)]
    [InlineData("enable", true, SpeakerState.Faulted)]
    [InlineData("pick", true, SpeakerState.Faulted)]
    public void Turning_monitoring_on_opens_the_speaker_or_faults(string how, bool failOpen, SpeakerState expected)
    {
        var h = new Harness();
        h.Speaker.FailOpens = failOpen;

        if (how == "enable") h.Controller.SetEnabled(true);
        else h.Controller.SelectDevice(Headphones);

        AssertRoutedTo(h, expected);
        Assert.Equal(expected == SpeakerState.On, h.Controller.IsSpeakerActive);
        Assert.True(h.Controller.Enabled);
        Assert.Equal(1, h.StateChanges);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Turning_monitoring_off_returns_to_the_game(bool fromFaulted)
    {
        var h = new Harness();
        h.Speaker.FailOpens = fromFaulted;
        h.Controller.SelectDevice(Headphones);

        h.Controller.SetEnabled(false);

        AssertRoutedTo(h, SpeakerState.Off);
        Assert.False(h.Speaker.IsOpen);
    }

    // A fault is left by picking the same device again, or re-enabling: both retry.
    [Theory]
    [InlineData("pick")]
    [InlineData("enable")]
    public void Faulted_retries_when_the_user_reconfirms(string how)
    {
        var h = new Harness();
        h.Speaker.FailOpens = true;
        h.Controller.SelectDevice(Headphones);
        AssertRoutedTo(h, SpeakerState.Faulted);

        h.Speaker.FailOpens = false;
        if (how == "pick") h.Controller.SelectDevice(Headphones);
        else h.Controller.SetEnabled(true);

        AssertRoutedTo(h, SpeakerState.On);
        Assert.Equal(2, h.Speaker.Opens);
    }

    [Fact]
    public void Reconfirming_a_working_device_does_not_reopen_it()
    {
        var h = new Harness();
        h.Controller.SelectDevice(Headphones);
        var changes = h.SettingChanges;

        h.Controller.SelectDevice(Headphones);
        h.Controller.SetEnabled(true);

        Assert.Equal(1, h.Speaker.Opens);
        Assert.Equal(changes, h.SettingChanges); // nothing to persist on a re-confirmation
    }

    [Fact]
    public void Picking_another_device_reopens_on_it()
    {
        var h = new Harness();
        h.Controller.SelectDevice(Headphones);

        h.Controller.SelectDevice(Speakers);

        AssertRoutedTo(h, SpeakerState.On);
        Assert.Equal(Speakers, h.Speaker.OpenedDeviceId);
    }

    [Fact]
    public void Losing_the_device_while_on_faults_and_raises_state_changed()
    {
        var h = new Harness();
        h.Controller.SelectDevice(Headphones);
        h.StateChanges = 0;

        h.Speaker.Lose();
        // The loss arrives on the device's thread: it's posted, not acted on there.
        Assert.Equal(1, h.Posted);
        Assert.Equal(SpeakerState.On, h.Controller.State);

        h.Drain();

        AssertRoutedTo(h, SpeakerState.Faulted);
        Assert.False(h.Controller.IsSpeakerActive);
        Assert.Equal(1, h.StateChanges);
        h.Tee.Send(Chunk);
        Assert.Equal(0, h.Game.Chunks);
    }

    [Fact]
    public void Open_device_going_unavailable_faults_even_before_playback_notices()
    {
        var h = new Harness();
        h.Controller.SelectDevice(Headphones);

        h.Watcher.RaiseAvailability(Headphones, false);
        h.Drain();

        AssertRoutedTo(h, SpeakerState.Faulted);
        Assert.False(h.Speaker.IsOpen);
    }

    [Fact]
    public void Another_device_going_unavailable_is_ignored()
    {
        var h = new Harness();
        h.Controller.SelectDevice(Headphones);

        h.Watcher.RaiseAvailability(Hdmi, false);
        h.Drain();

        AssertRoutedTo(h, SpeakerState.On);
    }

    [Fact]
    public void A_stale_loss_after_reopening_elsewhere_is_ignored()
    {
        var h = new Harness();
        h.Controller.SelectDevice(Headphones);

        h.Speaker.Lose();
        h.Controller.SelectDevice(Speakers); // the user moves on before the loss is handled
        h.Drain();

        AssertRoutedTo(h, SpeakerState.On);
        Assert.Equal(Speakers, h.Speaker.OpenedDeviceId);
    }

    [Fact]
    public void Faulted_recovers_when_its_device_returns_but_not_for_another_device()
    {
        var h = new Harness();
        h.Controller.SelectDevice(Headphones);
        h.Speaker.Lose();
        h.Drain();
        var opens = h.Speaker.Opens;

        h.Speaker.Present.Add(Hdmi);
        h.Watcher.RaiseAvailability(Hdmi, true);
        h.Drain();
        AssertRoutedTo(h, SpeakerState.Faulted); // don't jump to some other device
        Assert.Equal(opens, h.Speaker.Opens);

        h.Speaker.Present.Add(Headphones);
        h.Watcher.RaiseAvailability(Headphones, true);
        h.Drain();
        AssertRoutedTo(h, SpeakerState.On);
        Assert.Equal(Headphones, h.Speaker.OpenedDeviceId);
    }

    [Fact]
    public void System_default_follows_a_new_default_device()
    {
        var h = new Harness();
        h.Controller.SelectDevice(null);
        Assert.Equal(Speakers, h.Speaker.OpenedDeviceId);

        h.Speaker.Default = Headphones;
        h.Watcher.RaiseDefault(Headphones);
        h.Drain();

        AssertRoutedTo(h, SpeakerState.On);
        Assert.Equal(Headphones, h.Speaker.OpenedDeviceId);
    }

    [Fact]
    public void A_chosen_device_ignores_default_changes()
    {
        var h = new Harness();
        h.Controller.SelectDevice(Headphones);

        h.Speaker.Present.Add(Hdmi);
        h.Speaker.Default = Hdmi;
        h.Watcher.RaiseDefault(Hdmi);
        h.Drain();

        Assert.Equal(Headphones, h.Speaker.OpenedDeviceId);
        Assert.Equal(1, h.Speaker.Opens);
    }

    [Fact]
    public void Faulted_on_system_default_recovers_on_the_new_default()
    {
        var h = new Harness();
        h.Controller.SelectDevice(null);
        h.Speaker.Lose(); // speakers gone
        h.Drain();
        AssertRoutedTo(h, SpeakerState.Faulted);

        h.Speaker.Default = Headphones;
        h.Watcher.RaiseDefault(Headphones);
        h.Drain();

        AssertRoutedTo(h, SpeakerState.On);
        Assert.Equal(Headphones, h.Speaker.OpenedDeviceId);
    }

    [Fact]
    public void A_fallen_back_speaker_moves_to_the_chosen_device_when_it_returns()
    {
        var h = new Harness();
        h.Speaker.Present.Remove(Headphones);
        h.Controller.SelectDevice(Headphones);
        Assert.Equal(Speakers, h.Speaker.OpenedDeviceId); // fell back to the default

        h.Speaker.Present.Add(Headphones);
        h.Watcher.RaiseAvailability(Headphones, true);
        h.Drain();

        AssertRoutedTo(h, SpeakerState.On);
        Assert.Equal(Headphones, h.Speaker.OpenedDeviceId);
    }

    [Fact]
    public void Dispose_then_enable_is_a_noop()
    {
        var h = new Harness();
        h.Controller.Dispose();

        h.Controller.SetEnabled(true);
        h.Controller.SelectDevice(Headphones);
        h.Controller.SetVolume(0.5);

        Assert.Equal(SpeakerState.Disposed, h.Controller.State);
        Assert.Equal(0, h.Speaker.Opens);
        Assert.True(h.Speaker.Disposed);
        Assert.True(h.Watcher.Disposed);
        Assert.Equal(TeeRoute.PrimaryOnly, h.Tee.Route);
    }

    [Fact]
    public void A_device_event_posted_before_dispose_is_ignored_after_it()
    {
        var h = new Harness();
        h.Controller.SelectDevice(Headphones);
        h.Speaker.Lose();
        h.Controller.Dispose();
        var changes = h.StateChanges;

        h.Drain();

        Assert.Equal(SpeakerState.Disposed, h.Controller.State);
        Assert.Equal(changes, h.StateChanges);
        Assert.Equal(TeeRoute.PrimaryOnly, h.Tee.Route);
    }

    [Fact]
    public void Volume_and_device_are_kept_in_the_settings()
    {
        var h = new Harness();
        h.Controller.SelectDevice(Headphones);
        h.Controller.SetVolume(0.4);

        Assert.True(h.Settings.Get(PreviewSettings.Enabled));
        Assert.Equal(Headphones, h.Settings.Get(PreviewSettings.Device));
        Assert.Equal(0.4, h.Settings.Get(PreviewSettings.Volume));

        // The next start picks up where this one left off.
        var next = new Harness(h.Settings);
        Assert.True(next.Controller.Enabled);
        Assert.Equal(Headphones, next.Controller.DeviceId);
        Assert.Equal(0.4, next.Controller.Volume);
    }
}
