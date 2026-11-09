using System.Text.Json;
using System.Text.Json.Serialization;

namespace ControllerNavigation;

/// <summary>
/// Everything the owner can set (SPEC "Configurability"), as settings.json holds it. A missing
/// part takes its built-in default (Defaults), so a new PC works with no file at all. An instance
/// is never changed once published (SettingsStore.Current): an edit is a new instance, saved whole.
/// </summary>
sealed class Settings
{
    public int Version { get; set; } = 1;
    public Feel Feel { get; set; } = new();

    /// <summary>The map in apps that get the mouse.</summary>
    public ButtonLayout Base { get; set; } = Defaults.Base();

    /// <summary>Hold-button layers (default: one, on Start), each with its own map.</summary>
    public List<Layer> Layers { get; set; } = Defaults.Layers();

    /// <summary>
    /// Apps with a fixed answer: "always" gets the mouse (their overrides and title rules apply),
    /// "never" does not. Every other app is automatic (Detection).
    /// </summary>
    public List<AppRule> Apps { get; set; } = Defaults.Apps();

    /// <summary>How an app with no rule is told apart from a game (SPEC "Which apps get the mouse").</summary>
    public Detection Detection { get; set; } = new();

    /// <summary>Per pad (key: PadInfo.Id): on/off and the name it was seen with.</summary>
    public Dictionary<string, PadSetting> Pads { get; set; } = new();

    /// <summary>SDL mapping strings made by the "map this controller" wizard, added at start.</summary>
    public List<string> Mappings { get; set; } = new();
    public Drivers Drivers { get; set; } = new();
    public bool AutoUpdate { get; set; } = true;

