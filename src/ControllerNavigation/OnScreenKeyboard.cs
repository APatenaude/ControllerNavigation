using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace ControllerNavigation;

/// <summary>
/// The on-screen keyboard (lifted from HTPC): a band across the bottom of the screen the app in
/// front is on, which never takes the focus, so what it types (Input.Type, Input.Tap) goes to that
/// app. While it is open the engine hands it the base map's presses (Pad); the page keeps the
/// highlighted key, Shift and the symbols page. Its WebView2 starts at the first opening and stays
/// afterwards (a hidden window is cheap, starting one is not).
/// </summary>
sealed class OnScreenKeyboard : IKeyboardSurface, IDisposable
{
    readonly SynchronizationContext ui;
    volatile bool isOpen;
    KeyboardForm? form; // UI thread only
    bool runtimeMissingShown;

    public OnScreenKeyboard(SynchronizationContext ui) => this.ui = ui;

    public bool IsOpen => isOpen;

    public void Toggle() =>
        ui.Post(
            _ =>
            {
                if (isOpen)
                    CloseOnUi();
                else
                    OpenOnUi();
            },
            null
        );

    public void Close()
    {
        isOpen = false;
        ui.Post(_ => CloseOnUi(), null);
    }

    public void Pad(PadControl control, bool repeat)
    {
        if (!isOpen)
            return;
        if (control == PadControl.R3 || (control == PadControl.East && !repeat))
        {
            Close();
            return;
        }
        if (ButtonName(control, repeat) is { } button)
            ui.Post(_ => form?.Press(button), null);
    }

    public void Dispose()
    {
        isOpen = false;
        ReleaseModifiers();
        if (form is not { IsDisposed: false } f)
            return;
        try
        {
            if (f.InvokeRequired)
                f.BeginInvoke(f.Dispose);
            else
                f.Dispose();
        }
        catch (InvalidOperationException) { }
    }

    // The page's button names (keyboard.js onButton). Only movement, delete, cursor and held
    // directions repeat: a held A must not type the same letter over and over.
    static string? ButtonName(PadControl control, bool repeat) =>
        control switch
        {
            PadControl.Up => "up",
            PadControl.Down => "down",
            PadControl.Left => "left",
            PadControl.Right => "right",
            PadControl.West => "x",
            PadControl.LB => "lb",
            PadControl.RB => "rb",
            PadControl.South when !repeat => "a",
            PadControl.North when !repeat => "y",
            PadControl.LT when !repeat => "lt",
            PadControl.RT when !repeat => "rt",
            PadControl.Start when !repeat => "start",
            PadControl.Back when !repeat => "tab",
            _ => null,
        };

    void OpenOnUi()
    {
        try
        {
            if (form is null or { IsDisposed: true })
            {
                form = new KeyboardForm();
                form.Message += OnPageMessage;
                form.Failed += OnFailed;
                form.PageReset += ReleaseModifiers;
                _ = form.StartAsync();
            }
            ReleaseModifiers(); // the page opens with every modifier off
            isOpen = true;
            form.Open();
            Log.Info("Keyboard opened");
        }
        catch (Exception e)
        {
            isOpen = false;
            Log.Error("On-screen keyboard could not open", e);
        }
    }

    void CloseOnUi()
    {
        isOpen = false;
        ReleaseModifiers();
        if (form is { Visible: true } f)
        {
            f.Hide();
            Log.Info("Keyboard closed");
        }
    }

    // The modifiers the page holds down in Windows (its Ctrl, Alt, Shift and Win toggles): a held
    // Alt plus a tapped Tab is Alt+Tab. All released when the keyboard closes.
    readonly HashSet<ushort> held = new();
    readonly HashSet<ushort> used = new(); // held modifiers that a key or combination went out with
    readonly object heldLock = new();
    const ushort Alt = 0x12,
        Win = 0x5B,
        MaskKey = 0xE8; // unassigned: tapped so that Windows does not read Alt or Win let go of alone as a tap

    void ReleaseModifiers()
    {
        ushort[] release;
        bool mask;
        lock (heldLock)
        {
            release = held.ToArray();
            mask = release.Any(m => m is Alt or Win && !used.Contains(m));
            held.Clear();
            used.Clear();
        }
        if (mask)
            Input.Tap(MaskKey);
        if (release.Length > 0)
            Input.KeysUp(release);
    }

    // The page's messages: text to type, a key combination to tap, a modifier to hold or let go, or "close".
    // Only "up" and "close" still count once the keyboard is closed (a late "down" would hold a modifier nobody sees).
    void OnPageMessage(JsonElement m)
    {
        var type = WebUi.Str(m, "type");
        if (!isOpen && type is "type" or "combo" or "down")
            return;
        switch (type)
        {
            case "type":
                if (WebUi.Str(m, "text") is { Length: > 0 and <= 16 } text)
                {
                    MarkUsed();
                    Input.Type(text);
                }
                break;
            case "combo":
                if (KeyCombo.Parse(WebUi.Str(m, "keys")) is { } keys)
                    TapWithHeld(keys);
                break;
            case "down":
            case "up":
                if (
                    KeyCombo.Parse(WebUi.Str(m, "keys")) is [var modifier]
                    && KeyCombo.Modifiers.Contains(modifier)
                )
                    SetModifier(modifier, type == "down");
                break;
            case "close":
                CloseOnUi();
                break;
        }
    }

    void MarkUsed()
    {
        lock (heldLock)
            used.UnionWith(held);
    }

