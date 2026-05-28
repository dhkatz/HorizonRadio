using System;
using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using HorizonRadio.Core.Models;
using HorizonRadio.Core.Sources;
using LiveChartsCore;
using LiveChartsCore.Defaults;
using LiveChartsCore.SkiaSharpView;
using LiveChartsCore.SkiaSharpView.Painting;
using SkiaSharp;

namespace HorizonRadio.UI.ViewModels;

/// <summary>
/// FMOD bridge + normalizer telemetry. Updated on each
/// <c>{"event":"stats",...}</c> from the DLL (~2 Hz). The view turns
/// this into a hero status card, a live throughput chart, gain meters
/// and an underrun counter — see StatsView.axaml.
///
/// Series payloads are <see cref="ObservableCollection{Double}"/>
/// fixed-size sliding windows: LiveCharts subscribes to collection
/// changed events, so trimming the head + adding to the tail produces
/// smooth scrolling without rebinding the chart on every tick.
/// </summary>
public sealed partial class StatsViewModel : ViewModelBase
{
    /// <summary>Number of samples retained in the rolling charts.
    /// At the DLL's 2 Hz publish rate this is 30 seconds of history,
    /// which fits the "what's been happening for the last 30s" glance
    /// without needing zoom or pan controls.</summary>
    private const int HistoryLength = 60;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(BridgeStatusLabel))]
    [NotifyPropertyChangedFor(nameof(BridgeStatusBrush))]
    private bool bridgeInstalled;
    public string BridgeStatusLabel => BridgeInstalled ? "Installed" : "Dormant";
    public string BridgeStatusBrush => BridgeInstalled ? "#22c55e" : "#6b7280";

    [ObservableProperty] private ulong  framesIn;
    [ObservableProperty] private ulong  framesOut;
    [ObservableProperty] private ulong  underruns;
    [ObservableProperty] private float  normalizerGain   = 1.0f;
    [ObservableProperty] private float  limiterGain      = 1.0f;
    [ObservableProperty] private double framesInPerSecond;
    [ObservableProperty] private double framesOutPerSecond;
    [ObservableProperty] private bool   isConnected;

    /// <summary>Producer rate as a percentage of the 44.1 kHz target.
    /// 100 means the source is delivering frames at exactly the rate
    /// the FMOD mixer expects post-resample. Under ~95% sustained =
    /// underrun-prone. Tracking the rate (not the lifetime delta)
    /// avoids the resampler-induced drift the old "buffer pressure"
    /// metric was confused by: FramesOut counts 48 kHz consumption,
    /// FramesIn counts 44.1 kHz production, so their lifetime
    /// difference trends down forever even when the bridge is healthy.</summary>
    [ObservableProperty] private double producerRatePercent;

    /// <summary>Time since <see cref="BridgeInstalled"/> first went
    /// true. Reset on disconnect.</summary>
    [ObservableProperty] private string uptimeLabel = "—";

    [ObservableProperty] private string sourceDisplay = "No source";

    /// <summary>Rolling 30-second history feeding the throughput
    /// chart. ObservableCollection so LiveCharts picks up adds/removes
    /// without us rebinding the Series on every tick.</summary>
    public ObservableCollection<double> FramesInHistory  { get; } = new();
    public ObservableCollection<double> FramesOutHistory { get; } = new();
    public ObservableCollection<double> UnderrunSparkline { get; } = new();

    public ISeries[] ThroughputSeries { get; }
    public ISeries[] UnderrunSeries   { get; }
    public Axis[]    XAxes            { get; }
    public Axis[]    YAxes            { get; }
    public Axis[]    UnderrunXAxes    { get; }
    public Axis[]    UnderrunYAxes    { get; }

    private BridgeStats? _previous;
    private DateTime _previousTimestamp = DateTime.MinValue;
    private DateTime? _installedSince;

    public StatsViewModel(SourceRunner? runner = null)
    {
        // AnimationsSpeed = TimeSpan.Zero on every series: in a rolling-
        // window chart LiveCharts tweens each point's vertical position
        // between updates, which looks like the lines are wobbling as
        // they scroll left. We want a hard discrete update — new sample
        // appears at right edge, old sample drops off left, no in-between.
        ThroughputSeries = new ISeries[]
        {
            new LineSeries<double>
            {
                Values            = FramesInHistory,
                Name              = "Frames in",
                Stroke            = new SolidColorPaint(SKColor.Parse("#22c55e"), 2),
                Fill              = new SolidColorPaint(SKColor.Parse("#22c55e").WithAlpha(40)),
                GeometrySize      = 0,
                LineSmoothness    = 0.4,
                AnimationsSpeed   = TimeSpan.Zero,
                EasingFunction    = null,
            },
            new LineSeries<double>
            {
                Values            = FramesOutHistory,
                Name              = "Frames out",
                Stroke            = new SolidColorPaint(SKColor.Parse("#3b82f6"), 2),
                Fill              = new SolidColorPaint(SKColor.Parse("#3b82f6").WithAlpha(40)),
                GeometrySize      = 0,
                LineSmoothness    = 0.4,
                AnimationsSpeed   = TimeSpan.Zero,
                EasingFunction    = null,
            },
        };

        UnderrunSeries = new ISeries[]
        {
            new ColumnSeries<double>
            {
                Values          = UnderrunSparkline,
                Name            = "Underrun delta",
                Fill            = new SolidColorPaint(SKColor.Parse("#ef4444")),
                Stroke          = null,
                Padding         = 1,
                AnimationsSpeed = TimeSpan.Zero,
                EasingFunction  = null,
            },
        };

        // Axes invisible — the chart is decorative; readouts live in
        // the numeric tile next to it. Y axis gets a soft min of zero
        // so flatlines sit at the baseline rather than centring.
        XAxes = new[] { new Axis { IsVisible = false } };
        YAxes = new[] { new Axis
        {
            IsVisible       = false,
            MinLimit        = 0,
            // SeparatorsPaint = null hides the horizontal gridlines.
            SeparatorsPaint = null,
        }};
        UnderrunXAxes = new[] { new Axis { IsVisible = false } };
        UnderrunYAxes = new[] { new Axis
        {
            IsVisible       = false,
            MinLimit        = 0,
            SeparatorsPaint = null,
        }};

        // Seed both histories so the chart has a flat baseline before
        // the first stats event arrives (avoids a "zero-length series"
        // visual blip on first connection).
        for (int i = 0; i < HistoryLength; ++i)
        {
            FramesInHistory.Add(0);
            FramesOutHistory.Add(0);
            UnderrunSparkline.Add(0);
        }

        if (runner != null)
        {
            runner.ActiveSourceChanged += f => Dispatcher.UIThread.Post(() =>
                SourceDisplay = f?.DisplayName ?? "No source");
        }
    }

    public void Apply(BridgeStats stats)
    {
        var now = DateTime.UtcNow;
        double inRate  = 0, outRate = 0;
        ulong  underrunDelta = 0;
        if (_previous != null)
        {
            var dtSec = (now - _previousTimestamp).TotalSeconds;
            if (dtSec > 0.05)
            {
                inRate  = (stats.FramesIn  - _previous.FramesIn ) / dtSec;
                outRate = (stats.FramesOut - _previous.FramesOut) / dtSec;
            }
            underrunDelta = stats.Underruns - _previous.Underruns;
        }
        _previous          = stats;
        _previousTimestamp = now;

        BridgeInstalled  = stats.Installed;
        FramesIn         = stats.FramesIn;
        FramesOut        = stats.FramesOut;
        Underruns        = stats.Underruns;
        NormalizerGain   = stats.NormalizerGain;
        LimiterGain      = stats.LimiterGain;
        FramesInPerSecond  = inRate;
        FramesOutPerSecond = outRate;

        // Producer rate vs the 44.1 kHz target. A healthy C# source
        // should sit at ~100% steady-state. Brief dips on track-change
        // or pause are expected; sustained < ~95% means the source is
        // falling behind and the mixer will need to fill silence.
        const double targetFramesPerSecond = 44100.0;
        ProducerRatePercent = Math.Min(150.0, inRate / targetFramesPerSecond * 100.0);

        // Uptime: clock starts the first time we observe Installed=true
        // and resets on disconnect.
        if (stats.Installed && _installedSince == null) _installedSince = now;
        if (!stats.Installed) _installedSince = null;
        UptimeLabel = _installedSince is { } since
            ? FormatUptime(now - since)
            : "—";

        AppendRolling(FramesInHistory,  inRate);
        AppendRolling(FramesOutHistory, outRate);
        AppendRolling(UnderrunSparkline, underrunDelta);
    }

    public void SetConnectionState(bool connected)
    {
        IsConnected = connected;
        if (!connected)
        {
            BridgeInstalled    = false;
            FramesIn = FramesOut = Underruns = 0;
            FramesInPerSecond  = 0;
            FramesOutPerSecond = 0;
            NormalizerGain     = 1.0f;
            LimiterGain        = 1.0f;
            ProducerRatePercent = 0;
            UptimeLabel        = "—";
            _previous          = null;
            _installedSince    = null;
            for (int i = 0; i < HistoryLength; ++i)
            {
                FramesInHistory[i]  = 0;
                FramesOutHistory[i] = 0;
                UnderrunSparkline[i] = 0;
            }
        }
    }

    private static void AppendRolling(ObservableCollection<double> series, double value)
    {
        // ObservableCollection notifies once per mutation, so for a
        // rolling window we want: remove oldest, append newest. That's
        // two notifications per tick — fine at 2 Hz, and LiveCharts
        // batches them per render frame.
        if (series.Count >= HistoryLength) series.RemoveAt(0);
        series.Add(value);
    }

    private static string FormatUptime(TimeSpan t)
    {
        if (t.TotalHours >= 1) return $"{(int)t.TotalHours}h {t.Minutes}m";
        if (t.TotalMinutes >= 1) return $"{(int)t.TotalMinutes}m {t.Seconds}s";
        return $"{(int)t.TotalSeconds}s";
    }
}
