using System.Runtime.InteropServices;

namespace ControllerNavigation;

/// <summary>
/// The enabled pads merged, one poll: SDL's buttons (one bit per PadControl up to Touchpad),
/// SDL's axes (sticks -32768..32767 with Y down positive, triggers 0..32767), and the XInput
/// slot of the pad that last pressed a button (-1: not an XInput pad), for power off.
/// </summary>
readonly record struct PadFrame(
    uint Buttons,
    short LX,
    short LY,
    short RX,
    short RY,
    short LT,
    short RT,
    int XInputSlot
);

/// <summary>What is in front, as the engine needs it: allowed (after the on-delay), its app rule (null: this tool's own window), its process.</summary>
readonly record struct Front(bool Allowed, AppRule? Rule, uint Process = 0);

/// <summary>
/// Turns the pad into mouse, keyboard and system actions (SPEC "Base map", "Layer 2",
/// "Configurability"). Fed every poll on the input thread; everything it does goes out through
/// IOutput.
/// <para>
/// Who a press belongs to is decided once, when the control goes down, and kept until it is
/// released: a layer if a layer button is held (the most recent one), else the base map if the
/// app in front gets the mouse (Front.Allowed), else nobody. So a button held before the layer
/// button keeps its base action, a button pressed under it stays a layer button even if the layer
/// button goes first (HTPC's Start-chord rule), and a button already down when the controller turns
/// on (the A that closed a game) does nothing until it is let go. Whatever the base map holds down
/// is let go the moment the app in front stops getting the mouse or its map changes.
/// </para>
/// <para>
/// A pair (Catalog.Pairs) bound in the map runs when its second button goes down while the first
/// is still open to it: waiting, for a first whose own action is a click or key (sent once the
/// pair window passes, or as a tap if let go sooner), or simply held, for any other (a volume
/// step, which then stops repeating). Both stay used up until each is released.
/// </para>
/// </summary>
sealed class Engine
{
    /// <summary>PadControl's 23 controls, then the sticks used as arrows or WASD: left up, down, left, right, then the right stick's.</summary>
    const int Controls = 31,
        FirstStickKey = 23;

    /// <summary>In apps without the mouse, a layer combo counts only when its button comes this long after the layer button.</summary>
    const int GameChordDelayMs = 150;
    const int VolumeRepeatDelayMs = 400;
    const sbyte NoOwner = -2,
        BaseOwner = -1,
        KeyboardOwner = -3,
        LayerButtonOwner = -4,
        MenuOwner = -5;

    readonly IOutput output;
    readonly IKeyboardSurface keyboard;
    readonly IMenuSurface menu;
    volatile Compiled compiled;
    Compiled? active; // what the last poll used: a new one lets go of everything
    readonly Slot[] slots = new Slot[Controls];
    readonly List<int> heldLayers = new(); // layer indexes whose button is down, oldest first
    LayerHold[] layerHolds = Array.Empty<LayerHold>();
    uint previous; // control bits of the last poll (bit i: slots[i])
    bool wasAllowed;
    Map? lastMap;
    Motion? lastMotion;
    bool preciseOn; // ActionKind.PreciseToggle, until pressed again or another app comes in front
    uint lastProcess;
    bool menuWasOpen;

    public Engine(IOutput output, IKeyboardSurface keyboard, IMenuSurface menu, Settings settings)
    {
        this.output = output;
        this.keyboard = keyboard;
        this.menu = menu;
        compiled = Compiled.From(settings);
    }

    /// <summary>New settings: used from the next poll on (anything held is let go then). Any thread.</summary>
    public void Apply(Settings settings) => compiled = Compiled.From(settings);

    /// <summary>Everything off (tray pause), layers included. Any thread.</summary>
    public volatile bool Paused;

    struct Slot
    {
        public sbyte Owner;
        public Act? Act; // running: held down, or repeating
        public Act? Pending; // has a long-press action: decided when released or held long enough
        public Act? Waiting; // a click or key held back for the pair window
        public bool PairOpen, // the partner going down now makes the pair
            Paired;
        public long DownAt,
            NextRepeat;
    }

    struct LayerHold
    {
        public long DownAt;
        public bool Used,
            HoldDone;
        public Act? HoldAct; // the layer's own hold action, when it is a held one (released with the button)
    }

