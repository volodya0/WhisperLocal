using System.Diagnostics;
using System.Runtime.InteropServices;
using WhisperLocal.Config;
using static WhisperLocal.Native.JobObjectMethods;

namespace WhisperLocal.Native;

/// <summary>
/// Keeps child processes (whisper-server) from outliving WhisperLocal.
/// Windows: a job object with KILL_ON_JOB_CLOSE, so even a crash takes them down.
/// Elsewhere: the pid is remembered in a file and a leftover from a crashed run is killed on the next start.
/// </summary>
internal static class ChildProcessJob
{
    private static readonly Lazy<IntPtr> Job = new(CreateJob);
    private static readonly object Sync = new();

    private static string PidFile => Path.Combine(AppPaths.DataDir, "child-processes.pid");

    public static void Add(Process process)
    {
        if (!OperatingSystem.IsWindows())
        {
            Remember(process);
            return;
        }
        var job = Job.Value;
        if (job == IntPtr.Zero || !AssignProcessToJobObject(job, process.Handle))
            Log.Warn($"Could not assign process {process.Id} to the job object (error {Marshal.GetLastWin32Error()}); it may survive a crash");
    }

    /// <summary>Call after a child process was stopped normally.</summary>
    public static void Remove(int processId)
    {
        if (OperatingSystem.IsWindows())
            return;
        lock (Sync)
        {
            var lines = ReadPidFile().Where(e => e.Pid != processId).Select(e => $"{e.Pid} {e.Name}").ToList();
            WritePidFile(lines);
        }
    }

    /// <summary>Kills child processes left behind by a crashed run (no-op on Windows, where the job object did it).</summary>
    public static void KillOrphans()
    {
        if (OperatingSystem.IsWindows())
            return;
        lock (Sync)
        {
            foreach (var (pid, name) in ReadPidFile())
            {
                try
                {
                    using var p = Process.GetProcessById(pid);
                    // The pid may have been reused by an unrelated process
                    if (p.ProcessName != name)
                        continue;
                    Log.Warn($"Killing {name} (pid {pid}) left over from a previous run");
                    p.Kill(true);
                    p.WaitForExit(3000);
                }
                catch (ArgumentException)
                {
                    // Already gone
                }
                catch (Exception ex)
                {
                    Log.Warn($"Failed to kill leftover process {pid}: {ex.Message}");
                }
            }
            WritePidFile([]);
        }
    }

    private static void Remember(Process process)
    {
        lock (Sync)
        {
            var lines = ReadPidFile().Select(e => $"{e.Pid} {e.Name}").ToList();
            lines.Add($"{process.Id} {process.ProcessName}");
            WritePidFile(lines);
        }
    }

    private static List<(int Pid, string Name)> ReadPidFile()
    {
        try
        {
            if (!File.Exists(PidFile))
                return [];
            return File.ReadAllLines(PidFile)
                .Select(l => l.Split(' ', 2))
                .Where(p => p.Length == 2 && int.TryParse(p[0], out _))
                .Select(p => (int.Parse(p[0]), p[1]))
                .ToList();
        }
        catch (IOException)
        {
            return [];
        }
    }

    private static void WritePidFile(List<string> lines)
    {
        try
        {
            if (lines.Count == 0)
                File.Delete(PidFile);
            else
                File.WriteAllLines(PidFile, lines);
        }
        catch (IOException ex)
        {
            Log.Warn($"Cannot update {PidFile}: {ex.Message}");
        }
    }

    private static IntPtr CreateJob()
    {
        var job = CreateJobObject(IntPtr.Zero, null);
        if (job == IntPtr.Zero)
            return IntPtr.Zero;

        var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION();
        info.BasicLimitInformation.LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE;
        SetInformationJobObject(job, JobObjectExtendedLimitInformation, ref info,
            (uint)Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>());
        return job;
    }
}