    /// <summary>
    /// Drops what cannot work (unknown controls, actions or stick modes, out-of-range numbers) so
    /// the engine never meets it; each drop is one line for the log. Never throws.
    /// </summary>
    public List<string> Normalize()
    {
        var notes = new List<string>();
        Feel ??= new Feel();
        Feel.Clamp();
        Base ??= Defaults.Base();
        Base.Normalize("base map", notes);
        Layers ??= new();
        Apps ??= new();
        Pads ??= new();
        Mappings ??= new();
        Drivers ??= new();
        Detection ??= new();
        Detection.GameFolders = (Detection.GameFolders ?? new())
            .Where(f => !string.IsNullOrWhiteSpace(f))
            .Select(f => f.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        // A hand-edited file may hold nulls anywhere JSON allows them.
        Layers.RemoveAll(l => l is null);
        Apps.RemoveAll(a => a is null);
        Mappings.RemoveAll(string.IsNullOrWhiteSpace);
        foreach (var id in Pads.Where(p => p.Value is null).Select(p => p.Key).ToList())
            Pads.Remove(id);
        foreach (var layer in Layers.ToList())
        {
            if (!Catalog.IsControl(layer.Button))
            {
                notes.Add($"Layer {layer.Name}: button {layer.Button} unknown, layer dropped");
                Layers.Remove(layer);
                continue;
            }
            layer.Buttons ??= new();
            layer.HoldMs = Math.Clamp(layer.HoldMs, 100, 10_000);
            ButtonLayout.NormalizeButtons(layer.Buttons, $"layer {layer.Name}", notes);
            if (layer.Tap is not null && !layer.Tap.Normalize($"layer {layer.Name} tap", notes))
                layer.Tap = null;
            if (layer.Hold is not null && !layer.Hold.Normalize($"layer {layer.Name} hold", notes))
                layer.Hold = null;
        }
        foreach (var app in Apps)
        {
            app.Exes = (app.Exes ?? new())
                .Where(e => e is not null)
                .Select(e => e.Trim())
                .Where(e => e.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            app.Buttons ??= new();
            app.Name ??= "";
            app.Mode = app.Mode?.Trim().ToLowerInvariant() ?? AppMode.Always;
            if (app.Mode is not (AppMode.Always or AppMode.Never))
            {
                notes.Add($"App {app.Name}: mode {app.Mode} unknown, always used");
                app.Mode = AppMode.Always;
            }
            app.TitleOff = (app.TitleOff ?? new())
                .Where(t => !string.IsNullOrWhiteSpace(t))
                .ToList();
            if (app.LeftStick is not null && !Catalog.StickModes.Contains(app.LeftStick))
            {
                notes.Add($"App {app.Name}: left stick {app.LeftStick} unknown");
                app.LeftStick = null;
            }
            if (app.RightStick is not null && !Catalog.StickModes.Contains(app.RightStick))
            {
                notes.Add($"App {app.Name}: right stick {app.RightStick} unknown");
                app.RightStick = null;
            }
            ButtonLayout.NormalizeButtons(app.Buttons, $"app {app.Name}", notes);
        }
        return notes;
    }
}

/// <summary>How the sticks, triggers and repeats feel (SPEC "Feel").</summary>
sealed class Feel
{
    /// <summary>Pointer speed as a factor of 1.1 screen heights per second at full tilt.</summary>
    public double PointerSpeed { get; set; } = 1;

    /// <summary>The pointer's speed while a "precise" button is held, as a factor of the normal one.</summary>
    public double PreciseSpeed { get; set; } = 0.25;

    /// <summary>Scroll speed as a factor of 2400 wheel units (20 notches) per second at full tilt.</summary>
    public double ScrollSpeed { get; set; } = 1;

    /// <summary>Stick response past the dead zone: 1 linear, 2 squared (small tilts slow and exact).</summary>
    public double Curve { get; set; } = 2;

    /// <summary>Stick travel ignored around the centre, 0..32767.</summary>
    public int LeftDeadzone { get; set; } = 8000;
    public int RightDeadzone { get; set; } = 8000;

    /// <summary>How far a trigger goes down before it counts as pressed, in percent.</summary>
    public int TriggerPercent { get; set; } = 40;

    /// <summary>How far a stick used as arrows or WASD tilts before it counts, in percent.</summary>
    public int StickArrowPercent { get; set; } = 50;
    public int RepeatDelayMs { get; set; } = 350;
    public int RepeatEveryMs { get; set; } = 60;

    /// <summary>A binding with a long-press action: held this long, the long-press action runs instead.</summary>
    public int LongPressMs { get; set; } = 500;

    /// <summary>An allowed window must stay in front this long before the controller turns on (SPEC "Foreground tracking").</summary>
    public int OnDelayMs { get; set; } = 300;

    /// <summary>Volume change per step, in percent (rounded to Windows' 2 % per key press while ShowVolumeSlider is on).</summary>
    public int VolumeStep { get; set; } = 4;

    /// <summary>
    /// Volume and mute go through the volume keys, so Windows shows its volume slider (in steps of
    /// 2 %). Off: they set the level directly (any step, no slider; a game that swallows key
    /// presses cannot block it).
    /// </summary>
    public bool ShowVolumeSlider { get; set; } = true;

    public void Clamp()
    {
        PointerSpeed = Math.Clamp(PointerSpeed, 0.05, 10);
        PreciseSpeed = Math.Clamp(PreciseSpeed, 0.01, 1);
        ScrollSpeed = Math.Clamp(ScrollSpeed, 0.05, 10);
        Curve = Math.Clamp(Curve, 1, 4);
        LeftDeadzone = Math.Clamp(LeftDeadzone, 0, 30000);
        RightDeadzone = Math.Clamp(RightDeadzone, 0, 30000);
        TriggerPercent = Math.Clamp(TriggerPercent, 5, 95);
        StickArrowPercent = Math.Clamp(StickArrowPercent, 10, 95);
        RepeatDelayMs = Math.Clamp(RepeatDelayMs, 50, 5000);
        RepeatEveryMs = Math.Clamp(RepeatEveryMs, 15, 2000);
        LongPressMs = Math.Clamp(LongPressMs, 100, 5000);
        OnDelayMs = Math.Clamp(OnDelayMs, 0, 5000);
        VolumeStep = Math.Clamp(VolumeStep, 1, 25);
    }
}

/// <summary>A map: what the sticks do and what each control does (control id → binding).</summary>
sealed class ButtonLayout
{
    public string LeftStick { get; set; } = StickMode.Pointer;
    public string RightStick { get; set; } = StickMode.Scroll;
    public Dictionary<string, PadBinding> Buttons { get; set; } = new();

    public void Normalize(string where, List<string> notes)
    {
        if (!Catalog.StickModes.Contains(LeftStick))
        {
            notes.Add($"{where}: left stick {LeftStick} unknown");
            LeftStick = StickMode.None;
        }
        if (!Catalog.StickModes.Contains(RightStick))
        {
            notes.Add($"{where}: right stick {RightStick} unknown");
            RightStick = StickMode.None;
        }
        Buttons ??= new();
        NormalizeButtons(Buttons, where, notes);
    }

    public static void NormalizeButtons(
        Dictionary<string, PadBinding> buttons,
        string where,
        List<string> notes
    )
    {
        foreach (var (control, binding) in buttons.ToList())
        {
            if (!Catalog.IsControl(control))
            {
                notes.Add($"{where}: control {control} unknown, dropped");
                buttons.Remove(control);
            }
            else if (binding is null || !binding.Normalize($"{where} {control}", notes))
                buttons.Remove(control);
        }
    }
}

/// <summary>What one control does: an action (ActionKind), its argument, repeat, and an optional long-press action.</summary>
sealed class PadBinding
{
    public string Action { get; set; } = ActionKind.None;

    /// <summary>"Ctrl+Shift+T" for a key action, a program, file or URL for run.</summary>
    public string? Arg { get; set; }

    /// <summary>Held, the action repeats (keys, scroll steps, volume steps).</summary>
    public bool Repeat { get; set; }

    /// <summary>Held LongPressMs: this runs instead of Action (which then runs on a short press).</summary>
    public PadBinding? Hold { get; set; }

    public static PadBinding Of(string action, string? arg = null, bool repeat = false) =>
        new()
        {
            Action = action,
            Arg = arg,
            Repeat = repeat,
        };

    /// <summary>False when it cannot run (the caller drops it); a broken long-press part is dropped alone.</summary>
    public bool Normalize(string where, List<string> notes)
    {
        if (!ActionKind.All.Contains(Action))
        {
            notes.Add($"{where}: action {Action} unknown, dropped");
            return false;
        }
        if (Action == ActionKind.Key && KeyCombo.Parse(Arg) is null)
        {
            notes.Add($"{where}: keys \"{Arg}\" not understood, dropped");
            return false;
        }
        if (Action == ActionKind.Run && string.IsNullOrWhiteSpace(Arg))
        {
            notes.Add($"{where}: run with nothing to run, dropped");
            return false;
        }
        if (
            Hold is not null
            && (Hold.Hold is not null || !Hold.Normalize($"{where} (long press)", notes))
        )
            Hold = null;
        return true;
    }
}

/// <summary>A hold-button layer: while Button is held, new presses use this map (SPEC "Layer 2").</summary>
sealed class Layer
{
    public string Name { get; set; } = "Layer 2";
    public string Button { get; set; } = "start";

    /// <summary>Also in apps that do not get the mouse (games).</summary>
    public bool Everywhere { get; set; } = true;

    /// <summary>The layer button alone, a short press.</summary>
    public PadBinding? Tap { get; set; }

    /// <summary>The layer button alone, held HoldMs.</summary>
    public PadBinding? Hold { get; set; }
    public int HoldMs { get; set; } = 2000;
    public Dictionary<string, PadBinding> Buttons { get; set; } = new();
}

/// <summary>An app with a fixed answer: one or several exe file names, always or never the mouse, its overrides and title rules.</summary>
sealed class AppRule
{
    public string Name { get; set; } = "";

    /// <summary>Exe file names, matched case-insensitively, never full paths (an update moves the exe).</summary>
    public List<string> Exes { get; set; } = new();

    /// <summary>Off: the rule is ignored and the app is automatic again.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>AppMode.Always (the mouse, even if it holds a controller open) or AppMode.Never.</summary>
    public string Mode { get; set; } = AppMode.Always;
    public string? LeftStick { get; set; }
    public string? RightStick { get; set; }

    /// <summary>Controls whose binding differs from the base map in this app.</summary>
    public Dictionary<string, PadBinding> Buttons { get; set; } = new();

    /// <summary>Window-title globs (* and ?, matched against the whole title): while one matches, the controller is off.</summary>
    public List<string> TitleOff { get; set; } = new();
}

static class AppMode
{
    public const string Always = "always",
        Never = "never";
}

/// <summary>
/// An app with no rule gets the mouse unless one of these says it is a game, or an app that reads
/// the controller itself (SPEC "Which apps get the mouse").
/// </summary>
sealed class Detection
{
    /// <summary>An app with a game controller open reads it itself (games, Moonlight, emulators, Steam).</summary>
    public bool ControllerApps { get; set; } = true;

    /// <summary>The games Windows itself recognized (its Game Bar and Game Mode list).</summary>
    public bool KnownGames { get; set; } = true;

    /// <summary>
    /// Parts of a path: an exe whose full path contains one is a game. Catches games that read the
    /// controller through a Windows service (Xbox app games) or that cannot be looked into (anti-cheat).
    /// </summary>
    public List<string> GameFolders { get; set; } =
        new()
        {
            @"\XboxGames\",
            @"\steamapps\common\",
            @"\Epic Games\",
            @"\GOG Games\",
            @"\Riot Games\",
        };
}

sealed class PadSetting
{
    public string Name { get; set; } = "";
    public bool Enabled { get; set; } = true;
}

/// <summary>Which pad families SDL reads through its direct (HIDAPI) drivers instead of RawInput (SPEC "Reading only").</summary>
sealed class Drivers
{
    public bool PlayStation { get; set; } = true;

    /// <summary>Off by default: SDL's direct Switch driver may switch the pad to a report mode other apps cannot read.</summary>
    public bool Switch { get; set; }
}

static class StickMode
{
    public const string Pointer = "pointer",
        Scroll = "scroll",
        Arrows = "arrows",
        Wasd = "wasd",
        None = "none";
}

/// <summary>Action ids (PadBinding.Action). Held actions last as long as the button; the others run once per press (or repeat).</summary>
static class ActionKind
{
    public const string None = "none",
        Key = "key",
        MouseLeft = "mouseLeft",
        MouseRight = "mouseRight",
        MouseMiddle = "mouseMiddle",
        MouseBack = "mouseBack",
        MouseForward = "mouseForward",
        Precise = "precise",
        ScrollUp = "scrollUp",
        ScrollDown = "scrollDown",
        ScrollLeft = "scrollLeft",
        ScrollRight = "scrollRight",
        VolumeUp = "volumeUp",
        VolumeDown = "volumeDown",
        Mute = "mute",
        PlayPause = "playPause",
        NextTrack = "nextTrack",
        PreviousTrack = "previousTrack",
        WindowLeft = "windowLeft",
        WindowRight = "windowRight",
        Maximize = "maximize",
        Minimize = "minimize",
        MinimizeAll = "minimizeAll",
        Keyboard = "keyboard",
        Run = "run",
        Pause = "pause",
        PowerOff = "powerOff";

    public static readonly string[] All =
    {
        None,
        Key,
        MouseLeft,
        MouseRight,
        MouseMiddle,
        MouseBack,
        MouseForward,
        Precise,
        ScrollUp,
        ScrollDown,
        ScrollLeft,
        ScrollRight,
        VolumeUp,
        VolumeDown,
        Mute,
        PlayPause,
        NextTrack,
        PreviousTrack,
        WindowLeft,
        WindowRight,
        Maximize,
        Minimize,
        MinimizeAll,
        Keyboard,
        Run,
        Pause,
        PowerOff,
    };

    /// <summary>Down while the control is down (a drag, a held key, the precise pointer).</summary>
    public static bool IsHeld(string action) =>
        action
            is Key
                or MouseLeft
                or MouseRight
                or MouseMiddle
                or MouseBack
                or MouseForward
                or Precise;

    /// <summary>Run by ActionRunner (system actions), not sent as input by the engine.</summary>
    public static bool IsSystem(string action) =>
        action
            is VolumeUp
                or VolumeDown
                or Mute
                or PlayPause
                or NextTrack
                or PreviousTrack
                or WindowLeft
                or WindowRight
                or Maximize
                or Minimize
                or MinimizeAll
                or Keyboard
                or Run
                or Pause
                or PowerOff;

    /// <summary>For the settings window.</summary>
    public static readonly Dictionary<string, string> Labels = new()
    {
        [None] = "Nothing",
        [Key] = "Key or shortcut",
        [MouseLeft] = "Left click (hold = drag)",
        [MouseRight] = "Right click",
        [MouseMiddle] = "Middle click",
        [MouseBack] = "Mouse back button",
        [MouseForward] = "Mouse forward button",
        [Precise] = "Hold: precise pointer",
        [ScrollUp] = "Scroll up",
        [ScrollDown] = "Scroll down",
        [ScrollLeft] = "Scroll left",
        [ScrollRight] = "Scroll right",
        [VolumeUp] = "Volume up",
        [VolumeDown] = "Volume down",
        [Mute] = "Mute / unmute",
        [PlayPause] = "Play / pause",
        [NextTrack] = "Next track",
        [PreviousTrack] = "Previous track",
        [WindowLeft] = "Move window to the screen on the left",
        [WindowRight] = "Move window to the screen on the right",
        [Maximize] = "Maximize / restore window",
        [Minimize] = "Minimize window",
        [MinimizeAll] = "Minimize all windows",
        [Keyboard] = "On-screen keyboard",
        [Run] = "Run a program, file or URL",
        [Pause] = "Pause / resume the controller tool",
        [PowerOff] = "Turn off the controller",
    };
}

/// <summary>Control ids, stick modes, and the order of PadControl.</summary>
static class Catalog
{
    /// <summary>The ids of PadControl, in its order (settings.json and the settings window use these).</summary>
    public static readonly string[] ControlIds =
    {
        "south",
        "east",
        "west",
        "north",
        "back",
        "guide",
        "start",
        "l3",
        "r3",
        "lb",
        "rb",
        "up",
        "down",
        "left",
        "right",
        "misc1",
        "paddleR1",
        "paddleL1",
        "paddleR2",
        "paddleL2",
        "touchpad",
        "lt",
        "rt",
    };

    public static readonly string[] StickModes =
    {
        StickMode.Pointer,
        StickMode.Scroll,
        StickMode.Arrows,
        StickMode.Wasd,
        StickMode.None,
    };

    static readonly Dictionary<string, PadControl> ByName = ControlIds
        .Select((id, i) => (id, i))
        .ToDictionary(p => p.id, p => (PadControl)p.i, StringComparer.OrdinalIgnoreCase);

    public static bool IsControl(string? id) => id is not null && ByName.ContainsKey(id);

    public static PadControl? ControlOf(string? id) =>
        id is not null && ByName.TryGetValue(id, out var c) ? c : null;

    public static string IdOf(PadControl c) => ControlIds[(int)c];

    /// <summary>Xbox-style names for the log and the settings window (which also shows the pad's own labels).</summary>
    public static readonly Dictionary<string, string> Labels = new()
    {
        ["south"] = "A (bottom)",
        ["east"] = "B (right)",
        ["west"] = "X (left)",
        ["north"] = "Y (top)",
        ["back"] = "View / Select / Share",
        ["guide"] = "Guide (Xbox / PS / Home)",
        ["start"] = "Menu / Start / Options",
        ["l3"] = "L3 (left stick click)",
        ["r3"] = "R3 (right stick click)",
        ["lb"] = "LB / L1",
        ["rb"] = "RB / R1",
        ["up"] = "D-pad up",
        ["down"] = "D-pad down",
        ["left"] = "D-pad left",
        ["right"] = "D-pad right",
        ["misc1"] = "Share / Capture / Mic",
        ["paddleR1"] = "Right paddle 1",
        ["paddleL1"] = "Left paddle 1",
        ["paddleR2"] = "Right paddle 2",
        ["paddleL2"] = "Left paddle 2",
        ["touchpad"] = "Touchpad click",
        ["lt"] = "LT / L2",
        ["rt"] = "RT / R2",
    };
}

/// <summary>
/// The controls, numbered as SDL's SDL_GamepadButton up to Touchpad (the input thread copies
/// SDL's buttons straight in), then the triggers. One bit each in the engine's masks.
/// </summary>
enum PadControl
{
    South,
    East,
    West,
    North,
    Back,
    Guide,
    Start,
    L3,
    R3,
    LB,
    RB,
    Up,
    Down,
    Left,
    Right,
    Misc1,
    PaddleR1,
    PaddleL1,
    PaddleR2,
    PaddleL2,
    Touchpad,
    LT,
    RT,
}

/// <summary>Key names in PadBinding.Arg ("Ctrl+Shift+T", "Win+M", "F11") and their virtual-key codes.</summary>
static class KeyCombo
{
    static readonly (string Name, ushort Vk)[] Table = Build();
    static readonly Dictionary<string, ushort> VkOf = Table.ToDictionary(
        k => k.Name,
        k => k.Vk,
        StringComparer.OrdinalIgnoreCase
    );

    /// <summary>Modifiers, in the order they are pressed and written: Ctrl, Alt, Shift, Win.</summary>
    public static readonly ushort[] Modifiers = { 0x11, 0x12, 0x10, 0x5B };

    public static IEnumerable<string> Names => Table.Select(k => k.Name);

    static (string, ushort)[] Build()
    {
        var keys = new List<(string, ushort)>
        {
            ("Ctrl", 0x11),
            ("Alt", 0x12),
            ("Shift", 0x10),
            ("Win", 0x5B),
            ("Enter", 0x0D),
            ("Space", 0x20),
            ("Esc", 0x1B),
            ("Tab", 0x09),
            ("Backspace", 0x08),
            ("Delete", 0x2E),
            ("Insert", 0x2D),
            ("Home", 0x24),
            ("End", 0x23),
            ("PageUp", 0x21),
            ("PageDown", 0x22),
            ("Left", 0x25),
            ("Up", 0x26),
            ("Right", 0x27),
            ("Down", 0x28),
            ("Menu", 0x5D),
            ("PrintScreen", 0x2C),
            ("Pause", 0x13),
            ("CapsLock", 0x14),
            ("NumLock", 0x90),
            ("ScrollLock", 0x91),
            ("Minus", 0xBD),
            ("Equal", 0xBB),
            ("Comma", 0xBC),
            ("Period", 0xBE),
            ("Slash", 0xBF),
            ("Semicolon", 0xBA),
            ("Quote", 0xDE),
            ("BracketLeft", 0xDB),
            ("BracketRight", 0xDD),
            ("Backslash", 0xDC),
            ("Backquote", 0xC0),
            ("NumPlus", 0x6B),
            ("NumMinus", 0x6D),
            ("NumMultiply", 0x6A),
            ("NumDivide", 0x6F),
            ("NumDecimal", 0x6E),
            ("BrowserBack", 0xA6),
            ("BrowserForward", 0xA7),
            ("BrowserRefresh", 0xA8),
            ("BrowserStop", 0xA9),
            ("BrowserSearch", 0xAA),
            ("BrowserFavorites", 0xAB),
            ("BrowserHome", 0xAC),
            ("MediaPlayPause", 0xB3),
            ("MediaNext", 0xB0),
            ("MediaPrev", 0xB1),
            ("MediaStop", 0xB2),
            ("VolumeMute", 0xAD),
            ("VolumeDown", 0xAE),
            ("VolumeUp", 0xAF),
        };
        for (var c = 'A'; c <= 'Z'; c++)
            keys.Add((c.ToString(), c));
        for (var c = '0'; c <= '9'; c++)
            keys.Add((c.ToString(), c));
        for (var n = 0; n <= 9; n++)
            keys.Add(($"Num{n}", (ushort)(0x60 + n)));
        for (var f = 1; f <= 24; f++)
            keys.Add(($"F{f}", (ushort)(0x6F + f)));
        return keys.ToArray();
    }

    /// <summary>
    /// "Ctrl+Shift+T" as virtual-key codes, modifiers first in Modifiers' order: any modifiers,
    /// then at most one other key, at least one key in all ("Win" alone is the Start menu). Null if
    /// it is not that.
    /// </summary>
    public static ushort[]? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;
        var codes = new List<ushort>();
        foreach (
            var part in text.Split(
                '+',
                StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries
            )
        )
        {
            if (!VkOf.TryGetValue(part, out var vk) || codes.Contains(vk))
                return null;
            codes.Add(vk);
        }
        var rest = codes.Where(c => !Modifiers.Contains(c)).ToList();
        if (codes.Count == 0 || rest.Count > 1)
            return null;
        return Modifiers.Where(codes.Contains).Concat(rest).ToArray();
    }
}

/// <summary>The built-in defaults (SPEC "Base map", "Layer 2", "Which apps get the mouse"): HTPC's Mouse preset, Start as layer 2.</summary>
static class Defaults
{
    public static ButtonLayout Base() =>
        new()
        {
            LeftStick = StickMode.Pointer,
            RightStick = StickMode.Scroll,
            Buttons = new()
            {
                ["south"] = PadBinding.Of(ActionKind.Key, "Enter"),
                ["east"] = PadBinding.Of(ActionKind.Key, "Esc"),
                ["west"] = PadBinding.Of(ActionKind.MouseLeft),
                ["north"] = PadBinding.Of(ActionKind.Key, "Space"),
                ["up"] = PadBinding.Of(ActionKind.Key, "Up", repeat: true),
                ["down"] = PadBinding.Of(ActionKind.Key, "Down", repeat: true),
                ["left"] = PadBinding.Of(ActionKind.Key, "Left", repeat: true),
                ["right"] = PadBinding.Of(ActionKind.Key, "Right", repeat: true),
                ["lb"] = PadBinding.Of(ActionKind.Key, "Alt+Left"),
                ["rb"] = PadBinding.Of(ActionKind.Key, "Alt+Right"),
                ["lt"] = PadBinding.Of(ActionKind.MouseRight),
                ["rt"] = PadBinding.Of(ActionKind.Precise),
                ["back"] = PadBinding.Of(ActionKind.Key, "Esc"),
                ["l3"] = PadBinding.Of(ActionKind.MouseMiddle),
                ["r3"] = PadBinding.Of(ActionKind.Keyboard),
            },
        };

