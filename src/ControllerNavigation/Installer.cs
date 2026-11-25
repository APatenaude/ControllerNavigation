using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32;

namespace ControllerNavigation;

/// <summary>
/// Install and uninstall (SPEC "Install and updates"), lifted from HTPC's SetupElevation and
/// Install-Launcher.ps1. --install puts this exe in Program Files\ControllerNavigation, registers
/// the \ControllerNavigation\ tasks (AppTasks) for the user who asked and starts the app through
/// the Start task; --uninstall stops the app, removes the tasks and the folder and keeps the
/// user's settings (%LOCALAPPDATA%). A copy run from anywhere else offers to install itself
/// (OfferInstall). The elevated part runs only in the copy in Program Files: Windows elevates its
/// own command processor, which copies this exe there and starts that copy (Elevation.Trampoline).
/// </summary>
static class Installer
{
    /// <summary>Exit codes of --install and --uninstall (Cancelled: the permission prompt was declined, ERROR_CANCELLED).</summary>
    public const int Ok = 0,
        Failed = 1,
        Refused = 2,
        Cancelled = 1223;

    /// <summary>On the copy started through the prompt: it never asks again (no loop when it still lacks the rights).</summary>
    const string ElevatedFlag = "--elevated";

    /// <summary>Followed by the SID of the user who asked: the tasks are theirs.</summary>
    const string UserFlag = "--user";

    const string LogFile = "install.log";

    /// <summary>--install or --uninstall (args[0]); the exit code is the elevated copy's (Ok, Failed, Refused, Cancelled).</summary>
    public static int Run(string[] args)
    {
        var uninstall = args.FirstOrDefault() == "--uninstall";
        var mode = uninstall ? "Uninstall" : "Install";
        try
        {
            if (
                Environment.IsPrivilegedProcess
                && AppInfo.RunningInstalled
                && Elevation.StartedClean()
            )
            {
                if (!Elevation.Enter(mode, LogFile))
                    return Refused;
                return uninstall ? Uninstall() : Install(ValueAfter(args, UserFlag));
            }
            // Elevated anywhere else: never the user's log (Log.UseFile).
            if (Environment.IsPrivilegedProcess)
                Log.UseFile(Path.Combine(Elevation.LogDir, LogFile));
            if (args.Contains(ElevatedFlag))
            {
                var why =
                    !Environment.IsPrivilegedProcess ? "has no administrator rights"
                    : !AppInfo.RunningInstalled ? $"runs from {Environment.ProcessPath}"
                    : "was not started the clean way";
                Log.Warn($"{mode}: the elevated copy {why}: stopped");
                return Refused;
            }
            return StartElevated(uninstall);
        }
        catch (Exception e)
        {
            Log.Error($"{mode}: failed", e);
            return Failed;
        }
        finally
        {
            Log.Flush();
        }
    }

    /// <summary>
    /// At a normal start from anywhere but Program Files. An installed copy as new as this one:
    /// started instead (true). Otherwise the owner is asked to install this one (or update the
    /// installed one to it) with one permission prompt, and this copy ends either way (true). Dev
    /// builds (DEBUG) run in place: false, nothing asked. False too when something unexpected
    /// failed (logged): the app then runs from where it is. Never throws.
    /// </summary>
    public static bool OfferInstall()
    {
#if DEBUG
        return false;
#else
        return Offer();
#endif
    }

    static bool Offer()
    {
        // Run as administrator: never the user's log, as in Run.
        if (Environment.IsPrivilegedProcess)
            Log.UseFile(Path.Combine(Elevation.LogDir, LogFile));
        try
        {
            var installed = ExeVersion(AppInfo.InstalledExe);
            var mine = UpdateRules.ParseSemVer(AppInfo.Version);
            if (installed is not null && mine is not null && installed >= mine)
            {
                Log.Info(
                    $"Installed copy {installed} is as new as this one ({mine}): starting it instead"
                );
                StartInstalled();
                return true;
            }
            if (!AskToInstall(installed))
            {
                Log.Info("Install: declined");
                return true;
            }
            var code = StartElevated(uninstall: false);
            if (code is not (Ok or Cancelled))
                MessageBox.Show(
                    $"{AppInfo.Name} was not installed (error {code}). The details are in {Path.Combine(Elevation.LogDir, LogFile)}.",
                    AppInfo.Name,
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning
                );
            return true;
        }
        catch (Exception e)
        {
            Log.Error("Offering to install", e);
            return false;
        }
        finally
        {
            Log.Flush();
        }
    }

    static bool AskToInstall(Version? installed)
    {
        Application.EnableVisualStyles();
        var install = new TaskDialogButton(installed is null ? "Install" : "Update")
        {
            ShowShieldIcon = true,
        };
        var page = new TaskDialogPage
        {
            Caption = AppInfo.Name,
            Heading = installed is null
                ? $"Install {AppInfo.Name} {AppInfo.Version}?"
                : $"Update {AppInfo.Name} {installed} to {AppInfo.Version}?",
            Text =
                $"It goes to {AppInfo.InstallDir}, starts when you sign in and updates itself from its GitHub releases.\n\n"
                + "Windows asks for permission once. The prompt names the Windows Command Processor: it copies the app into Program Files and starts it there.",
            Icon = TaskDialogIcon.ShieldBlueBar,
            Buttons = { install, TaskDialogButton.Cancel },
            DefaultButton = install,
            AllowCancel = true,
        };
        return TaskDialog.ShowDialog(page) == install;
    }

    // Through the Start task (as the user, not elevated, whatever this process is); without it, a
    // plain start, unless this process is elevated (the app would be too).
    static void StartInstalled()
    {
        try
        {
            AppTasks.Run(AppTasks.Start);
            Log.Info("Started the installed copy through its Start task");
            return;
        }
        catch (Exception e)
        {
            Log.Warn($"The Start task did not run ({e.Message})");
        }
        if (Environment.IsPrivilegedProcess)
        {
            Log.Warn(
                "The installed copy was not started: this process is elevated, and the app must not be"
            );
            return;
        }
        using var p = Process.Start(
            new ProcessStartInfo(AppInfo.InstalledExe)
            {
                UseShellExecute = false,
                WorkingDirectory = AppInfo.InstallDir,
            }
        );
        Log.Info("Started the installed copy directly");
    }

