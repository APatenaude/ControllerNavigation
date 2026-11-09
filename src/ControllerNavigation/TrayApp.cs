using System.Drawing.Drawing2D;
using System.Reflection;
using Microsoft.Win32;

namespace ControllerNavigation;

/// <summary>
/// The app: the tray icon and its menu (SPEC "Tray icon"), and what ties the parts together:
/// settings, the input thread (PadReader feeding the Engine with the ForegroundTracker), the
/// outputs (Input, PointerFrames, ActionRunner, the on-screen keyboard), the elevated helper, the
/// updater, the settings window.
/// </summary>
sealed class TrayApp : ApplicationContext, ISettingsHost
{
    /// <summary>The pad must have rested this long before an update restarts the app.</summary>
    static readonly TimeSpan UpdateIdle = TimeSpan.FromMinutes(5);

    readonly SynchronizationContext ui;
    readonly SettingsStore store = new();
    readonly OnScreenKeyboard keyboard;
    readonly GameDetector detector;
    readonly ForegroundTracker tracker;
    readonly PointerFrames pointer = new();
    readonly ActionRunner runner;
    readonly Engine engine;
    readonly PadReader pads;
    readonly ElevatedInput elevated = new();
    readonly Updater updater;
    readonly NotifyIcon icon;
    readonly TrayIcons icons = new();
    readonly System.Windows.Forms.Timer stateTimer = new() { Interval = 500 };
    readonly EventWaitHandle showSettings,
        exitRequest;
    readonly RegisteredWaitHandle showWait,
        exitWait;
    SettingsForm? settingsForm;
    volatile bool paused,
        locked;
    string? shownState;

    public TrayApp()
    {
        ui = new WindowsFormsSynchronizationContext();
        SynchronizationContext.SetSynchronizationContext(ui);
        store.Load();
        keyboard = new OnScreenKeyboard(ui);
        detector = new GameDetector(() => store.Current);
        tracker = new ForegroundTracker(() => store.Current, detector);
        runner = new ActionRunner(() => store.Current.Feel, keyboard, TogglePause);
        engine = new Engine(new Output(this), keyboard, store.Current);
        pads = new PadReader(() => store.Current, tracker, engine);
        store.Changed += s =>
        {
            engine.Apply(s);
            RaiseChanged();
        };
        pads.Changed += RaiseChanged;
        pads.Changed += detector.DevicesChanged;
        Input.ElevatedSink = elevated.TrySend;
        var started = Environment.TickCount64;
        updater = new Updater(
            padIdle: () =>
                Environment.TickCount64 - Math.Max(started, pads.LastInputTick)
                > UpdateIdle.TotalMilliseconds,
            autoUpdate: () => store.Current.AutoUpdate
        );
        updater.Changed += RaiseChanged;

        icon = new NotifyIcon
        {
            Icon = icons.Off,
            Text = AppInfo.Name,
            Visible = true,
            ContextMenuStrip = BuildMenu(),
        };
        icon.MouseUp += (_, e) =>
        {
            if (e.Button == MouseButtons.Left)
                OpenMenu();
        };
        icon.DoubleClick += (_, _) => ShowSettings();
        stateTimer.Tick += (_, _) => ShowState();
        stateTimer.Start();

        // A second start opens the settings window here; the update job asks this copy to exit.
        showSettings = new EventWaitHandle(
            false,
            EventResetMode.AutoReset,
            Program.ShowSettingsEvent
        );
        exitRequest = new EventWaitHandle(false, EventResetMode.ManualReset, Program.ExitEvent);
        // Still set when the update job that asked the old copy to exit starts this one while it holds the event open.
        exitRequest.Reset();
        showWait = ThreadPool.RegisterWaitForSingleObject(
            showSettings,
            (_, _) => ui.Post(_ => ShowSettings(), null),
            null,
            -1,
            false
        );
        exitWait = ThreadPool.RegisterWaitForSingleObject(
            exitRequest,
            (_, _) =>
                ui.Post(
                    _ =>
                    {
                        Log.Info("Asked to exit (update)");
                        ExitThread();
                    },
                    null
                ),
            null,
            -1,
            true
        );

        SystemEvents.SessionSwitch += OnSessionSwitch;
        pads.Start();
        updater.Start();
        ShowState();
    }

    void TogglePause()
    {
        paused = !paused;
        Log.Info(paused ? "Paused" : "Resumed");
        ApplyPause();
        ui.Post(_ => ShowState(), null);
    }

