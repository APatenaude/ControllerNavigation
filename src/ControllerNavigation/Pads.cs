using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using SDL;
using static SDL.SDL3;

namespace ControllerNavigation;

/// <summary>
/// Reads every controller through SDL3 on the input thread (SPEC "Controllers: any pad"), merges
/// the enabled ones and feeds the engine at every poll, with the window in front. SDL is only
/// ever called from this thread; other threads ask through Post.
/// <para>
/// Reading only, never changing the pad (SPEC "Reading only"): enhanced reports off (SDL's
/// default switches PlayStation pads to reports that break DirectInput for other apps until the
/// pad is turned off), Home LEDs left alone, no rumble. Only enabled gamepads are opened: SDL's
/// DirectInput backend may take a device exclusively, which a game (a wheel with force feedback)
/// would then miss; a bare joystick is opened only while the mapping wizard reads it. SDL's own
/// events are off (nothing pumps a queue here): pads are found by listing them every 300 ms, and
/// their state is updated each poll.
/// </para>
/// </summary>
sealed unsafe class PadReader : IDisposable
{
    const int PollMs = 8,
        IdleMs = 50,
        ScanEveryMs = 300,
        StickRest = 8000,
        RawWatchMs = 2000;

    readonly Func<Settings> settings;
    readonly ForegroundTracker tracker;
    readonly Engine engine;
    readonly Thread thread;
    readonly ConcurrentQueue<Action> posted = new();
    readonly Dictionary<SDL_JoystickID, Pad> pads = new();
    volatile bool stopping;
    bool sdlUp;
    (bool PlayStation, bool Switch) drivers;
    Settings? padsSetWith;
    long nextScan;
    uint lastButtons;
    int xinputSlot = -1;

    sealed class Pad
    {
        public required SDL_JoystickID Id { get; init; }

        /// <summary>SDL knows its layout. Fixed for this object: a new mapping makes a new Pad.</summary>
        public required bool IsGamepad { get; init; }
        public required PadInfo Info { get; set; }
        public nint Gamepad; // SDL_Gamepad* while open (enabled gamepads only), else 0
        public nint RawJoystick; // SDL_Joystick* opened for the wizard on a bare joystick, else 0
        public int XInputSlot = -1;
        public uint LastButtons; // the last poll's, to tell which pad pressed something
    }

    public PadReader(Func<Settings> settings, ForegroundTracker tracker, Engine engine)
    {
        this.settings = settings;
        this.tracker = tracker;
        this.engine = engine;
        thread = new Thread(Run)
        {
            IsBackground = true,
            Name = "Input",
            Priority = ThreadPriority.Highest,
        };
    }

    public void Start() => thread.Start();

    public void Dispose()
    {
        stopping = true;
        if (thread.IsAlive)
            thread.Join(1000);
    }

    /// <summary>Pads connected or gone, or switched on or off. Raised on the input thread.</summary>
    public event Action? Changed;

    /// <summary>The connected pads (input thread writes, anyone reads).</summary>
    public IReadOnlyList<PadInfo> Connected => connected;
    volatile IReadOnlyList<PadInfo> connected = Array.Empty<PadInfo>();

    /// <summary>The enabled pads merged, as last polled.</summary>
    public PadLive Live => live.Value;
    volatile StrongBox<PadLive> live = new(default);

    /// <summary>Someone at a pad: a button, a trigger or a stick past rest (Environment.TickCount64; long.MinValue: never).</summary>
    public long LastInputTick => Interlocked.Read(ref lastInputTick);
    long lastInputTick = long.MinValue;

    /// <summary>The XInput slot of the pad that last pressed a button (-1: not an XInput pad), for power off.</summary>
    public int XInputSlot => xinputSlot;

    volatile string? rawWatch;
    long rawWatchUntil;
    volatile RawJoystick? rawLast;

    /// <summary>
    /// A pad's raw buttons, axes and hats for the mapping wizard. The first call starts watching it
    /// (read on the input thread from the next poll), so it returns null until then; it is watched
    /// while asked at least every 2 s.
    /// </summary>
    public RawJoystick? Raw(string padId)
    {
        rawWatch = padId;
        Interlocked.Exchange(ref rawWatchUntil, Environment.TickCount64 + RawWatchMs);
        return rawLast is { } r && r.Id == padId ? r : null;
    }