    void SetModifier(ushort modifier, bool down)
    {
        bool mask = false;
        lock (heldLock)
        {
            if (down)
            {
                if (!held.Add(modifier))
                    return;
                used.Remove(modifier);
            }
            else
            {
                if (!held.Remove(modifier))
                    return;
                mask = modifier is Alt or Win && !used.Contains(modifier);
                used.Remove(modifier);
            }
        }
        if (down)
        {
            Input.KeysDown(new[] { modifier });
            return;
        }
        if (mask)
            Input.Tap(MaskKey);
        Input.KeysUp(new[] { modifier });
    }

    // A modifier that is already held is not pressed (and released) again with the combination.
    void TapWithHeld(ushort[] keys)
    {
        ushort[] rest;
        lock (heldLock)
        {
            rest = keys.Where(k => !held.Contains(k)).ToArray();
            used.UnionWith(held);
        }
        if (rest.Length > 0)
            Input.Tap(rest);
    }

    void OnFailed(Exception e)
    {
        isOpen = false;
        ReleaseModifiers();
        var missing = WebUi.IsRuntimeMissing(e);
        Log.Error("On-screen keyboard: WebView2 did not start", e);
        form?.Dispose();
        form = null;
        if (!missing || runtimeMissingShown)
            return;
        runtimeMissingShown = true;
        MessageBox.Show(
            "The on-screen keyboard needs the Microsoft Edge WebView2 Runtime, which is missing or damaged on this PC. Install it from https://developer.microsoft.com/microsoft-edge/webview2/ and press the keyboard button again.",
            AppInfo.Name,
            MessageBoxButtons.OK,
            MessageBoxIcon.Warning
        );
    }
}

/// <summary>
/// The keyboard's window: borderless, topmost, a tool window that is never activated (not even by
/// a click), so the app in front keeps the focus. It is a band the screen's whole width, as high as
/// 440 of 1080 of that screen; the page (1920x440) scales to it.
/// </summary>
sealed class KeyboardForm : Form
{
    const int WS_EX_TOPMOST = 0x8,
        WS_EX_TOOLWINDOW = 0x80,
        WS_EX_NOACTIVATE = 0x08000000,
        WM_MOUSEACTIVATE = 0x21,
        MA_NOACTIVATE = 3;
    const int HeightOf1080 = 440;
    static readonly Color Back = Color.FromArgb(22, 24, 29);

    [DllImport("user32.dll")]
    static extern IntPtr GetForegroundWindow();

    readonly WebView2 web = new() { Dock = DockStyle.Fill, DefaultBackgroundColor = Back };
    readonly List<object> waiting = new(); // posted before the page said it was ready
    bool ready;

    /// <summary>A message from the page (type, combo, close). UI thread.</summary>
    public event Action<JsonElement>? Message;

    /// <summary>The page started again after it had been ready (it was reloaded). UI thread.</summary>
    public event Action? PageReset;

    /// <summary>The WebView2 could not start or died. UI thread.</summary>
    public event Action<Exception>? Failed;

    public KeyboardForm()
    {
        Text = "Keyboard";
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        ShowInTaskbar = false;
        BackColor = Back;
        Controls.Add(web);
    }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var p = base.CreateParams;
            p.ExStyle |= WS_EX_NOACTIVATE | WS_EX_TOPMOST | WS_EX_TOOLWINDOW;
            return p;
        }
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WM_MOUSEACTIVATE)
        {
            m.Result = MA_NOACTIVATE;
            return;
        }
        base.WndProc(ref m);
    }

    // WinForms would move the window to the rectangle Windows suggests for the new DPI: the band's instead.
    protected override void OnDpiChanged(DpiChangedEventArgs e)
    {
        base.OnDpiChanged(e);
        Fit();
    }

    /// <summary>Starts WebView2 and loads the page; reports a failure through Failed.</summary>
    public async Task StartAsync()
    {
        try
        {
            _ = Handle; // WebView2 needs the window to exist; it stays hidden until opened
            await web.EnsureCoreWebView2Async(await WebUi.GetEnvironmentAsync());
            var core = web.CoreWebView2;
            WebUi.Attach(
                core,
                "Keyboard",
                why => Failed?.Invoke(new InvalidOperationException(why))
            );
            core.WebMessageReceived += (_, e) =>
            {
                if (WebUi.Read(e, "Keyboard") is not { } m)
                    return;
                if (WebUi.Str(m, "type") == "ready")
                {
                    // A second "ready": the page was reloaded (a renderer crash) and shows no modifier held.
                    if (ready)
                        PageReset?.Invoke();
                    ready = true;
                    foreach (var queued in waiting)
                        WebUi.Post(core, queued);
                    waiting.Clear();
                    return;
                }
                Message?.Invoke(m);
            };
            core.Navigate(WebUi.Origin + "/keyboard.html");
        }
        catch (Exception e)
        {
            Failed?.Invoke(e);
        }
    }

    /// <summary>Puts the band on the screen the app in front is on and shows it without taking the focus.</summary>
    public void Open()
    {
        Fit();
        Post(new { type = "open" });
        if (!Visible)
            Show();
    }

    /// <summary>A button press for the page ("up", "a", "lt", ...).</summary>
    public void Press(string button)
    {
        Fit(); // the app may have moved to another screen since
        Post(new { type = "input", button });
    }

    // Kept until the page is ready (the first opening starts WebView2).
    void Post(object message)
    {
        if (ready)
            WebUi.Post(web.CoreWebView2, message);
        else if (waiting.Count < 32)
            waiting.Add(message);
    }

    void Fit()
    {
        var window = GetForegroundWindow();
        var screen =
            window != IntPtr.Zero && window != Handle
                ? Screen.FromHandle(window)
                : Screen.PrimaryScreen!;
        var area = screen.Bounds;
        var height = area.Height * HeightOf1080 / 1080;
        var band = new Rectangle(area.X, area.Bottom - height, area.Width, height);
        if (Bounds != band)
            Bounds = band;
    }
}
