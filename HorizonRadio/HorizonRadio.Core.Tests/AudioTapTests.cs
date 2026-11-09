using System.Buffers.Binary;
using System.Diagnostics;
using System.Runtime.InteropServices;
using HorizonRadio.Core.Sources;
using HorizonRadio.Core.Sources.Spotify;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using Xunit;

namespace HorizonRadio.Core.Tests;

public sealed class AudioTapTests
{
    private sealed class CollectingSink : IPcmSink
    {
        private readonly object _gate = new();
        public List<short> Samples { get; } = [];

        public bool Send(ReadOnlySpan<short> samples)
        {
            lock (_gate) Samples.AddRange(samples.ToArray());
            return true;
        }

        public short[] Snapshot()
        {
            lock (_gate) return [.. Samples];
        }
    }

    private static byte[] FloatSine(int rate, int channels, double hz, double seconds, float amplitude = 0.5f)
    {
        var frames = (int)(rate * seconds);
        var bytes = new byte[frames * channels * 4];
        for (var i = 0; i < frames; i++)
        {
            var v = (float)(amplitude * Math.Sin(2 * Math.PI * hz * i / rate));
            for (var c = 0; c < channels; c++)
                BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(((i * channels) + c) * 4), v);
        }
        return bytes;
    }

    // Upward zero crossings of the left channel per second.
    private static double Frequency(short[] stereo, int rate)
    {
        var crossings = 0;
        for (var i = 2; i < stereo.Length; i += 2)
            if (stereo[i - 2] < 0 && stereo[i] >= 0) crossings++;
        return crossings / (stereo.Length / 2.0 / rate);
    }

    [Fact]
    public void Converts_48k_float_to_44k_s16_stereo()
    {
        var converter = new AudioTapConverter();
        converter.SetFormat(new AudioTapFormat(48000, 2, 32, IsFloat: true));
        var input = FloatSine(48000, 2, 1000, 1.0);
        var output = new List<short>();
        for (var at = 0; at < input.Length; at += 480 * 8) // 10 ms blocks
            converter.Convert(input.AsSpan(at, Math.Min(480 * 8, input.Length - at)), pcm => output.AddRange(pcm.ToArray()));

        var frames = output.Count / 2;
        Assert.InRange(frames, 44100 - 300, 44100 + 10); // the resampler holds back a few ms
        Assert.InRange(Frequency([.. output], 44100), 990, 1010);
        Assert.InRange(output.Max(s => (int)s), 16000, 16800); // 0.5 of full scale
    }

    [Fact]
    public void Plays_mono_on_both_channels_and_reads_24_bit()
    {
        var converter = new AudioTapConverter();
        converter.SetFormat(new AudioTapFormat(44100, 1, 24, IsFloat: false));
        // One frame: 0x400000 = half scale in 24-bit.
        var output = new List<short>();
        converter.Convert(new byte[] { 0x00, 0x00, 0x40 }, pcm => output.AddRange(pcm.ToArray()));
        Assert.Equal([16384, 16384], output);
    }

    [Fact]
    public async Task Reads_the_tap_messages_in_order()
    {
        var stream = new MemoryStream();
        void Write(ushort kind, byte[] payload)
        {
            var header = new byte[12];
            BinaryPrimitives.WriteUInt32LittleEndian(header, 0x50545A48);
            BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(4), 1);
            BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(6), kind);
            BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(8), (uint)payload.Length);
            stream.Write(header);
            stream.Write(payload);
        }
        Write(3, "hooks installed"u8.ToArray());
        Write(2, new byte[16]);
        stream.Position = 0;

        var got = new List<(ushort Kind, int Size)>();
        await AudioTap.ReadMessagesAsync(stream, (payload, kind) => got.Add((kind, payload.Length)), CancellationToken.None);

        Assert.Equal([(3, 15), (2, 16)], got);
    }

    [Fact]
    public async Task Loads_into_another_process_and_reports_on_its_pipe()
    {
        if (!OperatingSystem.IsWindows() || AudioTap.FindDll() is not { } dll) return; // native side not built

        using var child = Process.Start(new ProcessStartInfo("cmd.exe", "/c ping -n 30 127.0.0.1 >nul")
        {
            CreateNoWindow = true,
            UseShellExecute = false,
        })!;
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            await using var tap = await AudioTap.AttachAsync(child, dll, cts.Token);
            var reading = tap.RunAsync(new CollectingSink(), cts.Token);

            // Installed with an audio device; without one (CI) the reason it couldn't open the device
            // comes first, then "hooks NOT installed…". Either way the tap loaded, set up and talks
            // over its pipe.
            await tap.Hooked.WaitAsync(cts.Token);
            Assert.StartsWith("hooks", tap.LastLog, StringComparison.Ordinal);
            await cts.CancelAsync();
            try { await reading; } catch (OperationCanceledException) { }
        }
        finally
        {
            child.Kill(entireProcessTree: true);
        }
    }

    [Fact]
    public async Task Takes_what_a_process_plays_and_leaves_it_silent()
    {
        // Needs the native build and a real output device (CI has neither device nor speakers).
        if (!OperatingSystem.IsWindows() || AudioTap.FindDll() is not { } dll) return;
        using (var devices = new MMDeviceEnumerator())
            if (!devices.HasDefaultAudioEndpoint(DataFlow.Render, Role.Console)) return;

        // Into this test process: its own WASAPI output is what gets tapped.
        NativeLibrary.Load(dll);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await using var tap = await AudioTap.ConnectAsync(Environment.ProcessId, cts.Token);
        var sink = new CollectingSink();
        var reading = tap.RunAsync(sink, cts.Token);
        Assert.True(await tap.Hooked.WaitAsync(cts.Token), tap.LastLog);

        var tone = new SignalGenerator(48000, 2) { Type = SignalGeneratorType.Sin, Frequency = 1000, Gain = 0.25 }
            .Take(TimeSpan.FromSeconds(1.5));
        using (var output = new WasapiOut(AudioClientShareMode.Shared, 50))
        {
            output.Init(tone);
            output.Play();
            while (output.PlaybackState == PlaybackState.Playing && !cts.IsCancellationRequested)
                await Task.Delay(50, cts.Token);
        }
        await Task.Delay(300, cts.Token);
        await cts.CancelAsync();
        try { await reading; } catch (OperationCanceledException) { }

        var heard = sink.Snapshot();
        Assert.True(heard.Length / 2 > 44100, $"only {heard.Length / 2} frames tapped");
        // The tone, without its leading and trailing silence.
        var loud = heard.Where((_, i) => i % 2 == 0).Select(Math.Abs).Max();
        Assert.InRange(loud, 7000, 9000); // 0.25 of full scale
        var start = Array.FindIndex(heard, s => Math.Abs((int)s) > 1000) & ~1;
        Assert.InRange(Frequency(heard[start..(start + 44100)], 44100), 980, 1020);
    }
}