    /// <summary>
    /// The elevated copy, started the trusted way (Elevation.Trampoline): through Windows'
    /// permission prompt (runas), or directly when this process has the rights already. Install
    /// copies this exe first and passes who asked. Waits for it; its exit code.
    /// </summary>
    static int StartElevated(bool uninstall)
    {
        var mode = uninstall ? "Uninstall" : "Install";
        if (uninstall && !File.Exists(AppInfo.InstalledExe))
        {
            Log.Info($"Uninstall: {AppInfo.InstalledExe} is not there, nothing to remove");
            return Ok;
        }
        var prompt = !Environment.IsPrivilegedProcess;
        List<string> args = uninstall ? ["--uninstall", ElevatedFlag] : ["--install", ElevatedFlag];
        // Elevated already, this token may be another account's: the copy takes the session's user.
        if (!uninstall && prompt)
            args.AddRange([UserFlag, WindowsIdentity.GetCurrent().User!.Value]);
        var line = Elevation.Trampoline(
            Environment.ProcessPath!,
            args,
            AppInfo.InstallDir,
            Guid.NewGuid().ToString("N")[..12],
            copy: !uninstall
        );
        if (line is null)
        {
            Log.Warn($"{mode}: not started from {Environment.ProcessPath} (a % in its path)");
            return Failed;
        }
        var psi = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "cmd.exe"), line)
        {
            UseShellExecute = prompt,
            Verb = prompt ? "runas" : "",
            WindowStyle = ProcessWindowStyle.Hidden,
            CreateNoWindow = !prompt,
            WorkingDirectory = Environment.SystemDirectory,
        };
        Process? p;
        try
        {
            p = Process.Start(psi);
        }
        catch (Win32Exception e) when (e.NativeErrorCode == Cancelled)
        {
            Log.Info($"{mode}: administrator rights declined");
            return Cancelled;
        }
        if (p is null)
        {
            Log.Warn($"{mode}: Windows did not start the elevated copy");
            return Failed;
        }
        using (p)
        {
            Log.Info(
                $"{mode}: elevated copy started{(prompt ? "" : " (this process had the rights already)")}; waiting for it"
            );
            try
            {
                if (!p.WaitForExit(TimeSpan.FromMinutes(5)))
                {
                    Log.Warn($"{mode}: the elevated copy still runs after 5 minutes");
                    return Failed;
                }
                Log.Info(
                    $"{mode}: the elevated copy ended with code {p.ExitCode} (its log: {Path.Combine(Elevation.LogDir, LogFile)})"
                );
                return p.ExitCode;
            }
            catch (Exception e) when (e is Win32Exception or InvalidOperationException)
            {
                Log.Info($"{mode}: the elevated copy runs; its exit code cannot be read from here");
                return Ok;
            }
        }
    }

    static int Install(string? askedBy)
    {
        var setup = Elevation.TakeSetupLock();
        if (setup is null)
        {
            Log.Warn("Install: another install or update runs; stopped");
            return Failed;
        }
        try
        {
            if (InstallingUser(askedBy) is not { } user)
                return Refused;
            Log.Info($"Install: for {user.Name} ({user.Sid})");
            RunningCopies.Stop(everything: false);
            try
            {
                AppTasks.Register(user.Name, user.Sid);
                RemoveSetAside();
                Log.Info("Install: done");
                return Ok;
            }
            finally
            {
                // The app stopped above comes back even when the tasks were not made (a Start task from before).
                AppTasks.StartApp("Install");
            }
        }
        catch (Exception e)
        {
            Log.Error("Install: failed", e);
            return Failed;
        }
        finally
        {
            setup.ReleaseMutex();
            setup.Dispose();
        }
    }

    // The user the tasks are for: the one signed in to this session (Windows' session record, not
    // this token: an administrator who approved the prompt for a standard account runs this as
    // themselves), who must be the one who asked when the copy that asked said so.
    static (string Name, string Sid)? InstallingUser(string? askedBy)
    {
        var (name, sid) = Elevation.SessionUser();
        if (name is null || sid is null)
        {
            Log.Warn(
                $"Install: Windows did not say who is signed in here ({name ?? "nobody"}): refused"
            );
            return null;
        }
        if (askedBy is not null && !string.Equals(askedBy, sid, StringComparison.OrdinalIgnoreCase))
        {
            Log.Warn($"Install: asked by {askedBy}, but {name} ({sid}) is signed in here: refused");
            return null;
        }
        // "Highest privileges" for a standard account is its standard token: the helper and the
        // update job then refuse to run.
        if (
            !string.Equals(
                WindowsIdentity.GetCurrent().User?.Value,
                sid,
                StringComparison.OrdinalIgnoreCase
            )
        )
            Log.Warn(
                $"Install: approved by {WindowsIdentity.GetCurrent().Name} for {name}, probably a standard account: windows running as administrator and updates need {name} to be an administrator"
            );
        return (name, sid);
    }

    static int Uninstall()
    {
        var setup = Elevation.TakeSetupLock();
        if (setup is null)
        {
            Log.Warn("Uninstall: an install or update runs; stopped");
            return Failed;
        }
        try
        {
            RunningCopies.Stop(everything: true);
            AppTasks.Delete();
            AppTasks.RemoveScripts();
            RemoveInstallDirLater();
            return Ok;
        }
        catch (Exception e)
        {
            Log.Error("Uninstall: failed", e);
            return Failed;
        }
        finally
        {
            setup.ReleaseMutex();
            setup.Dispose();
        }
    }

    // This process runs from the folder and cannot delete its own exe: Windows' command processor,
    // started hidden, removes the folder a few seconds after this process (and the one that waits
    // for it) ended.
    static void RemoveInstallDirLater()
    {
        var dir = AppInfo.InstallDir;
        var sys = Environment.SystemDirectory;
        if (dir.Contains('%') || dir.Contains('"') || sys.Contains('%'))
            throw new InvalidOperationException($"{dir} cannot be put in a command line safely");
        var line =
            $"/d /s /c \"\"{Path.Combine(sys, "PING.EXE")}\" -n 6 127.0.0.1 >nul & rd /s /q \"{dir}\"\"";
        Log.Info(
            $"Uninstall: tasks removed; {dir} goes once this process ends (the settings in {AppInfo.DataDir} stay)"
        );
        Log.Flush();
        using var p = Process.Start(
            new ProcessStartInfo(Path.Combine(sys, "cmd.exe"), line)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = sys,
            }
        );
    }

    /// <summary>
    /// Elevated, in InstallDir: the exes an install or update renamed aside (*.old: one still
    /// running stays for the next time) and downloads an interrupted update left (*.download).
    /// </summary>
    public static void RemoveSetAside()
    {
        foreach (var file in Directory.EnumerateFiles(AppInfo.InstallDir))
        {
            if (
                !file.EndsWith(".old", StringComparison.OrdinalIgnoreCase)
                && !file.EndsWith(".download", StringComparison.OrdinalIgnoreCase)
            )
                continue;
            try
            {
                File.Delete(file);
                Log.Info($"Removed {Path.GetFileName(file)}");
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                Log.Info($"{Path.GetFileName(file)} is still in use; removed next time");
            }
        }
    }

    /// <summary>An exe's file version as major.minor.build; null when there is no such file.</summary>
    public static Version? ExeVersion(string path)
    {
        if (!File.Exists(path))
            return null;
        var info = FileVersionInfo.GetVersionInfo(path);
        return new Version(info.FileMajorPart, info.FileMinorPart, info.FileBuildPart);
    }

    static string? ValueAfter(string[] args, string flag)
    {
        var at = Array.IndexOf(args, flag);
        return at >= 0 && at + 1 < args.Length ? args[at + 1] : null;
    }
}

