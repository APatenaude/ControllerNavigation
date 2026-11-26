using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Runtime.InteropServices;

namespace ControllerNavigation;

/// <summary>What the menu shows about the window in front: its exe (null: nothing, or this tool's own: no mode to set), its path (for the icon), its name, whether it gets the mouse, and why.</summary>
sealed record MenuFront(
    IntPtr Window,
    string? Exe,
    string? Path,
    string Name,
    bool Allowed,
    string Reason
);

/// <summary>What the menu reads and changes (TrayApp). UI thread.</summary>
interface IMenuHost
{
    MenuFront Front();
    Settings Settings { get; }

    /// <summary>AppMode.Always, AppMode.Never, or null (automatic).</summary>
    string? ModeOf(string exe);

    /// <summary>Saves the exe's mode as an app rule, as the tray's three choices do.</summary>
    void SetMode(string exe, string? mode, string from);

    /// <summary>Saves a change to the feel on top of the current settings.</summary>
    void ChangeFeel(Action<Feel> change);
    bool Paused { get; }
    void TogglePause();

    /// <summary>The pad that pressed last can be turned off (an XInput pad).</summary>
    bool CanTurnOff { get; }
    void TurnOffController();
    void ShowSettings();
}

enum MenuRow
{
    Mode,
    Speed,
    Acceleration,
    Scroll,
    Pause,
    TurnOff,
    Buttons,
    Settings,
}

/// <summary>A line of the buttons page: a control and what it does, or a heading.</summary>
sealed record CheatLine(string Control, string Action, bool Heading = false);

/// <summary>
/// The controller menu (SPEC "Controller menu"): a dark panel down the left edge of the screen the
/// app in front is on, which never takes the focus, so that app stays in front and keeps its answer.
/// Start + R3 opens it; while it is open the engine hands it every press (Pad). Up and down move,
/// left and right change the row's value (the app's mode is saved only with A, the speeds a moment
/// after the last change so the stick can try them), A does the row, B or Start closes, and so do
/// 15 s without a press and another window coming in front.
/// </summary>
sealed class ControllerMenu : IMenuSurface, IDisposable
{
    const int IdleCloseMs = 15_000,
        SaveAfterMs = 600;
    static readonly string?[] Modes = { null, AppMode.Always, AppMode.Never };

    /// <summary>A row that changes a number of the feel: its step and range in the menu, how it shows.</summary>
    internal sealed record Knob(
        double Step,
        double Min,
        double Max,
        Func<Feel, double> Get,
        Action<Feel, double> Set,
        Func<double, string> Show
    );

    internal static readonly Dictionary<MenuRow, Knob> Knobs = new()
    {
        [MenuRow.Speed] = new(
            0.1,
            0.2,
            4,
            f => f.PointerSpeed,
            (f, v) => f.PointerSpeed = v,
            v => $"x{v:0.0#}"
        ),
        [MenuRow.Acceleration] = new(
            0.25,
            1,
            5,
            f => f.Acceleration,
            (f, v) => f.Acceleration = v,
            v => v <= 1 ? "Off" : $"x{v:0.0#}"
        ),
        [MenuRow.Scroll] = new(
            0.1,
            0.2,
            4,
            f => f.ScrollSpeed,
            (f, v) => f.ScrollSpeed = v,
            v => $"x{v:0.0#}"
        ),
    };

    [DllImport("user32.dll")]
    static extern IntPtr GetForegroundWindow();

    readonly SynchronizationContext ui;
    readonly IMenuHost host;
    readonly System.Windows.Forms.Timer watch = new() { Interval = 250 };
    volatile bool isOpen;

    // UI thread only.
    MenuForm? form;
    MenuFront front = new(IntPtr.Zero, null, null, "", false, "");
    long lastPress,
        changedAt;
    readonly HashSet<MenuRow> changed = new();

    public ControllerMenu(SynchronizationContext ui, IMenuHost host)
    {
        this.ui = ui;
        this.host = host;
        watch.Tick += (_, _) => Watch();
    }

    public bool IsOpen => isOpen;

