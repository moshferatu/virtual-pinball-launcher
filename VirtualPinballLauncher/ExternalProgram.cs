using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace VirtualPinballLauncher;

public sealed class ExternalProgramSpec
{
    public required string Command { get; init; }
    public string? Arguments { get; init; }
    public string? WorkingDirectory { get; init; }
    public string? WaitForProcess { get; init; }
    public bool SingleInstance { get; init; }
    /// <summary>
    /// Starts console programs without a console window and hides any console window the
    /// program or its batch file opens later. Cabinet ports are often console-subsystem
    /// builds, so this is on by default for key bindings.
    /// </summary>
    public bool HideConsole { get; init; }
    public TimeSpan ActivationTimeout { get; init; } = TimeSpan.FromSeconds(10);

    public static ExternalProgramSpec From(KeyBinding binding) => new()
    {
        Command = ResolvePath(binding.Command ?? ""),
        Arguments = binding.Arguments,
        WorkingDirectory = binding.WorkingDirectory,
        WaitForProcess = binding.WaitForProcess,
        SingleInstance = binding.SingleInstance ?? false,
        HideConsole = !(binding.ShowConsole ?? false)
    };

    /// <summary>Relative commands are looked up next to the launcher executable.</summary>
    public static string ResolvePath(string command) =>
        string.IsNullOrWhiteSpace(command) || Path.IsPathRooted(command)
            ? command
            : Path.Combine(AppContext.BaseDirectory, command);
}

/// <summary>
/// Starts a program the way a table is started: bring its window to the front once it
/// appears, then wait until it is gone. The launched process is placed in a job object
/// so a batch file or wrapper that exits immediately still counts as running until
/// everything it started has finished.
/// </summary>
public static class ExternalProgram
{
    private const uint CREATE_SUSPENDED = 0x00000004;
    private const uint CREATE_UNICODE_ENVIRONMENT = 0x00000400;
    private const uint CREATE_NO_WINDOW = 0x08000000;
    private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
    private const int JobObjectBasicAccountingInformation = 1;

    /// <summary>How long a window must exist before it is activated, so it has finished setting itself up.</summary>
    private static readonly TimeSpan SettleDelay = TimeSpan.FromSeconds(1);

