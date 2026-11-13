using System;
using System.Collections.Generic;

namespace HorizonRadio.Core.Audio;

/// <summary>
/// The machine's local speakers and headphones, as the OS lists them: what local monitoring
/// (<see cref="PreviewController"/>) plays through. Core's own (<see cref="NoSpeakers"/>) has none;
/// the platform's (<c>HorizonRadio.Platform</c>, WASAPI on Windows) replaces it.
/// </summary>
public interface ISpeakers
{
    /// <summary>The render devices to pick from, for the output picker: "System default" (a null
    /// id) first, then each active device. Empty when there's nothing to play through.</summary>
    IReadOnlyList<AudioDevice> Devices { get; }

    /// <summary>An output that isn't open yet (<see cref="ISpeakerOutput.Open"/>), playing the
    /// pipeline's format.</summary>
    ISpeakerOutput CreateOutput();

    /// <summary>The OS's notices of devices coming and going, or null when it gives none.</summary>
    IAudioDeviceWatcher? WatchDevices();
}

/// <summary>A selectable audio render endpoint. <see cref="Id"/> is null for
/// the system-default entry.</summary>
public sealed record AudioDevice(string? Id, string Name);

/// <summary>No speakers: a host on an OS with no platform code for them (or a test). Local
/// monitoring can be switched on, but never opens a device.</summary>
public sealed class NoSpeakers : ISpeakers
{
    public IReadOnlyList<AudioDevice> Devices => [];

    public ISpeakerOutput CreateOutput() => new Closed();

    public IAudioDeviceWatcher? WatchDevices() => null;

    private sealed class Closed : ISpeakerOutput
    {
        public bool Open(string? deviceId) => false;

        public void Close() { }

        public bool IsOpen => false;

        public string? OpenedDeviceId => null;

        public float Volume { get; set; } = 1f;

        public event Action? Lost { add { } remove { } }

        public bool Send(ReadOnlySpan<short> samples) => false;

        public void Dispose() { }
    }
}
