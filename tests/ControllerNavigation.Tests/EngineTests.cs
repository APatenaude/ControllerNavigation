namespace ControllerNavigation.Tests;

static class EngineTests
{
    /// <summary>What the engine asked for, one line each: "down Enter", "up Enter", "mouse Left down", "wheel 120", "system volumeUp".</summary>
    sealed class Recorder : IOutput
    {
        public readonly List<string> Calls = new();
        public Motion? LastMotion;

        public void KeysDown(ushort[] keys) => Calls.Add("down " + Names(keys));

        public void KeysUp(ushort[] keys) => Calls.Add("up " + Names(keys));

        public void Mouse(Input.Button button, bool down) =>
            Calls.Add($"mouse {button} {(down ? "down" : "up")}");

        public void Wheel(int amount, bool horizontal) =>
            Calls.Add($"wheel{(horizontal ? " h" : "")} {amount}");

        /// <summary>The menu the "menu" action opens, as TrayApp's output does.</summary>
        public FakeMenu? Menu;

        public void System(string action, string? arg)
        {
            Calls.Add("system " + action + (arg is null ? "" : " " + arg));
            if (action == ActionKind.Menu)
                Menu?.Open();
        }

        public void Motion(Motion? motion) => LastMotion = motion;

        public List<string> Take()
        {
            var c = Calls.ToList();
            Calls.Clear();
            return c;
        }

        static string Names(ushort[] keys) =>
            string.Join(
                "+",
                keys.Select(k => KeyCombo.Names.First(n => KeyCombo.Parse(n) is [var v] && v == k))
            );
    }

    sealed class FakeKeyboard : IKeyboardSurface
    {
        public bool Open;
        public readonly List<string> Presses = new();
        public bool IsOpen => Open;

        public void Toggle() => Open = !Open;

        public void Close() => Open = false;

        public void Pad(PadControl control, bool repeat) =>
            Presses.Add(control + (repeat ? " repeat" : ""));
    }

    sealed class FakeMenu : IMenuSurface
    {
        public bool Shown;
        public readonly List<string> Presses = new();
        public bool IsOpen => Shown;

        public void Open() => Shown = true;

        public void Pad(PadControl control, bool repeat) =>
            Presses.Add(control + (repeat ? " repeat" : ""));
    }

    /// <summary>An engine and a clock: Poll sends a frame at the current time, then moves the clock on.</summary>
    sealed class Rig
    {
        public readonly Recorder Out = new();
        public readonly FakeKeyboard Keyboard = new();
        public readonly FakeMenu Menu = new();
        public readonly Engine Engine;
        public readonly Settings Settings;
        public long Now = 1000;
        public bool Allowed = true;
        public AppRule? Rule;
        public uint Process;

        public Rig(Settings? settings = null)
        {
            Settings = settings ?? GuideLayer();
            Settings.Normalize();
            Out.Menu = Menu;
            Engine = new Engine(Out, Keyboard, Menu, Settings);
        }

        public void Poll(
            uint buttons,
            long advance = 8,
            short lx = 0,
            short ly = 0,
            short lt = 0,
            short rt = 0
        )
        {
            Engine.Update(
                new PadFrame(buttons, lx, ly, 0, 0, lt, rt, 1),
                Now,
                new Front(Allowed, Rule, Process)
            );
            Now += advance;
        }

        /// <summary>Holds these buttons for ms milliseconds, polling every 8 ms.</summary>
        public void Hold(uint buttons, int ms, short lt = 0, short rt = 0)
        {
            for (var t = 0; t < ms; t += 8)
                Poll(buttons, lt: lt, rt: rt);
        }
    }

    /// <summary>
    /// The first defaults (layer 2 on Guide, held 2 s = power off, Menu = play/pause, volume on
    /// layer 2's triggers, no pairs): most groups check the engine's rules with it; "Engine: Start
    /// as layer 2" and "Engine: one map with the HTPC" check today's defaults.
    /// </summary>
    static Settings GuideLayer()
    {
        var s = new Settings();
        var layer = s.Layers[0];
        layer.Button = "guide";
        layer.Tap = null;
        layer.Hold = PadBinding.Of(ActionKind.PowerOff);
        layer.Buttons["start"] = PadBinding.Of(ActionKind.PlayPause);
        layer.Buttons["lt"] = PadBinding.Of(ActionKind.VolumeDown, repeat: true);
        layer.Buttons["rt"] = PadBinding.Of(ActionKind.VolumeUp, repeat: true);
        layer.Buttons["back"] = PadBinding.Of(ActionKind.Mute);
        s.Base.Buttons["start"] = PadBinding.Of(ActionKind.PlayPause);
        s.Base.Buttons["lb"] = PadBinding.Of(ActionKind.Key, "Alt+Left");
        s.Base.Buttons["rb"] = PadBinding.Of(ActionKind.Key, "Alt+Right");
        s.Base.Buttons["rt"] = PadBinding.Of(ActionKind.Precise);
        s.Base.Buttons["l3"] = PadBinding.Of(ActionKind.MouseMiddle);
        s.Base.Buttons.Remove("lb+rb");
        s.Base.Buttons.Remove("lt+rt");
        return s;
    }