    /// <summary>One poll (input thread).</summary>
    public void Update(in PadFrame f, long now, Front front)
    {
        var c = compiled;
        if (c != active)
        {
            // The menu let go of everything when it opened: a speed it saves keeps the precise toggle and a held D-pad.
            if (!menuWasOpen)
                ReleaseAll();
            active = c;
            layerHolds = new LayerHold[c.Layers.Length];
        }
        var feel = c.Feel;
        var paused = Paused;
        var allowed = front.Allowed && !paused;
        var map = front.Rule is { } rule && c.Apps.TryGetValue(rule, out var m) ? m : c.Base;
        // 0: nothing in front for a moment (a window closing, a UAC prompt), not another app.
        if (front.Process != 0 && front.Process != lastProcess)
        {
            lastProcess = front.Process;
            preciseOn = false;
        }

        if (menu.IsOpen)
        {
            MenuPoll(f, now, c, map, allowed);
            return;
        }
        menuWasOpen = false;

        if (paused)
        {
            ReleaseAll();
            var held = Bits(f, c, map, front.Allowed, previous);
            PausedPress(held & ~previous, held, c, map, front.Allowed);
            previous = held;
            SetMotion(null);
            return;
        }

        if (keyboard.IsOpen && !front.Allowed)
            keyboard.Close();
        if (wasAllowed && !allowed)
            ReleaseOwners(BaseOwner, KeyboardOwner);
        else if (allowed && lastMap is not null && map != lastMap)
            ReleaseOwners(BaseOwner);
        if (!allowed)
            ReleaseLayers(everywhereToo: false);
        wasAllowed = allowed;
        lastMap = map;

        var keyboardOpen = allowed && keyboard.IsOpen;
        var bits = Bits(f, c, map, allowed, previous, keyboardOpen);
        var changed = bits ^ previous;

        // Releases first, so a press in the same poll already sees the layer let go; then layer
        // buttons before the rest, so Guide and Y pressed in the same poll are a layer-2 Y.
        for (var i = 0; i < Controls; i++)
            if ((changed & (1u << i)) != 0 && (bits & (1u << i)) == 0)
                Released(i, now, c);
        for (var pass = 0; pass < 2; pass++)
        for (var i = 0; i < Controls; i++)
            if ((changed & bits & (1u << i)) != 0 && IsLayerButton(i, c) == (pass == 0))
                Pressed(i, now, c, map, allowed, keyboardOpen);

        for (var i = 0; i < Controls; i++)
            if ((bits & (1u << i)) != 0)
                Held(i, now, feel);
        for (var l = 0; l < c.Layers.Length; l++)
            if (heldLayers.Contains(l))
                LayerHeld(l, now, c, bits);
        previous = bits;

        SetMotion(allowed && !keyboardOpen ? MotionOf(f, map, feel) : null);
    }

    /// <summary>Lets go of everything held (input thread): pad gone, sleep, pause, new settings.</summary>
    public void ReleaseAll()
    {
        ReleaseOwners(BaseOwner, KeyboardOwner);
        ReleaseLayers(everywhereToo: true);
        // What is left: buttons pressed under a layer whose button was let go first (Guide + X, Guide released).
        for (var i = 0; i < Controls; i++)
        {
            if (slots[i].Act is { } act)
                Stop(act);
            if (slots[i].Owner != NoOwner)
                slots[i] = new Slot { Owner = NoOwner };
        }
        preciseOn = false;
        SetMotion(null);
    }

    static bool IsLayerButton(int i, Compiled c) => i < FirstStickKey && c.LayerOf[i] >= 0;

    /// <summary>Paused, a press bound to Pause still resumes and one bound to the menu opens it: under a held layer button, else in the base map of an app that gets the mouse.</summary>
    void PausedPress(uint pressed, uint held, Compiled c, Map map, bool allowed)
    {
        for (var i = 0; i < FirstStickKey; i++)
        {
            if ((pressed & (1u << i)) == 0 || IsLayerButton(i, c))
                continue;
            var layer = -1;
            for (var l = 0; l < c.Layers.Length; l++)
                if (
                    c.Layers[l].Button >= 0
                    && (held & (1u << c.Layers[l].Button)) != 0
                    && (allowed || c.Layers[l].Everywhere)
                )
                    layer = l;
            var act =
                layer >= 0 ? c.Layers[layer].Buttons[i]
                : allowed ? map.Buttons[i]
                : null;
            if (act?.Action is not (ActionKind.Pause or ActionKind.Menu))
                continue;
            output.System(act.Action, null);
            return;
        }
    }

