namespace ControllerNavigation.Tests;

static class ActionTests
{
    public static void Run()
    {
        T.Group(
            "Actions: monitor on the left or right",
            () =>
            {
                PixelRect[] two = [new(0, 0, 1920, 1080), new(1920, 0, 3840, 1080)];
                T.Equal(1, WindowMover.PickMonitor(two, 0, toRight: true), "right of the left one");
                T.Equal(
                    1,
                    WindowMover.PickMonitor(two, 0, toRight: false),
                    "left of the leftmost: round to the other end, as Win+Shift+Left"
                );
                T.Equal(
                    0,
                    WindowMover.PickMonitor(two, 1, toRight: false),
                    "left of the right one"
                );
                T.Equal(
                    0,
                    WindowMover.PickMonitor(two, 1, toRight: true),
                    "right of the rightmost: round to the other end"
                );

                // The list order is not the screen order: the nearest centre wins.
                PixelRect[] three =
                [
                    new(3840, 0, 5760, 1080),
                    new(0, 0, 1920, 1080),
                    new(1920, 0, 3840, 1080),
                ];
                T.Equal(
                    2,
                    WindowMover.PickMonitor(three, 1, toRight: true),
                    "nearest on the right, not the next in the list"
                );
                T.Equal(
                    0,
                    WindowMover.PickMonitor(three, 2, toRight: true),
                    "the far one when it is the only one"
                );
                T.Equal(
                    1,
                    WindowMover.PickMonitor(three, 2, toRight: false),
                    "left of the middle one"
                );

                // Stacked screens (the owner's: one above the other) share a centre: either way goes to the other one.
                PixelRect[] stacked = [new(0, 0, 3440, 1440), new(0, -1440, 3440, 0)];
                T.Equal(
                    1,
                    WindowMover.PickMonitor(stacked, 0, toRight: true),
                    "stacked: right goes to the other screen"
                );
                T.Equal(
                    0,
                    WindowMover.PickMonitor(stacked, 1, toRight: false),
                    "stacked: left goes to the other screen"
                );

                // Two screens at the same distance on one side: the one closer vertically.
                PixelRect[] tie =
                [
                    new(0, 0, 1920, 1080),
                    new(1920, 1080, 3840, 2160),
                    new(1920, 0, 3840, 1080),
                ];
                T.Equal(
                    2,
                    WindowMover.PickMonitor(tie, 0, toRight: true),
                    "same centre X: the closer in Y"
                );

                // A small screen next to a big one: centres decide, not edges.
                PixelRect[] mixed = [new(0, 0, 2560, 1440), new(2560, 0, 3840, 720)];
                T.Equal(
                    1,
                    WindowMover.PickMonitor(mixed, 0, toRight: true),
                    "small screen on the right"
                );
                T.Equal(
                    0,
                    WindowMover.PickMonitor(mixed, 1, toRight: false),
                    "big screen on the left"
                );
                T.Equal(
                    -1,
                    WindowMover.PickMonitor([new(0, 0, 100, 100)], 0, toRight: true),
                    "a single screen"
                );
            }
        );

        T.Group(
            "Actions: window rectangle on another screen",
            () =>
            {
                var left = new PixelRect(0, 0, 1920, 1040);
                var right = new PixelRect(1920, 0, 3840, 1040);

                T.Equal(
                    new PixelRect(2320, 100, 3320, 600),
                    WindowMover.Transfer(new PixelRect(400, 100, 1400, 600), left, right),
                    "same size screens: the same place, shifted"
                );

                var big = new PixelRect(3840, 0, 6400, 1400);
                T.Equal(
                    new PixelRect(3840, 0, 6400, 1400),
                    WindowMover.Transfer(left, left, big),
                    "a window filling a work area fills the other"
                );
                T.Equal(
                    new PixelRect(4480, 350, 5120, 700),
                    WindowMover.Transfer(new PixelRect(480, 260, 960, 520), left, big),
                    "a window at a share of the area keeps that share (position and size)"
                );

                var small = new PixelRect(1920, 0, 2720, 600);
                var shrunk = WindowMover.Transfer(new PixelRect(0, 0, 1920, 1040), left, small);
                T.Equal(
                    new PixelRect(1920, 0, 2720, 600),
                    shrunk,
                    "a window larger than the target shrinks to it"
                );

                var overhang = WindowMover.Transfer(
                    new PixelRect(1500, 800, 2300, 1300),
                    left,
                    right
                );
                T.Check(
                    overhang.Left >= right.Left
                        && overhang.Right <= right.Right
                        && overhang.Top >= right.Top
                        && overhang.Bottom <= right.Bottom,
                    "a window hanging off its screen lands inside the target"
                );
                var offLeft = WindowMover.Transfer(new PixelRect(-300, -50, 500, 400), left, right);
                T.Check(
                    offLeft.Left >= right.Left && offLeft.Top >= right.Top,
                    "a window above or left of its screen is pulled in"
                );
                T.Equal(800, offLeft.Width, "and keeps its size");

                T.Check(
                    WindowMover
                        .Transfer(new PixelRect(0, 0, 10, 10), new PixelRect(0, 0, 0, 0), right)
                        .Width >= 1,
                    "an empty source area does not divide by zero"
                );
            }
        );

        T.Group(
            "Actions: volume step",
            () =>
            {
                T.Equal(54, SoundVolume.Stepped(50, 4), "up");
                T.Equal(46, SoundVolume.Stepped(50, -4), "down");
                T.Equal(100, SoundVolume.Stepped(98, 4), "clamped at 100");
                T.Equal(0, SoundVolume.Stepped(2, -4), "clamped at 0");
                T.Equal("Volume 64%", new VolumeState(64, false).ToString(), "text");
                T.Equal("Volume 0% (muted)", new VolumeState(0, true).ToString(), "text, muted");
            }
        );

        T.Group(
            "Actions: run command",
            () =>
            {
                Func<string, bool> none = _ => false;
                Func<string, bool> app = p => p == @"C:\Program Files\App\app.exe";

                T.Equal(
                    (@"C:\Program Files\App\app.exe", "--flag"),
                    ShellCommands.SplitCommand("\"C:\\Program Files\\App\\app.exe\" --flag", none),
                    "quoted path then arguments"
                );
                T.Equal(
                    (@"C:\x y\a.exe", (string?)null),
                    ShellCommands.SplitCommand("\"C:\\x y\\a.exe\"", none),
                    "quoted path alone"
                );
                T.Equal(
                    ("C:\\x", (string?)null),
                    ShellCommands.SplitCommand("\"C:\\x", none),
                    "unterminated quote"
                );
                T.Equal(
                    ("https://example.com/a b", (string?)null),
                    ShellCommands.SplitCommand("https://example.com/a b", none),
                    "a URL as it is"
                );
                T.Equal(
                    ("calc", (string?)null),
                    ShellCommands.SplitCommand("  calc ", none),
                    "one word"
                );
                T.Equal(
                    ("notepad", @"C:\notes.txt"),
                    ShellCommands.SplitCommand(@"notepad C:\notes.txt", none),
                    "first word and arguments"
                );
                T.Equal(
                    (@"C:\Program Files\App\app.exe", (string?)null),
                    ShellCommands.SplitCommand(@"C:\Program Files\App\app.exe", app),
                    "an existing path with spaces as it is"
                );
                T.Equal(
                    (@"C:\Program Files\App\app.exe", "--flag -x"),
                    ShellCommands.SplitCommand(@"C:\Program Files\App\app.exe --flag -x", app),
                    "unquoted path with spaces, then arguments"
                );
                T.Equal(
                    ("ms-settings:display", (string?)null),
                    ShellCommands.SplitCommand("ms-settings:display", none),
                    "a protocol link"
                );
            }
        );

        T.Group(
            "Actions: fallbacks and keys",
            () =>
            {
                T.Equal(
                    "Win+Shift+Left",
                    WindowMover.FallbackCombo(ActionKind.WindowLeft, false),
                    "move left"
                );
                T.Equal(
                    "Win+Shift+Right",
                    WindowMover.FallbackCombo(ActionKind.WindowRight, true),
                    "move right"
                );
                T.Equal(
                    "Win+Up",
                    WindowMover.FallbackCombo(ActionKind.Maximize, false),
                    "maximize"
                );
                T.Equal(
                    "Win+Down",
                    WindowMover.FallbackCombo(ActionKind.Maximize, true),
                    "restore a maximized window"
                );
                T.Equal(
                    "Win+Down",
                    WindowMover.FallbackCombo(ActionKind.Minimize, false),
                    "minimize"
                );
                T.Check(
                    WindowMover.FallbackCombo(ActionKind.Mute, false) is null,
                    "not a window action"
                );
                foreach (
                    var action in new[]
                    {
                        ActionKind.WindowLeft,
                        ActionKind.WindowRight,
                        ActionKind.Maximize,
                        ActionKind.Minimize,
                    }
                )
                foreach (var zoomed in new[] { false, true })
                    T.Check(
                        KeyCombo.Parse(WindowMover.FallbackCombo(action, zoomed)) is not null,
                        $"{action} fallback is a key combo Parse understands"
                    );

                T.Check(
                    WindowMover.IsShellClass("Progman")
                        && WindowMover.IsShellClass("WorkerW")
                        && WindowMover.IsShellClass("Shell_TrayWnd"),
                    "desktop and taskbar classes"
                );
                T.Check(
                    !WindowMover.IsShellClass("Chrome_WidgetWin_1")
                        && !WindowMover.IsShellClass("progman"),
                    "other windows (case matters)"
                );

                T.Equal((ushort)0xB3, MediaSessions.KeyOf(ActionKind.PlayPause), "play/pause key");
                T.Equal((ushort)0xB0, MediaSessions.KeyOf(ActionKind.NextTrack), "next key");
                T.Equal(
                    (ushort)0xB1,
                    MediaSessions.KeyOf(ActionKind.PreviousTrack),
                    "previous key"
                );
                T.Check(KeyCombo.Parse("Win+M") is not null, "Win+M, minimize all's fallback");
            }
        );
    }
}