    /// <summary>Adds an SDL mapping string at once (the pad it names becomes a gamepad); false if SDL refuses it.</summary>
    public Task<bool> AddMapping(string mapping)
    {
        var done = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        Post(() =>
        {
            var result = SDL_AddGamepadMapping(mapping);
            if (result < 0)
                Log.Warn($"SDL refused the mapping: {SDL_GetError()}");
            else
                Log.Info($"Mapping added: {mapping}");
            nextScan = 0;
            done.TrySetResult(result >= 0);
        });
        return done.Task;
    }

    void Post(Action action) => posted.Enqueue(action);

    void Run()
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        while (!stopping)
        {
            try
            {
                var now = clock.ElapsedMilliseconds;
                var s = settings();
                if (!sdlUp || (s.Drivers.PlayStation, s.Drivers.Switch) != drivers)
                    StartSdl(s);
                while (posted.TryDequeue(out var action))
                    action();
                if (now >= nextScan)
                {
                    Scan(s);
                    nextScan = now + ScanEveryMs;
                }
                else if (s != padsSetWith)
                    Refresh(s);

                // With no pad open the window in front is still followed (the tray's "app that was
                // in front", the keyboard closing over a game), just less often.
                var open = pads.Values.Any(p => p.Gamepad != 0);
                if (open || rawWatch is not null)
                    SDL_UpdateGamepads();
                engine.Update(
                    open ? Merge(s) : new PadFrame(0, 0, 0, 0, 0, 0, 0, -1),
                    now,
                    tracker.Poll(now)
                );
                WatchRaw();
                Thread.Sleep(open ? PollMs : IdleMs);
            }
            catch (Exception e)
            {
                Log.Error("Input thread", e);
                Thread.Sleep(500);
            }
        }
        CloseAll();
        if (sdlUp)
            SDL_Quit();
    }

    void StartSdl(Settings s)
    {
        if (sdlUp)
        {
            CloseAll();
            SDL_Quit();
            sdlUp = false;
        }
        drivers = (s.Drivers.PlayStation, s.Drivers.Switch);
        SDL_SetHint(SDL_HINT_JOYSTICK_ENHANCED_REPORTS, "0");
        SDL_SetHint(SDL_HINT_JOYSTICK_ALLOW_BACKGROUND_EVENTS, "1");
        // Raw input on SDL's own thread: this one has no window message loop.
        SDL_SetHint(SDL_HINT_JOYSTICK_THREAD, "1");
        SDL_SetHint(SDL_HINT_JOYSTICK_HIDAPI_PS4, s.Drivers.PlayStation ? "1" : "0");
        SDL_SetHint(SDL_HINT_JOYSTICK_HIDAPI_PS5, s.Drivers.PlayStation ? "1" : "0");
        SDL_SetHint(SDL_HINT_JOYSTICK_HIDAPI_SWITCH, s.Drivers.Switch ? "1" : "0");
        SDL_SetHint(SDL_HINT_JOYSTICK_HIDAPI_JOY_CONS, s.Drivers.Switch ? "1" : "0");
        SDL_SetHint(SDL_HINT_JOYSTICK_HIDAPI_PS5_PLAYER_LED, "0");
        SDL_SetHint(SDL_HINT_JOYSTICK_HIDAPI_SWITCH_PLAYER_LED, "0");
        SDL_SetHint(SDL_HINT_JOYSTICK_HIDAPI_XBOX_360_PLAYER_LED, "0");
        // The Home LED hints are not set: "0" there would turn the LED off, unset leaves it alone.
        if (!SDL_Init(SDL_InitFlags.SDL_INIT_GAMEPAD))
        {
            Log.Error($"SDL did not start: {SDL_GetError()}");
            Thread.Sleep(5000);
            return;
        }
        SDL_SetGamepadEventsEnabled(false);
        SDL_SetJoystickEventsEnabled(false);
        foreach (var mapping in s.Mappings)
            if (SDL_AddGamepadMapping(mapping) < 0)
                Log.Warn($"Saved mapping refused by SDL: {SDL_GetError()} ({mapping})");
        sdlUp = true;
        nextScan = 0;
        Log.Info(
            $"SDL started (PlayStation direct driver {(s.Drivers.PlayStation ? "on" : "off")}, Switch {(s.Drivers.Switch ? "on" : "off")})"
        );
    }

    /// <summary>Lists the pads: new ones are described, gone ones closed, a joystick that a new mapping made a gamepad starts afresh.</summary>
    void Scan(Settings s)
    {
        SDL_UpdateGamepads();
        var seen = new HashSet<SDL_JoystickID>();
        using (var ids = SDL_GetJoysticks())
        {
            if (ids is not null)
                for (var i = 0; i < ids.Count; i++)
                {
                    var id = ids[i];
                    seen.Add(id);
                    var isGamepad = (bool)SDL_IsGamepad(id);
                    if (pads.TryGetValue(id, out var known) && known.IsGamepad == isGamepad)
                        continue;
                    if (known is not null)
                        Close(known);
                    var pad = new Pad
                    {
                        Id = id,
                        IsGamepad = isGamepad,
                        Info = Describe(id, isGamepad, s),
                    };
                    pads[id] = pad;
                    Log.Info(
                        $"Controller found: {pad.Info.Name} ({pad.Info.Type}, {pad.Info.Vendor:X4}:{pad.Info.Product:X4}{(pad.Info.Virtual ? ", virtual" : "")}{(pad.Info.Mapped ? "" : ", no layout yet")}, {(pad.Info.Enabled ? "on" : "off")}) {pad.Info.Path}"
                    );
                }
        }
        var gone = pads.Keys.Where(id => !seen.Contains(id)).ToList();
        foreach (var id in gone)
        {
            Log.Info($"Controller gone: {pads[id].Info.Name}");
            Close(pads[id]);
        }
        if (gone.Count > 0 && !pads.Values.Any(p => p.Gamepad != 0))
            engine.ReleaseAll();
        // SDL may give an Xbox pad its XInput slot only after it is opened.
        foreach (var pad in pads.Values.Where(p => p.Gamepad != 0))
            pad.XInputSlot = XInputSlotOf(pad);
        Refresh(s);
    }

    /// <summary>Opens an enabled gamepad, closes a disabled one (SDL then lets go of the device).</summary>
    void Sync(Pad pad)
    {
        var want = pad.IsGamepad && pad.Info.Enabled;
        if (want && pad.Gamepad == 0)
        {
            var g = SDL_OpenGamepad(pad.Id);
            if (g == null)
            {
                Log.Warn($"Could not open controller {pad.Info.Name}: {SDL_GetError()}");
                return;
            }
            pad.Gamepad = (nint)g;
            pad.XInputSlot = XInputSlotOf(pad);
            pad.LastButtons = 0;
        }
        else if (!want && pad.Gamepad != 0)
        {
            SDL_CloseGamepad((SDL_Gamepad*)pad.Gamepad);
            pad.Gamepad = 0;
            pad.XInputSlot = -1;
        }
    }

    int XInputSlotOf(Pad pad)
    {
        var g = (SDL_Gamepad*)pad.Gamepad;
        if (
            SDL_GetGamepadType(g)
            is not (
                SDL_GamepadType.SDL_GAMEPAD_TYPE_XBOX360
                or SDL_GamepadType.SDL_GAMEPAD_TYPE_XBOXONE
            )
        )
            return -1;
        return SDL_GetGamepadPlayerIndex(g) is var p and >= 0 and <= 3 ? p : -1;
    }

    void Close(Pad pad)
    {
        if (pad.Gamepad != 0)
            SDL_CloseGamepad((SDL_Gamepad*)pad.Gamepad);
        if (pad.RawJoystick != 0)
            SDL_CloseJoystick((SDL_Joystick*)pad.RawJoystick);
        pads.Remove(pad.Id);
    }

    void CloseAll()
    {
        foreach (var pad in pads.Values.ToList())
            Close(pad);
        engine.ReleaseAll();
    }

    /// <summary>The pads' on/off from the settings (opening or closing them), and the list for the settings window.</summary>
    void Refresh(Settings s)
    {
        padsSetWith = s;
        foreach (var pad in pads.Values)
        {
            pad.Info = pad.Info with { Enabled = EnabledIn(s, pad.Info.Id, pad.Info.Virtual) };
            Sync(pad);
        }
        var list = pads
            .Values.Select(p => p.Info)
            .OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (list.SequenceEqual(connected))
            return;
        connected = list;
        try
        {
            Changed?.Invoke();
        }
        catch (Exception e)
        {
            Log.Error("Pads changed", e);
        }
    }

    static bool EnabledIn(Settings s, string id, bool isVirtual) =>
        s.Pads.TryGetValue(id, out var p) ? p.Enabled : !isVirtual;

    /// <summary>A pad as SDL lists it, without opening it.</summary>
    static PadInfo Describe(SDL_JoystickID id, bool isGamepad, Settings s)
    {
        var guid = GuidOf(SDL_GetJoystickGUIDForID(id));
        ushort vendor = SDL_GetJoystickVendorForID(id),
            product = SDL_GetJoystickProductForID(id);
        var path = SDL_GetJoystickPathForID(id);
        var name =
            (isGamepad ? SDL_GetGamepadNameForID(id) : SDL_GetJoystickNameForID(id))
            ?? "Controller";
        var isVirtual =
            vendor == 0x28DE && product == 0x11FF // Steam Input's virtual pad
            || DeviceTree.IsVirtual(path);
        var type = isGamepad
            ? SDL_GetGamepadTypeForID(id)
            : SDL_GamepadType.SDL_GAMEPAD_TYPE_UNKNOWN;
        var labels = !isGamepad
            ? ""
            : string.Join(
                " ",
                new[]
                {
                    SDL_GamepadButton.SDL_GAMEPAD_BUTTON_SOUTH,
                    SDL_GamepadButton.SDL_GAMEPAD_BUTTON_EAST,
                    SDL_GamepadButton.SDL_GAMEPAD_BUTTON_WEST,
                    SDL_GamepadButton.SDL_GAMEPAD_BUTTON_NORTH,
                }.Select(b => LabelName(SDL_GetGamepadButtonLabelForType(type, b)))
            );
        return new PadInfo(
            guid,
            name,
            isGamepad ? TypeName(type) : "joystick",
            vendor,
            product,
            isVirtual,
            EnabledIn(s, guid, isVirtual),
            true,
            isGamepad,
            labels,
            path
        );
    }

    /// <summary>The enabled pads as one: buttons pressed on any, each stick from the pad that tilts it most, triggers the deepest.</summary>
    PadFrame Merge(Settings s)
    {
        uint buttons = 0;
        short lx = 0,
            ly = 0,
            rx = 0,
            ry = 0,
            lt = 0,
            rt = 0;
        double left = 0,
            right = 0;
        foreach (var pad in pads.Values)
        {
            if (pad.Gamepad == 0 || !pad.Info.Enabled)
                continue;
            var g = (SDL_Gamepad*)pad.Gamepad;
            uint b = 0;
            for (var i = 0; i <= (int)PadControl.Touchpad; i++)
                if (SDL_GetGamepadButton(g, (SDL_GamepadButton)i))
                    b |= 1u << i;
            if ((b & ~pad.LastButtons) != 0 && pad.XInputSlot >= 0)
                xinputSlot = pad.XInputSlot;
            else if ((b & ~pad.LastButtons) != 0)
                xinputSlot = -1;
            pad.LastButtons = b;
            buttons |= b;
            short x = SDL_GetGamepadAxis(g, SDL_GamepadAxis.SDL_GAMEPAD_AXIS_LEFTX),
                y = SDL_GetGamepadAxis(g, SDL_GamepadAxis.SDL_GAMEPAD_AXIS_LEFTY);
            if ((double)x * x + (double)y * y > left)
            {
                left = (double)x * x + (double)y * y;
                lx = x;
                ly = y;
            }
            x = SDL_GetGamepadAxis(g, SDL_GamepadAxis.SDL_GAMEPAD_AXIS_RIGHTX);
            y = SDL_GetGamepadAxis(g, SDL_GamepadAxis.SDL_GAMEPAD_AXIS_RIGHTY);
            if ((double)x * x + (double)y * y > right)
            {
                right = (double)x * x + (double)y * y;
                rx = x;
                ry = y;
            }
            lt = Math.Max(lt, SDL_GetGamepadAxis(g, SDL_GamepadAxis.SDL_GAMEPAD_AXIS_LEFT_TRIGGER));
            rt = Math.Max(
                rt,
                SDL_GetGamepadAxis(g, SDL_GamepadAxis.SDL_GAMEPAD_AXIS_RIGHT_TRIGGER)
            );
        }
        var press = 32767 * s.Feel.TriggerPercent / 100;
        var all =
            buttons
            | (lt >= press ? 1u << (int)PadControl.LT : 0)
            | (rt >= press ? 1u << (int)PadControl.RT : 0);
        if (
            all != 0
            || left > (double)StickRest * StickRest
            || right > (double)StickRest * StickRest
        )
            Interlocked.Exchange(ref lastInputTick, Environment.TickCount64);
        if (
            all != lastButtons
            || live.Value.LX != lx
            || live.Value.LY != ly
            || live.Value.RX != rx
            || live.Value.RY != ry
            || live.Value.LT != lt
            || live.Value.RT != rt
        )
            live = new StrongBox<PadLive>(new PadLive(all, lx, ly, rx, ry, lt, rt));
        lastButtons = all;
        return new PadFrame(buttons, lx, ly, rx, ry, lt, rt, xinputSlot);
    }

    void WatchRaw()
    {
        var id = rawWatch;
        if (id is not null && Environment.TickCount64 > Interlocked.Read(ref rawWatchUntil))
        {
            rawWatch = id = null;
            rawLast = null;
        }
        // A bare joystick is open only while the wizard reads it.
        foreach (var other in pads.Values.Where(p => p.RawJoystick != 0 && p.Info.Id != id))
        {
            SDL_CloseJoystick((SDL_Joystick*)other.RawJoystick);
            other.RawJoystick = 0;
        }
        if (id is null)
            return;
        var pad = pads.Values.FirstOrDefault(p => p.Info.Id == id);
        if (pad is not null && pad.Gamepad == 0 && pad.RawJoystick == 0)
            pad.RawJoystick = (nint)SDL_OpenJoystick(pad.Id);
        var j =
            pad is null ? null
            : pad.Gamepad != 0 ? SDL_GetGamepadJoystick((SDL_Gamepad*)pad.Gamepad)
            : (SDL_Joystick*)pad.RawJoystick;
        if (pad is null || j == null)
        {
            rawLast = null;
            return;
        }
        var buttons = new bool[Math.Max(0, SDL_GetNumJoystickButtons(j))];
        for (var i = 0; i < buttons.Length; i++)
            buttons[i] = SDL_GetJoystickButton(j, i);
        var axes = new short[Math.Max(0, SDL_GetNumJoystickAxes(j))];
        for (var i = 0; i < axes.Length; i++)
            axes[i] = SDL_GetJoystickAxis(j, i);
        var hats = new byte[Math.Max(0, SDL_GetNumJoystickHats(j))];
        for (var i = 0; i < hats.Length; i++)
            hats[i] = SDL_GetJoystickHat(j, i);
        rawLast = new RawJoystick(id, id, pad.Info.Name, buttons, axes, hats);
    }

    static string GuidOf(SDL_GUID guid)
    {
        var text = stackalloc byte[33];
        SDL_GUIDToString(guid, text, 33);
        return Marshal.PtrToStringUTF8((nint)text) ?? "";
    }

    static string LabelName(SDL_GamepadButtonLabel label) =>
        label switch
        {
            SDL_GamepadButtonLabel.SDL_GAMEPAD_BUTTON_LABEL_A => "A",
            SDL_GamepadButtonLabel.SDL_GAMEPAD_BUTTON_LABEL_B => "B",
            SDL_GamepadButtonLabel.SDL_GAMEPAD_BUTTON_LABEL_X => "X",
            SDL_GamepadButtonLabel.SDL_GAMEPAD_BUTTON_LABEL_Y => "Y",
            SDL_GamepadButtonLabel.SDL_GAMEPAD_BUTTON_LABEL_CROSS => "Cross",
            SDL_GamepadButtonLabel.SDL_GAMEPAD_BUTTON_LABEL_CIRCLE => "Circle",
            SDL_GamepadButtonLabel.SDL_GAMEPAD_BUTTON_LABEL_SQUARE => "Square",
            SDL_GamepadButtonLabel.SDL_GAMEPAD_BUTTON_LABEL_TRIANGLE => "Triangle",
            _ => "?",
        };

    static string TypeName(SDL_GamepadType type) =>
        type switch
        {
            SDL_GamepadType.SDL_GAMEPAD_TYPE_STANDARD => "standard",
            SDL_GamepadType.SDL_GAMEPAD_TYPE_XBOX360 => "xbox360",
            SDL_GamepadType.SDL_GAMEPAD_TYPE_XBOXONE => "xboxOne",
            SDL_GamepadType.SDL_GAMEPAD_TYPE_PS3 => "ps3",
            SDL_GamepadType.SDL_GAMEPAD_TYPE_PS4 => "ps4",
            SDL_GamepadType.SDL_GAMEPAD_TYPE_PS5 => "ps5",
            SDL_GamepadType.SDL_GAMEPAD_TYPE_NINTENDO_SWITCH_PRO => "switchPro",
            SDL_GamepadType.SDL_GAMEPAD_TYPE_NINTENDO_SWITCH_JOYCON_LEFT
            or SDL_GamepadType.SDL_GAMEPAD_TYPE_NINTENDO_SWITCH_JOYCON_RIGHT
            or SDL_GamepadType.SDL_GAMEPAD_TYPE_NINTENDO_SWITCH_JOYCON_PAIR => "joyCon",
            SDL_GamepadType.SDL_GAMEPAD_TYPE_GAMECUBE => "gameCube",
            SDL_GamepadType.SDL_GAMEPAD_TYPE_STEAM => "steam",
            _ => "unknown",
        };
}

