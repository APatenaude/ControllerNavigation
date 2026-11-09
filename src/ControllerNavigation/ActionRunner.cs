using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using Windows.Foundation;
using Windows.Media.Control;

namespace ControllerNavigation;

/// <summary>
/// Runs the system actions a button can be bound to (ActionKind.IsSystem): volume, media, window
/// moves, minimize all, the on-screen keyboard, run, pause, power off. They go through APIs, not
/// shortcut keys, so a game that swallows keys cannot eat them (SPEC "Layer 2"); a key shortcut is
/// only the fallback when Windows refuses an API. Run is called on the input thread and returns at
/// once: the work is queued to one STA worker thread (Shell COM needs one) that runs actions in
/// order. Repeats of a held button are cheap: the COM objects and the media session manager are kept.
/// </summary>
sealed class ActionRunner : IDisposable
{
    const int MaxQueued = 64;
    const int NoteEveryMs = 500;

    readonly Func<Feel> feel;
    readonly IKeyboardSurface keyboard;
    readonly Action togglePause;
    readonly BlockingCollection<Job> queue = new(MaxQueued);
    readonly Thread worker;
    readonly SoundVolume audio = new();
    readonly ShellCommands shell = new();
    readonly MediaSessions media = new();
    readonly Dictionary<string, long> nextNote = new();
    int disposed;

    readonly record struct Job(string Action, string? Arg, ActionTarget Target);

    public ActionRunner(Func<Feel> feel, IKeyboardSurface keyboard, Action togglePause)
    {
        this.feel = feel;
        this.keyboard = keyboard;
        this.togglePause = togglePause;
        worker = new Thread(Work) { IsBackground = true, Name = "ActionRunner" };
        worker.SetApartmentState(ApartmentState.STA);
        worker.Start();
    }

    /// <summary>Queues a system action for the worker thread; returns at once, never throws.</summary>
    /// <param name="action">An ActionKind id.</param>
    /// <param name="arg">The binding's argument (what to run for "run").</param>
    /// <param name="target">The window in front and the pad that asked.</param>
    public void Run(string action, string? arg, ActionTarget target)
    {
        try
        {
            if (!queue.TryAdd(new Job(action, arg, target)))
                Note("queueFull", new(false, $"Actions are queued up, {action} dropped"));
        }
        catch (Exception e) when (e is InvalidOperationException or ObjectDisposedException)
        {
            // disposed: the app is closing
        }
    }