    /// <summary>
    /// The menu is open: it gets every new button press (held D-pad directions repeat), nothing else
    /// runs. What was down when it opened (Start and R3) or when it closes (the A that picked) stays
    /// ignored until released. The sticks keep moving the pointer where the app gets the mouse.
    /// </summary>
    void MenuPoll(in PadFrame f, long now, Compiled c, Map map, bool allowed)
    {
        if (!menuWasOpen)
        {
            menuWasOpen = true;
            var precise = preciseOn;
            ReleaseAll();
            preciseOn = precise;
        }
        var bits = Bits(f, c, map, allowed, previous);
        for (var i = 0; i < FirstStickKey; i++)
        {
            ref var s = ref slots[i];
            if ((bits & (1u << i)) == 0)
                s = new Slot { Owner = NoOwner };
            else if ((previous & (1u << i)) == 0)
            {
                s = new Slot
                {
                    Owner = MenuOwner,
                    DownAt = now,
                    NextRepeat = now + c.Feel.RepeatDelayMs,
                };
                menu.Pad((PadControl)i, repeat: false);
            }
            else if (s.Owner == MenuOwner && IsDirection(i) && now >= s.NextRepeat)
            {
                s.NextRepeat = now + c.Feel.RepeatEveryMs;
                menu.Pad((PadControl)i, repeat: true);
            }
        }
        previous = bits;
        SetMotion(allowed && !keyboard.IsOpen ? MotionOf(f, map, c.Feel) : null);
    }

    void Pressed(int i, long now, Compiled c, Map map, bool allowed, bool keyboardOpen)
    {
        ref var s = ref slots[i];
        s = new Slot { Owner = NoOwner, DownAt = now };
        var layer = i < FirstStickKey ? c.LayerOf[i] : -1;
        if (layer >= 0 && (allowed || c.Layers[layer].Everywhere))
        {
            s.Owner = LayerButtonOwner;
            heldLayers.Remove(layer);
            heldLayers.Add(layer);
            layerHolds[layer] = new LayerHold { DownAt = now };
            return;
        }
        if (heldLayers.Count > 0)
        {
            var top = heldLayers[^1];
            layerHolds[top].Used = true;
            // In a game, a combo the game itself uses (Select + Start quits RetroBat) must not also
            // mute the PC: only a button pressed well after the layer button counts there.
            if (!allowed && now - layerHolds[top].DownAt < GameChordDelayMs)
                return;
            s.Owner = (sbyte)top;
            var act = i < FirstStickKey ? c.Layers[top].Buttons[i] : null;
            Begin(ref s, allowed || act is null ? act : SystemOnly(act), now, c.Feel);
            return;
        }
        if (!allowed)
            return;
        // The bumpers stay the volume over the keyboard.
        if (keyboardOpen && (PadControl)i is not (PadControl.LB or PadControl.RB))
        {
            s.Owner = KeyboardOwner;
            s.NextRepeat = now + c.Feel.RepeatDelayMs;
            keyboard.Pad(KeyboardControl(i), repeat: false);
            return;
        }
        s.Owner = BaseOwner;
        var own = map.Buttons[i];
        if (
            PartnerOf(i) is (int pair, int partner)
            && map.Pairs[pair] is { } both
            && !IsLayerButton(partner, c)
        )
        {
            ref var other = ref slots[partner];
            if (other.Owner == BaseOwner && other.PairOpen)
            {
                if (other.Act is { } running)
                    Stop(running);
                other.Act = other.Waiting = other.Pending = null;
                other.PairOpen = false;
                other.Paired = s.Paired = true;
                Start(both);
                s.Act = both;
                s.NextRepeat = now + Pace(both, c.Feel).Delay;
                return;
            }
            s.PairOpen = true;
            // A partner already down cannot come now: no reason to hold the click back.
            if (
                (previous & (1u << partner)) == 0
                && own is { Hold: null }
                && ActionKind.IsHeld(own.Action)
            )
            {
                s.Waiting = own;
                return;
            }
        }
        Begin(ref s, own, now, c.Feel);
    }

    void Begin(ref Slot s, Act? act, long now, Feel feel)
    {
        if (act is null)
            return;
        if (act.Hold is not null)
        {
            s.Pending = act;
            return;
        }
        Start(act);
        s.Act = act;
        s.NextRepeat = now + Pace(act, feel).Delay;
    }

    /// <summary>In a game a layer runs system actions only, each part on its own: Start + LB must not send Alt+Left to it.</summary>
    static Act? SystemOnly(Act act)
    {
        var hold = act.Hold is { } h && ActionKind.IsSystem(h.Action) ? h : null;
        var keep = ActionKind.IsSystem(act.Action);
        if (keep && hold == act.Hold)
            return act;
        if (!keep && hold is null)
            return null;
        return new Act
        {
            Action = keep ? act.Action : ActionKind.None,
            Arg = act.Arg,
            Repeat = act.Repeat,
            Keys = act.Keys,
            Mouse = act.Mouse,
            Hold = hold,
        };
    }

    /// <summary>A held volume button steps slower than a held key repeats.</summary>
    static (int Delay, int Every) Pace(Act act, Feel feel) =>
        act.Action is ActionKind.VolumeUp or ActionKind.VolumeDown
            ? (VolumeRepeatDelayMs, feel.VolumeRepeatMs)
            : (feel.RepeatDelayMs, feel.RepeatEveryMs);

