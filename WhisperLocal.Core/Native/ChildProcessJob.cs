using System.Diagnostics;
using System.Runtime.InteropServices;
using WhisperLocal.Config;
using static WhisperLocal.Native.JobObjectMethods;

namespace WhisperLocal.Native;

/// <summary>
/// Keeps child processes (whisper-server) from outliving WhisperLocal.
/// Windows: a job object with KILL_ON_JOB_CLOSE, so even a crash takes them down.
/// Elsewhere: each child is remembered in a file with the pid of the WhisperLocal process that started it;
/// children whose owner is gone (a crashed run) are killed on the next start. Children of a live WhisperLocal
/// process (e.g. the menu bar app while --batch runs) are left alone.
/// </summary>
internal static class ChildProcessJob
{
    private static readonly Lazy<IntPtr> Job = new(CreateJob);
    private static readonly object Sync = new();

    private static string PidFile => Path.Combine(AppPaths.DataDir, "child-processes.pid");

    /// <param name="name">Process name of the child (its file name without extension), checked before killing it later.</param>
    public static void Add(Process process, string name)
    {
        if (!OperatingSystem.IsWindows())
        {
            Remember(process.Id, name);
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
            WritePidFile(ReadPidFile().Where(e => e.Pid != processId).ToList());
        }
    }

    /// <summary>Kills child processes left behind by a crashed run (no-op on Windows, where the job object did it).</summary>
    public static void KillOrphans()
    {
        if (OperatingSystem.IsWindows())
            return;
        lock (Sync)
        {
            var keep = new List<(int Pid, int Owner, string Name)>();
            foreach (var (pid, owner, name) in ReadPidFile())
            {
                if (IsAlive(owner))
                {
                    keep.Add((pid, owner, name));
                    continue;
                }
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
            WritePidFile(keep);
        }
    }

    private static void Remember(int pid, string name)
    {
        lock (Sync)
        {
            var entries = ReadPidFile();
            entries.Add((pid, Environment.ProcessId, name));
            WritePidFile(entries);
        }
    }

    private static bool IsAlive(int pid)
    {
        if (pid == Environment.ProcessId)
            return true;
        try
        {
            using var p = Process.GetProcessById(pid);
            return !p.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    /// <summary>Lines "child-pid owner-pid name".</summary>
    private static List<(int Pid, int Owner, string Name)> ReadPidFile()
    {
        try
        {
            if (!File.Exists(PidFile))
                return [];
            return File.ReadAllLines(PidFile)
                .Select(l => l.Split(' ', 3))
                .Where(p => p.Length == 3 && int.TryParse(p[0], out _) && int.TryParse(p[1], out _))
                .Select(p => (int.Parse(p[0]), int.Parse(p[1]), p[2]))
                .ToList();
        }
        catch (IOException)
        {
            return [];
        }
    }

    private static void WritePidFile(List<(int Pid, int Owner, string Name)> entries)
    {
        try
        {
            if (entries.Count == 0)
                File.Delete(PidFile);
            else
                File.WriteAllLines(PidFile, entries.Select(e => $"{e.Pid} {e.Owner} {e.Name}"));
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
