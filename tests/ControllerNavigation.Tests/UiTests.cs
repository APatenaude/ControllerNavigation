namespace ControllerNavigation.Tests;

static class UiTests
{
    static RawJoystick Raw(Action<bool[], short[], byte[]>? change = null)
    {
        var buttons = new bool[16];
        var axes = new short[] { 0, 0, 0, 0, -32768, -32768 };
        var hats = new byte[1];
        change?.Invoke(buttons, axes, hats);
        return new RawJoystick(
            "p",
            "03000000c82d00000360000000000000",
            "8BitDo Pro 2",
            buttons,
            axes,
            hats
        );
    }

    static WizardStep Step(string id) => MappingWizardLogic.Steps.First(s => s.Id == id);

    public static void Run()
    {
        T.Group(
            "UI: checks",
            () =>
            {
                T.Check(Checks.IsSwitchOn(null), "a missing value is on");
                T.Check(Checks.IsSwitchOn(1), "1 is on");
                T.Check(!Checks.IsSwitchOn(0), "0 is off");
                T.Check(!Checks.IsSwitchOn("0"), "\"0\" is off");
                T.Check(Checks.Run().Count == 4, "four checks");
            }
        );

        T.Group(
            "UI: exe names",
            () =>
            {
                T.Equal("vlc.exe", SettingsForm.CleanExe("vlc"), "a bare name gets .exe");
                T.Equal("Discord.exe", SettingsForm.CleanExe("  Discord "), "trimmed");
                T.Equal(
                    "chrome.exe",
                    SettingsForm.CleanExe(@"C:\Program Files\Google\chrome.exe"),
                    "a path becomes the file name"
                );
                T.Equal("a.exe", SettingsForm.CleanExe("x/y/a.exe"), "forward slashes too");
                T.Equal(
                    "tool.bin",
                    SettingsForm.CleanExe("tool.bin"),
                    "an existing extension stays"
                );
                T.Equal("", SettingsForm.CleanExe("   "), "nothing stays nothing");
            }
        );

        T.Group(
            "UI: key names",
            () =>
            {
                T.Equal(
                    "Ctrl+Shift+T",
                    KeyNames.Canonical("shift+ctrl+t"),
                    "canonical order and case"
                );
                T.Equal("Win+E", KeyNames.Canonical("win+e"), "Win combination");
                T.Equal("F11", KeyNames.Canonical("f11"), "function key");
                T.Check(KeyNames.Canonical("A+B") is null, "two plain keys refused");
                T.Equal("A", KeyNames.NameOf(Keys.A), "Keys.A is A");
                T.Equal("Left", KeyNames.NameOf(Keys.Left), "Keys.Left is Left");
                T.Equal("Win", KeyNames.NameOf(Keys.RWin), "right Win is Win");
                T.Check(KeyNames.IsModifier("Ctrl") && !KeyNames.IsModifier("A"), "modifiers");
            }
        );

        T.Group(
            "UI: mapping wizard",
            () =>
            {
                var rest = Raw();
                var none = new List<string>();
                T.Equal(
                    "b3",
                    MappingWizardLogic.Detect(
                        Step("north"),
                        Raw((b, _, _) => b[3] = true),
                        rest,
                        none
                    ),
                    "button"
                );
                T.Check(
                    MappingWizardLogic.Detect(
                        Step("south"),
                        Raw((b, _, _) => b[3] = true),
                        rest,
                        new[] { "b3" }
                    )
                        is null,
                    "a button already used is not offered again"
                );
                T.Equal(
                    "h0.4",
                    MappingWizardLogic.Detect(Step("down"), Raw((_, _, h) => h[0] = 4), rest, none),
                    "hat bit"
                );
                T.Equal(
                    "a4",
                    MappingWizardLogic.Detect(
                        Step("lt"),
                        Raw((_, a, _) => a[4] = 32767),
                        rest,
                        none
                    ),
                    "trigger resting at the minimum: whole axis"
                );
                T.Equal(
                    "+a2",
                    MappingWizardLogic.Detect(
                        Step("rt"),
                        Raw((_, a, _) => a[2] = 30000),
                        rest,
                        none
                    ),
                    "trigger resting at zero: positive half"
                );
                T.Equal(
                    "-a2",
                    MappingWizardLogic.Detect(
                        Step("rt"),
                        Raw((_, a, _) => a[2] = -30000),
                        rest,
                        none
                    ),
                    "trigger resting at zero: negative half"
                );
                T.Equal(
                    "a0",
                    MappingWizardLogic.Detect(
                        Step("leftx"),
                        Raw((_, a, _) => a[0] = 30000),
                        rest,
                        none
                    ),
                    "stick pushed right"
                );
                T.Equal(
                    "a1~",
                    MappingWizardLogic.Detect(
                        Step("lefty"),
                        Raw((_, a, _) => a[1] = -30000),
                        rest,
                        none
                    ),
                    "stick axis inverted"
                );
                T.Check(
                    MappingWizardLogic.Detect(
                        Step("rightx"),
                        Raw((_, a, _) => a[0] = 30000),
                        rest,
                        new[] { "a0" }
                    )
                        is null,
                    "an axis already used by a stick is skipped"
                );
                T.Check(
                    MappingWizardLogic.Detect(
                        Step("leftx"),
                        Raw((_, a, _) => a[0] = 5000),
                        rest,
                        none
                    )
                        is null,
                    "a small movement is ignored"
                );
                T.Check(MappingWizardLogic.IsNeutral(rest, rest), "rest is neutral");
                T.Check(
                    !MappingWizardLogic.IsNeutral(Raw((b, _, _) => b[0] = true), rest),
                    "a pressed button is not"
                );

                var results = new Dictionary<string, string>
                {
                    ["south"] = "b0",
                    ["east"] = "b1",
                    ["dpup"] = "ignored",
                    ["up"] = "h0.1",
                    ["lt"] = "a4",
                    ["rightx"] = "a2~",
                };
                T.Equal(
                    "guid,Pad  name,a:b0,b:b1,dpup:h0.1,lefttrigger:a4,rightx:a2~,platform:Windows,",
                    MappingWizardLogic.Build("guid", "Pad, name", results),
                    "mapping string in SDL's format, commas dropped from the name"
                );
            }
        );
    }
}