    static (int Pair, int Partner)? PartnerOf(int i)
    {
        for (var p = 0; p < Catalog.Pairs.Length; p++)
        {
            var (_, first, second) = Catalog.Pairs[p];
            if (i == (int)first)
                return (p, (int)second);
            if (i == (int)second)
                return (p, (int)first);
        }
        return null;
    }

    void Held(int i, long now, Feel feel)
    {
        ref var s = ref slots[i];
        if (s.Owner == KeyboardOwner)
        {
            // Held directions move on, held backspace (West) and cursor (LT/RT) go on deleting and moving.
            if (
                (
                    IsDirection(i)
                    || (PadControl)i is PadControl.West or PadControl.LT or PadControl.RT
                )
                && now >= s.NextRepeat
            )
            {
                s.NextRepeat = now + feel.RepeatEveryMs;
                keyboard.Pad(KeyboardControl(i), repeat: true);
            }
            return;
        }
        if (s.Waiting is { } waiting && now - s.DownAt >= feel.PairWindowMs)
        {
            s.Waiting = null;
            s.PairOpen = false;
            Begin(ref s, waiting, now, feel);
        }
        if (s.Pending is { } pending && now - s.DownAt >= feel.LongPressMs)
        {
            s.Pending = null;
            s.PairOpen = false;
            Start(pending.Hold!);
            s.Act = pending.Hold;
            s.NextRepeat = now + Pace(pending.Hold!, feel).Delay;
        }
        if (s.Act is { Repeat: true } act && now >= s.NextRepeat)
        {
            s.NextRepeat = now + Pace(act, feel).Every;
            Repeat(act);
        }
    }

    void Released(int i, long now, Compiled c)
    {
        ref var s = ref slots[i];
        if (s.Paired && PartnerOf(i) is (_, int partner))
        {
            ref var other = ref slots[partner];
            if (other.Paired && other.Act is { } both)
            {
                Stop(both);
                other.Act = null;
            }
        }
        if (s.Owner == LayerButtonOwner)
        {
            var layer = c.LayerOf[i];
            if (layer >= 0 && heldLayers.Remove(layer))
                LayerReleased(layer, now, c);
        }
        else if ((s.Pending ?? s.Waiting) is { } tap)
        {
            Start(tap);
            Stop(tap);
        } // let go before the long press or the pair window: the short action
        else if (s.Act is { } act)
            Stop(act);
        s = new Slot { Owner = NoOwner };
    }

    void LayerHeld(int l, long now, Compiled c, uint bits)
    {
        ref var h = ref layerHolds[l];
        var layer = c.Layers[l];
        // Any other control down, even one held since before (a throttle on RT): no tap, no hold (power off).
        if ((bits & ~(1u << layer.Button)) != 0)
            h.Used = true;
        if (h.Used || h.HoldDone || layer.Hold is null || now - h.DownAt < layer.HoldMs)
            return;
        h.HoldDone = true;
        Start(layer.Hold);
        if (ActionKind.IsHeld(layer.Hold.Action))
            h.HoldAct = layer.Hold;
    }

    void LayerReleased(int l, long now, Compiled c)
    {
        ref var h = ref layerHolds[l];
        var layer = c.Layers[l];
        if (h.HoldAct is { } held)
            Stop(held);
        // The tap is the button's own job, so it runs only where the base map does: Start pressed
        // in a game pauses the game, never the music too. On the on-screen keyboard it is the
        // keyboard's (Start there is Enter and close).
        else if (wasAllowed && !h.Used && !h.HoldDone && now - h.DownAt < c.Feel.LongPressMs)
        {
            if (keyboard.IsOpen)
                keyboard.Pad((PadControl)layer.Button, repeat: false);
            else if (layer.Tap is { } tap)
            {
                Start(tap);
                Stop(tap);
            }
        }
        h = default;
    }

    /// <summary>Lets go of the controls these owners hold; they stay ignored until released.</summary>
    void ReleaseOwners(params sbyte[] owners)
    {
        for (var i = 0; i < Controls; i++)
        {
            ref var s = ref slots[i];
            if (!owners.Contains(s.Owner))
                continue;
            if (s.Act is { } act)
                Stop(act);
            s = new Slot { Owner = NoOwner };
        }
    }

    /// <summary>Layers limited to apps that get the mouse go with them (everywhereToo false keeps the Everywhere ones).</summary>
    void ReleaseLayers(bool everywhereToo)
    {
        var c = active;
        if (c is null)
            return;
        foreach (var l in heldLayers.ToList())
        {
            if (!everywhereToo && c.Layers[l].Everywhere)
                continue;
            heldLayers.Remove(l);
            if (layerHolds[l].HoldAct is { } held)
                Stop(held);
            layerHolds[l] = default;
            for (var i = 0; i < Controls; i++)
            {
                ref var s = ref slots[i];
                if (s.Owner != l && !(s.Owner == LayerButtonOwner && c.LayerOf[i] == l))
                    continue;
                if (s.Act is { } act)
                    Stop(act);
                s = new Slot { Owner = NoOwner };
            }
        }
    }

