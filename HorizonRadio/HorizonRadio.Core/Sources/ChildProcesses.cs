using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace HorizonRadio.Core.Sources;

/// <summary>
/// Ties the tools Horizon Radio starts (librespot, ffmpeg, yt-dlp) to the app's own lifetime: each
/// is put in one Windows job object that kills everything in it when the app's handle to it closes,
/// which Windows does however the app ends (closed, crashed, stopped from the debugger). Without
/// it a librespot outlives the app and stays logged in as the same Spotify Connect device, so the
/// next session's play commands go to it instead and nothing is heard.
/// </summary>
public static class ChildProcesses
{
    private const int JobObjectExtendedLimitInformation = 9;
    private const uint JobObjectLimitKillOnJobClose = 0x2000;

    private static readonly object Gate = new();
    private static IntPtr _job;
    private static bool _unavailable;

    /// <summary>Put <paramref name="process"/> in the app's job, so it ends when the app does
    /// (and so do the processes it starts later). Best effort: false when it couldn't be tied
    /// (not Windows, or the process already exited).</summary>
    public static bool TieToApp(Process process)
    {
        ArgumentNullException.ThrowIfNull(process);
        if (!OperatingSystem.IsWindows()) return false;
        var job = Job();
        if (job == IntPtr.Zero) return false;
        try
        {
            if (AssignProcessToJobObject(job, process.Handle)) return true;
            Trace.WriteLine($"[hzn-child] could not tie pid={process.Id} to the app: error {Marshal.GetLastPInvokeError()}");
        }
        catch (InvalidOperationException) { } // already exited
        return false;
    }

    // The job lives as long as the app: its handle is never closed, so Windows closes it (and
    // kills what's in it) when the process ends.
    private static IntPtr Job()
    {
        lock (Gate)
        {
            if (_job != IntPtr.Zero || _unavailable) return _job;
            var job = CreateJobObject(IntPtr.Zero, null);
            if (job == IntPtr.Zero) { _unavailable = true; return IntPtr.Zero; }

            var info = new ExtendedLimitInformation
            {
                BasicLimitInformation = new BasicLimitInformation { LimitFlags = JobObjectLimitKillOnJobClose },
            };
            var size = Marshal.SizeOf<ExtendedLimitInformation>();
            var ptr = Marshal.AllocHGlobal(size);
            try
            {
                Marshal.StructureToPtr(info, ptr, false);
                if (!SetInformationJobObject(job, JobObjectExtendedLimitInformation, ptr, (uint)size))
                {
                    Trace.WriteLine($"[hzn-child] job setup failed: error {Marshal.GetLastPInvokeError()}");
                    CloseHandle(job);
                    _unavailable = true;
                    return IntPtr.Zero;
                }
            }
            finally { Marshal.FreeHGlobal(ptr); }
            return _job = job;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BasicLimitInformation
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize;
        public UIntPtr MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ExtendedLimitInformation
    {
        public BasicLimitInformation BasicLimitInformation;
        public IoCounters IoInfo;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateJobObject(IntPtr attributes, string? name);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetInformationJobObject(IntPtr job, int infoClass, IntPtr info, uint length);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);
}
