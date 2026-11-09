using System.Runtime.InteropServices;

namespace ControllerNavigation;

/// <summary>
/// Keyboard and mouse input for the app in front, through SendInput (lifted from HTPC's Input.cs).
/// Apps see it as a real keyboard and mouse. While an elevated window is in front, the records go
/// through the elevated input helper instead (ToElevated, ElevatedSink).
/// </summary>
static class Input
{
    [StructLayout(LayoutKind.Sequential)]
    struct MouseInput
    {
        public int X,
            Y;
        public int Data;
        public uint Flags,
            Time;
        public IntPtr Extra;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct KeyInput
    {
        public ushort Vk,
            Scan;
        public uint Flags,
            Time;
        public IntPtr Extra;
    }

    // The union's size is that of its largest member (MOUSEINPUT); x64 aligns it at 8.
    [StructLayout(LayoutKind.Explicit)]
    struct Union
    {
        [FieldOffset(0)]
        public MouseInput Mouse;

        [FieldOffset(0)]
        public KeyInput Key;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct INPUT
    {
        public uint Type;
        public Union U;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct POINT
    {
        public int X,
            Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct RECT
    {
        public int Left,
            Top,
            Right,
            Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct MONITORINFO
    {
        public int Size;
        public RECT Monitor,
            Work;
        public uint Flags;
    }

    [DllImport("user32.dll", SetLastError = true)]
    static extern uint SendInput(uint count, INPUT[] inputs, int size);

    [DllImport("user32.dll")]
    static extern uint MapVirtualKey(uint code, uint mapType);

    [DllImport("user32.dll")]
    static extern int GetSystemMetrics(int index);

    [DllImport("user32.dll")]
    static extern bool GetCursorPos(out POINT point);

    [DllImport("user32.dll")]
    static extern IntPtr MonitorFromPoint(POINT point, uint flags);

    [DllImport("user32.dll")]
    static extern bool GetMonitorInfo(IntPtr monitor, ref MONITORINFO info);

    const uint INPUT_MOUSE = 0,
        INPUT_KEYBOARD = 1;
    const uint KEYEVENTF_EXTENDEDKEY = 0x1,
        KEYEVENTF_KEYUP = 0x2,
        KEYEVENTF_UNICODE = 0x4;
    const uint MOUSEEVENTF_MOVE = 0x1,
        MOUSEEVENTF_ABSOLUTE = 0x8000,
        MOUSEEVENTF_VIRTUALDESK = 0x4000,
        MOUSEEVENTF_WHEEL = 0x800,
        MOUSEEVENTF_HWHEEL = 0x1000;
    const uint MONITOR_DEFAULTTONULL = 0,
        MONITOR_DEFAULTTONEAREST = 2;
    const int SM_XVIRTUALSCREEN = 76,
        SM_YVIRTUALSCREEN = 77,
        SM_CXVIRTUALSCREEN = 78,
        SM_CYVIRTUALSCREEN = 79;

    /// <summary>Marks our own input (dwExtraInfo), so it can be told apart from a real mouse.</summary>
    public static readonly IntPtr Tag = new(0x434E4156); // "CNAV"

    // Keys whose scan code needs the extended-key flag (arrows, navigation block, right Ctrl/Alt, ...).
    static readonly HashSet<ushort> Extended = new()
    {
        0x21,
        0x22,
        0x23,
        0x24,
        0x25,
        0x26,
        0x27,
        0x28,
        0x2D,
        0x2E, // PgUp PgDn End Home arrows Ins Del
        0x5B,
        0x5C,
        0x5D,
        0xA3,
        0xA5,
        0x6F,
        0x90,
        0x2C, // Win keys, Menu, RCtrl, RAlt, Num /, NumLock, PrintScreen
        0xA6,
        0xA7,
        0xA8,
        0xA9,
        0xAA,
        0xAB,
        0xAC, // browser keys
        0xAD,
        0xAE,
        0xAF,
        0xB0,
        0xB1,
        0xB2,
        0xB3, // volume and media keys
    };

    static INPUT Key(ushort vk, bool up)
    {
        var flags = up ? KEYEVENTF_KEYUP : 0;
        if (Extended.Contains(vk))
            flags |= KEYEVENTF_EXTENDEDKEY;
        return new INPUT
        {
            Type = INPUT_KEYBOARD,
            U = new Union
            {
                Key = new KeyInput
                {
                    Vk = vk,
                    Scan = (ushort)MapVirtualKey(vk, 0),
                    Flags = flags,
                    Extra = Tag,
                },
            },
        };
    }

    static INPUT Mouse(uint flags, int x = 0, int y = 0, int data = 0) =>
        new()
        {
            Type = INPUT_MOUSE,
            U = new Union
            {
                Mouse = new MouseInput
                {
                    X = x,
                    Y = y,
                    Data = data,
                    Flags = flags,
                    Extra = Tag,
                },
            },
        };

    /// <summary>
    /// An elevated window is in front (the input thread sets it from the foreground): Windows would
    /// not deliver what this process sends to it, so it goes through the elevated input helper
    /// (ElevatedSink) when that takes it. Back to false, key releases Windows may have refused
    /// meanwhile are sent again.
    /// </summary>
    public static bool ToElevated
    {
        get => toElevated;
        set
        {
            var was = toElevated;
            toElevated = value;
            if (was && !value)
                SendRefusedReleases();
        }
    }
    static volatile bool toElevated;

    /// <summary>The helper's end of it (ElevatedInput.TrySend, set at start): the records, true when it took them.</summary>
    public static Func<byte[], bool>? ElevatedSink;

    // Keys released while an elevated window was in front and the helper did not take the
    // release: Windows blocks it without saying so (UIPI), and the key would stay held everywhere.
    static readonly HashSet<ushort> refusedReleases = new();

    static void Send(params INPUT[] inputs)
    {
        if (inputs.Length == 0)
            return;
        if (ToElevated)
        {
            if (ElevatedSink?.Invoke(MemoryMarshal.AsBytes(inputs.AsSpan()).ToArray()) == true)
                return;
            lock (refusedReleases)
                foreach (var input in inputs)
                    if (input.Type == INPUT_KEYBOARD && input.U.Key.Vk != 0)
                    {
                        if ((input.U.Key.Flags & KEYEVENTF_KEYUP) != 0)
                            refusedReleases.Add(input.U.Key.Vk);
                        else
                            refusedReleases.Remove(input.U.Key.Vk);
                    }
        }
        SendLocal(inputs);
    }

    static void SendRefusedReleases()
    {
        ushort[] keys;
        lock (refusedReleases)
        {
            keys = refusedReleases.ToArray();
            refusedReleases.Clear();
        }
        if (keys.Length == 0)
            return;
        Log.Info($"Sending again {keys.Length} key release(s) an elevated window kept");
        SendLocal(keys.Select(k => Key(k, true)).ToArray());
    }

    static long nextRefusalLog;

    static void SendLocal(INPUT[] inputs)
    {
        if (SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>()) == inputs.Length)
            return;
        // Refused while a higher-privilege window or the lock screen is in front: once every 10 s,
        // not 60 lines a second while a stick moves.
        var now = Environment.TickCount64;
        if (now < Interlocked.Read(ref nextRefusalLog))
            return;
        Interlocked.Exchange(ref nextRefusalLog, now + 10_000);
        Log.Warn(
            $"SendInput sent less than asked (error {Marshal.GetLastWin32Error()}); a higher-privilege window is probably in front"
        );
    }

    /// <summary>The input helper (elevated): records the app framed (InputFrame), sent as they are.</summary>
    public static void SendRecords(byte[] records)
    {
        if (Marshal.SizeOf<INPUT>() != 40)
        {
            Log.Error("Input records are not the size the helper expects");
            return;
        } // InputFrame.RecordSize
        SendLocal(MemoryMarshal.Cast<byte, INPUT>(records).ToArray());
    }

    /// <summary>Presses a key or a combination (modifiers first) and keeps it down.</summary>
    public static void KeysDown(IReadOnlyList<ushort> keys) =>
        Send(keys.Select(k => Key(k, false)).ToArray());

    /// <summary>Releases a combination in reverse order.</summary>
    public static void KeysUp(IReadOnlyList<ushort> keys) =>
        Send(keys.Reverse().Select(k => Key(k, true)).ToArray());

    /// <summary>A key press and release (a combination is held together, then released).</summary>
    public static void Tap(params ushort[] keys) =>
        Send(
            keys.Select(k => Key(k, false))
                .Concat(keys.Reverse().Select(k => Key(k, true)))
                .ToArray()
        );

    /// <summary>Types text as Unicode characters, whatever the keyboard layout.</summary>
    public static void Type(string text)
    {
        var inputs = new List<INPUT>();
        foreach (var c in text)
        {
            if (c == '\n')
            {
                inputs.Add(Key(0x0D, false));
                inputs.Add(Key(0x0D, true));
                continue;
            }
            inputs.Add(
                new INPUT
                {
                    Type = INPUT_KEYBOARD,
                    U = new Union
                    {
                        Key = new KeyInput
                        {
                            Scan = c,
                            Flags = KEYEVENTF_UNICODE,
                            Extra = Tag,
                        },
                    },
                }
            );
            inputs.Add(
                new INPUT
                {
                    Type = INPUT_KEYBOARD,
                    U = new Union
                    {
                        Key = new KeyInput
                        {
                            Scan = c,
                            Flags = KEYEVENTF_UNICODE | KEYEVENTF_KEYUP,
                            Extra = Tag,
                        },
                    },
                }
            );
        }
        Send(inputs.ToArray());
    }

    public enum Button
    {
        Left,
        Right,
        Middle,
        Back,
        Forward,
    }

    public static void MouseButton(Button button, bool down)
    {
        var (flags, data) = button switch
        {
            Button.Left => (down ? 0x2u : 0x4u, 0),
            Button.Right => (down ? 0x8u : 0x10u, 0),
            Button.Middle => (down ? 0x20u : 0x40u, 0),
            Button.Back => (down ? 0x80u : 0x100u, 1), // XBUTTON1
            _ => (down ? 0x80u : 0x100u, 2), // XBUTTON2
        };
        Send(Mouse(flags, data: data));
    }

    /// <summary>
    /// Moves the pointer by (dx, dy) physical pixels. Sent as an absolute position on the whole
    /// virtual desktop, so Windows' pointer acceleration ("Enhance pointer precision") does not
    /// bend the controller's curve, and the pointer crosses to the other screens. A move that would
    /// leave every screen (a gap between monitors of different sizes) stops at the edge of the
    /// screen the pointer is on.
    /// </summary>
    public static void MoveBy(int dx, int dy)
    {
        if (dx == 0 && dy == 0)
            return;
        if (!GetCursorPos(out var at))
            return;
        var to = new POINT { X = at.X + dx, Y = at.Y + dy };
        if (MonitorFromPoint(to, MONITOR_DEFAULTTONULL) == IntPtr.Zero)
        {
            var info = new MONITORINFO { Size = Marshal.SizeOf<MONITORINFO>() };
            if (GetMonitorInfo(MonitorFromPoint(at, MONITOR_DEFAULTTONEAREST), ref info))
            {
                to.X = Math.Clamp(to.X, info.Monitor.Left, info.Monitor.Right - 1);
                to.Y = Math.Clamp(to.Y, info.Monitor.Top, info.Monitor.Bottom - 1);
            }
        }
        int left = GetSystemMetrics(SM_XVIRTUALSCREEN),
            top = GetSystemMetrics(SM_YVIRTUALSCREEN);
        int width = Math.Max(1, GetSystemMetrics(SM_CXVIRTUALSCREEN)),
            height = Math.Max(1, GetSystemMetrics(SM_CYVIRTUALSCREEN));
        // Absolute coordinates are 0..65535 across the virtual desktop; round to the pixel's centre.
        Send(
            Mouse(
                MOUSEEVENTF_MOVE | MOUSEEVENTF_ABSOLUTE | MOUSEEVENTF_VIRTUALDESK,
                (int)(((to.X - left) * 65536L + 32768) / width),
                (int)(((to.Y - top) * 65536L + 32768) / height)
            )
        );
    }

    /// <summary>The height in pixels of the screen the pointer is on (the pointer speed is in screen heights).</summary>
    public static int ScreenHeightAtPointer()
    {
        var info = new MONITORINFO { Size = Marshal.SizeOf<MONITORINFO>() };
        if (
            GetCursorPos(out var at)
            && GetMonitorInfo(MonitorFromPoint(at, MONITOR_DEFAULTTONEAREST), ref info)
        )
            return Math.Max(1, info.Monitor.Bottom - info.Monitor.Top);
        return 1080;
    }

    /// <summary>Wheel scroll: positive = up (or right, for horizontal). 120 is one notch.</summary>
    public static void Wheel(int amount, bool horizontal = false)
    {
        if (amount != 0)
            Send(Mouse(horizontal ? MOUSEEVENTF_HWHEEL : MOUSEEVENTF_WHEEL, data: amount));
    }
}
