using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace PromptedLearning.Core;

/// <summary>Host Job limit installed before sending any request bytes to the blocked worker.</summary>
internal sealed class WindowsProcessBudget : IDisposable
{
    private readonly SafeFileHandle handle;
    private WindowsProcessBudget(SafeFileHandle handle) => this.handle = handle;
    internal static WindowsProcessBudget Create(long memoryBytes)
    {
        if (!OperatingSystem.IsWindows() || memoryBytes <= 0) throw new NotSupportedException("The frozen native host requires Windows Job Object memory limits.");
        var handle = CreateJobObjectW(IntPtr.Zero, null);
        if (handle.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot create native host memory job.");
        var limits = new ExtendedLimits { Basic = new BasicLimits { Flags = 0x100 | 0x2000 }, ProcessMemory = (UIntPtr)(ulong)memoryBytes };
        if (!SetInformationJobObject(handle, 9, ref limits, (uint)Marshal.SizeOf<ExtendedLimits>()))
        { var error = Marshal.GetLastWin32Error(); handle.Dispose(); throw new Win32Exception(error, "Cannot configure native host memory job."); }
        return new(handle);
    }
    internal void Attach(Process process)
    {
        if (!AssignProcessToJobObject(handle, process.Handle)) throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot enforce native host memory job.");
    }
    public void Dispose() => handle.Dispose();
    [StructLayout(LayoutKind.Sequential)]
    private struct BasicLimits
    {
        public long ProcessTime, JobTime; public uint Flags; public UIntPtr MinWorkingSet, MaxWorkingSet;
        public uint ActiveProcesses; public UIntPtr Affinity; public uint Priority, Scheduling;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters { public ulong ReadOperations, WriteOperations, OtherOperations, ReadBytes, WriteBytes, OtherBytes; }
    [StructLayout(LayoutKind.Sequential)]
    private struct ExtendedLimits
    {
        public BasicLimits Basic; public IoCounters Io;
        public UIntPtr ProcessMemory, JobMemory, PeakProcessMemory, PeakJobMemory;
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateJobObjectW(IntPtr attributes, string? name);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetInformationJobObject(SafeFileHandle job, int informationClass, ref ExtendedLimits information, uint length);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AssignProcessToJobObject(SafeFileHandle job, IntPtr process);
}
