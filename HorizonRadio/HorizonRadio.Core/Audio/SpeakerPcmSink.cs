using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using HorizonRadio.Core.Sources;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace HorizonRadio.Core.Audio;

/// <summary>
/// An <see cref="IPcmSink"/> that plays the pipeline's PCM out of a local
/// speaker/headphone device via WASAPI. This is the "test playback" path:
/// it lets the app validate a source or profile without launching the game
/// (and later, in <see cref="Ipc"/>, doubles as the playback target for the
/// post-processed audio captured back from the game).
///
/// Sources push at wall-clock pace while WASAPI drains on its own render
/// clock; the two aren't synchronized, so we feed a bounded
/// <see cref="BufferedWaveProvider"/> with <c>DiscardOnBufferOverflow</c>.
/// Drift then surfaces as a capped latency / occasional dropped chunk
/// rather than unbounded buffer growth — fine for a preview/monitor path.
///
/// Not thread-safe across <see cref="Open"/>/<see cref="Close"/>: those are
/// expected to be driven from the UI thread. <see cref="Send"/> may be
/// called concurrently from a source's background loop.
///
/// Device loss (unplugged, disabled, driver reset) surfaces as WASAPI's render
/// thread dying with an error: <see cref="WasapiOut.PlaybackStopped"/>. We drop
/// the session there and raise <see cref="Lost"/>, but don't tear the device
/// down from that callback (it can run on the render thread itself, and
/// stopping a <see cref="WasapiOut"/> joins that thread); the next
/// <see cref="Open"/>/<see cref="Close"/> releases it.
/// </summary>
public sealed class SpeakerPcmSink : ISpeakerOutput
{
    private readonly WaveFormat _format;
    private readonly object _gate = new();

    // The open device. Swapped whole so Send and the stopped callback each see
    // one consistent output+buffer pair; the callback clears it with a
    // compare-exchange so a stale callback can't clear a newer session.
    private Session? _session;
    // A session that died on its own, held until the UI thread releases it.
    private Session? _lost;
    private volatile float _volume = 1.0f;

    private static void Log(string msg) => Debug.WriteLine($"[hzn-speaker] {msg}");

    /// <param name="sampleRate">PCM rate of the samples passed to <see cref="Send"/>.
    /// Defaults to the pipeline format (44.1 kHz); the game-capture path uses 48 kHz.</param>
    public SpeakerPcmSink(int sampleRate = AudioFormat.SampleRate)
    {
        _format = new WaveFormat(sampleRate, 16, AudioFormat.Channels);
    }

    public event Action? Lost;

    /// <summary>True while a WASAPI device is open and accepting samples.</summary>
    public bool IsOpen => Volatile.Read(ref _session) != null;

    public string? OpenedDeviceId => Volatile.Read(ref _session)?.DeviceId;

    /// <summary>Linear gain in [0, 1], applied in software to the PCM in
    /// <see cref="Send"/>. We scale the samples ourselves rather than using
    /// WASAPI's per-session volume, which proved unreliable across devices.
    /// Safe to set at any time.</summary>
    public float Volume
    {
        get => _volume;
        set => _volume = value < 0f ? 0f : (value > 1f ? 1f : value);
    }

