using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

namespace DiffusionNexus.UI.Services;

/// <summary>
/// Wraps a Windows Job Object configured with <c>JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE</c>.
/// Every child process assigned to this job will be terminated by the OS kernel
/// when the parent process exits — even on crash, <c>Environment.FailFast</c>,
/// or Task Manager kill. This prevents orphaned ComfyUI/Forge/etc. processes.
/// </summary>
/// <remarks>
/// TODO: Linux Implementation - use prctl(PR_SET_PDEATHSIG) or cgroups for equivalent behavior.
/// </remarks>
[SupportedOSPlatform("windows")]
internal sealed class ChildProcessJobObject : IDisposable
{
    private readonly SafeFileHandle _jobHandle;
    private bool _disposed;

    public ChildProcessJobObject()
    {
        _jobHandle = CreateJobObject(nint.Zero, "DiffusionNexus_ChildProcessJob");
        if (_jobHandle.IsInvalid)
        {
            var error = Marshal.GetLastWin32Error();
            throw new InvalidOperationException(
                $"Failed to create Job Object (Win32 error {error})");
        }

        // Configure: kill all processes in the job when the handle is closed
        var extendedInfo = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION
        {
            BasicLimitInformation = new JOBOBJECT_BASIC_LIMIT_INFORMATION
            {
                LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE
            }
        };

        var size = Marshal.SizeOf(typeof(JOBOBJECT_EXTENDED_LIMIT_INFORMATION));
        var infoPtr = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(extendedInfo, infoPtr, false);
            if (!SetInformationJobObject(
                    _jobHandle,
                    JobObjectInfoType.ExtendedLimitInformation,
                    infoPtr,
                    (uint)size))
            {
                var error = Marshal.GetLastWin32Error();
                throw new InvalidOperationException(
                    $"Failed to set Job Object information (Win32 error {error})");
            }
        }
        finally
        {
            Marshal.FreeHGlobal(infoPtr);
        }
    }

    /// <summary>
    /// Assigns a process to this job object. Once assigned, the OS will kill the
    /// process when the job handle is closed (i.e. when the parent app exits).
    /// </summary>
    /// <returns>True if the process was successfully assigned.</returns>
    public bool AssignProcess(Process process)
    {
        ArgumentNullException.ThrowIfNull(process);

        if (_disposed || _jobHandle.IsInvalid)
            return false;

        try
        {
            return AssignProcessToJobObject(_jobHandle, process.Handle);
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex,
                "ChildProcessJobObject: Failed to assign process {Pid} to job object",
                process.Id);
            return false;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _jobHandle.Dispose();
    }

    // ── P/Invoke declarations ──

    private const uint JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x2000;

    private enum JobObjectInfoType
    {
        ExtendedLimitInformation = 9
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_BASIC_LIMIT_INFORMATION
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public nuint MinimumWorkingSetSize;
        public nuint MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public nint Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IO_COUNTERS
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
    {
        public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
        public IO_COUNTERS IoInfo;
        public nuint ProcessMemoryLimit;
        public nuint JobMemoryLimit;
        public nuint PeakProcessMemoryUsed;
        public nuint PeakJobMemoryUsed;
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateJobObjectW",
        SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern SafeFileHandle CreateJobObject(nint lpJobAttributes, string lpName);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetInformationJobObject(
        SafeFileHandle hJob,
        JobObjectInfoType infoType,
        nint lpJobObjectInfo,
        uint cbJobObjectInfoLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AssignProcessToJobObject(SafeFileHandle hJob, nint hProcess);
}