    void Start(Act act)
    {
        switch (act.Action)
        {
            case ActionKind.Key:
                output.KeysDown(act.Keys!);
                break;
            case ActionKind.MouseLeft
            or ActionKind.MouseRight
            or ActionKind.MouseMiddle
            or ActionKind.MouseBack
            or ActionKind.MouseForward:
                output.Mouse(act.Mouse, true);
                break;
            case ActionKind.ScrollUp:
                output.Wheel(120, false);
                break;
            case ActionKind.ScrollDown:
                output.Wheel(-120, false);
                break;
            case ActionKind.ScrollRight:
                output.Wheel(120, true);
                break;
            case ActionKind.ScrollLeft:
                output.Wheel(-120, true);
                break;
            case ActionKind.PreciseToggle:
                preciseOn = !preciseOn;
                break;
            case ActionKind.Precise or ActionKind.None:
                break;
            default:
                output.System(act.Action, act.Arg);
                break;
        }
    }

    void Stop(Act act)
    {
        if (act.Action == ActionKind.Key)
            output.KeysUp(act.Keys!);
        else if (
            act.Action
            is ActionKind.MouseLeft
                or ActionKind.MouseRight
                or ActionKind.MouseMiddle
                or ActionKind.MouseBack
                or ActionKind.MouseForward
        )
            output.Mouse(act.Mouse, false);
    }

    /// <summary>A held key repeats its key-down (modifiers stay down), like a keyboard; the others run again.</summary>
    void Repeat(Act act)
    {
        if (act.Action == ActionKind.Key)
            output.KeysDown(new[] { act.Keys![^1] });
        else if (!ActionKind.IsHeld(act.Action))
            Start(act);
    }

    /// <summary>The controls down this poll: buttons, triggers (with some play so they do not flicker), sticks used as arrows or WASD.</summary>
    static uint Bits(
        in PadFrame f,
        Compiled c,
        Map map,
        bool allowed,
        uint was,
        bool keyboardOpen = false
    )
    {
        var bits = f.Buttons & ((1u << (int)PadControl.LT) - 1);
        var feel = c.Feel;
        var press = 32767 * feel.TriggerPercent / 100;
        bool Trigger(short value, PadControl t) =>
            value >= ((was & (1u << (int)t)) != 0 ? press / 2 : press);
        if (Trigger(f.LT, PadControl.LT))
            bits |= 1u << (int)PadControl.LT;
        if (Trigger(f.RT, PadControl.RT))
            bits |= 1u << (int)PadControl.RT;
        if (!allowed)
            return bits;
        var tilt = 32767 * feel.StickArrowPercent / 100;
        if (keyboardOpen || map.LeftStick is StickMode.Arrows or StickMode.Wasd)
            bits |= StickKeys(f.LX, f.LY, tilt, (was >> FirstStickKey) & 0xF) << FirstStickKey;
        if (!keyboardOpen && map.RightStick is StickMode.Arrows or StickMode.Wasd)
            bits |=
                StickKeys(f.RX, f.RY, tilt, (was >> (FirstStickKey + 4)) & 0xF)
                << (FirstStickKey + 4);
        return bits;
    }

    /// <summary>Up, down, left, right as 1, 2, 4, 8; a direction already down holds until the tilt drops to 3/4 of the threshold (no flicker).</summary>
    static uint StickKeys(short x, short y, int tilt, uint was)
    {
        int Tilt(uint bit) => (was & bit) != 0 ? tilt * 3 / 4 : tilt;
        uint bits = 0;
        if (y < -Tilt(1))
            bits |= 1; // up (SDL's Y grows downwards)
        if (y > Tilt(2))
            bits |= 2;
        if (x < -Tilt(4))
            bits |= 4;
        if (x > Tilt(8))
            bits |= 8;
        return bits;
    }

    static bool IsDirection(int i) =>
        i >= FirstStickKey
        || (PadControl)i is PadControl.Up or PadControl.Down or PadControl.Left or PadControl.Right;

    /// <summary>The keyboard navigates with the D-pad and the left stick alike.</summary>
    static PadControl KeyboardControl(int i) =>
        i < FirstStickKey
            ? (PadControl)i
            : ((i - FirstStickKey) % 4) switch
            {
                0 => PadControl.Up,
                1 => PadControl.Down,
                2 => PadControl.Left,
                _ => PadControl.Right,
            };