    public void Open()
    {
        if (isOpen)
            return;
        isOpen = true;
        ui.Post(_ => OpenOnUi(), null);
    }

    public void Pad(PadControl control, bool repeat)
    {
        if (isOpen)
            ui.Post(_ => Press(control, repeat), null);
    }

    /// <summary>Closes it if open (the session locked); any thread.</summary>
    public void Close()
    {
        isOpen = false;
        ui.Post(_ => CloseOnUi(), null);
    }

    public void Dispose()
    {
        isOpen = false;
        watch.Dispose();
        form?.Dispose();
    }

    void OpenOnUi()
    {
        try
        {
            front = host.Front();
            form ??= new MenuForm();
            var view = form.View;
            var settings = host.Settings;
            view.Exe = front.Exe;
            view.Name = front.Name;
            view.Status =
                host.Paused ? MenuStatus.Paused
                : front.Allowed ? MenuStatus.On
                : MenuStatus.Off;
            view.Reason = front.Reason;
            view.SetIcon(front.Path);
            view.Mode = view.Saved = front.Exe is { } exe
                ? Array.IndexOf(Modes, host.ModeOf(exe))
                : 0;
            view.Feel = SettingsStore.Copy(settings).Feel;
            view.Rows = RowsFor(front.Exe is not null, host.CanTurnOff);
            view.Lines = ButtonsOf(settings);
            (view.Focus, view.Page, view.Scroll) = (0, MenuPage.Main, 0);
            changed.Clear();
            lastPress = Environment.TickCount64;
            form.Place(front.Window);
            form.Invalidate();
            if (!form.Visible)
                form.Show();
            watch.Start();
            Log.Info($"Menu opened over {front.Exe ?? front.Name} ({view.Status}: {front.Reason})");
        }
        catch (Exception e)
        {
            Log.Error("Menu could not open", e);
            CloseOnUi();
        }
    }

    internal static MenuRow[] RowsFor(bool app, bool canTurnOff) =>
        new[]
        {
            MenuRow.Mode,
            MenuRow.Speed,
            MenuRow.Acceleration,
            MenuRow.Scroll,
            MenuRow.Pause,
            MenuRow.TurnOff,
            MenuRow.Buttons,
            MenuRow.Settings,
        }
            .Where(r => (r != MenuRow.Mode || app) && (r != MenuRow.TurnOff || canTurnOff))
            .ToArray();

    void CloseOnUi()
    {
        isOpen = false;
        watch.Stop();
        SaveFeel();
        form?.Hide();
    }

    void Watch()
    {
        var now = Environment.TickCount64;
        if (changed.Count > 0 && now - changedAt >= SaveAfterMs)
            SaveFeel();
        var window = GetForegroundWindow();
        if (
            now - lastPress > IdleCloseMs
            // NULL for a moment is a window closing or a mode switch, not another app
            || window != IntPtr.Zero && window != front.Window
        )
            CloseOnUi();
    }

    void SaveFeel()
    {
        if (changed.Count == 0 || form is null)
            return;
        var feel = form.View.Feel;
        var rows = changed.ToArray();
        changed.Clear();
        host.ChangeFeel(f =>
        {
            foreach (var row in rows)
                Knobs[row].Set(f, Knobs[row].Get(feel));
        });
        Log.Info("Menu: " + string.Join(", ", rows.Select(r => $"{r} {Knobs[r].Get(feel)}")));
    }