    /// <summary>How long the program must hold the foreground before activation counts as done.</summary>
    private static readonly TimeSpan StableForeground = TimeSpan.FromSeconds(2);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct STARTUPINFO
    {
        public int cb;
        public string? lpReserved;
        public string? lpDesktop;
        public string? lpTitle;
        public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
        public short wShowWindow, cbReserved2;
        public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_INFORMATION
    {
        public IntPtr hProcess, hThread;
        public int dwProcessId, dwThreadId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_BASIC_ACCOUNTING_INFORMATION
    {
        public long TotalUserTime, TotalKernelTime, ThisPeriodTotalUserTime, ThisPeriodTotalKernelTime;
        public uint TotalPageFaultCount, TotalProcesses, ActiveProcesses, TotalTerminatedProcesses;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateProcess(string? lpApplicationName, string lpCommandLine, IntPtr lpProcessAttributes,
        IntPtr lpThreadAttributes, bool bInheritHandles, uint dwCreationFlags, IntPtr lpEnvironment,
        string? lpCurrentDirectory, ref STARTUPINFO lpStartupInfo, out PROCESS_INFORMATION lpProcessInformation);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint ResumeThread(IntPtr hThread);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateJobObject(IntPtr lpJobAttributes, string? lpName);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AssignProcessToJobObject(SafeFileHandle hJob, IntPtr hProcess);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool QueryInformationJobObject(SafeFileHandle hJob, int jobObjectInformationClass,
        ref JOBOBJECT_BASIC_ACCOUNTING_INFORMATION lpJobObjectInformation, int cbJobObjectInformationLength, IntPtr lpReturnLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool IsProcessInJob(IntPtr processHandle, SafeFileHandle jobHandle, out bool result);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint dwDesiredAccess, bool bInheritHandle, int dwProcessId);

    /// <param name="onActivated">Called once the program's window has been brought to the front.</param>
    public static async Task RunAsync(ExternalProgramSpec spec, Action? onActivated = null, CancellationToken token = default)
    {
        if (!File.Exists(spec.Command))
            throw new FileNotFoundException($"Program not found:\n{spec.Command}", spec.Command);

        if (spec.SingleInstance)
        {
            var running = FindProcesses(spec.WaitForProcess ?? Path.GetFileName(spec.Command)).FirstOrDefault();
            if (running != null)
            {
                using (running)
                {
                    await ActivateAsync(pid => pid == running.Id, () => !running.HasExited, spec, onActivated, token);
                    await running.WaitForExitAsync(token);
                }
                return;
            }
        }

        if (!string.IsNullOrWhiteSpace(spec.WaitForProcess))
        {
            using var started = Process.Start(new ProcessStartInfo
            {
                FileName = spec.Command,
                Arguments = spec.Arguments ?? "",
                WorkingDirectory = WorkingDirectoryFor(spec),
                UseShellExecute = true,
                WindowStyle = spec.HideConsole ? ProcessWindowStyle.Hidden : ProcessWindowStyle.Normal
            }) ?? throw new InvalidOperationException($"Failed to start {spec.Command}.");

            var target = await WaitForProcessAsync(spec.WaitForProcess, spec.ActivationTimeout, token)
                ?? throw new InvalidOperationException($"{spec.WaitForProcess} did not start within {spec.ActivationTimeout.TotalSeconds:0} seconds.");
            using (target)
            {
                await ActivateAsync(pid => pid == target.Id, () => !target.HasExited, spec, onActivated, token);
                await target.WaitForExitAsync(token);
            }
            return;
        }

        var extension = Path.GetExtension(spec.Command).ToLowerInvariant();
        if (extension is not (".exe" or ".cmd" or ".bat" or ".com"))
        {
            // Documents, shortcuts, and other associations go through the shell; wait on what it hands back.
            using var opened = Process.Start(new ProcessStartInfo
            {
                FileName = spec.Command,
                Arguments = spec.Arguments ?? "",
                WorkingDirectory = WorkingDirectoryFor(spec),
                UseShellExecute = true
            });
            if (opened == null) return;
            await ActivateAsync(pid => pid == opened.Id, () => !opened.HasExited, spec, onActivated, token);
            await opened.WaitForExitAsync(token);
            return;
        }

        using var job = CreateJobObject(IntPtr.Zero, null);
        if (job.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error(), "CreateJobObject failed.");

        StartInJob(spec, job, extension);
        bool InJob(int pid) => ProcessIsInJob(pid, job);
        await ActivateAsync(InJob, () => ActiveProcesses(job) > 0, spec, onActivated, token);
        var ticks = 0;
        while (ActiveProcesses(job) > 0)
        {
            // A console window opened later by the batch file or the game is hidden as soon as it shows.
            if (spec.HideConsole && ticks++ % 4 == 0) WindowActivation.HideConsoleWindows(InJob);
            await Task.Delay(250, token);
        }
    }

    private static string WorkingDirectoryFor(ExternalProgramSpec spec) =>
        !string.IsNullOrWhiteSpace(spec.WorkingDirectory) ? spec.WorkingDirectory
        : Path.GetDirectoryName(spec.Command) ?? Environment.CurrentDirectory;

    private static void StartInJob(ExternalProgramSpec spec, SafeFileHandle job, string extension)
    {
        // Batch files need cmd.exe; CreateProcess does not consult file associations.
        var commandLine = extension is ".cmd" or ".bat"
            ? $"\"{Path.Combine(Environment.SystemDirectory, "cmd.exe")}\" /c \"\"{spec.Command}\" {spec.Arguments}\""
            : $"\"{spec.Command}\" {spec.Arguments}";

        var flags = CREATE_SUSPENDED | CREATE_UNICODE_ENVIRONMENT | (spec.HideConsole ? CREATE_NO_WINDOW : 0);
        var startup = new STARTUPINFO { cb = Marshal.SizeOf<STARTUPINFO>() };
        if (!CreateProcess(null, commandLine.TrimEnd(), IntPtr.Zero, IntPtr.Zero, false, flags, IntPtr.Zero,
                WorkingDirectoryFor(spec), ref startup, out var info))
            throw new Win32Exception(Marshal.GetLastWin32Error(), $"Failed to start {spec.Command}.");

        try
        {
            // Assign before the first instruction runs so every child inherits the job.
            if (!AssignProcessToJobObject(job, info.hProcess))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "AssignProcessToJobObject failed.");
            ResumeThread(info.hThread);
        }
        finally
        {
            CloseHandle(info.hThread);
            CloseHandle(info.hProcess);
        }
    }

    private static uint ActiveProcesses(SafeFileHandle job)
    {
        var info = new JOBOBJECT_BASIC_ACCOUNTING_INFORMATION();
        if (!QueryInformationJobObject(job, JobObjectBasicAccountingInformation, ref info, Marshal.SizeOf(info), IntPtr.Zero))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "QueryInformationJobObject failed.");
        return info.ActiveProcesses;
    }

    /// <summary>
    /// Waits for the program's main window, lets it settle, brings it to the foreground,
    /// and keeps checking until the program has held the foreground for a moment or the
    /// timeout passes. Games often open a helper or splash window first, so the largest
    /// visible window is re-evaluated on every pass and activation is repeated if the
    /// foreground drifts back to the launcher.
    /// </summary>
    private static async Task ActivateAsync(Func<int, bool> processMatches, Func<bool> stillRunning, ExternalProgramSpec spec,
        Action? onActivated, CancellationToken token)
    {
        var deadline = DateTime.UtcNow + spec.ActivationTimeout;
        DateTime? firstSeen = null;
        DateTime? foregroundSince = null;
        var notified = false;

        while (DateTime.UtcNow < deadline && stillRunning())
        {
            if (spec.HideConsole) WindowActivation.HideConsoleWindows(processMatches);
            var window = WindowActivation.FindMainWindow(processMatches, out _);
            if (window != IntPtr.Zero)
            {
                firstSeen ??= DateTime.UtcNow;
                if (DateTime.UtcNow - firstSeen >= SettleDelay)
                {
                    var foregroundPid = WindowActivation.ForegroundProcessId;
                    if (foregroundPid != 0 && processMatches(foregroundPid))
                    {
                        foregroundSince ??= DateTime.UtcNow;
                        if (!notified)
                        {
                            notified = true;
                            onActivated?.Invoke();
                        }
                        if (DateTime.UtcNow - foregroundSince >= StableForeground) return;
                    }
                    else
                    {
                        foregroundSince = null;
                        WindowActivation.TryActivate(window);
                    }
                }
            }
            await Task.Delay(250, token);
        }

        // The program never held the foreground; carry on anyway.
        if (!notified && firstSeen != null) onActivated?.Invoke();
    }

    private static async Task<Process?> WaitForProcessAsync(string name, TimeSpan timeout, CancellationToken token)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            var process = FindProcesses(name).FirstOrDefault();
            if (process != null) return process;
            await Task.Delay(250, token);
        }
        return null;
    }

    public static IEnumerable<Process> FindProcesses(string name)
    {
        var baseName = Path.GetFileNameWithoutExtension(name);
        return Process.GetProcessesByName(baseName).Where(p => p.SessionId == CurrentSessionId);
    }

    private static readonly int CurrentSessionId = Process.GetCurrentProcess().SessionId;

    private static bool ProcessIsInJob(int pid, SafeFileHandle job)
    {
        var handle = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (handle == IntPtr.Zero) return false;
        try { return IsProcessInJob(handle, job, out var inJob) && inJob; }
        finally { CloseHandle(handle); }
    }
}