    Motion? MotionOf(in PadFrame f, Map map, Feel feel)
    {
        var precise = preciseOn;
        foreach (var s in slots)
        {
            if (s.Act?.Action == ActionKind.Precise)
                precise = true;
            // A click held back for its pair starts where it was aimed: the pointer waits too.
            if (s.Waiting is not null)
                return null;
        }
        bool Moves(string mode, short x, short y, int deadzone) =>
            mode is StickMode.Pointer or StickMode.Scroll
            && (double)x * x + (double)y * y > (double)deadzone * deadzone;
        return
            Moves(map.LeftStick, f.LX, f.LY, feel.LeftDeadzone)
            || Moves(map.RightStick, f.RX, f.RY, feel.RightDeadzone)
            ? new Motion(map.LeftStick, map.RightStick, f.LX, f.LY, f.RX, f.RY, precise, feel)
            : null;
    }

    void SetMotion(Motion? m)
    {
        if (m == lastMotion)
            return;
        lastMotion = m;
        output.Motion(m);
    }

    /// <summary>A binding made ready to run: its keys parsed, its mouse button picked.</summary>
    sealed class Act
    {
        public required string Action { get; init; }
        public string? Arg { get; init; }
        public bool Repeat { get; init; }
        public ushort[]? Keys { get; init; }
        public Input.Button Mouse { get; init; }
        public Act? Hold { get; init; }

        public static Act? From(PadBinding? b, bool allowHold = true)
        {
            if (b is null || b.Action == ActionKind.None && b.Hold is null)
                return null;
            return new Act
            {
                Action = b.Action,
                Arg = b.Arg,
                Repeat = b.Repeat,
                Keys = b.Action == ActionKind.Key ? KeyCombo.Parse(b.Arg) : null,
                Mouse = b.Action switch
                {
                    ActionKind.MouseRight => Input.Button.Right,
                    ActionKind.MouseMiddle => Input.Button.Middle,
                    ActionKind.MouseBack => Input.Button.Back,
                    ActionKind.MouseForward => Input.Button.Forward,
                    _ => Input.Button.Left,
                },
                Hold = allowHold ? From(b.Hold, allowHold: false) : null,
            };
        }

        public static Act KeyAct(string combo, bool repeat) =>
            From(PadBinding.Of(ActionKind.Key, combo, repeat))!;
    }

    /// <summary>A map made ready: what each control does (stick keys included), each pair (Catalog.Pairs), what the sticks do.</summary>
    sealed class Map
    {
        public required string LeftStick { get; init; }
        public required string RightStick { get; init; }
        public required Act?[] Buttons { get; init; }
        public required Act?[] Pairs { get; init; }

        static readonly Act[] Arrows =
        {
            Act.KeyAct("Up", true),
            Act.KeyAct("Down", true),
            Act.KeyAct("Left", true),
            Act.KeyAct("Right", true),
        };
        static readonly Act[] Wasd =
        {
            Act.KeyAct("W", false),
            Act.KeyAct("S", false),
            Act.KeyAct("A", false),
            Act.KeyAct("D", false),
        };

        public static Map From(string left, string right, Dictionary<string, PadBinding> buttons)
        {
            var acts = new Act?[Controls];
            var pairs = new Act?[Catalog.Pairs.Length];
            foreach (var (id, binding) in buttons)
                if (Catalog.ControlOf(id) is { } control)
                    acts[(int)control] = Act.From(binding);
                else if (Catalog.PairOf(id) is { } pair)
                    pairs[pair] = Act.From(binding, allowHold: false);
            for (var d = 0; d < 4; d++)
            {
                acts[FirstStickKey + d] =
                    left == StickMode.Arrows ? Arrows[d]
                    : left == StickMode.Wasd ? Wasd[d]
                    : null;
                acts[FirstStickKey + 4 + d] =
                    right == StickMode.Arrows ? Arrows[d]
                    : right == StickMode.Wasd ? Wasd[d]
                    : null;
            }
            return new Map
            {
                LeftStick = left,
                RightStick = right,
                Buttons = acts,
                Pairs = pairs,
            };
        }
    }

    /// <param name="Button">The PadControl index of the layer's button (Settings.Normalize keeps only known ones).</param>
    sealed record CompiledLayer(
        int Button,
        bool Everywhere,
        Act? Tap,
        Act? Hold,
        int HoldMs,
        Act?[] Buttons
    );

    /// <summary>
    /// Settings made ready for the input thread. Apps without overrides share the base map's
    /// object, so moving between two of them is no map change: a drag carries on.
    /// </summary>
    sealed class Compiled
    {
        public required Feel Feel { get; init; }
        public required Map Base { get; init; }
        public required Dictionary<AppRule, Map> Apps { get; init; }
        public required CompiledLayer[] Layers { get; init; }

        /// <summary>Per PadControl: the index of the layer it is the button of, else -1 (the first layer wins a button used twice).</summary>
        public required int[] LayerOf { get; init; }

