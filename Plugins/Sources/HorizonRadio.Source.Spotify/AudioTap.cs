using System.Buffers;
using System.Buffers.Binary;
using System.ComponentModel;
using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using HorizonRadio.Sdk.Audio;
using HorizonRadio.Sdk.Sources;

namespace HorizonRadio.Source.Spotify;

/// <summary>
/// The app's end of horizon_audio_tap.dll (Native/): loads the DLL into a process the app started
/// (Spotify Soloist), then reads what that process plays from <c>\\.\pipe\HorizonRadio.tap.&lt;pid&gt;</c>
/// and hands it to a sink as the pipeline's PCM. The process itself plays silence.
/// </summary>
public sealed class AudioTap : IAsyncDisposable
{
    public const string DllName = "horizon_audio_tap.dll";

    private const uint Magic = 0x50545A48; // "HZTP"
    private const ushort Version = 1;
    private const int HeaderSize = 12;
    private const ushort KindFormat = 1, KindAudio = 2, KindLog = 3;

    private readonly NamedPipeClientStream _pipe;
    private readonly AudioTapConverter _converter = new();
    private long _framesOut;

    private AudioTap(NamedPipeClientStream pipe) => _pipe = pipe;

    /// <summary>The DLL bundled with the app, or null when this build doesn't have it.</summary>
    public static string? FindDll()
    {
        var path = Path.Combine(AppContext.BaseDirectory, DllName);
        return File.Exists(path) ? path : null;
    }

    /// <summary>Lines the tap logs (its setup steps, then hooks installed or not); raised on the
    /// reading thread.</summary>
    public event Action<string>? Log;

    /// <summary>Completes once the tap has set up while <see cref="RunAsync"/> reads: true when it
    /// takes the process's audio, false when it couldn't (the process plays out loud).</summary>
    public Task<bool> Hooked => _hooked.Task;

    /// <summary>The last line the tap logged: the step it's on while <see cref="Hooked"/> waits.</summary>
    public string? LastLog => _lastLog;

    private readonly TaskCompletionSource<bool> _hooked = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private volatile string? _lastLog;

    /// <summary>Frames delivered to the sink so far, at <see cref="AudioFormat.SampleRate"/>.</summary>
    public long FramesDelivered => Interlocked.Read(ref _framesOut);

    /// <summary>Load the tap into <paramref name="process"/> and connect to it.</summary>
    public static async Task<AudioTap> AttachAsync(Process process, string dllPath, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(process);
        LoadInto(process, dllPath);
        return await ConnectAsync(process.Id, ct).ConfigureAwait(false);
    }

    /// <summary>Connect to the tap already loaded into process <paramref name="processId"/>.</summary>
    internal static async Task<AudioTap> ConnectAsync(int processId, CancellationToken ct)
    {
        var pipe = new NamedPipeClientStream(".", PipeName(processId), PipeDirection.In, PipeOptions.Asynchronous);
        try
        {
            await pipe.ConnectAsync(TimeSpan.FromSeconds(10), ct).ConfigureAwait(false);
        }
        catch
        {
            await pipe.DisposeAsync().ConfigureAwait(false);
            throw;
        }
        return new AudioTap(pipe);
    }

    internal static string PipeName(int processId) => $"HorizonRadio.tap.{processId}";

    /// <summary>Read until the process goes away or <paramref name="ct"/> fires, sending its audio
    /// to <paramref name="sink"/>.</summary>
    public async Task RunAsync(IPcmSink sink, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(sink);
        try
        {
            await ReadMessagesAsync(_pipe, (payload, kind) => Handle(kind, payload, sink), ct).ConfigureAwait(false);
        }
        finally
        {
            _hooked.TrySetResult(false); // reading ended before the tap said it was set up
        }
    }

    private void Handle(ushort kind, ReadOnlySpan<byte> payload, IPcmSink sink)
    {
        switch (kind)
        {
            case KindFormat:
                _converter.SetFormat(AudioTapConverter.ParseFormat(payload));
                break;
            case KindAudio:
                if (_converter.Format == null) break; // can't happen: the tap announces first
                _converter.Convert(payload, pcm =>
                {
                    sink.Send(pcm);
                    Interlocked.Add(ref _framesOut, pcm.Length / 2);
                });
                break;
            case KindLog:
                var line = Encoding.UTF8.GetString(payload);
                _lastLog = line;
                if (line == "hooks installed") _hooked.TrySetResult(true);
                else if (line.StartsWith("hooks NOT installed", StringComparison.Ordinal)) _hooked.TrySetResult(false);
                Log?.Invoke(line);
                break;
        }
    }

