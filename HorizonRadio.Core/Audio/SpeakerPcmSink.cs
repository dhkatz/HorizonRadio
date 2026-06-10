using System;
using System.Collections.Generic;
using System.Diagnostics;
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
/// Not thread-safe across <see cref="Start"/>/<see cref="Stop"/>: those are
/// expected to be driven from the UI thread. <see cref="Send"/> may be
/// called concurrently from a source's background loop.
/// </summary>
public sealed class SpeakerPcmSink : IPcmSink, IDisposable
{
    private readonly WaveFormat _format;
    private readonly object _gate = new();

    private WasapiOut? _output;
    private BufferedWaveProvider? _buffer;
    private float _volume = 1.0f;

    private static void Log(string msg) => Debug.WriteLine($"[hzn-speaker] {msg}");

    /// <param name="sampleRate">PCM rate of the samples passed to <see cref="Send"/>.
    /// Defaults to the pipeline format (44.1 kHz); the game-capture path uses 48 kHz.</param>
    public SpeakerPcmSink(int sampleRate = AudioFormat.SampleRate)
    {
        _format = new WaveFormat(sampleRate, 16, AudioFormat.Channels);
    }

    /// <summary>True while a WASAPI device is open and accepting samples.</summary>
    public bool IsPlaying
    {
        get { lock (_gate) return _output != null; }
    }

    /// <summary>Linear gain in [0, 1], applied in software to the PCM in
    /// <see cref="Send"/>. We scale the samples ourselves rather than using
    /// WASAPI's per-session volume, which proved unreliable across devices.
    /// Safe to set at any time.</summary>
    public float Volume
    {
        get { lock (_gate) return _volume; }
        set
        {
            var v = value < 0f ? 0f : (value > 1f ? 1f : value);
            lock (_gate) _volume = v;
        }
    }

    /// <summary>
    /// Open the given render device (null = system default) and begin
    /// playing whatever <see cref="Send"/> pushes. Restarting after a
    /// <see cref="Stop"/> is supported; calling Start while already playing
    /// re-opens on the requested device.
    /// </summary>
    public void Start(string? deviceId = null)
    {
        lock (_gate)
        {
            StopLocked();

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
                catch (Exception ex2) { Log($"no render device available: {ex2.Message}"); return; }
            }

            // ~200 ms of shared-mode latency keeps preview responsive without
            // starving on a busy machine.
            var output = new WasapiOut(device, AudioClientShareMode.Shared, useEventSync: true, latency: 200);
            var buffer = new BufferedWaveProvider(_format)
            {
                BufferDuration = TimeSpan.FromMilliseconds(500),
                DiscardOnBufferOverflow = true,
            };
            output.Init(buffer);
            output.Play();

            _output = output;
            _buffer = buffer;
            device.Dispose();
            Log("playback started");
        }
    }

    /// <summary>Stop playback and release the device. Idempotent.</summary>
    public void Stop()
    {
        lock (_gate) StopLocked();
    }

    private void StopLocked()
    {
        if (_output == null) return;
        try { _output.Stop(); } catch (Exception ex) { Log($"stop: {ex.Message}"); }
        try { _output.Dispose(); } catch { }
        _output = null;
        _buffer = null;
        Log("playback stopped");
    }

    public bool Send(ReadOnlySpan<short> samples)
    {
        BufferedWaveProvider? buffer;
        float vol;
        lock (_gate) { buffer = _buffer; vol = _volume; }
        if (buffer == null) return false;

        // Apply the monitor gain in software. At unity we copy straight through;
        // otherwise scale each s16 sample (with clamp) into a temp buffer.
        byte[] bytes;
        if (vol >= 0.999f)
        {
            bytes = System.Runtime.InteropServices.MemoryMarshal.AsBytes(samples).ToArray();
        }
        else
        {
            var scaled = new short[samples.Length];
            for (var i = 0; i < samples.Length; i++)
            {
                var v = (int)(samples[i] * vol);
                scaled[i] = (short)(v > short.MaxValue ? short.MaxValue
                                  : v < short.MinValue ? short.MinValue : v);
            }
            bytes = new byte[scaled.Length * sizeof(short)];
            Buffer.BlockCopy(scaled, 0, bytes, 0, bytes.Length);
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

    public void Dispose() => Stop();

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
}

/// <summary>A selectable audio render endpoint. <see cref="Id"/> is null for
/// the system-default entry.</summary>
public sealed record AudioDevice(string? Id, string Name);