        public static Compiled From(Settings s)
        {
            var baseMap = Map.From(s.Base.LeftStick, s.Base.RightStick, s.Base.Buttons);
            var apps = new Dictionary<AppRule, Map>(ReferenceEqualityComparer.Instance);
            foreach (var app in s.Apps)
            {
                if (app.Buttons.Count == 0 && app.LeftStick is null && app.RightStick is null)
                {
                    apps[app] = baseMap;
                    continue;
                }
                var buttons = new Dictionary<string, PadBinding>(
                    s.Base.Buttons.Concat(app.Buttons).GroupBy(p => p.Key).Select(g => g.Last())
                );
                // An app that gives LT its own job does not inherit the base map's LT + RT.
                foreach (var (id, first, second) in Catalog.Pairs)
                    if (
                        !app.Buttons.ContainsKey(id)
                        && (
                            app.Buttons.ContainsKey(Catalog.IdOf(first))
                            || app.Buttons.ContainsKey(Catalog.IdOf(second))
                        )
                    )
                        buttons.Remove(id);
                apps[app] = Map.From(
                    app.LeftStick ?? s.Base.LeftStick,
                    app.RightStick ?? s.Base.RightStick,
                    buttons
                );
            }
            var layerOf = Enumerable.Repeat(-1, FirstStickKey).ToArray();
            var layers = new CompiledLayer[s.Layers.Count];
            for (var l = 0; l < s.Layers.Count; l++)
            {
                var layer = s.Layers[l];
                var acts = new Act?[FirstStickKey];
                foreach (var (id, binding) in layer.Buttons)
                    if (Catalog.ControlOf(id) is { } control)
                        acts[(int)control] = Act.From(binding);
                layers[l] = new CompiledLayer(
                    Catalog.ControlOf(layer.Button) is { } own ? (int)own : 0,
                    layer.Everywhere,
                    Act.From(layer.Tap),
                    Act.From(layer.Hold, allowHold: false),
                    layer.HoldMs,
                    acts
                );
                if (Catalog.ControlOf(layer.Button) is { } button && layerOf[(int)button] < 0)
                    layerOf[(int)button] = l;
            }
            return new Compiled
            {
                Feel = s.Feel,
                Base = baseMap,
                Apps = apps,
                Layers = layers,
                LayerOf = layerOf,
            };
        }
    }
}

/// <summary>
/// Moves the pointer and scrolls once per displayed frame, from a thread paced by the compositor
/// (DwmFlush), sized by the time since the last frame (lifted from HTPC's PadMapper: sent at the
/// poll rate against a 60 Hz screen it beat, and scrolling looked uneven). Sleeps while the
/// sticks rest.
/// </summary>
sealed class PointerFrames
{
    [DllImport("dwmapi.dll")]
    static extern int DwmFlush();

    // Full tilt before acceleration: 134 mm a second on a desk screen (0.4 heights of the owner's
    // 34-inch ultrawides), physical so that a scaled-up screen is not faster. A taller screen is
    // watched from farther away (a TV from the couch): x the square root of its height over a desk
    // screen's, 1.55 on a 65-inch TV (the owner, 26 Nov 2026: 134 there too slow, 2.4 times too fast).
    const double PointerMmPerSecond = 134;
    const double DeskScreenMm = 335; // also what a screen that reports no size counts as: 0.4 of its height a second
    const int SmallestMm = 100; // under it, no size or an EDID that holds only the aspect ratio (16 x 9 cm)
    const double ScrollSpeed = 2400; // wheel units per second at full tilt (20 notches)

    volatile Motion? motion;
    readonly ManualResetEventSlim moving = new(false);

    // Sub-pixel and sub-unit remainders, one per stick: both sticks may move the pointer.
    readonly (double X, double Y)[] remainders = new (double, double)[2];
    readonly EdgeBoost[] boosts = new EdgeBoost[2];

    public PointerFrames() =>
        new Thread(Frames)
        {
            IsBackground = true,
            Name = "Pointer frames",
            Priority = ThreadPriority.AboveNormal,
        }.Start();

    /// <summary>The sticks as last polled (null: at rest); any thread.</summary>
    public void Set(Motion? m)
    {
        motion = m;
        if (m is not null)
            moving.Set();
    }