    public static List<Layer> Layers() =>
        new()
        {
            new Layer
            {
                Name = "Layer 2",
                // Not Guide: Windows 11 opens Task View while Guide is held (owner's choice, 9 Nov 2026).
                Button = "start",
                Everywhere = true,
                // Start alone keeps its base-map job, in apps that get the mouse only (Engine).
                Tap = PadBinding.Of(ActionKind.PlayPause),
                HoldMs = 2000,
                Buttons = new()
                {
                    ["lt"] = PadBinding.Of(ActionKind.VolumeDown, repeat: true),
                    ["rt"] = PadBinding.Of(ActionKind.VolumeUp, repeat: true),
                    ["back"] = PadBinding.Of(ActionKind.Mute),
                    ["lb"] = PadBinding.Of(ActionKind.PreviousTrack),
                    ["rb"] = PadBinding.Of(ActionKind.NextTrack),
                    ["left"] = PadBinding.Of(ActionKind.WindowLeft),
                    ["right"] = PadBinding.Of(ActionKind.WindowRight),
                    ["up"] = PadBinding.Of(ActionKind.Maximize),
                    ["down"] = PadBinding.Of(ActionKind.Minimize),
                    ["north"] = PadBinding.Of(ActionKind.MinimizeAll),
                },
            },
        };

