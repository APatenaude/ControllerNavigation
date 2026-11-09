using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace ControllerNavigation;

/// <summary>
/// What on this PC would fight the tool (the settings window's Checks page): JoyXoff, Windows' own
/// controller features, Steam Input. Reads only, never writes the registry or touches a service.
/// </summary>
static class Checks
{
    const string GameBarKey = @"Software\Microsoft\GameBar";
    const string ControllerNavigationKey =
        @"SOFTWARE\Microsoft\Input\Settings\ControllerProcessor\ControllerToVKMapping";

    [StructLayout(LayoutKind.Sequential)]
    struct SERVICE_STATUS
    {
        public uint ServiceType,
            CurrentState,
            ControlsAccepted,
            Win32ExitCode,
            ServiceSpecificExitCode,
            CheckPoint,
            WaitHint;
    }

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern IntPtr OpenSCManager(string? machine, string? database, uint access);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern IntPtr OpenService(IntPtr manager, string name, uint access);

    [DllImport("advapi32.dll", SetLastError = true)]
    static extern bool QueryServiceStatus(IntPtr service, out SERVICE_STATUS status);

    [DllImport("advapi32.dll")]
    static extern bool CloseServiceHandle(IntPtr handle);

    const uint SC_MANAGER_CONNECT = 0x1,
        SERVICE_QUERY_STATUS = 0x4,
        SERVICE_START_PENDING = 2,
        SERVICE_RUNNING = 4;

    /// <summary>Every check, in the order the page lists them. Never throws: a check that cannot be read says so.</summary>
    public static IReadOnlyList<CheckResult> Run() =>
        new[]
        {
            Safe("JoyXoff", JoyXoff),
            Safe("Xbox Game Bar and the Guide button", GameBar),
            Safe("Windows' own controller navigation", WindowsControllerNavigation),
            Safe("Steam Input", Steam),
        };

    /// <summary>A registry switch is on when its value is missing or not zero (how Windows reads these).</summary>
    public static bool IsSwitchOn(object? value) =>
        value switch
        {
            null => true,
            int i => i != 0,
            long l => l != 0,
            string s => s.Trim() != "0",
            _ => true,
        };

    static CheckResult Safe(string name, Func<CheckResult> check)
    {
        try
        {
            return check();
        }
        catch (Exception e)
        {
            Log.Warn($"Check {name} failed: {e.Message}");
            return new CheckResult(name, true, "Could not be read on this PC.", null);
        }
    }

    static CheckResult JoyXoff()
    {
        var process = ProcessRunning("Joyxoff");
        var service = ServiceRunning("JoyXoffSvc");
        if (!process && !service)
            return new CheckResult("JoyXoff", true, "JoyXoff is not running.", null);
        var what =
            process && service ? "its program and its service are"
            : process ? "its program is"
            : "its service (JoyXoffSvc) is";
        return new CheckResult(
            "JoyXoff",
            false,
            $"JoyXoff is running ({what} up). It also drives the mouse from the controller, so both tools would act at once.",
            "Exit JoyXoff, then uninstall it, or set the JoyXoffSvc service to Disabled in the Services app."
        );
    }

    static CheckResult GameBar()
    {
        const string name = "Xbox Game Bar and the Guide button";
        var on = IsSwitchOn(
            Registry.GetValue(@"HKEY_CURRENT_USER\" + GameBarKey, "UseNexusForGameBarEnabled", null)
        );
        return on
            ? new CheckResult(
                name,
                false,
                "The Guide button opens the Xbox Game Bar, which gets in the way of the Guide layer.",
                "Windows Settings > Gaming > Game Bar: turn off \"Allow your controller to open Game Bar\" (older Windows: \"Open Xbox Game Bar using this button on a controller\")."
            )
            : new CheckResult(
                name,
                true,
                "The Guide button does not open the Xbox Game Bar.",
                null
            );
    }

    static CheckResult WindowsControllerNavigation()
    {
        const string name = "Windows' own controller navigation";
        using var key = RegistryKey
            .OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64)
            .OpenSubKey(ControllerNavigationKey);
        return IsSwitchOn(key?.GetValue("Enabled"))
            ? new CheckResult(
                name,
                false,
                "Windows turns a controller into arrow keys, Enter and Esc inside its own screens (Start, Search, Settings, the taskbar), on top of this tool.",
                "In an administrator terminal run: reg add \"HKLM\\"
                    + ControllerNavigationKey
                    + "\" /v Enabled /t REG_DWORD /d 0 /f   then restart Windows."
            )
            : new CheckResult(name, true, "Windows' own controller navigation is off.", null);
    }

    static CheckResult Steam()
    {
        const string name = "Steam Input";
        var installed =
            Registry.GetValue(@"HKEY_CURRENT_USER\Software\Valve\Steam", "SteamPath", null)
                is not null
            || ProcessRunning("steam");
        return installed
            ? new CheckResult(
                name,
                true,
                "Reminder: Steam is installed. Steam Input can turn the controller into a second mouse and keyboard outside games, and this cannot be read from here.",
                "In Steam open Settings > Controller. Turn off the Desktop Layout (it makes the pad a mouse and keyboard) and the Guide Button Chord Layout (it reacts to Guide plus a button)."
            )
            : new CheckResult(name, true, "Steam is not installed.", null);
    }

    static bool ProcessRunning(string name)
    {
        var found = Process.GetProcessesByName(name);
        foreach (var p in found)
            p.Dispose();
        return found.Length > 0;
    }

    static bool ServiceRunning(string name)
    {
        var manager = OpenSCManager(null, null, SC_MANAGER_CONNECT);
        if (manager == IntPtr.Zero)
            return false;
        try
        {
            var service = OpenService(manager, name, SERVICE_QUERY_STATUS);
            if (service == IntPtr.Zero)
                return false;
            try
            {
                return QueryServiceStatus(service, out var status)
                    && status.CurrentState is SERVICE_RUNNING or SERVICE_START_PENDING;
            }
            finally
            {
                CloseServiceHandle(service);
            }
        }
        finally
        {
            CloseServiceHandle(manager);
        }
    }
}