    /// <summary>Finishes the queued actions (3 s at most) and releases the COM objects.</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
            return;
        queue.CompleteAdding();
        if (worker.Join(3000))
            queue.Dispose();
        else
            Log.Warn("Action worker did not stop in time");
    }

    void Work()
    {
        try
        {
            foreach (var job in queue.GetConsumingEnumerable())
            {
                try
                {
                    Execute(job);
                }
                catch (Exception e)
                {
                    Log.Error($"Action {job.Action} failed", e);
                }
            }
        }
        finally
        {
            audio.Dispose();
            shell.Dispose();
        }
    }

    void Execute(Job job)
    {
        switch (job.Action)
        {
            // Keys for Windows' slider, except with an elevated window in front: there Windows may
            // refuse the keys (the helper may not be connected yet), so the level is set directly.
            case ActionKind.VolumeUp:
                Note(
                    "volume",
                    feel().ShowVolumeSlider && !job.Target.Elevated
                        ? VolumeKeys(VkVolumeUp, feel().VolumeStep)
                        : VolumeResult(audio.Change(feel().VolumeStep))
                );
                break;
            case ActionKind.VolumeDown:
                Note(
                    "volume",
                    feel().ShowVolumeSlider && !job.Target.Elevated
                        ? VolumeKeys(VkVolumeDown, feel().VolumeStep)
                        : VolumeResult(audio.Change(-feel().VolumeStep))
                );
                break;
            case ActionKind.Mute:
                Note(
                    "volume",
                    feel().ShowVolumeSlider && !job.Target.Elevated
                        ? VolumeKeys(VkVolumeMute, 2)
                        : VolumeResult(audio.ToggleMute())
                );
                break;
            case ActionKind.PlayPause:
            case ActionKind.NextTrack:
            case ActionKind.PreviousTrack:
                media.Send(job.Action);
                break;
            case ActionKind.WindowLeft:
            case ActionKind.WindowRight:
            case ActionKind.Maximize:
            case ActionKind.Minimize:
                Note(job.Action, WindowMover.Run(job.Action, job.Target));
                break;
            case ActionKind.MinimizeAll:
                Note(job.Action, MinimizeAll());
                break;
            case ActionKind.Keyboard:
                keyboard.Toggle();
                Note(job.Action, new(true, "On-screen keyboard toggled"));
                break;
            case ActionKind.Pause:
                togglePause();
                Note(job.Action, new(true, "Pause toggled"));
                break;
            case ActionKind.Run:
                Note(job.Action, ShellCommands.Run(job.Arg));
                break;
            case ActionKind.PowerOff:
                Note(job.Action, PowerOff(job.Target.XInputSlot));
                break;
            default:
                Note(
                    "unknown:" + job.Action,
                    new(false, $"{job.Action} is not a system action, ignored")
                );
                break;
        }
    }

    const ushort VkVolumeMute = 0xAD,
        VkVolumeDown = 0xAE,
        VkVolumeUp = 0xAF;

    /// <summary>
    /// The volume key, pressed once per 2 % of the step (Windows' own step per press), so Windows
    /// shows its volume slider as for a keyboard's volume keys.
    /// </summary>
    static ActionResult VolumeKeys(ushort vk, int step)
    {
        var presses = Math.Max(1, (int)Math.Round(step / 2.0));
        for (var i = 0; i < presses; i++)
            Input.Tap(vk);
        return new(
            true,
            vk == VkVolumeMute
                ? "Mute key"
                : $"Volume key {(vk == VkVolumeUp ? "up" : "down")} x{presses}"
        );
    }

    static ActionResult VolumeResult(VolumeState? state) =>
        state is { } s
            ? new(true, s.ToString())
            : new(false, "Volume: no audio output could be reached");

    ActionResult MinimizeAll()
    {
        if (shell.MinimizeAll())
            return new(true, "Minimize all");
        Input.Tap(KeyCombo.Parse("Win+M")!);
        return new(true, "Minimize all: the shell refused, sent Win+M");
    }

    // A held button repeats an action every ~60 ms: one log line per key every NoteEveryMs.
    void Note(string key, ActionResult result)
    {
        var now = Environment.TickCount64;
        lock (nextNote)
        {
            if (nextNote.TryGetValue(key, out var next) && now < next)
                return;
            nextNote[key] = now + NoteEveryMs;
        }
        if (result.Ok)
            Log.Info(result.Message);
        else
            Log.Warn(result.Message);
    }

    // Undocumented, like HTPC's use of it: turns a wireless Xbox pad off, as holding its Home button does.
    [DllImport("xinput1_4.dll", EntryPoint = "#103")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    static extern uint XInputPowerOffController(uint index);

    static ActionResult PowerOff(int slot)
    {
        if (slot is < 0 or > 3)
            return new(
                false,
                "This pad cannot be turned off from Windows (only XInput pads can); its own button hold does it"
            );
        try
        {
            var result = XInputPowerOffController((uint)slot);
            return new(
                result == 0,
                $"Controller power off (slot {slot}): {(result == 0 ? "done" : $"refused ({result})")}"
            );
        }
        catch (Exception e)
        {
            return new(false, $"Controller power off (slot {slot}) failed: {e.Message}");
        }
    }
}

/// <summary>What an action did, for the log: a warning when it did not work.</summary>
readonly record struct ActionResult(bool Ok, string Message);

/// <summary>The default output's volume (0 to 100) and mute.</summary>
readonly record struct VolumeState(int Percent, bool Muted)
{
    public override string ToString() =>
        Muted ? $"Volume {Percent}% (muted)" : $"Volume {Percent}%";
}

/// <summary>
/// The default sound output's master volume and mute through Core Audio (IAudioEndpointVolume,
/// COM interop lifted from HTPC's AudioOutputs.cs / CoreAudio). The endpoint is kept between calls
/// (a held button repeats every ~60 ms) and acquired again when Windows says the default output
/// changed or a call fails. Used from the worker thread only, which also disposes it.
/// </summary>
sealed class SoundVolume : IDisposable
{
    [DllImport("ole32.dll")]
    static extern int CoCreateInstance(
        ref Guid clsid,
        IntPtr outer,
        int context,
        ref Guid iid,
        out IntPtr instance
    );

    static readonly Guid EnumeratorClass = new("BCDE0395-E52F-467C-8E3D-C4579291692E"),
        IUnknown = new("00000000-0000-0000-C000-000000000046");