    void Press(PadControl control, bool repeat)
    {
        if (!isOpen || form is null)
            return;
        lastPress = Environment.TickCount64;
        var view = form.View;
        if (view.Page == MenuPage.Buttons)
        {
            switch (control)
            {
                case PadControl.Up:
                    view.Scroll = Math.Max(0, view.Scroll - 1);
                    break;
                case PadControl.Down:
                    view.Scroll = Math.Min(form.MostScroll, view.Scroll + 1);
                    break;
                case PadControl.East or PadControl.South when !repeat:
                    view.Page = MenuPage.Main;
                    break;
                case PadControl.Start when !repeat:
                    CloseOnUi();
                    return;
                default:
                    return;
            }
            form.Invalidate();
            return;
        }
        var row = view.Rows[view.Focus];
        var sign = control == PadControl.Left ? -1 : 1;
        switch (control)
        {
            case PadControl.Up:
                view.Focus = Math.Max(0, view.Focus - 1);
                break;
            case PadControl.Down:
                view.Focus = Math.Min(view.Rows.Length - 1, view.Focus + 1);
                break;
            case PadControl.Left or PadControl.Right when row == MenuRow.Mode:
                view.Mode = Math.Clamp(view.Mode + sign, 0, Modes.Length - 1);
                break;
            case PadControl.Left or PadControl.Right when Knobs.TryGetValue(row, out var knob):
                // A value the settings window set outside the menu's range is kept, not jumped from.
                var old = knob.Get(view.Feel);
                var value = Math.Round(old / knob.Step + sign) * knob.Step;
                knob.Set(
                    view.Feel,
                    Math.Clamp(value, Math.Min(knob.Min, old), Math.Max(knob.Max, old))
                );
                changed.Add(row);
                changedAt = lastPress;
                break;
            case PadControl.South when !repeat:
                Pick(row, view);
                return;
            case PadControl.East or PadControl.Start when !repeat:
                CloseOnUi();
                return;
            default:
                return;
        }
        form.Invalidate();
    }

    void Pick(MenuRow row, MenuView view)
    {
        switch (row)
        {
            case MenuRow.Mode:
                CloseOnUi();
                if (view.Mode != view.Saved && front.Exe is { } exe)
                    host.SetMode(exe, Modes[view.Mode], "Menu");
                break;
            case MenuRow.Pause:
                CloseOnUi();
                host.TogglePause();
                break;
            case MenuRow.TurnOff:
                CloseOnUi();
                host.TurnOffController();
                break;
            case MenuRow.Buttons:
                (view.Page, view.Scroll) = (MenuPage.Buttons, 0);
                form!.Invalidate();
                break;
            case MenuRow.Settings:
                CloseOnUi();
                host.ShowSettings();
                break;
        }
    }

    // --- The buttons page -----------------------------------------------------------------------

    /// <summary>The sticks, the base map, then each layer: what every bound control does, in short.</summary>
    internal static List<CheatLine> ButtonsOf(Settings s)
    {
        var lines = new List<CheatLine>
        {
            new("Buttons", "", Heading: true),
            new(
                "Left stick",
                SettingsForm.StickLabels.GetValueOrDefault(s.Base.LeftStick, s.Base.LeftStick)
            ),
            new(
                "Right stick",
                SettingsForm.StickLabels.GetValueOrDefault(s.Base.RightStick, s.Base.RightStick)
            ),
        };
        void Add(Dictionary<string, PadBinding> buttons)
        {
            foreach (var id in Catalog.ControlIds.Concat(Catalog.Pairs.Select(p => p.Id)))
                if (ActionName(buttons.GetValueOrDefault(id)) is { } action)
                    lines.Add(new(ControlName(id), action));
        }
        Add(s.Base.Buttons);
        foreach (var layer in s.Layers)
        {
            var button = ControlName(layer.Button);
            lines.Add(new($"Hold {button}", "", Heading: true));
            if (ActionName(layer.Tap) is { } tap)
                lines.Add(new($"{button} alone", tap));
            if (ActionName(layer.Hold) is { } hold)
                lines.Add(new($"{button} held", hold));
            Add(layer.Buttons);
        }
        return lines;
    }

    /// <summary>"A", "View", "LB + RB", "D-pad up": the Xbox-style name without its details.</summary>
    static string ControlName(string id) =>
        CutAtFirst(Catalog.Labels.GetValueOrDefault(id, id), " (", " / ", " together");

