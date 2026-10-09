using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

namespace BackupService.Scheduling.ScheduledTasks
{
    /// <summary>
    /// A Windows Job Object holding everything a scheduled-task step starts — its own process and whatever that
    /// launches (a <c>start /b …</c>, a launcher's helper). It lets the runner (1) treat the step as finished only once
    /// every one of those has exited, and (2) stop them all at once: <see cref="Terminate"/> on Stop, and — because
    /// the job is created with KILL_ON_JOB_CLOSE — when the handle closes, which includes the app exiting or crashing.
    /// A process can't otherwise be reached once the one that started it has exited.
    /// </summary>
    [SupportedOSPlatform("windows")]
    internal sealed class WindowsJobObject : IDisposable
    {
        private const uint JobObjectLimitKillOnJobClose = 0x2000;
        private const int JobObjectBasicAccountingInformation = 1;
        private const int JobObjectExtendedLimitInformation = 9;

        private readonly SafeFileHandle _handle;

        private WindowsJobObject(SafeFileHandle handle) => _handle = handle;

        /// <summary>A new job set to kill its processes when closed, or null if one can't be created.</summary>
        public static WindowsJobObject? TryCreate()
        {
            var handle = CreateJobObject(IntPtr.Zero, null);
            if (handle.IsInvalid)
            {
                return null;
            }

            var limits = new JobObjectExtendedLimitInformationData
            {
                BasicLimitInformation = new JobObjectBasicLimitInformation { LimitFlags = JobObjectLimitKillOnJobClose },
            };
            if (!SetInformationJobObject(handle, JobObjectExtendedLimitInformation, ref limits, (uint)Marshal.SizeOf<JobObjectExtendedLimitInformationData>()))
            {
                handle.Dispose();
                return null;
            }

            return new WindowsJobObject(handle);
        }

        /// <summary>Adds a just-started process (and so everything it goes on to start) to the job.</summary>
        public bool TryAssign(Process process)
        {
            try
            {
                return AssignProcessToJobObject(_handle, process.Handle);
            }
            catch (InvalidOperationException)
            {
                return false; // already exited
            }
        }

        /// <summary>How many of the job's processes are still running.</summary>
        public uint ActiveProcesses =>
            QueryInformationJobObject(_handle, JobObjectBasicAccountingInformation, out JobObjectBasicAccountingInformationData info,
                (uint)Marshal.SizeOf<JobObjectBasicAccountingInformationData>(), IntPtr.Zero)
                ? info.ActiveProcesses
                : 0;

        /// <summary>Waits until every process in the job has exited.</summary>
        public async Task WaitUntilEmptyAsync(CancellationToken cancellationToken)
        {
            while (ActiveProcesses > 0)
            {
                await Task.Delay(250, cancellationToken);
            }
        }

        /// <summary>Kills every process in the job.</summary>
        public void Terminate() => TerminateJobObject(_handle, 1);

        public void Dispose() => _handle.Dispose(); // KILL_ON_JOB_CLOSE: anything still in the job ends with it

        [StructLayout(LayoutKind.Sequential)]
        private struct JobObjectBasicLimitInformation
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
        private struct JobObjectExtendedLimitInformationData
        {
            public JobObjectBasicLimitInformation BasicLimitInformation;
            public IoCounters IoInfo;
            public UIntPtr ProcessMemoryLimit;
            public UIntPtr JobMemoryLimit;
            public UIntPtr PeakProcessMemoryUsed;
            public UIntPtr PeakJobMemoryUsed;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct JobObjectBasicAccountingInformationData
        {
            public long TotalUserTime;
            public long TotalKernelTime;
            public long ThisPeriodTotalUserTime;
            public long ThisPeriodTotalKernelTime;
            public uint TotalPageFaultCount;
            public uint TotalProcesses;
            public uint ActiveProcesses;
            public uint TotalTerminatedProcesses;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern SafeFileHandle CreateJobObject(IntPtr securityAttributes, string? name);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetInformationJobObject(SafeFileHandle job, int infoClass, ref JobObjectExtendedLimitInformationData info, uint length);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool QueryInformationJobObject(SafeFileHandle job, int infoClass, out JobObjectBasicAccountingInformationData info, uint length, IntPtr returnLength);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool AssignProcessToJobObject(SafeFileHandle job, IntPtr process);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool TerminateJobObject(SafeFileHandle job, uint exitCode);
    }
}