    // Only apps that keep the controller open without using it, so detection alone would leave
    // them out: checked on the owner's PC on 9 Nov 2026, Explorer, Chrome and Discord hold the
    // 8BitDo open (the other browsers have the same gamepad support for web pages); Start, Search,
    // Settings, Edge and VLC do not, or cannot be looked into, so they get the mouse anyway.
    public static List<AppRule> Apps() =>
        new()
        {
            new AppRule
            {
                Name = "Desktop and File Explorer",
                Exes = new() { "explorer.exe" },
            },
            new AppRule
            {
                Name = "Web browsers",
                Exes = new()
                {
                    "chrome.exe",
                    "msedge.exe",
                    "firefox.exe",
                    "brave.exe",
                    "opera.exe",
                    "vivaldi.exe",
                },
            },
            new AppRule
            {
                Name = "Discord",
                Exes = new() { "Discord.exe" },
            },
            // Steam's windows belong to steamwebhelper.exe, which does not hold the pad (steam.exe
            // does), so detection would give Big Picture the mouse: the desktop client gets it, Big
            // Picture, which Steam drives with the pad itself, is left alone.
            new AppRule
            {
                Name = "Steam",
                Exes = new() { "steamwebhelper.exe" },
                TitleOff = new() { "Steam Big Picture Mode*" },
            },
        };
}

/// <summary>
/// settings.json in %LOCALAPPDATA%\ControllerNavigation: read at start, written whole on each
/// save (a temporary file moved over the old one, so a crash never leaves half a file). A file that
/// cannot be read is kept as settings.bad.json and the defaults are used.
/// </summary>
sealed class SettingsStore
{
    public static string FilePath => Path.Combine(AppInfo.DataDir, "settings.json");