    /// <summary>
    /// Locked or paused: everything off, layers included (nothing could reach a lock screen anyway).
    /// Sleep needs nothing: the pads drop off and the engine lets go then, and no resume event is
    /// relied on (Windows sends none to apps after a wake nobody touched the keyboard or mouse for).
    /// </summary>
    void ApplyPause()
    {
        engine.Paused = paused || locked;
        tracker.Suspended = locked;
        // Paused, the pad no longer reaches the keyboard, so it closes (letting go of its held modifiers).
        if (paused || locked)
            keyboard.Close();
    }

    void OnSessionSwitch(object? sender, SessionSwitchEventArgs e)
    {
        if (
            e.Reason
            is SessionSwitchReason.SessionLock
                or SessionSwitchReason.ConsoleDisconnect
                or SessionSwitchReason.RemoteDisconnect
        )
            locked = true;
        else if (
            e.Reason
            is SessionSwitchReason.SessionUnlock
                or SessionSwitchReason.ConsoleConnect
                or SessionSwitchReason.RemoteConnect
        )
            locked = false;
        else
            return;
        Log.Info($"Session: {e.Reason}");
        ApplyPause();
    }

    // --- Tray -------------------------------------------------------------------------------

    ContextMenuStrip BuildMenu()
    {
        var menu = new ContextMenuStrip();
        menu.Opening += (_, _) => FillMenu(menu);
        return menu;
    }

    /// <summary>Built at each opening: "the app that was in front" is the last one before the tray was clicked.</summary>
    void FillMenu(ContextMenuStrip menu)
    {
        menu.Items.Clear();
        menu.Items.Add(
            new ToolStripMenuItem($"{AppInfo.Name} {AppInfo.Version}") { Enabled = false }
        );
        menu.Items.Add(new ToolStripSeparator());
        if (tracker.LastOther is { } last)
        {
            menu.Items.Add(new ToolStripMenuItem($"Mouse in {last.Exe}") { Enabled = false });
            foreach (var item in ModeItems(last.Exe))
                menu.Items.Add(item);
        }
        var running = new ToolStripMenuItem("Running apps");
        foreach (var app in ForegroundTracker.RunningApps())
        {
            var entry = new ToolStripMenuItem(
                $"{app.Exe}  ({Shorten(app.Title, 50)})  {ModeOf(app.Exe)}"
            );
            entry.DropDownItems.AddRange(ModeItems(app.Exe));
            running.DropDownItems.Add(entry);
        }
        if (running.DropDownItems.Count == 0)
            running.DropDownItems.Add(new ToolStripMenuItem("No windows") { Enabled = false });
        menu.Items.Add(running);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(
            new ToolStripMenuItem(paused ? "Resume" : "Pause", null, (_, _) => TogglePause())
            {
                Checked = paused,
            }
        );
        menu.Items.Add(new ToolStripMenuItem("Settings...", null, (_, _) => ShowSettings()));
        menu.Items.Add(new ToolStripMenuItem("Exit", null, (_, _) => ExitThread()));
    }

    static string Shorten(string text, int max) =>
        text.Length <= max ? text : text[..(max - 3)] + "...";

    AppRule? RuleOf(string exe) =>
        store.Current.Apps.FirstOrDefault(a =>
            a.Enabled && a.Exes.Contains(exe, StringComparer.OrdinalIgnoreCase)
        );

    /// <summary>"Always", "Excluded" or "Automatic" for an exe.</summary>
    string ModeOf(string exe) =>
        RuleOf(exe)?.Mode switch
        {
            AppMode.Always => "Always",
            AppMode.Never => "Excluded",
            _ => "Automatic",
        };

    /// <summary>The three choices for an exe, the current one checked.</summary>
    ToolStripItem[] ModeItems(string exe)
    {
        var current = RuleOf(exe)?.Mode;
        ToolStripMenuItem Item(string text, string? mode) =>
            new(text, null, (_, _) => SetMode(exe, mode)) { Checked = current == mode };
        return new ToolStripItem[]
        {
            Item("Automatic (unless it reads the controller or is a game)", null),
            Item("Always use the mouse here", AppMode.Always),
            Item("Exclude (never use the mouse here)", AppMode.Never),
        };
    }