/// <summary>
/// The rules every elevated mode follows (install, uninstall, the update job, the input helper),
/// lifted from HTPC's SetupElevation, Rights and Register-InputTask.ps1. A highest-privileges task
/// or an elevated copy that ran a file the user can change would be a way around User Account
/// Control, so:
///   - it runs only as InstalledExe, in Program Files where Windows (HKLM) keeps it, in a folder
///     only administrators can change (Enter, UntrustedReason);
///   - .NET never unpacks it where the user can write. This exe is one self-extracting file and
///     .NET puts its native libraries in %TEMP%\.net, reusing what is there without checking it.
///     An elevated process gets the user's own environment (HKCU\Environment is theirs to write),
///     so what starts one (the trampoline, the task scripts) points .NET at BundleDir and clears the
///     switches that load code from elsewhere before .NET starts (StartEnvironment);
///   - once running, its whole environment becomes Windows' own (ApplyCleanEnvironment), and
///     system tools are named by their full path.
/// </summary>
static class Elevation
{
    /// <summary>Where .NET unpacks the elevated copies (admin-only, as InstallDir is).</summary>
    public static string BundleDir => Path.Combine(AppInfo.InstallDir, "bundle");

    /// <summary>The elevated copies' TEMP and TMP, never the user's %TEMP%.</summary>
    public static string TempDir => Path.Combine(AppInfo.InstallDir, "temp");

    /// <summary>The elevated modes' logs (install.log, update.log, input-helper.log): administrators write, users read.</summary>
    public static string LogDir => Path.Combine(AppInfo.InstallDir, "logs");

    /// <summary>Variables that load code or write files into a program: never kept, from anywhere.</summary>
    static readonly string[] DroppedPrefixes =
    [
        "COMPlus_",
        "DOTNET_",
        "CORECLR_",
        "COR_",
        "WEBVIEW2_",
    ];

    public static bool SameFile(string? a, string? b) =>
        a is not null
        && b is not null
        && string.Equals(
            Path.GetFullPath(a).TrimEnd('\\'),
            Path.GetFullPath(b).TrimEnd('\\'),
            StringComparison.OrdinalIgnoreCase
        );

    /// <summary>
    /// The start of every elevated mode: its log goes to LogDir (or, without the rights, the user's
    /// own log), and it goes on only when elevated, run as InstalledExe from where Windows keeps
    /// Program Files (HKLM, not the environment), in a folder only administrators can change,
    /// started the clean way (StartedClean). Its environment then becomes Windows' own. False
    /// (logged) when it must stop.
    /// </summary>
    public static bool Enter(string mode, string logFile)
    {
        var elevated = Environment.IsPrivilegedProcess;
        var installed = AppInfo.RunningInstalled;
        if (elevated && installed)
            try
            {
                Directory.CreateDirectory(LogDir);
            }
            catch (Exception) { }
        Log.UseFile(elevated ? Path.Combine(LogDir, logFile) : null);
        Log.Info(
            $"{mode}: {AppInfo.Name} {AppInfo.Version} ({Environment.ProcessPath}, pid {Environment.ProcessId})"
        );
        var real = RealInstallDir();
        var why =
            !elevated ? "not elevated"
            : !installed ? $"not run as {AppInfo.InstalledExe}"
            : !SameFile(AppInfo.InstallDir, real)
                ? $"Windows keeps Program Files elsewhere ({real})"
            : !StartedClean()
                ? "not started by its task or the installer (.NET may have unpacked it where the user can write)"
            : UntrustedReason(
                AppInfo.InstallDir,
                NoSplitToken() ? WindowsIdentity.GetCurrent().User?.Value : null
            );
        if (why is not null)
        {
            Log.Warn($"{mode}: refused, {why}");
            return false;
        }
        // A switch the start does not clear (a later .NET's): too late to undo, but it shows here.
        var left = Environment
            .GetEnvironmentVariables()
            .Keys.Cast<string>()
            .Where(n =>
                DroppedPrefixes.Any(p => n.StartsWith(p, StringComparison.OrdinalIgnoreCase))
                && n is not ("DOTNET_BUNDLE_EXTRACT_BASE_DIR" or "DOTNET_EnableDiagnostics")
            )
            .ToList();
        if (left.Count > 0)
            Log.Warn($"{mode}: started with {string.Join(", ", left)} set");
        ApplyCleanEnvironment();
        return true;
    }

