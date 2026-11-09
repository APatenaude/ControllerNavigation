using System.Text.Json;

namespace ControllerNavigation.Tests;

static class SettingsTests
{
    public static void Run()
    {
        T.Group(
            "Settings: key combos",
            () =>
            {
                T.Check(
                    KeyCombo.Parse("Ctrl+Shift+T") is [0x11, 0x10, 0x54],
                    "Ctrl+Shift+T, modifiers first"
                );
                T.Check(
                    KeyCombo.Parse("t+shift+ctrl") is [0x11, 0x10, 0x54],
                    "any order and case, written modifiers first"
                );
                T.Check(KeyCombo.Parse("Win+M") is [0x5B, 0x4D], "Win+M");
                T.Check(KeyCombo.Parse("Win") is [0x5B], "Win alone (the Start menu)");
                T.Check(KeyCombo.Parse("A+B") is null, "two plain keys refused");
                T.Check(KeyCombo.Parse("Ctrl+Ctrl") is null, "a key twice refused");
                T.Check(KeyCombo.Parse("Hyper+X") is null, "an unknown name refused");
                T.Check(
                    KeyCombo.Parse(" ") is null && KeyCombo.Parse(null) is null,
                    "nothing refused"
                );
            }
        );

        T.Group(
            "Settings: defaults and normalize",
            () =>
            {
                var s = new Settings();
                T.Equal(0, s.Normalize().Count, "the built-in defaults are clean");
                T.Check(
                    s.Layers
                        is [
                            { Button: "start", Everywhere: true, Tap.Action: ActionKind.PlayPause },
                        ],
                    "one layer, on Start (tap: play/pause), everywhere"
                );
                T.Check(!s.Base.Buttons.ContainsKey("guide"), "Guide is left to Windows");
                T.Check(
                    s.Apps.All(a => a.Mode == AppMode.Always),
                    "the built-in app rules are 'always'"
                );
                T.Check(
                    s.Apps.Any(a => a.Exes.Contains("msedge.exe"))
                        && s.Detection is { ControllerApps: true, KnownGames: true },
                    "Edge too, detection on"
                );
                T.Check(
                    s.Apps.Any(a => a.Exes.Contains("chrome.exe")),
                    "Chrome always gets the mouse"
                );
                T.Check(
                    !s.Apps.Any(a =>
                        a.Exes.Contains("vlc.exe") || a.Exes.Contains("SearchHost.exe")
                    ),
                    "only apps that hold the controller open are built-in exceptions"
                );
                T.Check(s.Feel.ShowVolumeSlider, "volume shows Windows' slider by default");
                T.Check(
                    s.Apps.FirstOrDefault(a => a.Exes.Contains("steamwebhelper.exe"))
                        is { Mode: AppMode.Always } steam
                        && steam.TitleOff.Any(t => TitleRules.Matches("Steam Big Picture Mode", t))
                        && !steam.TitleOff.Any(t => TitleRules.Matches("Steam", t)),
                    "Steam's window gets the mouse, Big Picture is left alone"
                );
                var odd = JsonSerializer.Deserialize<Settings>(
                    """{ "apps": [ { "name": null, "exes": ["x.exe"], "mode": "Never" } ], "layers": [ { "name": "L" } ] }""",
                    SettingsStore.Json
                )!;
                odd.Normalize();
                T.Check(
                    odd.Apps is [{ Name: "", Mode: AppMode.Never }],
                    "a hand-written \"Never\" and a null name are kept sensibly"
                );
                T.Check(
                    odd.Layers is [{ Button: "start" }],
                    "a layer written without a button is on Start"
                );
                T.Check(
                    s.Base.Buttons["r3"].Action == ActionKind.Keyboard,
                    "R3 opens the keyboard"
                );

                var bad = JsonSerializer.Deserialize<Settings>(
                    """
                    { "base": { "leftStick": "warp", "buttons": { "south": { "action": "teleport" }, "nose": { "action": "key", "arg": "A" },
                      "east": { "action": "key", "arg": "A+B" }, "west": { "action": "mouseLeft", "hold": { "action": "nope" } } } },
                      "layers": [ { "button": "chin" } ], "feel": { "curve": 99 } }
                    """,
                    SettingsStore.Json
                )!;
                var notes = bad.Normalize();
                T.Equal(StickMode.None, bad.Base.LeftStick, "an unknown stick mode becomes none");
                T.Check(
                    !bad.Base.Buttons.ContainsKey("south")
                        && !bad.Base.Buttons.ContainsKey("nose")
                        && !bad.Base.Buttons.ContainsKey("east"),
                    "an unknown action, control or key combo is dropped"
                );
                T.Check(
                    bad.Base.Buttons["west"] is { Action: ActionKind.MouseLeft, Hold: null },
                    "a broken long press is dropped alone"
                );
                T.Equal(0, bad.Layers.Count, "a layer on an unknown button is dropped");
                T.Equal(4.0, bad.Feel.Curve, "out-of-range numbers are clamped");
                T.Check(notes.Count >= 6, $"each drop is noted ({notes.Count})");
            }
        );

        T.Group(
            "Settings: JSON round trip",
            () =>
            {
                var s = new Settings();
                s.Apps[1].TitleOff.Add("* on Xbox.com - Google Chrome");
                var copy = SettingsStore.Copy(s);
                T.Check(
                    !ReferenceEquals(copy, s)
                        && copy.Apps[1].TitleOff.SequenceEqual(s.Apps[1].TitleOff),
                    "a deep copy keeps the title rule"
                );
                var partial = JsonSerializer.Deserialize<Settings>(
                    """{ "feel": { "pointerSpeed": 2 } }""",
                    SettingsStore.Json
                )!;
                T.Check(
                    partial.Feel.PointerSpeed == 2
                        && partial.Apps.Count == 4
                        && partial.Layers.Count == 1,
                    "a partial file keeps the other defaults"
                );
            }
        );
    }
}