    public static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        WriteIndented = true,
    };

    volatile Settings current = new();

    /// <summary>The settings in use; never changed in place (Save publishes a new instance).</summary>
    public Settings Current => current;

    /// <summary>Raised after Save, on the saving thread.</summary>
    public event Action<Settings>? Changed;

    public void Load()
    {
        var path = FilePath;
        if (!File.Exists(path))
        {
            Log.Info("No settings file: built-in defaults");
            current = Normalized(new Settings());
            return;
        }
        try
        {
            var read =
                JsonSerializer.Deserialize<Settings>(File.ReadAllText(path), Json)
                ?? new Settings();
            current = Normalized(read);
            Log.Info($"Settings read from {path}");
        }
        catch (Exception e)
        {
            Log.Error($"Settings file unreadable, kept as settings.bad.json; defaults in use", e);
            try
            {
                File.Copy(path, Path.ChangeExtension(path, ".bad.json"), overwrite: true);
            }
            catch (Exception) { }
            current = Normalized(new Settings());
        }
    }

    /// <summary>Normalizes, writes and publishes the new settings; false (and nothing changed) if the file cannot be written.</summary>
    public bool Save(Settings settings)
    {
        var s = Normalized(settings);
        try
        {
            Directory.CreateDirectory(AppInfo.DataDir);
            var temp = FilePath + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(s, Json));
            File.Move(temp, FilePath, overwrite: true);
        }
        catch (Exception e)
        {
            Log.Error("Settings not saved", e);
            return false;
        }
        current = s;
        Log.Info("Settings saved");
        Changed?.Invoke(s);
        return true;
    }

    /// <summary>A deep copy through JSON: the settings window edits a copy, never Current.</summary>
    public static Settings Copy(Settings s) =>
        JsonSerializer.Deserialize<Settings>(JsonSerializer.Serialize(s, Json), Json)!;

    static Settings Normalized(Settings s)
    {
        foreach (var note in s.Normalize())
            Log.Warn("Settings: " + note);
        return s;
    }
}