    // Program Files as HKLM says (only administrators write it), whatever the environment says.
    static string? RealInstallDir()
    {
        using var key = RegistryKey
            .OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64)
            .OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion");
        return key?.GetValue("ProgramFilesDir") is string dir
            ? Path.Combine(dir, "ControllerNavigation")
            : null;
    }

    /// <summary>
    /// Started the way the trampoline and the task scripts start an elevated copy: .NET unpacked
    /// into BundleDir, no startup hook. Read before ApplyCleanEnvironment.
    /// </summary>
    public static bool StartedClean() =>
        SameFile(Environment.GetEnvironmentVariable("DOTNET_BUNDLE_EXTRACT_BASE_DIR"), BundleDir)
        && string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DOTNET_STARTUP_HOOKS"));

    /// <summary>
    /// What an elevated start sets before .NET runs (the trampoline's line, the task scripts):
    /// .NET unpacks into installDir\bundle; the switches that load code from elsewhere are cleared
    /// (a profiler, a startup hook, extra dependencies, the diagnostics ports, host traces, crash
    /// dumps, an ICU of its own); Windows' folders, PATH and TEMP (installDir\temp) come from
    /// Windows itself, until the copy remakes its whole environment. Null when a value has a % or a
    /// quote (cmd would expand or end it).
    /// </summary>
    public static List<(string Name, string Value)>? StartEnvironment(string installDir)
    {
        static string Folder(Environment.SpecialFolder f) => Environment.GetFolderPath(f);
        var win = Folder(Environment.SpecialFolder.Windows);
        var sys = Environment.SystemDirectory;
        var temp = Path.Combine(installDir, "temp");
        List<(string, string)> vars =
        [
            ("DOTNET_BUNDLE_EXTRACT_BASE_DIR", Path.Combine(installDir, "bundle")),
            ("DOTNET_EnableDiagnostics", "0"),
            ("DOTNET_STARTUP_HOOKS", ""),
            ("DOTNET_ADDITIONAL_DEPS", ""),
            ("CORECLR_ENABLE_PROFILING", ""),
            ("COR_ENABLE_PROFILING", ""),
            ("COREHOST_TRACE", ""),
            ("COREHOST_TRACEFILE", ""),
            ("DOTNET_HOST_TRACE", ""),
            ("DOTNET_HOST_TRACEFILE", ""),
            ("DOTNET_DbgEnableMiniDump", ""),
            ("COMPlus_DbgEnableMiniDump", ""),
            ("DOTNET_EnableCrashReport", ""),
            ("COMPlus_EnableCrashReport", ""),
            ("DOTNET_SYSTEM_GLOBALIZATION_APPLOCALICU", ""),
            // A standalone GC, loaded from a named file; event traces written to a chosen file.
            ("DOTNET_GCName", ""),
            ("COMPlus_GCName", ""),
            ("DOTNET_GCPath", ""),
            ("COMPlus_GCPath", ""),
            ("DOTNET_EnableEventPipe", ""),
            ("COMPlus_EnableEventPipe", ""),
            ("DOTNET_EventPipeOutputPath", ""),
            ("COMPlus_EventPipeOutputPath", ""),
            ("SystemRoot", win),
            ("windir", win),
            ("ProgramFiles", Folder(Environment.SpecialFolder.ProgramFiles)),
            ("ProgramW6432", Folder(Environment.SpecialFolder.ProgramFiles)),
            ("ProgramFiles(x86)", Folder(Environment.SpecialFolder.ProgramFilesX86)),
            ("CommonProgramFiles", Folder(Environment.SpecialFolder.CommonProgramFiles)),
            ("CommonProgramW6432", Folder(Environment.SpecialFolder.CommonProgramFiles)),
            ("CommonProgramFiles(x86)", Folder(Environment.SpecialFolder.CommonProgramFilesX86)),
            ("ProgramData", Folder(Environment.SpecialFolder.CommonApplicationData)),
            ("ALLUSERSPROFILE", Folder(Environment.SpecialFolder.CommonApplicationData)),
            ("PATH", $@"{sys};{win};{sys}\Wbem;{sys}\WindowsPowerShell\v1.0\"),
            ("TEMP", temp),
            ("TMP", temp),
        ];
        return vars.Any(v => v.Item2.Contains('%') || v.Item2.Contains('"')) ? null : vars;
    }

    /// <summary>
    /// cmd.exe's command line that starts the elevated copy (lifted from HTPC's Trampoline): the
    /// StartEnvironment, then, with copy, this exe copied to installDir (a copy in use renamed aside
    /// first, suffix: a name no one can guess; removed later, Installer.RemoveSetAside), then that
    /// copy started and waited for (its exit code is cmd's). /d: no AutoRun commands; /e:on and
    /// /v:off whatever the user's registry says (HKCU is theirs to write, and so is
    /// HKCU\Environment: this line holds no %variable%, which cmd would fill in from it). Null when
    /// a path has a % in it or an argument is not plain (letters, digits, dashes).
    /// </summary>
    public static string? Trampoline(
        string exe,
        IEnumerable<string> args,
        string installDir,
        string suffix,
        bool copy
    )
    {
        var env = StartEnvironment(installDir);
        var list = args.ToList();
        if (
            env is null
            || exe.Contains('%')
            || installDir.Contains('%')
            || suffix.Length == 0
            || !suffix.All(char.IsAsciiLetterOrDigit)
            || !list.All(a => a.Length > 0 && a.All(c => char.IsAsciiLetterOrDigit(c) || c == '-'))
        )
            return null;
        var target = Path.Combine(installDir, AppInfo.ExeName);
        var steps = env.Select(v => $"set \"{v.Name}={v.Value}\"").ToList();
        steps.Add($"mkdir \"{Path.Combine(installDir, "bundle")}\" 2>nul");
        steps.Add($"mkdir \"{Path.Combine(installDir, "temp")}\" 2>nul");
        var start =
            $"start \"\" /wait /d \"{installDir}\" \"{target}\" {CommandLine(list)}".TrimEnd();
        if (!copy || SameFile(exe, target))
            steps.Add(start);
        else
        {
            steps.Add($"move /y \"{target}\" \"{target}.{suffix}.old\" >nul 2>nul");
            steps.Add($"copy /b /y \"{exe}\" \"{target}\" >nul && {start}");
        }
        return $"/d /e:on /v:off /s /c \"{string.Join(" & ", steps)}\"";
    }

    /// <summary>
    /// The script an elevated task runs (lifted from HTPC's Register-InputTask.ps1): the
    /// StartEnvironment, then installDir's exe in that mode (cmd waits for it, so the task runs as
    /// long as it does). Null when it cannot be written safely (a % or a character beyond ASCII).
    /// </summary>
    public static string? TaskScript(string installDir, string mode)
    {
        var env = StartEnvironment(installDir);
        if (env is null)
            return null;
        var lines = new List<string>
        {
            "@echo off",
            $"rem Written by {AppInfo.Name}'s installer (Installer.cs): {mode} started with Windows' own environment.",
        };
        lines.AddRange(env.Select(v => $"set \"{v.Name}={v.Value}\""));
        lines.Add($"mkdir \"{Path.Combine(installDir, "bundle")}\" 2>nul");
        lines.Add($"mkdir \"{Path.Combine(installDir, "temp")}\" 2>nul");
        lines.Add($"\"{Path.Combine(installDir, AppInfo.ExeName)}\" {mode}");
        var text = string.Join("\r\n", lines) + "\r\n";
        return text.Contains('%') || text.Any(c => c > 127) ? null : text;
    }

    /// <summary>
    /// An elevated mode's environment, for itself and anything it starts, made from Windows' own
    /// values instead of the user's: the machine's variables (HKLM's Session Manager\Environment)
    /// expanded with Windows' folders and each other, never with a user's variable; Windows' folders
    /// from Windows itself; the user's basics from their account and known folders; TEMP and TMP =
    /// TempDir. Everything else goes: the user's own variables and PATH additions (a proxy, a .NET
    /// switch) and any COMPlus_*, DOTNET_*, CORECLR_*, COR_* or WEBVIEW2_* variable, the machine's
    /// included.
    /// </summary>
    public static Dictionary<string, string> CleanEnvironment(
        IReadOnlyDictionary<string, string> machine
    )
    {
        static string Folder(Environment.SpecialFolder f) => Environment.GetFolderPath(f);
        var win = Folder(Environment.SpecialFolder.Windows);
        var profile = Folder(Environment.SpecialFolder.UserProfile);
        var windows = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["SystemRoot"] = win,
            ["windir"] = win,
            ["SystemDrive"] = Path.GetPathRoot(win)!.TrimEnd('\\'),
            ["ProgramFiles"] = Folder(Environment.SpecialFolder.ProgramFiles),
            ["ProgramW6432"] = Folder(Environment.SpecialFolder.ProgramFiles),
            ["ProgramFiles(x86)"] = Folder(Environment.SpecialFolder.ProgramFilesX86),
            ["CommonProgramFiles"] = Folder(Environment.SpecialFolder.CommonProgramFiles),
            ["CommonProgramW6432"] = Folder(Environment.SpecialFolder.CommonProgramFiles),
            ["CommonProgramFiles(x86)"] = Folder(Environment.SpecialFolder.CommonProgramFilesX86),
            ["ProgramData"] = Folder(Environment.SpecialFolder.CommonApplicationData),
            ["ALLUSERSPROFILE"] = Folder(Environment.SpecialFolder.CommonApplicationData),
            ["PUBLIC"] =
                Path.GetDirectoryName(Folder(Environment.SpecialFolder.CommonDocuments)) ?? "",
            ["COMPUTERNAME"] = Environment.MachineName,
        };
        var env = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string Expand(string value, int depth = 0) =>
            depth > 4
                ? value
                : System.Text.RegularExpressions.Regex.Replace(
                    value,
                    "%([^%]+)%",
                    m =>
                        windows.TryGetValue(m.Groups[1].Value, out var w) ? w
                        : machine.TryGetValue(m.Groups[1].Value, out var v) ? Expand(v, depth + 1)
                        : m.Value
                );
        foreach (var (name, value) in machine)
            if (!DroppedPrefixes.Any(p => name.StartsWith(p, StringComparison.OrdinalIgnoreCase)))
                env[name] = Expand(value);
        foreach (var (name, value) in windows)
            env[name] = value;
        env["USERNAME"] = Environment.UserName;
        env["USERDOMAIN"] = Environment.UserDomainName;
        env["USERPROFILE"] = profile;
        env["HOMEDRIVE"] = Path.GetPathRoot(profile)!.TrimEnd('\\');
        env["HOMEPATH"] = profile[env["HOMEDRIVE"].Length..];
        env["APPDATA"] = Folder(Environment.SpecialFolder.ApplicationData);
        env["LOCALAPPDATA"] = Folder(Environment.SpecialFolder.LocalApplicationData);
        env["TEMP"] = env["TMP"] = TempDir;
        return env;
    }

    static Dictionary<string, string> MachineEnvironment()
    {
        var vars = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        using var key = Registry.LocalMachine.OpenSubKey(
            @"SYSTEM\CurrentControlSet\Control\Session Manager\Environment"
        );
        foreach (var name in key?.GetValueNames() ?? [])
            if (
                name.Length > 0
                && key!.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames)
                    is string value
            )
                vars[name] = value;
        return vars;
    }

    static void ApplyCleanEnvironment()
    {
        var clean = CleanEnvironment(MachineEnvironment());
        Directory.CreateDirectory(TempDir);
        var dropped = 0;
        foreach (var name in Environment.GetEnvironmentVariables().Keys.Cast<string>().ToList())
            if (!clean.ContainsKey(name))
            {
                Environment.SetEnvironmentVariable(name, null);
                dropped++;
            }
        foreach (var (name, value) in clean)
            Environment.SetEnvironmentVariable(name, value);
        Log.Info(
            $"Environment made Windows' own ({clean.Count} variables, {dropped} of the user's dropped; TEMP {TempDir})"
        );
    }

    /// <summary>Arguments as one command line that Windows splits back into the same list.</summary>
    public static string CommandLine(IEnumerable<string> args) =>
        string.Join(' ', args.Select(Quote));

    static string Quote(string arg)
    {
        if (arg.Length > 0 && !arg.Any(c => char.IsWhiteSpace(c) || c == '"'))
            return arg;
        var quoted = new StringBuilder("\"");
        var slashes = 0;
        foreach (var c in arg)
        {
            if (c == '\\')
            {
                slashes++;
                continue;
            }
            // Backslashes before a quote are doubled and the quote escaped; elsewhere they stay as they are.
            quoted.Append('\\', c == '"' ? slashes * 2 + 1 : slashes).Append(c);
            slashes = 0;
        }
        return quoted.Append('\\', slashes * 2).Append('"').ToString();
    }

    /// <summary>
    /// Why a folder is not safe for an elevated process to rely on, or null when it is (lifted from
    /// HTPC, setup\lib\UpdateCore.ps1's Get-UntrustedReason): a junction or link, an owner other
    /// than SYSTEM, Administrators or TrustedInstaller, or write rights for anyone else. alsoTrusted:
    /// one more SID that may own and write it (a folder made by an administrator with no split
    /// token: its owner and its CREATOR OWNER rights are that user's, an administrator whatever
    /// runs).
    /// </summary>
    public static string? UntrustedReason(string dir, string? alsoTrusted = null)
    {
        string[] trusted =
        [
            "S-1-5-18",
            "S-1-5-32-544",
            "S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464",
        ];
        if (alsoTrusted is not null)
            trusted = [.. trusted, alsoTrusted];
        const FileSystemRights write =
            FileSystemRights.WriteData
            | FileSystemRights.AppendData
            | FileSystemRights.WriteExtendedAttributes
            | FileSystemRights.WriteAttributes
            | FileSystemRights.Delete
            | FileSystemRights.DeleteSubdirectoriesAndFiles
            | FileSystemRights.ChangePermissions
            | FileSystemRights.TakeOwnership;
        var info = new DirectoryInfo(dir);
        if (!info.Exists)
            return $"{dir} is not there";
        if (info.Attributes.HasFlag(FileAttributes.ReparsePoint))
            return $"{dir} is a junction or link";
        var acl = info.GetAccessControl();
        var owner = acl.GetOwner(typeof(SecurityIdentifier))?.Value;
        if (owner is null || !trusted.Contains(owner))
            return $"{dir} is owned by {owner}";
        foreach (
            FileSystemAccessRule rule in acl.GetAccessRules(true, true, typeof(SecurityIdentifier))
        )
        {
            var sid = rule.IdentityReference.Value;
            // CREATOR OWNER: only what someone creates, which needs a write right refused on its own.
            if (
                rule.AccessControlType != AccessControlType.Allow
                || trusted.Contains(sid)
                || sid == "S-1-3-0"
            )
                continue;
            // 0x40000000 GENERIC_WRITE, 0x10000000 GENERIC_ALL (seen on inherit-only entries)
            if (
                (rule.FileSystemRights & write) != 0
                || ((int)rule.FileSystemRights & 0x50000000) != 0
            )
                return $"{dir} lets {sid} change it";
        }
        return null;
    }

    /// <summary>
    /// A folder only SYSTEM and Administrators can change and users may read (lifted from HTPC's
    /// New-TrustedDirectory): made so when missing. One already there must be trusted
    /// (UntrustedReason), else this throws: a standard user may make folders in ProgramData.
    /// </summary>
    public static void MakeTrustedDir(string dir)
    {
        if (!Directory.Exists(dir))
        {
            var security = new DirectorySecurity();
            security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            const InheritanceFlags inherit =
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
            foreach (
                var (sid, rights) in new[]
                {
                    (WellKnownSidType.LocalSystemSid, FileSystemRights.FullControl),
                    (WellKnownSidType.BuiltinAdministratorsSid, FileSystemRights.FullControl),
                    (WellKnownSidType.BuiltinUsersSid, FileSystemRights.ReadAndExecute),
                }
            )
                security.AddAccessRule(
                    new FileSystemAccessRule(
                        new SecurityIdentifier(sid, null),
                        rights,
                        inherit,
                        PropagationFlags.None,
                        AccessControlType.Allow
                    )
                );
            security.SetOwner(
                new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null)
            );
            new DirectoryInfo(dir).Create(security);
        }
        if (UntrustedReason(dir) is { } why)
            throw new InvalidOperationException(
                $"{why}: it was not made by this installer; remove it, then install again"
            );
    }

    /// <summary>
    /// Elevated with no standard-rights twin (User Account Control off, the built-in
    /// Administrator): TokenElevationType is TokenElevationTypeDefault (lifted from HTPC's Rights.Read).
    /// </summary>
    static bool NoSplitToken()
    {
        if (!Environment.IsPrivilegedProcess)
            return false;
        using var me = WindowsIdentity.GetCurrent(TokenAccessLevels.Query);
        return GetTokenInformation(
                me.AccessToken,
                18 /* TokenElevationType */
                ,
                out var type,
                4,
                out _
            )
            && type == 1; // TokenElevationTypeDefault
    }

    [DllImport("advapi32.dll", SetLastError = true)]
    static extern bool GetTokenInformation(
        Microsoft.Win32.SafeHandles.SafeAccessTokenHandle token,
        int infoClass,
        out int info,
        int length,
        out int returned
    );

    [DllImport("wtsapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern bool WTSQuerySessionInformationW(
        IntPtr server,
        int sessionId,
        int infoClass,
        out IntPtr buffer,
        out int bytes
    );

    [DllImport("wtsapi32.dll")]
    static extern void WTSFreeMemory(IntPtr memory);

    /// <summary>
    /// The user signed in to this session, from Windows' session record (not this process's
    /// token): DOMAIN\name, and its SID when Windows can look the name up. Nulls when it says nothing.
    /// </summary>
    public static (string? Name, string? Sid) SessionUser()
    {
        static string? Query(int infoClass)
        {
            if (
                !WTSQuerySessionInformationW(
                    IntPtr.Zero /* this server */
                    ,
                    -1 /* this session */
                    ,
                    infoClass,
                    out var buffer,
                    out _
                )
            )
                return null;
            try
            {
                return Marshal.PtrToStringUni(buffer);
            }
            finally
            {
                WTSFreeMemory(buffer);
            }
        }
        var user = Query(
            5 /* WTSUserName */
        );
        if (string.IsNullOrEmpty(user))
            return (null, null);
        var domain = Query(
            7 /* WTSDomainName */
        );
        var name = string.IsNullOrEmpty(domain) ? user : $@"{domain}\{user}";
        try
        {
            return (name, new NTAccount(name).Translate(typeof(SecurityIdentifier)).Value);
        }
        catch (Exception)
        {
            return (name, null);
        }
    }

    /// <summary>
    /// One install, uninstall or update job at a time on this PC: the held mutex (the caller
    /// releases it on the same thread), or null when another holds it.
    /// </summary>
    public static Mutex? TakeSetupLock()
    {
        Mutex? mutex = null;
        try
        {
            mutex = new Mutex(false, @"Global\ControllerNavigation-Setup");
            try
            {
                if (mutex.WaitOne(TimeSpan.FromSeconds(2)))
                    return mutex;
            }
            catch (AbandonedMutexException)
            {
                return mutex;
            }
        }
        catch (Exception e) when (e is UnauthorizedAccessException or IOException)
        {
            Log.Warn($"The setup lock could not be opened ({e.Message})");
        }
        mutex?.Dispose();
        return null;
    }
}