    static uint B(params PadControl[] controls) =>
        controls.Aggregate(0u, (bits, c) => bits | 1u << (int)c);

    const short Full = 32767;

    public static void Run()
    {
        T.Group(
            "Engine: base map",
            () =>
            {
                var r = new Rig();
                r.Poll(0);
                r.Poll(B(PadControl.South));
                r.Poll(0);
                T.Check(r.Out.Take() is ["down Enter", "up Enter"], "A is Enter, down then up");

                r.Poll(B(PadControl.West));
                r.Poll(B(PadControl.West));
                r.Poll(0);
                T.Check(
                    r.Out.Take() is ["mouse Left down", "mouse Left up"],
                    "X is a left click held as long as X (a drag)"
                );

                r.Poll(B(PadControl.LB));
                r.Poll(0);
                T.Check(
                    r.Out.Take() is ["down Alt+Left", "up Alt+Left"],
                    "LB is Alt+Left, held together"
                );

                r.Poll(B(PadControl.Start));
                r.Poll(0);
                T.Check(r.Out.Take() is ["system playPause"], "Menu is play/pause, once");
            }
        );

        T.Group(
            "Engine: repeat",
            () =>
            {
                var r = new Rig();
                r.Poll(0);
                r.Hold(B(PadControl.Down), 350 + 60 * 3 + 4);
                r.Poll(0);
                var calls = r.Out.Take();
                T.Check(
                    calls.First() == "down Down" && calls.Last() == "up Down",
                    "D-pad down: arrow down, then up"
                );
                T.Equal(
                    4,
                    calls.Count(c => c == "down Down"),
                    "held: the key-down repeats after 350 ms, then every 60 ms"
                );
            }
        );

        T.Group(
            "Engine: off where the app does not get the mouse, inherited buttons",
            () =>
            {
                var r = new Rig { Allowed = false };
                r.Poll(B(PadControl.South));
                r.Poll(0);
                T.Check(r.Out.Take().Count == 0, "a game in front: A does nothing");

                r.Poll(B(PadControl.South)); // A held in the game...
                r.Allowed = true;
                r.Poll(B(PadControl.South)); // ...the desktop comes forward with A still down
                r.Poll(0);
                T.Check(
                    r.Out.Take().Count == 0,
                    "the A that closed the game does not press Enter on the desktop"
                );
                r.Poll(B(PadControl.South));
                r.Poll(0);
                T.Check(r.Out.Take() is ["down Enter", "up Enter"], "a new press does");

                r.Poll(B(PadControl.West));
                r.Allowed = false;
                r.Poll(B(PadControl.West));
                T.Check(
                    r.Out.Take() is ["mouse Left down", "mouse Left up"],
                    "a drag is let go the moment the app stops being allowed"
                );
                r.Poll(0);
                T.Check(r.Out.Take().Count == 0, "and nothing more on release");
            }
        );

        T.Group(
            "Engine: layer 2 on Guide",
            () =>
            {
                var r = new Rig { Allowed = false };
                r.Hold(B(PadControl.Guide), 200); // in a game, a combo needs the layer button held first
                r.Hold(B(PadControl.Guide, PadControl.RT), 100, rt: Full);
                r.Poll(B(PadControl.Guide));
                r.Poll(0);
                T.Check(
                    r.Out.Take() is ["system volumeUp"],
                    "in a game, Guide + RT is volume up (layers work everywhere)"
                );

                r.Hold(B(PadControl.Guide), 200);
                r.Hold(B(PadControl.Guide), 100, rt: Full);
                r.Hold(B(PadControl.Guide), 500, rt: Full);
                T.Equal(
                    1 + 2,
                    r.Out.Take().Count(c => c == "system volumeUp"),
                    "held 600 ms, volume up repeats at 400 and 525 ms (slower than keys)"
                );
                r.Poll(0);

                r.Allowed = true;
                r.Poll(B(PadControl.RT), rt: Full); // precise pointer, base map
                r.Poll(B(PadControl.RT, PadControl.Guide), rt: Full); // Guide pressed after RT
                r.Poll(B(PadControl.Guide));
                r.Poll(0);
                T.Check(
                    r.Out.Take().Count == 0,
                    "RT held before Guide keeps its base action (precise, nothing sent)"
                );

                r.Poll(B(PadControl.Guide));
                r.Poll(B(PadControl.Guide, PadControl.LT), lt: Full);
                r.Poll(B(PadControl.LT), lt: Full); // Guide let go first
                r.Poll(0);
                T.Check(
                    r.Out.Take() is ["system volumeDown"],
                    "LT pressed under Guide stays a layer button when Guide goes first: no right click"
                );

                r.Poll(B(PadControl.Guide));
                r.Poll(B(PadControl.Guide, PadControl.South));
                r.Poll(0);
                T.Check(
                    r.Out.Take().Count == 0,
                    "a button with no layer-2 action does nothing under Guide (no base Enter)"
                );
            }
        );

        T.Group(
            "Engine: Start as layer 2 (today's defaults)",
            () =>
            {
                var r = new Rig(new Settings());
                r.Poll(0);
                r.Poll(B(PadControl.Start));
                r.Poll(0);
                T.Check(
                    r.Out.Take() is ["system playPause"],
                    "Start alone in an app with the mouse: play/pause, on release"
                );

                r.Poll(B(PadControl.Start));
                r.Hold(B(PadControl.Start), 100, rt: Full);
                r.Poll(0);
                T.Check(
                    r.Out.Take() is ["system nextTrack"],
                    "Start + RT: next track, no play/pause"
                );
                r.Poll(B(PadControl.Start));
                r.Poll(B(PadControl.Start, PadControl.LB));
                r.Poll(0);
                T.Check(
                    r.Out.Take() is ["down Alt+Left", "up Alt+Left"],
                    "Start + LB: back (Alt+Left)"
                );

                r.Allowed = false;
                r.Poll(B(PadControl.Start));
                r.Poll(0);
                T.Check(
                    r.Out.Take().Count == 0,
                    "Start alone in a game: nothing (the game pauses, not the music)"
                );
                r.Poll(B(PadControl.Start), rt: Full);
                r.Poll(0);
                r.Poll(B(PadControl.Start));
                r.Poll(B(PadControl.Start), rt: Full);
                r.Poll(0);
                T.Check(
                    r.Out.Take().Count == 0,
                    "in a game, the game's own Start + RT (pressed together or quickly) does nothing here"
                );
                r.Hold(B(PadControl.Start), 200);
                r.Hold(B(PadControl.Start), 50, rt: Full);
                r.Poll(0);
                T.Check(
                    r.Out.Take() is ["system nextTrack"],
                    "Start held, then RT, in a game: next track"
                );
                r.Hold(B(PadControl.Start), 200);
                r.Hold(B(PadControl.Start, PadControl.LB), 50);
                r.Poll(0);
                T.Check(
                    r.Out.Take().Count == 0,
                    "in a game, Start + LB sends the game no Alt+Left (layers run system actions only there)"
                );

                r.Allowed = true;
                r.Poll(0);
                r.Keyboard.Open = true;
                r.Poll(B(PadControl.Start));
                r.Poll(0);
                T.Check(
                    r.Out.Take().Count == 0 && r.Keyboard.Presses.LastOrDefault() == "Start",
                    "on-screen keyboard open: Start goes to the keyboard (Enter and close), no play/pause"
                );
                r.Keyboard.Open = false;

                r.Allowed = true;
                r.Hold(B(PadControl.Guide), 2500);
                r.Poll(0);
                T.Check(r.Out.Take().Count == 0, "Guide is left to Windows: nothing, held or not");
            }
        );

        T.Group(
            "Engine: one map with the HTPC (today's defaults)",
            () =>
            {
                var r = new Rig(new Settings());
                r.Poll(0);
                r.Poll(0, rt: Full);
                T.Check(r.Out.Take().Count == 0, "RT waits for LT a moment");
                r.Hold(0, 100, rt: Full);
                r.Poll(0);
                T.Check(
                    r.Out.Take() is ["mouse Left down", "mouse Left up"],
                    "RT is a left click once the pair window passes, held as long as RT"
                );
                r.Poll(0, rt: Full);
                r.Poll(0, rt: Full);
                r.Poll(0);
                T.Check(
                    r.Out.Take() is ["mouse Left down", "mouse Left up"],
                    "RT let go inside the window: a click"
                );

                r.Poll(0, lx: 20000, rt: Full);
                T.Check(r.Out.LastMotion is null, "the pointer waits with the click");
                r.Hold(0, 100, rt: Full);
                r.Poll(0, lx: 20000, rt: Full);
                T.Check(
                    r.Out.LastMotion is { LX: 20000 },
                    "and moves again once the click is down (a drag from where it was aimed)"
                );
                r.Poll(0);
                r.Out.Take();

                r.Poll(0, lt: Full);
                r.Hold(0, 40, lt: Full, rt: Full);
                T.Check(r.Out.Take() is ["mouse Middle down"], "LT + RT together: middle click");
                r.Hold(0, 200, lt: Full);
                r.Poll(0);
                T.Check(
                    r.Out.Take() is ["mouse Middle up"],
                    "let go with the first trigger; the other then does nothing"
                );

                r.Poll(B(PadControl.LB));
                T.Check(r.Out.Take() is ["system volumeDown"], "LB: volume down at once");
                r.Hold(B(PadControl.LB), 540);
                r.Poll(0);
                T.Equal(
                    2,
                    r.Out.Take().Count(c => c == "system volumeDown"),
                    "held: it steps again at 400 and 525 ms"
                );

                r.Poll(B(PadControl.LB));
                r.Poll(B(PadControl.LB, PadControl.RB));
                r.Hold(B(PadControl.LB, PadControl.RB), 800);
                r.Hold(B(PadControl.LB), 300);
                r.Poll(0);
                T.Check(
                    r.Out.Take() is ["system volumeDown", "system mute"],
                    "LB, then RB with it: mute, once, and LB stops stepping"
                );

                r.Poll(B(PadControl.L3));
                r.Poll(0);
                r.Poll(0, lx: 20000);
                T.Check(r.Out.LastMotion is { Precise: true }, "L3 turns precise on");
                r.Poll(B(PadControl.L3));
                r.Poll(0, lx: 20000);
                T.Check(r.Out.LastMotion is { Precise: false }, "and off");
                r.Poll(B(PadControl.L3));
                r.Poll(0);
                r.Process = 42;
                r.Poll(0, lx: 20000);
                T.Check(
                    r.Out.LastMotion is { Precise: false },
                    "another app in front: precise is off again"
                );
                r.Poll(0);
                r.Out.Take();

                r.Keyboard.Open = true;
                r.Poll(B(PadControl.RB));
                r.Poll(0);
                r.Hold(0, 350 + 60 + 4, lt: Full);
                r.Poll(0);
                T.Check(
                    r.Out.Take() is ["system volumeUp"]
                        && r.Keyboard.Presses.SequenceEqual(new[] { "LT", "LT repeat" }),
                    $"keyboard open: RB is still the volume, LT moves its cursor ({string.Join(", ", r.Keyboard.Presses)})"
                );
                r.Keyboard.Open = false;
            }
        );

        T.Group(
            "Engine: pairs and who owns a press",
            () =>
            {
                var s = new Settings();
                s.Apps[1].Buttons["lt"] = PadBinding.Of(ActionKind.Key, "F5");
                var r = new Rig(s) { Rule = s.Apps[1] };
                r.Poll(0);
                r.Poll(0, lt: Full);
                T.Check(
                    r.Out.Take() is ["down F5"],
                    "an app that gives LT its own job has no LT + RT: no wait"
                );
                r.Poll(0);
                r.Out.Take();

                var g = new Rig(new Settings()) { Allowed = false };
                g.Poll(0, lt: Full);
                g.Allowed = true;
                g.Poll(0, lt: Full);
                g.Hold(0, 100, lt: Full, rt: Full);
                g.Poll(0);
                T.Check(
                    g.Out.Take() is ["mouse Left down", "mouse Left up"],
                    "LT held since a game: RT is only a left click, no middle click"
                );

                var l = new Rig(new Settings());
                l.Poll(0);
                l.Poll(B(PadControl.Start));
                l.Poll(B(PadControl.Start), lt: Full);
                l.Poll(0, lt: Full); // Start let go: LT stays the layer's
                l.Hold(0, 100, lt: Full, rt: Full);
                l.Poll(0);
                T.Check(
                    l.Out.Take() is ["system previousTrack", "mouse Left down", "mouse Left up"],
                    "LT pressed under Start, RT after: previous track, then a left click"
                );

                l.Hold(0, 100, rt: Full);
                l.Out.Take();
                l.Poll(0, lt: Full, rt: Full);
                T.Check(
                    l.Out.Take() is ["mouse Right down"],
                    "LT during an RT drag: a right click at once, no wait"
                );
                l.Poll(0);
                l.Out.Take();

                var h = new Settings();
                h.Layers[0].Buttons["north"] = new PadBinding
                {
                    Action = ActionKind.Key,
                    Arg = "F1",
                    Hold = PadBinding.Of(ActionKind.MinimizeAll),
                };
                var game = new Rig(h) { Allowed = false };
                game.Hold(B(PadControl.Start), 200);
                game.Hold(B(PadControl.Start, PadControl.North), 100);
                game.Poll(B(PadControl.Start));
                game.Hold(B(PadControl.Start), 200);
                game.Hold(B(PadControl.Start, PadControl.North), 600);
                game.Poll(0);
                T.Check(
                    game.Out.Take() is ["system minimizeAll"],
                    "in a game, a layer key with a system long press: the key never, the long press still"
                );
            }
        );

        T.Group(
            "Engine: acceleration",
            () =>
            {
                var b = new PointerFrames.EdgeBoost();
                T.Equal(1.0, b.Factor(0.95, 0, 3, 1000), "at the edge: not at once");
                T.Equal(1.0, b.Factor(0.95, 200, 3, 1000), "nor after 200 ms");
                T.Check(
                    Math.Abs(b.Factor(0.95, 450, 3, 1000) - 1.5) < 1e-9
                        && Math.Abs(b.Factor(0.95, 700, 3, 1000) - 2) < 1e-9,
                    "a straight line: x1.5 a quarter of the way up a 1 s ramp, x2 half way"
                );
                T.Equal(3.0, b.Factor(0.87, 1200, 3, 1000), "full after 1.2 s, kept down to 85 %");
                T.Equal(1.0, b.Factor(0.8, 1210, 3, 1000), "off the edge: back to 1 at once");
                T.Equal(
                    1.0,
                    b.Factor(0.88, 1220, 3, 1000),
                    "back over 85 % but under 90 %: still 1"
                );
                T.Equal(
                    1.0,
                    new PointerFrames.EdgeBoost().Factor(1, 5000, 1, 1000),
                    "acceleration 1 is off"
                );
                double MmPerSecond(int pixels, int mm) =>
                    PointerFrames.FullTilt(pixels, mm) / pixels * mm;
                T.Check(
                    Math.Abs(MmPerSecond(1440, 334) - 134) < 1e-9
                        && Math.Abs(MmPerSecond(1080, 299) - 134) < 1e-9,
                    "134 mm a second on desk screens, a 24-inch one too (smaller: not slower)"
                );
                T.Check(
                    Math.Abs(
                        MmPerSecond(2160, 810) / MmPerSecond(1440, 334) - Math.Sqrt(810 / 335.0)
                    ) < 1e-9,
                    "a 65-inch TV at 200 %: x1.55, the square root of its height over a desk screen's"
                );
                T.Check(
                    Math.Abs(PointerFrames.FullTilt(1080, 0) / 1080 - 0.4) < 0.001
                        && PointerFrames.FullTilt(2160, 90) == PointerFrames.FullTilt(2160, 0),
                    "a screen that reports no size, or under 10 cm (an aspect ratio only): 0.4 of its height a second"
                );
                var quick = new PointerFrames.EdgeBoost();
                quick.Factor(1, 0, 3, 500);
                T.Check(
                    Math.Abs(quick.Factor(1, 450, 3, 500) - 2) < 1e-9,
                    "a 500 ms ramp: half way after 450 ms"
                );
            }
        );

        T.Group(
            "Engine: Guide alone",
            () =>
            {
                var r = new Rig();
                r.Poll(0);
                r.Hold(B(PadControl.Guide), 2100);
                r.Hold(B(PadControl.Guide), 500);
                r.Poll(0);
                T.Check(r.Out.Take() is ["system powerOff"], "held 2 s alone: power off, once");

                r.Poll(B(PadControl.Guide));
                r.Poll(0);
                T.Check(r.Out.Take().Count == 0, "a tap does nothing (no tap action by default)");

                r.Poll(B(PadControl.Guide));
                r.Poll(B(PadControl.Guide, PadControl.North));
                r.Hold(B(PadControl.Guide), 2100);
                r.Poll(0);
                T.Check(
                    r.Out.Take() is ["system minimizeAll"],
                    "Guide used with another button never powers off"
                );
            }
        );

        T.Group(
            "Engine: long press",
            () =>
            {
                var s = new Settings();
                s.Base.Buttons["north"] = new PadBinding
                {
                    Action = ActionKind.Key,
                    Arg = "Space",
                    Hold = PadBinding.Of(ActionKind.Key, "Tab"),
                };
                var r = new Rig(s);
                r.Poll(0);
                r.Hold(B(PadControl.North), 200);
                r.Poll(0);
                T.Check(
                    r.Out.Take() is ["down Space", "up Space"],
                    "a short press runs the short action on release"
                );
                r.Hold(B(PadControl.North), 600);
                T.Check(r.Out.Take() is ["down Tab"], "held 500 ms: the long-press action starts");
                r.Poll(0);
                T.Check(r.Out.Take() is ["up Tab"], "and ends with the button");
            }
        );

        T.Group(
            "Engine: app overrides and map changes",
            () =>
            {
                var s = GuideLayer();
                s.Apps[1].Buttons["start"] = PadBinding.Of(ActionKind.Key, "F11");
                var r = new Rig(s) { Rule = s.Apps[1] };
                r.Poll(0);
                r.Poll(B(PadControl.Start));
                r.Poll(0);
                T.Check(r.Out.Take() is ["down F11", "up F11"], "Chrome's override: Menu is F11");

                r.Rule = s.Apps[0];
                r.Poll(B(PadControl.West));
                r.Rule = s.Apps[2];
                r.Poll(B(PadControl.West));
                T.Check(
                    r.Out.Take() is ["mouse Left down"],
                    "a drag carries on between two apps on the base map"
                );
                r.Rule = s.Apps[1];
                r.Poll(B(PadControl.West));
                T.Check(
                    r.Out.Take() is ["mouse Left up"],
                    "moving to an app with its own map lets go"
                );
                r.Poll(0);
            }
        );

        T.Group(
            "Engine: on-screen keyboard",
            () =>
            {
                var r = new Rig();
                r.Poll(0);
                r.Keyboard.Open = true;
                r.Poll(B(PadControl.South));
                r.Poll(0);
                r.Hold(B(PadControl.Right), 350 + 60 + 4);
                r.Poll(0);
                r.Poll(0, ly: -Full);
                r.Poll(0);
                T.Check(
                    r.Out.Calls.Count == 0,
                    "nothing reaches the app while the keyboard is open"
                );
                T.Check(
                    r.Keyboard.Presses.Take(3)
                        .SequenceEqual(new[] { "South", "Right", "Right repeat" }),
                    $"the keyboard gets the presses, directions repeat ({string.Join(", ", r.Keyboard.Presses)})"
                );
                T.Check(
                    r.Keyboard.Presses.Last() == "Up",
                    "the left stick moves on the keyboard too"
                );
                r.Allowed = false;
                r.Poll(0);
                T.Check(
                    !r.Keyboard.Open,
                    "a window that is not allowed comes forward: the keyboard closes"
                );
            }
        );

        T.Group(
            "Engine: review fixes",
            () =>
            {
                var r = new Rig();
                r.Poll(0);
                r.Poll(B(PadControl.Guide, PadControl.North));
                r.Poll(0);
                T.Check(
                    r.Out.Take() is ["system minimizeAll"],
                    "Guide and Y in the same poll: layer 2's Y, no base Space"
                );

                r.Hold(B(PadControl.RT), 50, rt: Full);
                r.Hold(B(PadControl.RT, PadControl.Guide), 2100, rt: Full);
                r.Poll(0);
                T.Check(
                    !r.Out.Take().Contains("system powerOff"),
                    "a trigger held since before Guide: no power off after 2 s"
                );

                r.Poll(B(PadControl.Guide));
                r.Poll(B(PadControl.Guide, PadControl.West));
                r.Poll(B(PadControl.West)); // Guide let go first: X stays a layer button
                r.Settings.Layers[0].Buttons["west"] = PadBinding.Of(ActionKind.MouseLeft);
                r.Engine.Apply(SettingsStore.Copy(r.Settings));
                r.Poll(B(PadControl.West));
                r.Poll(0);
                r.Out.Take();
                r.Poll(B(PadControl.Guide));
                r.Poll(B(PadControl.Guide, PadControl.West));
                r.Poll(B(PadControl.West));
                r.Engine.Paused = true;
                r.Poll(B(PadControl.West));
                T.Check(
                    r.Out.Take() is ["mouse Left down", "mouse Left up"],
                    "a click held under a released layer is let go at a pause"
                );
                r.Engine.Paused = false;
                r.Poll(0);

                var s = GuideLayer();
                s.Layers[0].Buttons["back"] = PadBinding.Of(ActionKind.Pause);
                var p = new Rig(s);
                p.Poll(0);
                p.Engine.Paused = true;
                p.Poll(B(PadControl.Guide));
                p.Poll(B(PadControl.Guide, PadControl.Back));
                T.Check(
                    p.Out.Take() is ["system pause"],
                    "paused, Guide + View bound to pause still resumes"
                );

                var a = new Settings();
                a.Base.LeftStick = StickMode.Arrows;
                var k = new Rig(a);
                k.Poll(0);
                k.Poll(0, ly: -17000);
                k.Poll(0, ly: -15000);
                k.Poll(0, ly: -17000);
                k.Poll(0, ly: -11000);
                T.Check(
                    k.Out.Take() is ["down Up", "up Up"],
                    "a stick hovering at the arrow threshold does not flicker"
                );

                var kb = new Rig();
                kb.Poll(0);
                kb.Keyboard.Open = true;
                kb.Hold(B(PadControl.West), 350 + 60 + 4);
                kb.Hold(B(PadControl.South), 500);
                kb.Poll(0);
                T.Check(
                    kb.Keyboard.Presses.SequenceEqual(new[] { "West", "West repeat", "South" }),
                    $"keyboard: a held backspace repeats, a held A types once ({string.Join(", ", kb.Keyboard.Presses)})"
                );
            }
        );

        T.Group(
            "Engine: pause and new settings",
            () =>
            {
                var r = new Rig();
                r.Poll(0);
                r.Poll(B(PadControl.West));
                r.Engine.Paused = true;
                r.Poll(B(PadControl.West));
                r.Poll(B(PadControl.Guide));
                r.Hold(B(PadControl.Guide, PadControl.RT), 50, rt: Full);
                T.Check(
                    r.Out.Take() is ["mouse Left down", "mouse Left up"],
                    "paused: what was held is let go, layers are off too"
                );
                r.Engine.Paused = false;
                r.Poll(B(PadControl.Guide, PadControl.RT), rt: Full);
                r.Poll(0);
                T.Check(
                    r.Out.Take().Count == 0,
                    "buttons held across the pause do nothing until released"
                );

                r.Poll(B(PadControl.West));
                r.Engine.Apply(GuideLayer());
                r.Poll(B(PadControl.West));
                T.Check(
                    r.Out.Take() is ["mouse Left down", "mouse Left up"],
                    "new settings let go of what is held"
                );
            }
        );

        T.Group(
            "Engine: sticks",
            () =>
            {
                var r = new Rig();
                r.Poll(0, lx: 20000);
                T.Check(
                    r.Out.LastMotion is { Left: StickMode.Pointer, LX: 20000 },
                    "the left stick moves the pointer"
                );
                r.Poll(0, lx: 3000);
                T.Check(r.Out.LastMotion is null, "inside the dead zone: at rest");
                r.Allowed = false;
                r.Poll(0, lx: 20000);
                T.Check(r.Out.LastMotion is null, "a game in front: no pointer");

                var s = new Settings();
                s.Base.LeftStick = StickMode.Arrows;
                var a = new Rig(s);
                a.Poll(0);
                a.Poll(0, ly: -Full);
                a.Poll(0);
                T.Check(
                    a.Out.Take() is ["down Up", "up Up"],
                    "a stick set to arrows: up is the up arrow"
                );
                T.Check(a.Out.LastMotion is null, "and moves no pointer");

                var (x, y) = PointerFrames.Curve(32767, 0, 8000, 2);
                T.Check(Math.Abs(x - 1) < 1e-9 && y == 0, "full tilt is 1");
                T.Check(
                    PointerFrames.Curve(8000, 0, 8000, 2) == (0, 0),
                    "the dead zone's edge is 0"
                );
                var half = PointerFrames.Curve((short)(8000 + (32767 - 8000) / 2), 0, 8000, 2).X;
                T.Check(
                    Math.Abs(half - 0.25) < 0.01,
                    $"half way past the dead zone, squared: a quarter ({half:0.000})"
                );
            }
        );

        T.Group(
            "Engine: triggers",
            () =>
            {
                var r = new Rig();
                r.Poll(0);
                r.Poll(0, lt: 12000);
                T.Check(r.Out.Take().Count == 0, "LT under 40 %: not pressed");
                r.Poll(0, lt: 14000);
                r.Poll(0, lt: 8000);
                T.Check(
                    r.Out.Take() is ["mouse Right down"],
                    "past 40 % pressed, and still down at 25 % (some play)"
                );
                r.Poll(0, lt: 5000);
                T.Check(r.Out.Take() is ["mouse Right up"], "under 20 %: released");
            }
        );

        T.Group(
            "Engine: controller menu (today's defaults)",
            () =>
            {
                var r = new Rig(new Settings());
                r.Poll(0);
                r.Hold(B(PadControl.Start), 200);
                r.Poll(B(PadControl.Start, PadControl.R3));
                T.Check(
                    r.Menu.Shown && r.Out.Take() is ["system menu"],
                    "Start + R3 opens the menu"
                );
                r.Poll(B(PadControl.Start, PadControl.R3, PadControl.Down));
                r.Poll(B(PadControl.Start, PadControl.Down));
                r.Poll(B(PadControl.Down));
                r.Poll(0);
                r.Poll(B(PadControl.South));
                T.Check(
                    r.Out.Take().Count == 0
                        && r.Menu.Presses.SequenceEqual(new[] { "Down", "South" }),
                    $"open, Start still held: D-pad down goes to the menu (no minimize), A is no Enter, Start let go is no play/pause ({string.Join(", ", r.Menu.Presses)})"
                );
                r.Menu.Shown = false; // A picked: the menu closed while A is still down
                r.Poll(B(PadControl.South));
                r.Poll(0);
                T.Check(r.Out.Take().Count == 0, "the A that picked is no Enter once it closes");
                r.Poll(B(PadControl.South));
                r.Poll(0);
                T.Check(r.Out.Take() is ["down Enter", "up Enter"], "the next A is Enter again");

                r.Menu.Shown = true;
                r.Menu.Presses.Clear();
                r.Hold(B(PadControl.Down), 350 + 60 + 4);
                r.Hold(B(PadControl.South), 500);
                r.Poll(0, lx: Full);
                T.Check(
                    r.Menu.Presses.SequenceEqual(new[] { "Down", "Down repeat", "South" })
                        && r.Out.LastMotion is not null,
                    $"a held D-pad direction repeats, a held A does not, the stick still moves the pointer ({string.Join(", ", r.Menu.Presses)})"
                );

                var k = new Rig(new Settings());
                k.Poll(0);
                k.Poll(B(PadControl.L3));
                k.Poll(0);
                k.Menu.Shown = true;
                k.Poll(0);
                k.Settings.Feel.PointerSpeed = 2; // the menu saved a speed
                k.Engine.Apply(SettingsStore.Copy(k.Settings));
                k.Poll(0);
                k.Poll(0, lx: Full);
                T.Check(
                    k.Out.LastMotion is { Precise: true }
                        && k.Out.LastMotion.Feel.PointerSpeed == 2,
                    "a speed saved while it is open applies at once and keeps the precise toggle"
                );

                var g = new Rig(new Settings()) { Allowed = false };
                g.Poll(0);
                g.Hold(B(PadControl.Start), 200);
                g.Poll(B(PadControl.Start, PadControl.R3));
                g.Poll(B(PadControl.Start));
                g.Poll(0);
                g.Poll(B(PadControl.South), lx: Full);
                T.Check(
                    g.Out.Take() is ["system menu"]
                        && g.Menu.Presses is ["South"]
                        && g.Out.LastMotion is null,
                    "in a game: it opens and gets the presses, the stick moves no pointer"
                );

                var p = new Rig(new Settings());
                p.Poll(0);
                p.Engine.Paused = true;
                p.Poll(B(PadControl.Start));
                p.Poll(B(PadControl.Start, PadControl.R3));
                p.Poll(0);
                p.Poll(B(PadControl.Down));
                T.Check(
                    p.Out.Take() is ["system menu"] && p.Menu.Presses is ["Down"],
                    "paused: Start + R3 still opens it, and it gets the presses"
                );
            }
        );

        T.Group(
            "Foreground: title rules",
            () =>
            {
                T.Check(
                    TitleRules.Matches(
                        "Halo | Xbox Cloud Gaming on Xbox.com - Google Chrome",
                        "* on Xbox.com - Google Chrome"
                    ),
                    "an end-anchored glob matches"
                );
                T.Check(
                    !TitleRules.Matches(
                        "Xbox.com - Google Chrome review - YouTube - Google Chrome",
                        "* on Xbox.com - Google Chrome"
                    ),
                    "a page that only mentions it does not"
                );
                T.Check(
                    TitleRules.Matches(
                        "GEFORCE now - google chrome",
                        "GeForce NOW - Google Chrome"
                    ),
                    "case is ignored"
                );
                T.Check(
                    TitleRules.Matches("Tab 1 - Chrome", "Tab ? - Chrome")
                        && !TitleRules.Matches("Tab 12 - Chrome", "Tab ? - Chrome"),
                    "? is one character"
                );
                T.Check(
                    TitleRules.Matches("a (b) [c]", "a (b) [c]"),
                    "regex characters are plain text"
                );
            }
        );

        T.Group(
            "Pads: virtual devices",
            () =>
            {
                T.Equal(
                    @"HID\VID_045E&PID_028E&IG_00\3&2a0e5a0f&0&0000",
                    DeviceTree.InstanceIdOf(
                        @"\\?\HID#VID_045E&PID_028E&IG_00#3&2a0e5a0f&0&0000#{4d1e55b2-f16f-11cf-88cb-001111000030}"
                    ),
                    "a HID path as an instance id"
                );
                T.Check(
                    DeviceTree.InstanceIdOf("XInput#0") is null
                        && DeviceTree.InstanceIdOf(null) is null,
                    "not a device path: unknown"
                );
                T.Check(
                    DeviceTree.IsVirtualParent(@"ROOT\SYSTEM\0001") == true,
                    "a bus right under the root (ViGEm) is virtual"
                );
                T.Check(
                    DeviceTree.IsVirtualParent(@"SWD\MSRRAS\MS_NDISWANIP") == true,
                    "a software device is virtual"
                );
                T.Check(
                    DeviceTree.IsVirtualParent(@"PCI\VEN_8086&DEV_7AE0") == false,
                    "the PCI bus is hardware"
                );
                T.Check(
                    DeviceTree.IsVirtualParent(@"BTHLE\Dev_e417d8") == false,
                    "Bluetooth is hardware"
                );
                T.Check(
                    DeviceTree.IsVirtualParent(@"USB\ROOT_HUB30\4&1a2b") is null,
                    "a USB hub: keep walking up"
                );
            }
        );
    }
}