    void Frames()
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        double last = -1;
        while (true)
        {
            moving.Wait();
            var m = motion;
            if (m is null)
            {
                moving.Reset();
                if (motion is not null)
                    moving.Set(); // moved again meanwhile
                remainders[0] = remainders[1] = default;
                boosts[0] = boosts[1] = default;
                last = -1;
                continue;
            }
            // Next frame. DwmFlush returns at once when nothing is being composed (display off):
            // then a plain 60 Hz pace.
            var before = clock.Elapsed.TotalMilliseconds;
            if (DwmFlush() != 0 || clock.Elapsed.TotalMilliseconds - before < 2)
                Thread.Sleep(16);
            // Read again after the wait: a game that came in front meanwhile must not get one last move.
            if ((m = motion) is null)
                continue;
            var now = clock.Elapsed.TotalMilliseconds;
            var dt = last < 0 ? 1 / 60.0 : Math.Clamp(now - last, 0, 50) / 1000.0;
            last = now;
            try
            {
                Stick(0, m.Left, m.LX, m.LY, m.Feel.LeftDeadzone, dt, now, m);
                Stick(1, m.Right, m.RX, m.RY, m.Feel.RightDeadzone, dt, now, m);
            }
            catch (Exception e)
            {
                Log.Error("Pointer frame", e);
            }
        }
    }

    void Stick(
        int stick,
        string mode,
        short x,
        short y,
        int deadzone,
        double dt,
        double now,
        Motion m
    )
    {
        ref var rest = ref remainders[stick];
        ref var boost = ref boosts[stick];
        var (cx, cy) = Curve(x, y, deadzone, m.Feel.Curve);
        if (cx == 0 && cy == 0 || mode is not (StickMode.Pointer or StickMode.Scroll))
        {
            rest = default;
            boost = default;
            return;
        }
        if (mode == StickMode.Pointer)
        {
            if (m.Precise)
                boost = default;
            var factor =
                m.Feel.PointerSpeed
                * (
                    m.Precise
                        ? m.Feel.PreciseSpeed
                        : boost.Factor(
                            Travel(x, y, deadzone),
                            now,
                            m.Feel.Acceleration,
                            m.Feel.AccelerationMs
                        )
                );
            var (pixels, mm) = Input.ScreenHeightAtPointer();
            var speed = FullTilt(pixels, mm) * dt * factor;
            rest.X += cx * speed;
            rest.Y += cy * speed; // SDL's Y grows downwards, as the screen's
            int dx = (int)rest.X,
                dy = (int)rest.Y;
            rest.X -= dx;
            rest.Y -= dy;
            Input.MoveBy(dx, dy);
        }
        else
        {
            var amount = ScrollSpeed * dt * m.Feel.ScrollSpeed;
            rest.X += cx * amount;
            rest.Y -= cy * amount; // stick up (negative Y) = wheel up (positive)
            int h = (int)rest.X,
                v = (int)rest.Y;
            rest.X -= h;
            rest.Y -= v;
            Input.Wheel(v);
            Input.Wheel(h, horizontal: true);
        }
    }

    /// <summary>Pixels a second at full tilt, before acceleration, on a screen this tall in pixels and millimetres (0: unknown).</summary>
    internal static double FullTilt(int pixels, int mm)
    {
        double height = mm >= SmallestMm ? mm : DeskScreenMm;
        return PointerMmPerSecond
            * Math.Sqrt(Math.Max(height, DeskScreenMm) / DeskScreenMm)
            * pixels
            / height;
    }

    /// <summary>0..1 past the dead zone, raised to the curve's power: small tilts slow and exact, full tilt quick.</summary>
    internal static (double X, double Y) Curve(short x, short y, int deadzone, double power)
    {
        var m = Travel(x, y, deadzone);
        if (m == 0)
            return (0, 0);
        var scale = Math.Pow(m, power) / Math.Sqrt((double)x * x + (double)y * y);
        return (x * scale, y * scale);
    }

    /// <summary>How far the stick is tilted past the dead zone, 0..1.</summary>
    internal static double Travel(short x, short y, int deadzone)
    {
        var magnitude = Math.Sqrt((double)x * x + (double)y * y);
        return magnitude <= deadzone || magnitude == 0
            ? 0
            : Math.Min(1, (magnitude - deadzone) / (32767.0 - deadzone));
    }

    /// <summary>
    /// Pointer acceleration: once the stick has stayed at the edge DwellMs, the speed ramps up to
    /// the Acceleration setting over rampMs (AccelerationMs), so a small move stays slow and a
    /// long one across the screen gets quick; off the edge it is back to 1 at once. Measured
    /// before the curve, and with some play (On / Off) so a pad whose gate stops short of full
    /// travel still reaches it. A straight line on purpose: an exponential climb (tried 26 Nov
    /// 2026) picked up too slowly.
    /// </summary>
    internal struct EdgeBoost
    {
        const double On = 0.9,
            Off = 0.85,
            DwellMs = 200;
        bool atEdge;
        double since;

        public double Factor(double travel, double now, double max, double rampMs)
        {
            if (travel < (atEdge ? Off : On))
            {
                atEdge = false;
                return 1;
            }
            if (!atEdge)
            {
                atEdge = true;
                since = now;
            }
            return 1 + (max - 1) * Math.Clamp((now - since - DwellMs) / rampMs, 0, 1);
        }
    }
}