/// <summary>
/// The scheduled tasks in \ControllerNavigation\, registered at install for the user who asked,
/// through Task Scheduler's COM interface as HTPC's AsUser does (no task file on disk a standard
/// program could swap in between):
///   Input   InstalledExe --input-helper, elevated (highest privileges), on demand (ElevatedInput)
///   Update  InstalledExe --apply-update, elevated, on demand (Updater, UpdateJob)
///   Start   InstalledExe, at that user's sign-in, not elevated; also how the installer and the
///           update job start the app as the user
/// All in the user's session (interactive token), so the update job's Program.ExitEvent (Local\)
/// reaches the app. The elevated two start through a script in ProgramData\ControllerNavigation
/// (Elevation.TaskScript) run by conhost --headless, which keeps the command prompt's window from
/// flashing and taking the focus; it re-quotes its arguments and breaks anything with a space, so
/// the task's command line has no space or quote. The user may run the tasks, not change them.
/// </summary>
static class AppTasks
{
    public const string Input = "Input",
        Update = "Update",
        Start = "Start";

    const string FolderName = "ControllerNavigation";

    static string ScriptDir =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "ControllerNavigation"
        );

    /// <summary>Makes or replaces the three tasks for this user (DOMAIN\name and SID), and the elevated ones' scripts.</summary>
    public static void Register(string userName, string sid)
    {
        var sys = Environment.SystemDirectory;
        var conhost = Path.Combine(sys, "conhost.exe");
        var cmd = Path.Combine(sys, "cmd.exe");
        Elevation.MakeTrustedDir(ScriptDir);
        dynamic service = Scheduler();
        var folder = TaskFolder(service, create: true)!;
        // The user's own SID gets read and run: at medium integrity Administrators is deny-only.
        var sddl = $"D:P(A;;FA;;;SY)(A;;FA;;;BA)(A;;GRGX;;;{sid})";
        foreach (
            var (name, file, mode, limit, what) in new[]
            {
                (
                    Input,
                    "input-helper.cmd",
                    "--input-helper",
                    "PT0S",
                    "the input helper for administrator windows"
                ),
                (
                    Update,
                    "apply-update.cmd",
                    "--apply-update",
                    "PT1H",
                    "applies an update from the GitHub releases"
                ),
            }
        )
        {
            var script = WriteScript(file, mode);
            var arguments = $"--headless {cmd} /d /e:on /v:off /c {script}";
            if (arguments.Contains('"') || script.Contains(' ') || cmd.Contains(' '))
                throw new InvalidOperationException(
                    $"a space in {script} or {cmd}: the task's command line cannot be made without quotes"
                );
            var task = Definition(service, userName, conhost, arguments, sys, what, elevated: true);
            task.Settings.MultipleInstances = 2; // TASK_INSTANCES_IGNORE_NEW
            task.Settings.Hidden = true;
            task.Settings.ExecutionTimeLimit = limit;
            Save(folder, name, task, sddl);
        }
        var start = Definition(
            service,
            userName,
            AppInfo.InstalledExe,
            "",
            AppInfo.InstallDir,
            "starts the app at sign-in",
            elevated: false
        );
        // A second start finds the running app, which opens its settings window (Program.Main).
        start.Settings.MultipleInstances = 0; // TASK_INSTANCES_PARALLEL
        var logon = start.Triggers.Create(9); // TASK_TRIGGER_LOGON
        logon.UserId = userName;
        Save(folder, Start, start, sddl);
    }

    static dynamic Definition(
        dynamic service,
        string userName,
        string exe,
        string arguments,
        string workingDir,
        string description,
        bool elevated
    )
    {
        var task = service.NewTask(0);
        task.RegistrationInfo.Author = AppInfo.Name;
        task.RegistrationInfo.Description = $"{AppInfo.Name}: {description}";
        task.Principal.UserId = userName;
        task.Principal.LogonType = 3; // TASK_LOGON_INTERACTIVE_TOKEN: in the user's session, while signed in
        task.Principal.RunLevel = elevated ? 1 : 0; // TASK_RUNLEVEL_HIGHEST, TASK_RUNLEVEL_LUA
        task.Settings.DisallowStartIfOnBatteries = false;
        task.Settings.StopIfGoingOnBatteries = false;
        task.Settings.ExecutionTimeLimit = "PT0S"; // the default ends a task after 3 days
        task.Settings.Priority = 4; // normal, not Task Scheduler's below normal
        var action = task.Actions.Create(0); // TASK_ACTION_EXEC
        action.Path = exe;
        action.Arguments = arguments;
        action.WorkingDirectory = workingDir;
        return task;
    }

    static void Save(dynamic folder, string name, dynamic task, string sddl)
    {
        var registered = folder.RegisterTaskDefinition(
            name,
            task,
            6, /* TASK_CREATE_OR_UPDATE */
            null,
            null,
            3, /* TASK_LOGON_INTERACTIVE_TOKEN */
            null
        );
        registered.SetSecurityDescriptor(sddl, 0);
        Log.Info($"Task \\{FolderName}\\{name} registered");
    }

    // Written whole each time (a new file takes the folder's admin-only rights).
    static string WriteScript(string file, string mode)
    {
        var path = Path.Combine(ScriptDir, file);
        var text =
            Elevation.TaskScript(AppInfo.InstallDir, mode)
            ?? throw new InvalidOperationException(
                $"{AppInfo.InstallDir} cannot be put in a script safely (a % or a character beyond ASCII)"
            );
        File.Delete(path);
        File.WriteAllText(path, text, Encoding.ASCII);
        Log.Info($"{path} written");
        return path;
    }

    /// <summary>Uninstall: the scripts and their folder (a link left there is removed, never followed).</summary>
    public static void RemoveScripts()
    {
        var dir = new DirectoryInfo(ScriptDir);
        if (!dir.Exists)
            return;
        if (dir.Attributes.HasFlag(FileAttributes.ReparsePoint))
            dir.Delete();
        else
            dir.Delete(recursive: true);
        Log.Info($"{ScriptDir} removed");
    }

    /// <summary>Runs a task now, as its own user (the user's own processes may run them). Throws when it is not there.</summary>
    public static void Run(string name)
    {
        dynamic service = Scheduler();
        TaskFolder(service, create: false)!.GetTask(name).Run(null);
    }

    /// <summary>The installer: the app started as the user, not elevated, through the Start task. Logged; never throws.</summary>
    public static void StartApp(string mode) => AppStarter()(mode);

    /// <summary>
    /// StartApp in two steps: the Start task looked up now, run when the returned action is
    /// called (with the log's mode). The update job looks it up before its swap: a single-file
    /// process reads each assembly from its exe's path the first time it needs one, and once the
    /// new version is there that read fails (a FileNotFoundException with no message), so the
    /// COM binder must be loaded before. Neither step throws.
    /// </summary>
    public static Action<string> AppStarter()
    {
        dynamic? task = null;
        var why = "";
        try
        {
            task = TaskFolder(Scheduler(), create: false)!.GetTask(Start);
        }
        catch (Exception e)
        {
            why = $"{e.GetType().Name}: {e.Message}";
        }
        return mode =>
        {
            try
            {
                if (task is not null)
                {
                    task.Run(null);
                    Log.Info($"{mode}: the app started through the Start task");
                    return;
                }
            }
            catch (Exception e)
            {
                why = $"{e.GetType().Name}: {e.Message}";
            }
            Log.Warn($"{mode}: the app did not start ({why}); it starts at the next sign-in");
        };
    }

    /// <summary>Whether a task runs now, and its last run's result (its program's exit code). Throws when it is not there.</summary>
    public static (bool Running, int LastResult) State(string name)
    {
        dynamic service = Scheduler();
        var task = TaskFolder(service, create: false)!.GetTask(name);
        return (
            (int)task.State
                == 4 /* TASK_STATE_RUNNING */
            ,
            (int)task.LastTaskResult
        );
    }

    /// <summary>Uninstall: the three tasks and their folder; what is not there is skipped.</summary>
    public static void Delete()
    {
        dynamic service = Scheduler();
        var folder = TaskFolder(service, create: false);
        if (folder is null)
        {
            Log.Info($"No \\{FolderName} task folder");
            return;
        }
        foreach (var name in new[] { Input, Update, Start })
            try
            {
                folder.DeleteTask(name, 0);
                Log.Info($"Task \\{FolderName}\\{name} removed");
            }
            catch (Exception e)
            {
                Log.Info($"Task \\{FolderName}\\{name} not removed ({e.Message})");
            }
        try
        {
            service.GetFolder("\\").DeleteFolder(FolderName, 0);
        }
        catch (Exception e)
        {
            Log.Warn($"Task folder \\{FolderName} not removed ({e.Message})");
        }
    }

    // \ControllerNavigation; null (or made, with create) when it is not there.
    static dynamic? TaskFolder(dynamic service, bool create)
    {
        try
        {
            return service.GetFolder($"\\{FolderName}");
        }
        catch (Exception) when (create)
        {
            return service.GetFolder("\\").CreateFolder(FolderName, null);
        }
        catch (Exception)
        {
            return null;
        }
    }

    static dynamic Scheduler()
    {
        dynamic service = Activator.CreateInstance(
            Type.GetTypeFromProgID("Schedule.Service", true)!
        )!;
        service.Connect();
        return service;
    }
}

