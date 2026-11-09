namespace ControllerNavigation;

// The types that cross features: the engine and its outputs, the pads, the settings window's host,
// the updater's status. Each feature's own types live in its file.

/// <summary>Where the app lives, the same on every PC (SPEC "Install and updates").</summary>
static class AppInfo
{
    public const string Name = "Controller Navigation";

    /// <summary>The GitHub repository releases come from (owner/name).</summary>
    public const string Repo = "APatenaude/ControllerNavigation";
    public const string ExeName = "ControllerNavigation.exe";

    /// <summary>This build's version (Directory.Build.props), "0.1.0".</summary>
    public static readonly string Version = typeof(AppInfo).Assembly.GetName().Version is { } v
        ? $"{v.Major}.{v.Minor}.{v.Build}"
        : "0.0.0";

    /// <summary>The user's settings and logs: %LOCALAPPDATA%\ControllerNavigation.</summary>
    public static string DataDir =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ControllerNavigation"
        );

    /// <summary>C:\Program Files\ControllerNavigation: admin-write only, which the elevated tasks' trust rests on.</summary>
    public static string InstallDir =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            "ControllerNavigation"
        );

    public static string InstalledExe => Path.Combine(InstallDir, ExeName);

    /// <summary>This process is the installed copy (not a download, not a dev build).</summary>
    public static bool RunningInstalled =>
        string.Equals(
            Path.GetFullPath(Environment.ProcessPath ?? ""),
            Path.GetFullPath(InstalledExe),
            StringComparison.OrdinalIgnoreCase
        );
}

/// <summary>What the engine asks for: input for the app in front, system actions, pointer motion. Called on the input thread.</summary>
interface IOutput
{
    void KeysDown(ushort[] keys);
    void KeysUp(ushort[] keys);
    void Mouse(Input.Button button, bool down);

    /// <summary>Wheel units, 120 a notch; positive = up (or right).</summary>
    void Wheel(int amount, bool horizontal);

    /// <summary>An ActionKind.IsSystem action (volume, media, window, keyboard, run, pause, power off).</summary>
    void System(string action, string? arg);

    /// <summary>The sticks' motion for the pointer thread; null when they rest or the pointer is off.</summary>
    void Motion(Motion? motion);
}

/// <summary>
/// What the sticks do this poll, handed whole to the pointer thread (record equality: an unchanged
/// state allocates nothing new). Axes are SDL's, -32768..32767, Y down positive.
/// </summary>
sealed record Motion(
    string Left,
    string Right,
    short LX,
    short LY,
    short RX,
    short RY,
    bool Precise,
    Feel Feel
);

/// <summary>
/// The on-screen keyboard (OnScreenKeyboard). While it is open, the base map's presses go to it
/// instead (the pad types on it); layers still work.
/// </summary>
interface IKeyboardSurface
{
    /// <summary>Read on the input thread at every poll.</summary>
    bool IsOpen { get; }

    /// <summary>Opens or closes it; from any thread.</summary>
    void Toggle();

    /// <summary>Closes it if open; from any thread (a window that does not get the mouse came forward).</summary>
    void Close();

    /// <summary>A control pressed while it is open (repeat: a held D-pad direction or stick); from the input thread.</summary>
    void Pad(PadControl control, bool repeat);
}

/// <summary>The window a system action acts on, and the pad that asked (power off).</summary>
readonly record struct ActionTarget(IntPtr Window, bool Elevated, int XInputSlot);

/// <summary>A pad as the settings window lists it.</summary>
/// <param name="Id">The key in Settings.Pads: SDL's GUID for the model (two pads of one model share it).</param>
/// <param name="Type">SDL's type: "xbox360", "xboxOne", "ps4", "ps5", "switchPro", "standard", "unknown", ...</param>
/// <param name="Virtual">A software pad (Steam Input, ViGEm, Parsec, a streaming guest): off unless switched on.</param>
/// <param name="Mapped">SDL knows its layout (a gamepad); false: a joystick the wizard can map.</param>
/// <param name="Labels">The face buttons' own names, south east west north: "A B X Y", "Cross Circle Square Triangle", "B A Y X".</param>
sealed record PadInfo(
    string Id,
    string Name,
    string Type,
    ushort Vendor,
    ushort Product,
    bool Virtual,
    bool Enabled,
    bool Connected,
    bool Mapped,
    string Labels,
    string? Path
);

/// <summary>The enabled pads merged, as last polled (the settings window's button test). Buttons: one bit per PadControl.</summary>
readonly record struct PadLive(
    uint Buttons,
    short LX,
    short LY,
    short RX,
    short RY,
    short LT,
    short RT
);

/// <summary>A joystick's raw state, for the mapping wizard: SDL mapping strings name these (b0, a1, h0.4).</summary>
sealed record RawJoystick(
    string Id,
    string Guid,
    string Name,
    bool[] Buttons,
    short[] Axes,
    byte[] Hats
);

/// <summary>A top-level window's app, for "add an app" in the tray and the settings window.</summary>
sealed record RunningApp(string Exe, string Title);

/// <summary>
/// The updater's state for the settings window and the tray. State: off (auto-update off),
/// notInstalled (not running from Program Files: nothing to update), checking, upToDate,
/// available (newer release found), waitingIdle (until the pad rests), installing, failed.
/// </summary>
sealed record UpdateStatus(
    string Current,
    string? Latest,
    string State,
    string? Message,
    DateTime? LastCheckUtc
);

/// <summary>Something on this PC that fights the tool (the settings window's Checks page).</summary>
/// <param name="Ok">True: nothing to do. False: Fix says what to change.</param>
sealed record CheckResult(string Name, bool Ok, string Detail, string? Fix);

/// <summary>What the settings window reads and changes (TrayApp implements it).</summary>
interface ISettingsHost
{
    Settings Settings { get; }

    /// <summary>Normalizes, saves and applies; false if it could not be written.</summary>
    bool Save(Settings settings);
    IReadOnlyList<PadInfo> Pads();
    PadLive Live();

    /// <summary>Null when that pad is not connected.</summary>
    RawJoystick? Raw(string padId);

    /// <summary>A mapping from the wizard: added to SDL at once and kept in the settings; false if SDL refuses it.</summary>
    bool AddMapping(string mapping);
    IReadOnlyList<RunningApp> RunningApps();

    /// <summary>The last window title seen for this exe (to write a title rule from), or null.</summary>
    string? LastTitle(string exe);
    UpdateStatus Update { get; }
    Task CheckForUpdate();
    IReadOnlyList<CheckResult> Checks();

    /// <summary>Pads connected or gone, the update state, settings saved: the window refreshes. Any thread.</summary>
    event Action? Changed;
}