    static string? ActionName(PadBinding? b)
    {
        if (b is null)
            return null;
        var main = b.Action switch
        {
            ActionKind.None => null,
            ActionKind.Key => b.Arg,
            ActionKind.Run => "Run " + Path.GetFileName(b.Arg ?? ""),
            _ => CutAtFirst(ActionKind.Labels.GetValueOrDefault(b.Action, b.Action), " ("),
        };
        return (main, ActionName(b.Hold)) switch
        {
            (null, null) => null,
            (_, null) => main,
            (null, var hold) => "hold: " + hold,
            (_, var hold) => $"{main}, hold: {hold}",
        };
    }

    static string CutAtFirst(string text, params string[] marks)
    {
        var at = marks.Select(m => text.IndexOf(m, StringComparison.Ordinal)).Where(i => i > 0);
        return at.Any() ? text[..at.Min()] : text;
    }
}

enum MenuPage
{
    Main,
    Buttons,
}

enum MenuStatus
{
    On,
    Off,
    Paused,
}

/// <summary>What the menu window draws: set by ControllerMenu, read by MenuForm's paint.</summary>
sealed class MenuView : IDisposable
{
    public static readonly string[] ModeNames = { "Automatic", "Always", "Excluded" };

    /// <summary>The app in front; null: nothing in front, or this tool's own window (no mode row).</summary>
    public string? Exe;
    public string Name = "",
        Reason = "";
    public MenuStatus Status;
    public Bitmap? Icon;

    /// <summary>Indexes in ModeNames: the one picked with left and right, the one saved.</summary>
    public int Mode,
        Saved;

    /// <summary>A copy of the settings' feel, changed by the speed rows.</summary>
    public Feel Feel = new();
    public MenuRow[] Rows = Array.Empty<MenuRow>();
    public int Focus;
    public MenuPage Page;
    public List<CheatLine> Lines = new();
    public int Scroll;

    /// <summary>The exe's own icon, drawn at the size the header needs; none when it has none.</summary>
    public void SetIcon(string? path)
    {
        Icon?.Dispose();
        Icon = null;
        if (path is null)
            return;
        try
        {
            using var icon = System.Drawing.Icon.ExtractIcon(path, 0, 128);
            Icon = icon?.ToBitmap();
        }
        catch (Exception e)
            when (e
                    is IOException
                        or UnauthorizedAccessException
                        or ArgumentException
                        or ExternalException
            )
        {
            // an exe the shell cannot read (gone after an update, an offline share; a COMException
            // for E_FAIL): the header without its icon
        }
    }

    public void Dispose() => Icon?.Dispose();
}

/// <summary>
/// The menu's window: borderless, topmost, a tool window that is never activated, as the on-screen
/// keyboard's. The screen's full height, sized in units of 1/40 of it (so it reads from a couch on
/// a TV), drawn in Windows 11's dark colors with its icon font.
/// </summary>
sealed class MenuForm : Form
{
    const int WS_EX_TOPMOST = 0x8,
        WS_EX_TOOLWINDOW = 0x80,
        WS_EX_NOACTIVATE = 0x08000000,
        WM_MOUSEACTIVATE = 0x21,
        MA_NOACTIVATE = 3;

    static readonly Color Back = Color.FromArgb(32, 32, 32),
        Card = Color.FromArgb(45, 45, 45),
        Raised = Color.FromArgb(58, 58, 58),
        Edge = Color.FromArgb(64, 64, 64),
        Primary = Color.White,
        Secondary = Color.FromArgb(160, 160, 160),
        Faint = Color.FromArgb(110, 110, 110),
        Accent = Color.FromArgb(96, 205, 255),
        OnAccent = Color.Black,
        Green = Color.FromArgb(108, 203, 95),
        Amber = Color.FromArgb(252, 200, 60);

    static readonly string TextFont = FirstInstalled("Segoe UI Variable Text", "Segoe UI"),
        TitleFont = FirstInstalled("Segoe UI Variable Display", "Segoe UI"),
        IconFont = FirstInstalled("Segoe Fluent Icons", "Segoe MDL2 Assets");

    // Segoe Fluent Icons' code points (the same in Segoe MDL2 Assets).
    const string MouseIcon = "",
        PointerIcon = "",
        SpeedIcon = "",
        ScrollIcon = "",
        PauseIcon = "",
        PlayIcon = "",
        PowerIcon = "",
        PadIcon = "",
        GearIcon = "",
        LeftIcon = "",
        RightIcon = "";