/// <summary>The app's processes running from Program Files (lifted from HTPC's LauncherUpdate.ps1 and Native.ProcessInfo).</summary>
static class RunningCopies
{
    static readonly string[] Modes =
    [
        "--input-helper",
        "--apply-update",
        "--install",
        "--uninstall",
    ];

    /// <summary>
    /// Asks the tray app in this session to exit (Program.ExitEvent), waits up to 10 s, then ends
    /// what is left as a last resort: the tray app in this session, or with everything, every
    /// process of a file in InstallDir but the install or uninstall that asked (uninstall: the
    /// files must go).
    /// </summary>
    public static void Stop(bool everything)
    {
        var session = Process.GetCurrentProcess().SessionId;
        bool Wanted(Process p, string? commandLine) =>
            everything
                ? !HasMode(commandLine, "--install", "--uninstall")
                : p.SessionId == session && commandLine is not null && !HasMode(commandLine, Modes);
        try
        {
            using var exit = EventWaitHandle.OpenExisting(Program.ExitEvent);
            exit.Set();
            Log.Info("Asked the app to exit");
        }
        catch (WaitHandleCannotBeOpenedException)
        {
            Log.Info("The app does not run in this session");
        }
        catch (Exception e) when (e is UnauthorizedAccessException or IOException)
        {
            Log.Warn($"The app's exit event could not be set ({e.Message})");
        }
        var until = Environment.TickCount64 + 10_000;
        while (true)
        {
            var left = Find(Wanted);
            if (left.Count == 0)
                return;
            if (Environment.TickCount64 >= until)
            {
                foreach (var (p, commandLine) in left)
                    using (p)
                        try
                        {
                            p.Kill();
                            p.WaitForExit(5000);
                            Log.Warn($"Ended pid {p.Id} ({commandLine}): it did not exit");
                        }
                        catch (Exception e)
                        {
                            Log.Warn($"Could not end pid {p.Id}: {e.Message}");
                        }
                return;
            }
            foreach (var (p, _) in left)
                p.Dispose();
            Thread.Sleep(250);
        }
    }