    /// <summary>
    /// Open the given render device (null = system default) and begin
    /// playing whatever <see cref="Send"/> pushes. Calling Open while already
    /// playing re-opens on the requested device. Returns false (never throws) if
    /// no device could be opened.
    /// </summary>
    public bool Open(string? deviceId = null)
    {
        lock (_gate)
        {
            CloseLocked();

            MMDevice? device = null;
            using var enumerator = new MMDeviceEnumerator();
            try
            {
                device = deviceId != null
                    ? enumerator.GetDevice(deviceId)
                    : enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            }
            catch (Exception ex)
            {
                // Saved device may have been unplugged; fall back to default.
                Log($"device '{deviceId}' unavailable ({ex.Message}); using default");
                try { device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia); }
                catch (Exception ex2) { Log($"no render device available: {ex2.Message}"); return false; }
            }

            WasapiOut? output = null;
            Session? session = null;
            try
            {
                // ~200 ms of shared-mode latency keeps preview responsive
                // without starving on a busy machine.
                output = new WasapiOut(device, AudioClientShareMode.Shared, useEventSync: true, latency: 200);
                var buffer = new BufferedWaveProvider(_format)
                {
                    BufferDuration = TimeSpan.FromMilliseconds(500),
                    DiscardOnBufferOverflow = true,
                };
                output.Init(buffer);
                var opened = session = new Session(output, buffer, device.ID);
                opened.Stopped = (_, e) => OnPlaybackStopped(opened, e);
                output.PlaybackStopped += opened.Stopped;
                // Publish before Play so a device that dies at once is still caught.
                Volatile.Write(ref _session, opened);
                output.Play();
                Log("playback started");
                return true;
            }
            catch (Exception ex)
            {
                // Device found but couldn't be opened (held exclusively, format
                // unsupported, yanked between enumerate and Init). Leave no
                // session so IsOpen reports false and callers treat the output
                // as unreachable — never throw out of Open (it runs from a
                // binding setter on the UI thread).
                Log($"failed to open output: {ex.Message}");
                if (session != null)
                {
                    Interlocked.CompareExchange(ref _session, null, session);
                    session.Dispose();
                }
                else
                {
                    try { output?.Dispose(); } catch { }
                }
                return false;
            }
            finally
            {
                device.Dispose();
            }
        }
    }

    /// <summary>Stop playback and release the device. Idempotent.</summary>
    public void Close()
    {
        lock (_gate) CloseLocked();
    }

    private void CloseLocked()
    {
        var session = Interlocked.Exchange(ref _session, null);
        session?.Dispose();
        Interlocked.Exchange(ref _lost, null)?.Dispose();
        if (session != null) Log("playback stopped");
    }

    // Raised by NAudio when the render thread exits: on that thread, or posted to
    // the sync context the WasapiOut was created on. Close unsubscribes before
    // stopping, so a current session stopping here stopped on its own. Never take
    // _gate here: Open/Close hold it while joining the render thread.
    private void OnPlaybackStopped(Session session, StoppedEventArgs e)
    {
        if (Interlocked.CompareExchange(ref _session, null, session) != session) return; // already replaced
        Interlocked.Exchange(ref _lost, session);
        Log($"device lost: {e.Exception?.Message ?? "playback stopped"}");
        Lost?.Invoke();
    }

    public bool Send(ReadOnlySpan<short> samples)
    {
        var buffer = Volatile.Read(ref _session)?.Buffer;
        var vol = _volume;
        if (buffer == null) return false;

        // Apply the monitor gain in software, writing s16 little-endian into a
        // single buffer. At unity we copy straight through; otherwise scale each
        // sample (with clamp). One allocation per chunk on either path.
        var bytes = new byte[samples.Length * sizeof(short)];
        if (vol >= 0.999f)
        {
            System.Runtime.InteropServices.MemoryMarshal.AsBytes(samples).CopyTo(bytes);
        }
        else
        {
            for (var i = 0; i < samples.Length; i++)
            {
                var v = (int)(samples[i] * vol);
                v = v > short.MaxValue ? short.MaxValue : v < short.MinValue ? short.MinValue : v;
                bytes[2 * i] = (byte)v;
                bytes[2 * i + 1] = (byte)(v >> 8);
            }
        }

        try
        {
            // AddSamples copies into the provider's internal ring synchronously.
            buffer.AddSamples(bytes, 0, bytes.Length);
            return true;
        }
        catch (Exception ex)
        {
            Log($"send failed: {ex.Message}");
            return false;
        }
    }

    public void Dispose() => Close();

    /// <summary>Stable id + friendly name for each active render endpoint, for
    /// the UI's device picker. The empty-id entry represents "system default".</summary>
    public static IReadOnlyList<AudioDevice> EnumerateRenderDevices()
    {
        var list = new List<AudioDevice> { new(null, "System default") };
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            foreach (var d in enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
            {
                list.Add(new AudioDevice(d.ID, d.FriendlyName));
                d.Dispose();
            }
        }
        catch (Exception ex)
        {
            Log($"enumerate failed: {ex.Message}");
        }
        return list;
    }

    /// <summary>An open device: the output and the buffer it drains, swapped as one.</summary>
    private sealed class Session(WasapiOut output, BufferedWaveProvider buffer, string deviceId) : IDisposable
    {
        public WasapiOut Output { get; } = output;
        public BufferedWaveProvider Buffer { get; } = buffer;
        public string DeviceId { get; } = deviceId;
        public EventHandler<StoppedEventArgs>? Stopped { get; set; }

        public void Dispose()
        {
            // Unsubscribe first: our own stop raises PlaybackStopped, which isn't a loss.
            if (Stopped != null) Output.PlaybackStopped -= Stopped;
            try { Output.Stop(); } catch (Exception ex) { Log($"stop: {ex.Message}"); }
            try { Output.Dispose(); } catch { }
        }
    }
}

/// <summary>A selectable audio render endpoint. <see cref="Id"/> is null for
/// the system-default entry.</summary>
public sealed record AudioDevice(string? Id, string Name);