    /// <summary>
    /// Gives an exe a fixed answer (its own rule, or the existing rule with that answer switched back
    /// on), or makes it automatic again (taken out of every rule; a rule left with no exe goes).
    /// </summary>
    void SetMode(string exe, string? mode)
    {
        var s = SettingsStore.Copy(store.Current);
        // Kept only when it already gives this answer and is on: switching a whole unticked rule
        // back on (every browser for chrome.exe) would change more than this one app.
        var same = mode is null
            ? null
            : s.Apps.FirstOrDefault(a =>
                a.Enabled
                && a.Mode == mode
                && a.Exes.Contains(exe, StringComparer.OrdinalIgnoreCase)
            );
        var emptied = new List<AppRule>();
        foreach (var rule in s.Apps.Where(r => r != same))
            if (
                rule.Exes.RemoveAll(e => e.Equals(exe, StringComparison.OrdinalIgnoreCase)) > 0
                && rule.Exes.Count == 0
            )
                emptied.Add(rule);
        s.Apps.RemoveAll(emptied.Contains);
        if (same is null && mode is not null)
            s.Apps.Add(
                new AppRule
                {
                    Name = Path.GetFileNameWithoutExtension(exe),
                    Exes = new() { exe },
                    Mode = mode,
                }
            );
        Log.Info($"Tray: {exe} set to {mode ?? "automatic"}");
        store.Save(s);
    }

    /// <summary>A left click opens the same menu as a right click (NotifyIcon only does that itself for the right button).</summary>
    void OpenMenu() =>
        typeof(NotifyIcon)
            .GetMethod("ShowContextMenu", BindingFlags.Instance | BindingFlags.NonPublic)
            ?.Invoke(icon, null);

    void ShowSettings()
    {
        if (settingsForm is null || settingsForm.IsDisposed)
        {
            settingsForm = new SettingsForm(this);
            settingsForm.FormClosed += (_, _) => settingsForm = null;
            settingsForm.Show();
        }
        if (settingsForm.WindowState == FormWindowState.Minimized)
            settingsForm.WindowState = FormWindowState.Normal;
        settingsForm.Activate();
    }

    /// <summary>The icon and its tooltip: paused, no controller, mouse (the app in front gets it, and why), or left alone (and why).</summary>
    void ShowState()
    {
        var connected = pads.Connected.Count(p => p.Enabled);
        var front = tracker.Current;
        var (state, image) =
            paused ? ("paused", icons.Paused)
            : connected == 0 ? ("no controller", icons.Off)
            : tracker.LastAllowed
                ? ($"mouse in {front.Exe ?? "this app"} ({tracker.Reason})", icons.On)
            : ($"{front.Exe ?? "nothing"} left alone ({tracker.Reason})", icons.Off);
        var update =
            updater.Status.State == "available" || updater.Status.State == "waitingIdle"
                ? $"\nUpdate {updater.Status.Latest} ready"
                : "";
        var text = $"{AppInfo.Name}: {state}{update}";
        if (text == shownState)
            return;
        shownState = text;
        icon.Icon = image;
        icon.Text = Shorten(text, 127);
    }

    void RaiseChanged()
    {
        try
        {
            Changed?.Invoke();
        }
        catch (Exception e)
        {
            Log.Error("Settings window refresh", e);
        }
    }

    protected override void ExitThreadCore()
    {
        stateTimer.Stop();
        SystemEvents.SessionSwitch -= OnSessionSwitch;
        showWait.Unregister(null);
        exitWait.Unregister(null);
        settingsForm?.Close();
        pads.Dispose();
        updater.Dispose();
        runner.Dispose();
        keyboard.Dispose();
        detector.Dispose();
        elevated.Dispose();
        icon.Visible = false;
        icon.Dispose();
        icons.Dispose();
        showSettings.Dispose();
        exitRequest.Dispose();
        base.ExitThreadCore();
    }

    // --- ISettingsHost (the settings window, UI thread) -------------------------------------

    public Settings Settings => store.Current;

    public bool Save(Settings settings) => store.Save(settings);

    /// <summary>The connected pads, then the ones the settings know that are not connected now.</summary>
    public IReadOnlyList<PadInfo> Pads()
    {
        var list = pads.Connected.ToList();
        foreach (var (id, p) in store.Current.Pads)
            if (list.All(c => c.Id != id))
                list.Add(
                    new PadInfo(
                        id,
                        p.Name,
                        "unknown",
                        0,
                        0,
                        false,
                        p.Enabled,
                        false,
                        true,
                        "",
                        null
                    )
                );
        return list;
    }

    public PadLive Live() => pads.Live;

    public RawJoystick? Raw(string padId) => pads.Raw(padId);

    public bool AddMapping(string mapping)
    {
        var task = pads.AddMapping(mapping);
        if (!task.Wait(TimeSpan.FromSeconds(2)) || !task.Result)
            return false;
        var s = SettingsStore.Copy(store.Current);
        var guid = mapping.Split(',')[0];
        s.Mappings.RemoveAll(m => m.Split(',')[0].Equals(guid, StringComparison.OrdinalIgnoreCase));
        s.Mappings.Add(mapping);
        return store.Save(s);
    }

    public IReadOnlyList<RunningApp> RunningApps() => ForegroundTracker.RunningApps();