    // The tray app is started with no mode; the helpers, jobs and installs name theirs.
    static bool HasMode(string? commandLine, params string[] modes) =>
        commandLine is not null
        && modes.Any(m => commandLine.Contains(m, StringComparison.OrdinalIgnoreCase));

    // The processes of a file in InstallDir that wanted picks, under any name (the exe an update
    // renamed aside keeps running as it was), but this one.
    static List<(Process Process, string? CommandLine)> Find(Func<Process, string?, bool> wanted)
    {
        var found = new List<(Process, string?)>();
        foreach (var p in Process.GetProcesses())
        {
            var (path, commandLine) =
                p.Id != Environment.ProcessId
                && p.ProcessName.StartsWith(
                    "ControllerNavigation",
                    StringComparison.OrdinalIgnoreCase
                )
                    ? ProcessInfo(p.Id)
                    : (null, null);
            if (
                path is not null
                && Elevation.SameFile(Path.GetDirectoryName(path), AppInfo.InstallDir)
                && wanted(p, commandLine)
            )
                found.Add((p, commandLine));
            else
                p.Dispose();
        }
        return found;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern IntPtr OpenProcess(uint access, bool inherit, uint processId);

    [DllImport("kernel32.dll")]
    static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    static extern bool QueryFullProcessImageName(
        IntPtr process,
        int flags,
        StringBuilder name,
        ref int size
    );

    [DllImport("ntdll.dll")]
    static extern int NtQueryInformationProcess(
        IntPtr process,
        int infoClass,
        IntPtr info,
        int length,
        out int returnLength
    );

    const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

    /// <summary>A process's program path and command line (null where it cannot be read).</summary>
    public static (string? Path, string? CommandLine) ProcessInfo(int processId)
    {
        var h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, (uint)processId);
        if (h == IntPtr.Zero)
            return (null, null);
        try
        {
            var name = new StringBuilder(1024);
            var size = name.Capacity;
            var path = QueryFullProcessImageName(h, 0, name, ref size) ? name.ToString() : null;
            // ProcessCommandLineInformation (60): a UNICODE_STRING followed by the text.
            string? commandLine = null;
            NtQueryInformationProcess(h, 60, IntPtr.Zero, 0, out var needed);
            if (needed > 0)
            {
                var buffer = Marshal.AllocHGlobal(needed);
                try
                {
                    if (NtQueryInformationProcess(h, 60, buffer, needed, out _) == 0)
                        commandLine = Marshal.PtrToStringUni(
                            Marshal.ReadIntPtr(buffer, IntPtr.Size),
                            (ushort)Marshal.ReadInt16(buffer) / 2
                        );
                }
                finally
                {
                    Marshal.FreeHGlobal(buffer);
                }
            }
            return (path, commandLine);
        }
        finally
        {
            CloseHandle(h);
        }
    }
}