    // The wire format: 12-byte header (magic, version, kind, size), then the payload.
    internal static async Task ReadMessagesAsync(Stream stream, ReadOnlySpanAction<byte, ushort> onMessage,
                                                 CancellationToken ct)
    {
        var header = new byte[HeaderSize];
        var payload = new byte[64 * 1024];
        while (!ct.IsCancellationRequested)
        {
            if (!await ReadExactlyOrEndAsync(stream, header, ct).ConfigureAwait(false)) return;
            if (BinaryPrimitives.ReadUInt32LittleEndian(header) != Magic)
                throw new InvalidDataException("The audio tap's stream is out of step.");
            if (BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(4)) != Version)
                throw new InvalidDataException("The audio tap is a different version than the app.");
            var kind = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(6));
            var size = (int)BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(8));
            if (size > payload.Length) payload = new byte[size];
            if (!await ReadExactlyOrEndAsync(stream, payload.AsMemory(0, size), ct).ConfigureAwait(false)) return;
            onMessage(payload.AsSpan(0, size), kind);
        }
    }

    private static async Task<bool> ReadExactlyOrEndAsync(Stream stream, Memory<byte> buffer, CancellationToken ct)
    {
        try
        {
            await stream.ReadExactlyAsync(buffer, ct).ConfigureAwait(false);
            return true;
        }
        catch (EndOfStreamException)
        {
            return false;
        }
    }

    public ValueTask DisposeAsync() => _pipe.DisposeAsync();

    // LoadLibraryW run on a thread in the target: it loads the DLL from disk like any other.
    internal static void LoadInto(Process process, string dllPath)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("The audio tap is Windows-only.");
        var handle = process.Handle;
        var bytes = Encoding.Unicode.GetBytes(dllPath + "\0");
        var remote = Native.VirtualAllocEx(handle, IntPtr.Zero, (nuint)bytes.Length, Native.MemCommitReserve, Native.PageReadWrite);
        if (remote == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastPInvokeError(), "Couldn't reserve memory in the process.");
        try
        {
            if (!Native.WriteProcessMemory(handle, remote, bytes, (nuint)bytes.Length, out _))
                throw new Win32Exception(Marshal.GetLastPInvokeError(), "Couldn't write the tap's path into the process.");
            var loadLibrary = Native.GetProcAddress(Native.GetModuleHandleW("kernel32.dll"), "LoadLibraryW");
            var thread = Native.CreateRemoteThread(handle, IntPtr.Zero, 0, loadLibrary, remote, 0, out _);
            if (thread == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastPInvokeError(), "Couldn't start the tap in the process.");
            try
            {
                if (Native.WaitForSingleObject(thread, 10_000) != 0)
                    throw new TimeoutException("The audio tap didn't load in time.");
                Native.GetExitCodeThread(thread, out var module);
                if (module == 0) throw new InvalidOperationException($"The process couldn't load {dllPath}.");
            }
            finally { Native.CloseHandle(thread); }
        }
        finally { Native.VirtualFreeEx(handle, remote, 0, Native.MemRelease); }
    }

    private static class Native
    {
        public const uint MemCommitReserve = 0x3000, MemRelease = 0x8000, PageReadWrite = 0x04;

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern IntPtr VirtualAllocEx(IntPtr process, IntPtr address, nuint size, uint type, uint protect);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool VirtualFreeEx(IntPtr process, IntPtr address, nuint size, uint type);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool WriteProcessMemory(IntPtr process, IntPtr address, byte[] buffer, nuint size, out nuint written);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        public static extern IntPtr GetModuleHandleW(string name);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Ansi, BestFitMapping = false)]
        public static extern IntPtr GetProcAddress(IntPtr module, string name);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern IntPtr CreateRemoteThread(IntPtr process, IntPtr attributes, nuint stackSize, IntPtr start,
                                                       IntPtr parameter, uint flags, out uint threadId);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GetExitCodeThread(IntPtr thread, out uint exitCode);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool CloseHandle(IntPtr handle);
    }
}