/// <summary>
/// Whether a device is a software one (ViGEm, Parsec, a streaming host's pad), from where it hangs
/// in Windows' device tree: walking up from the device, a real pad reaches the PCI bus, ACPI or a
/// Bluetooth radio before the root; a virtual bus driver sits right under the root (ROOT\..., or
/// SWD\ for software devices). Unknown (no path, not in the tree): not virtual.
/// </summary>
static class DeviceTree
{
    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    static extern int CM_Locate_DevNodeW(out uint devInst, string deviceId, uint flags);

    [DllImport("cfgmgr32.dll")]
    static extern int CM_Get_Parent(out uint parent, uint devInst, uint flags);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    static extern int CM_Get_Device_IDW(uint devInst, StringBuilder buffer, int length, uint flags);

    public static bool IsVirtual(string? path)
    {
        if (InstanceIdOf(path) is not { } id || CM_Locate_DevNodeW(out var node, id, 0) != 0)
            return false;
        var text = new StringBuilder(512);
        for (var depth = 0; depth < 32 && CM_Get_Parent(out node, node, 0) == 0; depth++)
        {
            text.Clear();
            if (CM_Get_Device_IDW(node, text, text.Capacity, 0) != 0)
                return false;
            if (IsVirtualParent(text.ToString()) is { } verdict)
                return verdict;
        }
        return false;
    }