    // Methods in vtable order up to the ones used (mmdeviceapi.h, endpointvolume.h).
    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDeviceEnumerator
    {
        [PreserveSig]
        int EnumAudioEndpoints(int dataFlow, int stateMask, out IntPtr devices);

        [PreserveSig]
        int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice device);

        [PreserveSig]
        int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice device);

        [PreserveSig]
        int RegisterEndpointNotificationCallback(IMMNotificationClient client);

        [PreserveSig]
        int UnregisterEndpointNotificationCallback(IMMNotificationClient client);
    }

    [StructLayout(LayoutKind.Sequential)]
    struct PropKey
    {
        public Guid Format;
        public int Id;
    }

    [ComImport, Guid("7991EEC9-7E89-4D85-8390-6C703CEC60C0")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMNotificationClient
    {
        [PreserveSig]
        int OnDeviceStateChanged([MarshalAs(UnmanagedType.LPWStr)] string id, int state);

        [PreserveSig]
        int OnDeviceAdded([MarshalAs(UnmanagedType.LPWStr)] string id);

        [PreserveSig]
        int OnDeviceRemoved([MarshalAs(UnmanagedType.LPWStr)] string id);

        [PreserveSig]
        int OnDefaultDeviceChanged(
            int flow,
            int role,
            [MarshalAs(UnmanagedType.LPWStr)] string? id
        );

        [PreserveSig]
        int OnPropertyValueChanged([MarshalAs(UnmanagedType.LPWStr)] string id, PropKey key);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDevice
    {
        [PreserveSig]
        int Activate(
            ref Guid iid,
            int clsCtx,
            IntPtr activationParams,
            [MarshalAs(UnmanagedType.IUnknown)] out object endpoint
        );

        [PreserveSig]
        int OpenPropertyStore(int access, out IntPtr properties);

        [PreserveSig]
        int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
    }

    [ComImport, Guid("657804FA-D6AD-4496-8A60-352752AF4F89")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioEndpointVolumeCallback
    {
        [PreserveSig]
        int OnNotify(IntPtr data);
    }

    [ComImport, Guid("5CDF2C82-841E-4546-9722-0CF74078229A")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioEndpointVolume
    {
        [PreserveSig]
        int RegisterControlChangeNotify(IAudioEndpointVolumeCallback notify);

        [PreserveSig]
        int UnregisterControlChangeNotify(IAudioEndpointVolumeCallback notify);

        [PreserveSig]
        int GetChannelCount(out uint count);

        [PreserveSig]
        int SetMasterVolumeLevel(float levelDb, ref Guid context);

        [PreserveSig]
        int SetMasterVolumeLevelScalar(float level, ref Guid context);

        [PreserveSig]
        int GetMasterVolumeLevel(out float levelDb);

        [PreserveSig]
        int GetMasterVolumeLevelScalar(out float level);

        [PreserveSig]
        int SetChannelVolumeLevel(uint channel, float levelDb, ref Guid context);

        [PreserveSig]
        int SetChannelVolumeLevelScalar(uint channel, float level, ref Guid context);

        [PreserveSig]
        int GetChannelVolumeLevel(uint channel, out float levelDb);

        [PreserveSig]
        int GetChannelVolumeLevelScalar(uint channel, out float level);

        [PreserveSig]
        int SetMute([MarshalAs(UnmanagedType.Bool)] bool mute, ref Guid context);

        [PreserveSig]
        int GetMute([MarshalAs(UnmanagedType.Bool)] out bool mute);
    }

    const int eRender = 0,
        eMultimedia = 1,
        CLSCTX_ALL = 23;

    object? enumerator;
    object? endpointObject;
    IAudioEndpointVolume? endpoint;
    DefaultOutputWatcher? watcher;
    bool watchTried;

    // Set from a thread of Windows' own when the default output or a device's state changes.
    volatile bool stale = true;

    /// <summary>A volume of 0..100 moved by a signed number of points, kept in range.</summary>
    internal static int Stepped(int percent, int delta) => Math.Clamp(percent + delta, 0, 100);

    /// <summary>Moves the volume by delta points (clamped to 0..100); a rise also unmutes. Null when no output answers.</summary>
    public VolumeState? Change(int delta) =>
        With(v =>
        {
            Check(v.GetMasterVolumeLevelScalar(out var level));
            Check(v.GetMute(out var muted));
            var percent = Stepped((int)Math.Round(level * 100), delta);
            var context = Guid.Empty;
            if (percent != (int)Math.Round(level * 100))
                Check(v.SetMasterVolumeLevelScalar(percent / 100f, ref context));
            if (delta > 0 && muted)
            {
                Check(v.SetMute(false, ref context));
                muted = false;
            }
            return new VolumeState(percent, muted);
        });

    /// <summary>Mutes or unmutes. Null when no output answers.</summary>
    public VolumeState? ToggleMute() =>
        With(v =>
        {
            Check(v.GetMasterVolumeLevelScalar(out var level));
            Check(v.GetMute(out var muted));
            var context = Guid.Empty;
            Check(v.SetMute(!muted, ref context));
            return new VolumeState((int)Math.Round(level * 100), !muted);
        });

    public void Dispose()
    {
        ReleaseEndpoint();
        if (watcher is not null && enumerator is not null)
        {
            try
            {
                ((IMMDeviceEnumerator)enumerator).UnregisterEndpointNotificationCallback(watcher);
            }
            catch (Exception) { }
        }
        watcher = null;
        Release(ref enumerator);
    }

    // One retry on a fresh endpoint: the default output may have gone since the last call.
    VolumeState? With(Func<IAudioEndpointVolume, VolumeState> use)
    {
        for (var attempt = 0; attempt < 2; attempt++)
        {
            try
            {
                return use(Acquire());
            }
            catch (Exception e)
            {
                ReleaseEndpoint();
                if (attempt == 1)
                    Log.Warn($"Volume: {e.Message}");
            }
        }
        return null;
    }

    IAudioEndpointVolume Acquire()
    {
        if (endpoint is not null && !stale)
            return endpoint;
        ReleaseEndpoint();
        enumerator ??= NewEnumerator();
        WatchDefaultOutput();
        // Cleared before the lookup, so a change during it marks the endpoint stale again;
        // never cleared without a watcher (then every call looks the output up).
        stale = watcher is null;
        var devices = (IMMDeviceEnumerator)enumerator;
        Check(devices.GetDefaultAudioEndpoint(eRender, eMultimedia, out var device));
        try
        {
            var iid = typeof(IAudioEndpointVolume).GUID;
            Check(device.Activate(ref iid, CLSCTX_ALL, IntPtr.Zero, out var opened));
            endpointObject = opened;
            endpoint = (IAudioEndpointVolume)opened;
            return endpoint;
        }
        finally
        {
            Marshal.ReleaseComObject(device);
        }
    }

    void WatchDefaultOutput()
    {
        if (watchTried)
            return;
        watchTried = true;
        try
        {
            var w = new DefaultOutputWatcher(() => stale = true);
            Check(((IMMDeviceEnumerator)enumerator!).RegisterEndpointNotificationCallback(w));
            watcher = w;
        }
        catch (Exception e)
        {
            Log.Warn(
                $"Watching the sound outputs: {e.Message}; the volume looks the output up each time"
            );
        }
    }

    void ReleaseEndpoint()
    {
        endpoint = null;
        Release(ref endpointObject);
    }

    static void Check(int hr) => Marshal.ThrowExceptionForHR(hr);

    static void Release(ref object? o)
    {
        if (o is not null && Marshal.IsComObject(o))
            Marshal.ReleaseComObject(o);
        o = null;
    }

    // A wrapper of the enumerator for this caller alone (GetUniqueObjectForIUnknown): HTPC found that
    // a shared one throws InvalidCastException while another wrapper of it is alive.
    static object NewEnumerator()
    {
        var clsid = EnumeratorClass;
        var iid = IUnknown;
        Check(CoCreateInstance(ref clsid, IntPtr.Zero, CLSCTX_ALL, ref iid, out var unknown));
        try
        {
            return Marshal.GetUniqueObjectForIUnknown(unknown);
        }
        finally
        {
            Marshal.Release(unknown);
        }
    }

    // Windows calls these on a thread of its own: only note the change, never call Core Audio here.
    [ComVisible(true)]
    sealed class DefaultOutputWatcher(Action changed) : IMMNotificationClient
    {
        int Changed()
        {
            changed();
            return 0;
        }

        public int OnDeviceStateChanged(string id, int state) => Changed();

        public int OnDeviceAdded(string id) => Changed();

        public int OnDeviceRemoved(string id) => Changed();

        public int OnDefaultDeviceChanged(int flow, int role, string? id) => Changed();

        public int OnPropertyValueChanged(string id, PropKey key) => 0;
    }
}

/// <summary>
/// Play / pause, next and previous through the system media session manager (the session Windows
/// calls current), the way the media overlay does; the media key is the fallback when there is no
/// session or it refuses. The manager is requested once and kept. Calls run on the thread pool, one
/// after the other, so a slow player never holds the worker thread.
/// </summary>
sealed class MediaSessions
{
    static readonly TimeSpan CallTimeout = TimeSpan.FromSeconds(2);

    readonly object gate = new();
    Task tail = Task.CompletedTask;
    GlobalSystemMediaTransportControlsSessionManager? manager;

    /// <summary>The virtual-key code of a media action's key (ActionKind.PlayPause, NextTrack, PreviousTrack).</summary>
    internal static ushort KeyOf(string action) =>
        action switch
        {
            ActionKind.NextTrack => 0xB0,
            ActionKind.PreviousTrack => 0xB1,
            _ => 0xB3,
        };

    /// <summary>Queues the media action; returns at once, never throws.</summary>
    public void Send(string action)
    {
        lock (gate)
            tail = tail.ContinueWith(
                    _ => SendAsync(action),
                    CancellationToken.None,
                    TaskContinuationOptions.None,
                    TaskScheduler.Default
                )
                .Unwrap();
    }

    async Task SendAsync(string action)
    {
        string? source = null;
        var why = "no media session";
        try
        {
            manager ??= await Timed(
                GlobalSystemMediaTransportControlsSessionManager.RequestAsync(),
                "the media sessions"
            );
            var session = manager.GetCurrentSession();
            if (session is not null)
            {
                source = session.SourceAppUserModelId;
                if (await Timed(Operation(session, action), $"media {action} to {source}"))
                {
                    Log.Info($"Media {action} to {source}: done");
                    return;
                }
                why = $"{source} refused it";
            }
        }
        catch (Exception e)
        {
            manager = null; // asked for again next time (the media service restarted)
            why = e.Message;
        }
        Log.Info($"Media {action}: {why}, sent the media key");
        try
        {
            Input.Tap(KeyOf(action));
        }
        catch (Exception e)
        {
            Log.Warn($"Media key for {action}: {e.Message}");
        }
    }

    static IAsyncOperation<bool> Operation(
        GlobalSystemMediaTransportControlsSession session,
        string action
    ) =>
        action switch
        {
            ActionKind.NextTrack => session.TrySkipNextAsync(),
            ActionKind.PreviousTrack => session.TrySkipPreviousAsync(),
            _ => session.TryTogglePlayPauseAsync(),
        };

    // A frozen player never answers: wait CallTimeout, then cancel and leave the call behind.
    static async Task<T> Timed<T>(IAsyncOperation<T> operation, string what)
    {
        var task = operation.AsTask();
        try
        {
            return await task.WaitAsync(CallTimeout);
        }
        catch (TimeoutException)
        {
            try
            {
                operation.Cancel();
            }
            catch (Exception) { }
            _ = task.ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);
            throw new TimeoutException(
                $"{what}: no answer within {CallTimeout.TotalSeconds:0.##} s"
            );
        }
    }
}

/// <summary>A rectangle in physical pixels (Right and Bottom exclusive).</summary>
readonly record struct PixelRect(int Left, int Top, int Right, int Bottom)
{
    public int Width => Right - Left;
    public int Height => Bottom - Top;
}

/// <summary>
/// Window actions on the window in front: move to the screen on the left or right, maximize or
/// restore, minimize. Done with SetWindowPos / ShowWindowAsync; when Windows refuses (an elevated
/// window under UIPI, or the pad asked for an elevated target) the Win-key shortcut Windows has
/// for it is sent instead (through the elevated helper when Input.ToElevated is set).
/// </summary>
static class WindowMover
{
    [StructLayout(LayoutKind.Sequential)]
    struct NativeRect
    {
        public int Left,
            Top,
            Right,
            Bottom;

        public PixelRect ToPixelRect() => new(Left, Top, Right, Bottom);
    }

    [StructLayout(LayoutKind.Sequential)]
    struct MonitorInfo
    {
        public int Size;
        public NativeRect Monitor,
            Work;
        public uint Flags;
    }

    delegate bool MonitorEnumProc(IntPtr monitor, IntPtr dc, IntPtr rect, IntPtr data);

    [DllImport("user32.dll")]
    static extern IntPtr GetAncestor(IntPtr window, uint flags);

    [DllImport("user32.dll")]
    static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

    [DllImport("user32.dll", EntryPoint = "GetClassNameW", CharSet = CharSet.Unicode)]
    static extern int GetClassName(IntPtr window, StringBuilder text, int max);

    [DllImport("user32.dll")]
    static extern bool IsWindow(IntPtr window);

    [DllImport("user32.dll")]
    static extern bool IsZoomed(IntPtr window);

    [DllImport("user32.dll")]
    static extern bool IsIconic(IntPtr window);

    [DllImport("user32.dll")]
    static extern bool ShowWindow(IntPtr window, int command);

    [DllImport("user32.dll", SetLastError = true)]
    static extern bool ShowWindowAsync(IntPtr window, int command);

    [DllImport("user32.dll", SetLastError = true)]
    static extern bool GetWindowRect(IntPtr window, out NativeRect rect);

    [DllImport("user32.dll", SetLastError = true)]
    static extern bool SetWindowPos(
        IntPtr window,
        IntPtr insertAfter,
        int x,
        int y,
        int width,
        int height,
        uint flags
    );

    [DllImport("user32.dll")]
    static extern IntPtr MonitorFromWindow(IntPtr window, uint flags);

    [DllImport("user32.dll")]
    static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);

    [DllImport("user32.dll")]
    static extern bool EnumDisplayMonitors(
        IntPtr dc,
        IntPtr clip,
        MonitorEnumProc callback,
        IntPtr data
    );

    const uint GA_ROOTOWNER = 3;
    const uint MONITOR_DEFAULTTONEAREST = 2;
    const int SW_MAXIMIZE = 3,
        SW_MINIMIZE = 6,
        SW_RESTORE = 9;
    const uint SWP_NOZORDER = 0x4,
        SWP_NOACTIVATE = 0x10,
        SWP_NOOWNERZORDER = 0x200;

    static readonly string[] ShellClasses =
    {
        "Progman",
        "WorkerW",
        "Shell_TrayWnd",
        "Shell_SecondaryTrayWnd",
    };

    enum Step
    {
        Done,
        NothingToDo,
        Refused,
    }

    /// <summary>
    /// The Windows shortcut that does the same as the action, sent when Windows refuses the API:
    /// a window move, maximize (restore when it is maximized) or minimize. Null for other actions.
    /// </summary>
    internal static string? FallbackCombo(string action, bool maximized) =>
        action switch
        {
            ActionKind.WindowLeft => "Win+Shift+Left",
            ActionKind.WindowRight => "Win+Shift+Right",
            ActionKind.Maximize => maximized ? "Win+Down" : "Win+Up",
            ActionKind.Minimize => "Win+Down",
            _ => null,
        };

    /// <summary>The desktop and the shell's windows: never moved, maximized or minimized.</summary>
    internal static bool IsShellClass(string className) =>
        ShellClasses.Contains(className, StringComparer.Ordinal);

    /// <summary>
    /// The monitor on one side of the current one: among the monitors whose centre is left (or
    /// right) of the current monitor's centre, the nearest, then the one closest vertically. None
    /// on that side (the end of the row, or screens stacked one above the other): the next one
    /// round, as Windows' Win+Shift+Left/Right does, in left-then-top order. -1 with one screen.
    /// </summary>
    internal static int PickMonitor(IReadOnlyList<PixelRect> monitors, int current, bool toRight)
    {
        var here = monitors[current];
        var best = -1;
        long bestAcross = 0,
            bestUp = 0;
        for (var i = 0; i < monitors.Count; i++)
        {
            if (i == current)
                continue;
            // Left + Right is twice the centre: no fractions.
            long across = toRight
                ? (monitors[i].Left + monitors[i].Right) - (here.Left + here.Right)
                : (here.Left + here.Right) - (monitors[i].Left + monitors[i].Right);
            if (across <= 0)
                continue;
            long up = Math.Abs((monitors[i].Top + monitors[i].Bottom) - (here.Top + here.Bottom));
            if (best < 0 || across < bestAcross || (across == bestAcross && up < bestUp))
                (best, bestAcross, bestUp) = (i, across, up);
        }
        if (best >= 0 || monitors.Count < 2)
            return best;
        var round = Enumerable
            .Range(0, monitors.Count)
            .OrderBy(i => monitors[i].Left)
            .ThenBy(i => monitors[i].Top)
            .ToList();
        var at = round.IndexOf(current);
        return round[(at + (toRight ? 1 : monitors.Count - 1)) % monitors.Count];
    }

    /// <summary>
    /// A window's rectangle on another work area: the same share of it (position and size),
    /// shrunk to fit and kept inside it.
    /// </summary>
    internal static PixelRect Transfer(PixelRect window, PixelRect from, PixelRect to)
    {
        double sx = (double)to.Width / Math.Max(1, from.Width),
            sy = (double)to.Height / Math.Max(1, from.Height);
        var width = Math.Clamp((int)Math.Round(window.Width * sx), 1, Math.Max(1, to.Width));
        var height = Math.Clamp((int)Math.Round(window.Height * sy), 1, Math.Max(1, to.Height));
        var left = to.Left + (int)Math.Round((window.Left - from.Left) * sx);
        var top = to.Top + (int)Math.Round((window.Top - from.Top) * sy);
        left = Math.Clamp(left, to.Left, Math.Max(to.Left, to.Right - width));
        top = Math.Clamp(top, to.Top, Math.Max(to.Top, to.Bottom - height));
        return new PixelRect(left, top, left + width, top + height);
    }

    /// <summary>Runs one window action (ActionKind.WindowLeft, WindowRight, Maximize, Minimize) on the target's window.</summary>
    public static ActionResult Run(string action, ActionTarget target)
    {
        var window = RootWindow(target.Window);
        if (window == IntPtr.Zero)
            return new(false, $"{action}: no window in front");
        if (NotForUs(window) is { } why)
            return new(true, $"{action}: skipped, {why}");

        var refusal = "the window is elevated";
        if (!target.Elevated)
        {
            var step = Act(action, window, out var message);
            if (step != Step.Refused)
                return new(true, message);
            refusal = message;
        }
        var combo = FallbackCombo(action, IsZoomed(window));
        if (combo is null)
            return new(false, $"{action}: not a window action");
        Input.Tap(KeyCombo.Parse(combo)!);
        return new(true, $"{action}: refused ({refusal}), sent {combo}");
    }

    static Step Act(string action, IntPtr window, out string message)
    {
        switch (action)
        {
            case ActionKind.Maximize:
                var restore = IsZoomed(window);
                return Show(
                    window,
                    restore ? SW_RESTORE : SW_MAXIMIZE,
                    restore ? "Window restored" : "Window maximized",
                    out message
                );
            case ActionKind.Minimize:
                return Show(window, SW_MINIMIZE, "Window minimized", out message);
            default:
                return MoveToSide(window, action == ActionKind.WindowRight, out message);
        }
    }

    static Step Show(IntPtr window, int command, string done, out string message)
    {
        if (ShowWindowAsync(window, command))
        {
            message = done;
            return Step.Done;
        }
        message = $"ShowWindowAsync error {Marshal.GetLastWin32Error()}";
        return Step.Refused;
    }

    static Step MoveToSide(IntPtr window, bool toRight, out string message)
    {
        var side = toRight ? "right" : "left";
        var screens = Screens();
        var here = MonitorFromWindow(window, MONITOR_DEFAULTTONEAREST);
        var from = screens.FindIndex(s => s.Handle == here);
        if (from < 0 || IsIconic(window))
        {
            message = "Window not moved: no visible screen to move it from";
            return Step.NothingToDo;
        }
        var to = PickMonitor(screens.Select(s => s.Monitor).ToList(), from, toRight);
        if (to < 0)
        {
            message = $"Window not moved: no screen on the {side}";
            return Step.NothingToDo;
        }

        // A maximized window is restored, moved and maximized again on the other screen.
        var maximized = IsZoomed(window);
        if (maximized)
        {
            ShowWindow(window, SW_RESTORE);
            if (IsZoomed(window))
            {
                message = "the window would not restore";
                return Step.Refused;
            }
        }
        if (!GetWindowRect(window, out var current))
        {
            message = $"GetWindowRect error {Marshal.GetLastWin32Error()}";
            if (maximized)
                ShowWindow(window, SW_MAXIMIZE);
            return Step.Refused;
        }

        var wanted = Transfer(current.ToPixelRect(), screens[from].Work, screens[to].Work);
        var moved = Place(window, wanted, out var error);
        // Moving between screens of different scale makes the window resize itself: once more
        // fixes the size.
        if (moved && GetWindowRect(window, out var after) && after.ToPixelRect() != wanted)
            moved = Place(window, wanted, out error);
        if (maximized)
            ShowWindow(window, SW_MAXIMIZE);
        message = moved
            ? $"Window moved to the screen on the {side}"
            : $"SetWindowPos error {error}";
        return moved ? Step.Done : Step.Refused;
    }

    static bool Place(IntPtr window, PixelRect r, out int error)
    {
        var ok = SetWindowPos(
            window,
            IntPtr.Zero,
            r.Left,
            r.Top,
            r.Width,
            r.Height,
            SWP_NOZORDER | SWP_NOACTIVATE | SWP_NOOWNERZORDER
        );
        error = ok ? 0 : Marshal.GetLastWin32Error();
        return ok;
    }

    static IntPtr RootWindow(IntPtr window)
    {
        if (window == IntPtr.Zero || !IsWindow(window))
            return IntPtr.Zero;
        var root = GetAncestor(window, GA_ROOTOWNER);
        return root == IntPtr.Zero ? window : root;
    }

    // Null when the window may be acted on; else why not.
    static string? NotForUs(IntPtr window)
    {
        var name = new StringBuilder(64);
        GetClassName(window, name, name.Capacity);
        if (IsShellClass(name.ToString()))
            return "it is the desktop or the taskbar";
        GetWindowThreadProcessId(window, out var process);
        return process == Environment.ProcessId ? "it is this tool's own window" : null;
    }

    static List<(IntPtr Handle, PixelRect Monitor, PixelRect Work)> Screens()
    {
        var list = new List<(IntPtr, PixelRect, PixelRect)>();
        EnumDisplayMonitors(
            IntPtr.Zero,
            IntPtr.Zero,
            (monitor, _, _, _) =>
            {
                var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
                if (GetMonitorInfo(monitor, ref info))
                    list.Add((monitor, info.Monitor.ToPixelRect(), info.Work.ToPixelRect()));
                return true;
            },
            IntPtr.Zero
        );
        return list;
    }
}

/// <summary>
/// Shell actions: minimize all windows (Shell.Application, late-bound COM created once, on the STA
/// worker thread that calls it) and run a program, file or URL.
/// </summary>
sealed class ShellCommands : IDisposable
{
    object? shell;

    /// <summary>Shell.Application.MinimizeAll; false when the shell would not (the caller sends Win+M).</summary>
    public bool MinimizeAll()
    {
        try
        {
            shell ??= Activator.CreateInstance(Type.GetTypeFromProgID("Shell.Application", true)!);
            shell!
                .GetType()
                .InvokeMember("MinimizeAll", BindingFlags.InvokeMethod, null, shell, null);
            return true;
        }
        catch (Exception e)
        {
            Log.Warn($"Shell.Application MinimizeAll: {e.Message}");
            Dispose(); // created again next time (the shell restarted)
            return false;
        }
    }

    public void Dispose()
    {
        if (shell is not null && Marshal.IsComObject(shell))
            Marshal.ReleaseComObject(shell);
        shell = null;
    }

    /// <summary>
    /// Starts a program path, a file or a URL with the shell, as the binding's argument says it:
    /// a quoted path then its arguments, an existing path as it is, a URL as it is, else the first
    /// word and its arguments. Never throws.
    /// </summary>
    public static ActionResult Run(string? command)
    {
        if (string.IsNullOrWhiteSpace(command))
            return new(false, "Run: nothing to run");
        var (file, arguments) = SplitCommand(
            Environment.ExpandEnvironmentVariables(command),
            path => File.Exists(path) || Directory.Exists(path)
        );
        try
        {
            var info = new ProcessStartInfo(file) { UseShellExecute = true };
            if (arguments is not null)
                info.Arguments = arguments;
            if (File.Exists(file))
                info.WorkingDirectory = Path.GetDirectoryName(Path.GetFullPath(file)) ?? "";
            Process.Start(info)?.Dispose();
            return new(true, $"Run {file}{(arguments is null ? "" : " " + arguments)}");
        }
        catch (Exception e)
        {
            return new(false, $"Run {file} failed: {e.Message}");
        }
    }

    /// <summary>The file (or URL) and the arguments of a run command; exists says whether a path is a file or folder.</summary>
    internal static (string File, string? Arguments) SplitCommand(
        string command,
        Func<string, bool> exists
    )
    {
        var text = command.Trim();
        if (text.StartsWith('"'))
        {
            var close = text.IndexOf('"', 1);
            if (close < 0)
                return (text[1..], null);
            var rest = text[(close + 1)..].Trim();
            return (text[1..close], rest.Length == 0 ? null : rest);
        }
        if (text.Contains("://") || !text.Contains(' ') || exists(text))
            return (text, null);
        // An unquoted path with spaces ("C:\Program Files\App\app.exe --flag"): the longest start that exists.
        for (var i = text.LastIndexOf(' '); i > 0; i = text.LastIndexOf(' ', i - 1))
            if (exists(text[..i]))
                return (text[..i], text[(i + 1)..].Trim());
        var space = text.IndexOf(' ');
        return (text[..space], text[(space + 1)..].Trim());
    }
}