    public readonly MenuView View = new();
    IntPtr over;
    float u = 27; // the unit: 1/40 of the screen's height

    /// <summary>How far the buttons page can scroll, as last drawn (lines that did not fit).</summary>
    public int MostScroll { get; private set; }

    public MenuForm()
    {
        Text = "Controller menu";
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        ShowInTaskbar = false;
        AutoScaleMode = AutoScaleMode.None;
        BackColor = Back;
        SetStyle(
            ControlStyles.AllPaintingInWmPaint
                | ControlStyles.UserPaint
                | ControlStyles.OptimizedDoubleBuffer,
            true
        );
    }

    static string FirstInstalled(params string[] families)
    {
        using var installed = new InstalledFontCollection();
        return families.FirstOrDefault(f => installed.Families.Any(i => i.Name == f))
            ?? families[^1];
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

    // WinForms would move the window to the rectangle Windows suggests for the new DPI.
    protected override void OnDpiChanged(DpiChangedEventArgs e)
    {
        base.OnDpiChanged(e);
        Place(over);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            View.Dispose();
        base.Dispose(disposing);
    }

    /// <summary>Down the left edge of the screen this window is on (or the pointer's).</summary>
    public void Place(IntPtr window)
    {
        over = window;
        PlaceOn(
            (
                window != IntPtr.Zero
                    ? Screen.FromHandle(window)
                    : Screen.FromPoint(Cursor.Position)
            ).Bounds
        );
    }

    /// <summary>Down the left edge of this screen area (the tests draw it at any size).</summary>
    internal void PlaceOn(Rectangle area)
    {
        u = area.Height / 40f;
        var panel = new Rectangle(area.X, area.Y, (int)(14 * u), area.Height);
        if (Bounds != panel)
            Bounds = panel;
    }

    // --- Painting -------------------------------------------------------------------------------

    Graphics g = null!;
    float pad,
        right;

    Font Px(float units, string family, FontStyle style = FontStyle.Regular) =>
        new(family, units * u, style, GraphicsUnit.Pixel);

    void Label(
        string text,
        Font font,
        Color color,
        RectangleF box,
        TextFormatFlags align = TextFormatFlags.Left
    ) =>
        TextRenderer.DrawText(
            g,
            text,
            font,
            Rectangle.Round(box),
            color,
            align
                | TextFormatFlags.VerticalCenter
                | TextFormatFlags.SingleLine
                | TextFormatFlags.EndEllipsis
                | TextFormatFlags.NoPadding
        );

    void Fill(Color color, RectangleF box, float radius)
    {
        using var brush = new SolidBrush(color);
        using var path = TrayIcons.Rounded(box.X, box.Y, box.Width, box.Height, radius);
        g.FillPath(brush, path);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        pad = u;
        right = ClientSize.Width - pad;
        var y = Header(pad);
        y = View.Page == MenuPage.Main ? Rows(y) : Buttons(y);
        Footer();
        using var line = new Pen(Edge);
        g.DrawLine(line, ClientSize.Width - 1, 0, ClientSize.Width - 1, ClientSize.Height);
    }

    // The app's icon and name, then a dot and why it gets the mouse or not.
    float Header(float top)
    {
        using var title = Px(0.82f, TitleFont, FontStyle.Bold);
        using var small = Px(0.5f, TextFont);
        var left = pad;
        if (View.Icon is { } icon)
        {
            var size = 1.7f * u;
            g.DrawImage(icon, pad, top + 0.05f * u, size, size);
            left += size + 0.5f * u;
        }
        Label(View.Name, title, Primary, new RectangleF(left, top, right - left, 1.1f * u));
        var (dot, status) = View.Status switch
        {
            MenuStatus.Paused => (Secondary, "Paused"),
            MenuStatus.On => (Green, "Mouse on"),
            _ => (Amber, "Left alone"),
        };
        var lineTop = top + 1.15f * u;
        using (var brush = new SolidBrush(dot))
            g.FillEllipse(brush, left, lineTop + 0.2f * u, 0.36f * u, 0.36f * u);
        var reason = View.Status == MenuStatus.Paused ? "" : " · " + View.Reason;
        Label(
            status + reason,
            small,
            Secondary,
            new RectangleF(left + 0.6f * u, lineTop, right - left - 0.6f * u, 0.76f * u)
        );
        return top + 2.6f * u;
    }

    float Rows(float y)
    {
        using var heading = Px(0.48f, TextFont, FontStyle.Bold);
        using var body = Px(0.58f, TextFont);
        using var glyphs = Px(0.6f, IconFont);
        using var segment = Px(0.5f, TextFont);
        string? section = null;
        for (var i = 0; i < View.Rows.Length; i++)
        {
            var row = View.Rows[i];
            var name = row switch
            {
                MenuRow.Mode => "This app",
                MenuRow.Speed or MenuRow.Acceleration or MenuRow.Scroll => "Pointer",
                _ => "Controller Navigation",
            };
            if (name != section)
            {
                section = name;
                y += 0.55f * u;
                Label(name, heading, Secondary, new RectangleF(pad, y, right - pad, 0.8f * u));
                y += 0.95f * u;
            }
            var height = (row == MenuRow.Mode ? 2.8f : 1.55f) * u;
            var box = new RectangleF(pad / 2, y, ClientSize.Width - pad, height);
            var focused = i == View.Focus;
            if (focused)
            {
                Fill(Card, box, u / 5);
                var pill = 0.75f * u;
                Fill(
                    Accent,
                    new RectangleF(box.X + 0.12f * u, y + (1.55f * u - pill) / 2, 0.12f * u, pill),
                    0.06f * u
                );
            }
            var line = new RectangleF(pad, y, right - pad, 1.55f * u);
            var (icon, label) = row switch
            {
                MenuRow.Mode => (MouseIcon, "Mouse in this app"),
                MenuRow.Speed => (PointerIcon, "Pointer speed"),
                MenuRow.Acceleration => (SpeedIcon, "Top speed"),
                MenuRow.Scroll => (ScrollIcon, "Scroll speed"),
                MenuRow.Pause => View.Status == MenuStatus.Paused
                    ? (PlayIcon, "Resume Controller Navigation")
                    : (PauseIcon, "Pause Controller Navigation"),
                MenuRow.TurnOff => (PowerIcon, "Turn off this controller"),
                MenuRow.Buttons => (PadIcon, "What the buttons do"),
                _ => (GearIcon, "All settings"),
            };
            var textLeft = pad + 1.25f * u;
            Label(icon, glyphs, focused ? Accent : Secondary, line with { Width = u });
            Label(label, body, Primary, line with { X = textLeft, Width = right - textLeft });
            if (ControllerMenu.Knobs.TryGetValue(row, out var knob))
                Value(knob.Show(knob.Get(View.Feel)), body, glyphs, line, focused);
            else if (row == MenuRow.Mode)
                Segments(segment, textLeft, y + 1.45f * u, focused);
            y += height + 0.15f * u;
        }
        return y;
    }

    // "<  x1.0  >" at the row's right end, the arrows lit while it has the focus.
    void Value(string text, Font font, Font glyphs, RectangleF line, bool focused)
    {
        var arrow = 0.7f * u;
        var width = 2.4f * u;
        var arrows = focused ? Accent : Edge;
        Label(
            RightIcon,
            glyphs,
            arrows,
            line with
            {
                X = right - arrow,
                Width = arrow,
            },
            TextFormatFlags.Right
        );
        Label(
            text,
            font,
            Primary,
            line with
            {
                X = right - arrow - width,
                Width = width,
            },
            TextFormatFlags.HorizontalCenter
        );
        Label(LeftIcon, glyphs, arrows, line with { X = right - 2 * arrow - width, Width = arrow });
    }

    // The three modes side by side, the picked one filled; "A to save" once it differs from the saved one.
    void Segments(Font font, float left, float top, bool focused)
    {
        var gap = u / 5;
        var w = (right - left - 2 * gap) / 3;
        var h = 1.1f * u;
        for (var m = 0; m < MenuView.ModeNames.Length; m++)
        {
            var box = new RectangleF(left + m * (w + gap), top, w, h);
            var picked = m == View.Mode;
            Fill(
                picked ? Accent
                    : focused ? Raised
                    : Card,
                box,
                u / 5
            );
            Label(
                MenuView.ModeNames[m],
                font,
                picked ? OnAccent : Primary,
                box,
                TextFormatFlags.HorizontalCenter
            );
        }
        if (View.Mode != View.Saved)
            Label(
                "A to save",
                font,
                Accent,
                new RectangleF(left, top - 1.45f * u, right - left, 1.55f * u),
                TextFormatFlags.Right
            );
    }

    float Buttons(float y)
    {
        using var heading = Px(0.48f, TextFont, FontStyle.Bold);
        using var small = Px(0.5f, TextFont);
        var lineHeight = 0.82f * u;
        var bottom = ClientSize.Height - pad - 1.9f * u;
        var split = pad + 3.8f * u;
        // An action too long for one line takes two ("Move window to the screen on the left").
        var heights = View
            .Lines.Select(l =>
                !l.Heading && TextRenderer.MeasureText(g, l.Action, small).Width > right - split
                    ? 2 * lineHeight - 0.15f * u
                    : lineHeight
            )
            .ToArray();
        var room = bottom - y;
        MostScroll = heights.Length;
        for (var sum = 0f; MostScroll > 0 && sum + heights[MostScroll - 1] <= room; )
            sum += heights[--MostScroll];
        for (var i = Math.Min(View.Scroll, MostScroll); i < heights.Length; i++)
        {
            if (y + heights[i] > bottom)
                break;
            var line = View.Lines[i];
            if (line.Heading)
                Label(
                    line.Control,
                    heading,
                    Accent,
                    new RectangleF(pad, y + 0.15f * u, right - pad, lineHeight)
                );
            else
            {
                void Cell(string text, Color color, float left, float width) =>
                    TextRenderer.DrawText(
                        g,
                        text,
                        small,
                        Rectangle.Round(new RectangleF(left, y + 0.16f * u, width, heights[i])),
                        color,
                        TextFormatFlags.WordBreak
                            | TextFormatFlags.EndEllipsis
                            | TextFormatFlags.NoPadding
                    );
                Cell(line.Control, Secondary, pad, split - pad - 0.2f * u);
                Cell(line.Action, Primary, split, right - split);
            }
            y += heights[i];
        }
        return y;
    }

    // The buttons to use here as small key caps, and the version.
    void Footer()
    {
        using var small = Px(0.46f, TextFont);
        using var cap = Px(0.42f, TextFont, FontStyle.Bold);
        var top = ClientSize.Height - pad - 1.5f * u;
        var x = pad;
        var keys =
            View.Page == MenuPage.Main
                ? new[] { ("A", "Select"), ("B", "Close"), ("D-pad", "Move, change") }
            : MostScroll > 0 ? new[] { ("B", "Back"), ("D-pad", "Scroll") }
            : new[] { ("B", "Back") };
        foreach (var (key, what) in keys)
        {
            var size = TextRenderer.MeasureText(g, key, cap);
            var w = Math.Max(0.75f * u, size.Width + 0.4f * u);
            var box = new RectangleF(x, top, w, 0.75f * u);
            Fill(Raised, box, 0.375f * u);
            Label(key, cap, Primary, box, TextFormatFlags.HorizontalCenter);
            x += w + 0.25f * u;
            var label = TextRenderer.MeasureText(g, what, small);
            Label(what, small, Secondary, new RectangleF(x, top, label.Width + 2, 0.75f * u));
            x += label.Width + 0.6f * u;
        }
        Label(
            $"{AppInfo.Name} {AppInfo.Version}",
            small,
            Faint,
            new RectangleF(pad, top + 0.9f * u, right - pad, 0.7f * u)
        );
    }
}