    /// <summary>True: a virtual bus; false: real hardware; null: keep walking up.</summary>
    internal static bool? IsVirtualParent(string id)
    {
        if (
            id.StartsWith(@"PCI\", StringComparison.OrdinalIgnoreCase)
            || id.StartsWith(@"ACPI\", StringComparison.OrdinalIgnoreCase)
            || id.StartsWith(@"BTH", StringComparison.OrdinalIgnoreCase)
            || id.StartsWith(@"HTREE\", StringComparison.OrdinalIgnoreCase)
        )
            return false;
        if (
            id.StartsWith(@"ROOT\", StringComparison.OrdinalIgnoreCase)
            || id.StartsWith(@"SWD\", StringComparison.OrdinalIgnoreCase)
        )
            return true;
        return null;
    }

    /// <summary>"\\?\HID#VID_045E&amp;PID_028E&amp;IG_00#3&amp;2a0e5a0f&amp;0&amp;0000#{4d1e55b2-...}" as the instance id "HID\VID_045E&amp;PID_028E&amp;IG_00\3&amp;2a0e5a0f&amp;0&amp;0000".</summary>
    internal static string? InstanceIdOf(string? path)
    {
        if (path is null || !path.StartsWith(@"\\?\", StringComparison.Ordinal))
            return null;
        var rest = path[4..];
        var guid = rest.LastIndexOf("#{", StringComparison.Ordinal);
        if (guid > 0)
            rest = rest[..guid];
        return rest.Replace('#', '\\');
    }
}