    public string? LastTitle(string exe) => tracker.LastTitle(exe);

    public UpdateStatus Update => updater.Status;

    public Task CheckForUpdate() => updater.CheckNowAsync();

    public IReadOnlyList<CheckResult> Checks() => ControllerNavigation.Checks.Run();

    public event Action? Changed;

    /// <summary>What the engine asks for (input thread). Keyboard and pause are done here: they must not wait behind a slow audio call in the runner's queue.</summary>
    sealed class Output(TrayApp app) : IOutput
    {
        public void KeysDown(ushort[] keys) => Input.KeysDown(keys);

        public void KeysUp(ushort[] keys) => Input.KeysUp(keys);

        public void Mouse(Input.Button button, bool down) => Input.MouseButton(button, down);

        public void Wheel(int amount, bool horizontal) => Input.Wheel(amount, horizontal);

        public void Motion(Motion? motion) => app.pointer.Set(motion);

        public void System(string action, string? arg)
        {
            if (action == ActionKind.Keyboard)
                app.keyboard.Toggle();
            else if (action == ActionKind.Pause)
                app.TogglePause();
            else
            {
                var front = app.tracker.Current;
                app.runner.Run(
                    action,
                    arg,
                    new ActionTarget(front.Window, front.Elevated, app.pads.XInputSlot)
                );
            }
        }
    }
}

/// <summary>
/// The tray's icons, drawn at the tray's size with the shapes of dev\Build-Icon.ps1 (keep the two
/// in step): blue when the controller drives the app in front, grey when it does not, grey with
/// pause bars when paused.
/// </summary>
sealed class TrayIcons : IDisposable
{
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    static extern bool DestroyIcon(IntPtr icon);

    public Icon On { get; }
    public Icon Off { get; }
    public Icon Paused { get; }

    public TrayIcons()
    {
        var size = SystemInformation.SmallIconSize.Width;
        On = Draw(size, Color.FromArgb(37, 99, 235), pauseBars: false);
        Off = Draw(size, Color.FromArgb(107, 114, 128), pauseBars: false);
        Paused = Draw(size, Color.FromArgb(107, 114, 128), pauseBars: true);
    }

    static Icon Draw(int size, Color tile, bool pauseBars)
    {
        using var bmp = new Bitmap(size, size);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);
            var s = size / 32f;
            using var tileBrush = new SolidBrush(tile);
            using var white = new SolidBrush(Color.White);
            using (var path = Rounded(0, 0, size, size, 7 * s))
                g.FillPath(tileBrush, path);
            using (var body = Rounded(5 * s, 10 * s, 22 * s, 11 * s, 5.5f * s))
                g.FillPath(white, body);
            g.FillEllipse(white, 4 * s, 13 * s, 9 * s, 11 * s);
            g.FillEllipse(white, 19 * s, 13 * s, 9 * s, 11 * s);
            g.FillRectangle(tileBrush, 8 * s, 14.5f * s, 6 * s, 2 * s);
            g.FillRectangle(tileBrush, 10 * s, 12.5f * s, 2 * s, 6 * s);
            g.FillEllipse(tileBrush, 20 * s, 13 * s, 2.6f * s, 2.6f * s);
            g.FillEllipse(tileBrush, 22.4f * s, 15.4f * s, 2.6f * s, 2.6f * s);
            g.FillEllipse(tileBrush, 17.6f * s, 15.4f * s, 2.6f * s, 2.6f * s);
            g.FillEllipse(tileBrush, 20 * s, 17.8f * s, 2.6f * s, 2.6f * s);
            if (pauseBars)
            {
                using var dark = new SolidBrush(Color.FromArgb(31, 41, 55));
                g.FillEllipse(dark, 15 * s, 15 * s, 17 * s, 17 * s);
                g.FillRectangle(white, 19.5f * s, 19 * s, 3 * s, 9 * s);
                g.FillRectangle(white, 24.5f * s, 19 * s, 3 * s, 9 * s);
            }
        }
        var handle = bmp.GetHicon();
        // Icon.FromHandle does not own the handle: clone, then free ours.
        var icon = (Icon)Icon.FromHandle(handle).Clone();
        DestroyIcon(handle);
        return icon;
    }

    static GraphicsPath Rounded(float x, float y, float w, float h, float r)
    {
        var p = new GraphicsPath();
        var d = 2 * r;
        p.AddArc(x, y, d, d, 180, 90);
        p.AddArc(x + w - d, y, d, d, 270, 90);
        p.AddArc(x + w - d, y + h - d, d, d, 0, 90);
        p.AddArc(x, y + h - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    public void Dispose()
    {
        On.Dispose();
        Off.Dispose();
        Paused.Dispose();
    }
}
