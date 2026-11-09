using System.ComponentModel;
using System.Text.RegularExpressions;

namespace ControllerNavigation;

// The Controllers dialog: the pads with an on/off box each, the live button test, the driver
// choices and the "map this controller" wizard.
sealed partial class SettingsForm
{
    static readonly Dictionary<string, string> TypeLabels = new()
    {
        ["xbox360"] = "Xbox 360",
        ["xboxOne"] = "Xbox One / Series",
        ["ps3"] = "PlayStation 3",
        ["ps4"] = "PlayStation 4",
        ["ps5"] = "PlayStation 5",
        ["switchPro"] = "Switch Pro",
        ["standard"] = "Standard gamepad",
        ["unknown"] = "Unknown layout",
    };

    readonly System.Windows.Forms.Timer liveTimer = new() { Interval = 33 };
    readonly ListView padList = new()
    {
        View = View.Details,
        CheckBoxes = true,
        FullRowSelect = true,
        HideSelection = false,
        MultiSelect = false,
        Dock = DockStyle.Fill,
    };
    readonly WrapLabel padEmpty = new()
    {
        Text =
            "No controller found. Connect one by cable or Bluetooth: it shows up here by itself.",
        Muted = true,
        Dock = DockStyle.Fill,
    };
    readonly WrapLabel padDetail = new() { Dock = DockStyle.Fill };
    readonly Button mapButton = new()
    {
        Text = "Map this controller...",
        AutoSize = true,
        Margin = new Padding(3, 3, 3, 9),
    };
    readonly PadTestPanel test = new() { Dock = DockStyle.Fill };
    readonly CheckBox playStation = new() { Text = "PlayStation controllers", AutoSize = true };
    readonly CheckBox switchPads = new() { Text = "Nintendo Switch controllers", AutoSize = true };
    Control controllersPage = null!;
    List<PadInfo> pads = new();
    bool wizardOpen,
        controllersOpen;

    Control BuildControllersPage()
    {
        padList.Columns.Add("Controller", Px(250));
        padList.Columns.Add("Type", Px(120));
        padList.Columns.Add("Status", Px(170));
        padList.Resize += (_, _) => FitFirstColumn(padList);
        padList.ItemChecked += OnPadChecked;
        padList.SelectedIndexChanged += (_, _) => ShowPad();
        mapButton.Click += (_, _) => RunWizard();
        liveTimer.Tick += (_, _) => PollLive();
        playStation.CheckedChanged += (_, _) =>
        {
            if (loading)
                return;
            edit.Drivers.PlayStation = playStation.Checked;
            Touch();
        };
        switchPads.CheckedChanged += (_, _) =>
        {
            if (loading)
                return;
            edit.Drivers.Switch = switchPads.Checked;
            Touch();
        };

        var drivers = new GroupBox
        {
            Text = "Controller drivers",
            Dock = DockStyle.Fill,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Padding = new Padding(8, 4, 8, 8),
        };
        var driverTable = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            ColumnCount = 1,
        };
        driverTable.Controls.Add(
            new WrapLabel
            {
                Text =
                    "How the tool reads each family of controllers. A change may need the controller reconnected, or the tool restarted.",
                Muted = true,
                Dock = DockStyle.Fill,
            }
        );
        driverTable.Controls.Add(playStation);
        driverTable.Controls.Add(
            new WrapLabel
            {
                Text =
                    "Read through SDL's direct driver: touchpad, extra buttons and the Guide button work. The controller's mode is not changed, so games keep working.",
                Muted = true,
                Dock = DockStyle.Fill,
                Padding = new Padding(20, 0, 0, 0),
            }
        );
        driverTable.Controls.Add(switchPads);
        driverTable.Controls.Add(
            new WrapLabel
            {
                Text =
                    "Off by default: SDL's direct driver may change the controller's report mode, which other apps cannot always read. Switch it on only if a Switch controller is not recognised without it.",
                Muted = true,
                Dock = DockStyle.Fill,
                Padding = new Padding(20, 0, 0, 0),
            }
        );
        drivers.Controls.Add(driverTable);

        var table = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 1,
        };
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        table.RowStyles.Add(new RowStyle(SizeType.Absolute, 120));
        table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        table.RowStyles.Add(new RowStyle(SizeType.Absolute, 170));
        table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        table.Controls.Add(padEmpty, 0, 0);
        table.Controls.Add(padList, 0, 1);
        table.Controls.Add(padDetail, 0, 2);
        table.Controls.Add(mapButton, 0, 3);
        table.Controls.Add(
            new Label
            {
                Text =
                    "Test: press buttons and move the sticks on a controller that is switched on",
                AutoSize = true,
                Margin = new Padding(3, 6, 3, 3),
            },
            0,
            4
        );
        table.Controls.Add(test, 0, 5);
        table.Controls.Add(drivers, 0, 6);

        var scroller = new Panel { AutoScroll = true };
        scroller.Controls.Add(table);
        loaders.Add(() =>
        {
            playStation.Checked = edit.Drivers.PlayStation;
            switchPads.Checked = edit.Drivers.Switch;
            LoadPads();
        });
        controllersPage = scroller;
        return scroller;
    }

    void ResetControllers()
    {
        edit.Pads.Clear();
        edit.Drivers = new Drivers();
    }

    static void FitFirstColumn(ListView list)
    {
        if (list.Columns.Count < 2)
            return;
        var others = 0;
        for (var i = 1; i < list.Columns.Count; i++)
            others += list.Columns[i].Width;
        list.Columns[0].Width = Math.Max(
            120,
            list.ClientSize.Width - others - (list.Columns.Count == 1 ? 8 : 4)
        );
    }

    bool PadEnabled(PadInfo pad) =>
        edit.Pads.TryGetValue(pad.Id, out var saved) ? saved.Enabled : pad.Enabled;

    void LoadPads()
    {
        var was = loading;
        loading = true;
        try
        {
            var selected =
                (padList.SelectedItems.Count > 0 ? padList.SelectedItems[0].Tag : null) as PadInfo;
            pads = host.Pads().ToList();
            padList.BeginUpdate();
            padList.Items.Clear();
            foreach (var pad in pads)
            {
                var state = pad.Connected ? "Connected" : "Not connected";
                if (pad.Virtual)
                    state += ", virtual";
                if (!pad.Mapped)
                    state += ", layout unknown";
                var item = new ListViewItem(pad.Name) { Tag = pad, Checked = PadEnabled(pad) };
                item.SubItems.Add(TypeLabels.GetValueOrDefault(pad.Type, pad.Type));
                item.SubItems.Add(state);
                padList.Items.Add(item);
                if (selected is not null && selected.Id == pad.Id)
                    item.Selected = true;
            }
            padList.EndUpdate();
            FitFirstColumn(padList);
            if (padList.SelectedItems.Count == 0 && padList.Items.Count > 0)
                padList.Items[0].Selected = true;
            padEmpty.Visible = pads.Count == 0;
            padList.Visible = pads.Count > 0;
            ShowPad();
        }
        finally
        {
            loading = was;
        }
    }

    void ShowPad()
    {
        var pad =
            (padList.SelectedItems.Count > 0 ? padList.SelectedItems[0].Tag : null) as PadInfo;
        mapButton.Enabled = pad is { Connected: true, Mapped: false };
        if (pad is null)
        {
            padDetail.Text = pads.Count > 0 ? "Select a controller to see its details." : "";
            return;
        }
        var lines = new List<string>
        {
            $"{pad.Name}   (vendor {pad.Vendor:x4}, product {pad.Product:x4})",
        };
        var face = (pad.Labels ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (face.Length == 4)
            lines.Add(
                $"Face buttons: south {face[0]}, east {face[1]}, west {face[2]}, north {face[3]}."
            );
        if (pad.Virtual)
            lines.Add(
                "A virtual controller (Steam Input, ViGEm, Parsec or a streaming guest creates these). It is off by default so a guest who streams in cannot drive this desktop. Tick it only if you use this pad yourself."
            );
        if (!pad.Mapped)
            lines.Add(
                "The layout of this controller is not known. Use \"Map this controller\" and press each button once."
            );
        padDetail.Text = string.Join("\r\n", lines);
    }

    void OnPadChecked(object? sender, ItemCheckedEventArgs e)
    {
        // The ListView raises this for every item when its handle is created (opening the dialog):
        // only a change from what the pad is already set to is an edit.
        if (loading || e.Item.Tag is not PadInfo pad || PadEnabled(pad) == e.Item.Checked)
            return;
        edit.Pads[pad.Id] = new PadSetting { Name = pad.Name, Enabled = e.Item.Checked };
        Touch();
    }

    void RunWizard()
    {
        if (padList.SelectedItems.Count == 0 || padList.SelectedItems[0].Tag is not PadInfo pad)
            return;
        wizardOpen = true;
        UpdateLiveTimer();
        try
        {
            using var wizard = new MappingWizard(host, pad);
            WhileModal(() => wizard.ShowDialog(this));
        }
        finally
        {
            wizardOpen = false;
            UpdateLiveTimer();
            LoadPads();
        }
    }

    void UpdateLiveTimer() => liveTimer.Enabled = controllersOpen && !wizardOpen;

    void PollLive()
    {
        try
        {
            var live = host.Live();
            if (live.Equals(test.Live))
                return;
            test.Live = live;
            test.Invalidate();
        }
        catch (Exception e)
        {
            Log.Warn("Settings window: live read failed: " + e.Message);
            liveTimer.Stop();
        }
    }
}

/// <summary>The live button test: a chip per button that lights while pressed, both sticks, both triggers.</summary>
sealed class PadTestPanel : Control
{
    static readonly Dictionary<string, string> Short = new()
    {
        ["south"] = "South",
        ["east"] = "East",
        ["west"] = "West",
        ["north"] = "North",
        ["back"] = "View",
        ["guide"] = "Guide",
        ["start"] = "Menu",
        ["l3"] = "L3",
        ["r3"] = "R3",
        ["lb"] = "LB",
        ["rb"] = "RB",
        ["up"] = "Up",
        ["down"] = "Down",
        ["left"] = "Left",
        ["right"] = "Right",
        ["misc1"] = "Share",
        ["paddleR1"] = "P R1",
        ["paddleL1"] = "P L1",
        ["paddleR2"] = "P R2",
        ["paddleL2"] = "P L2",
        ["touchpad"] = "Touch",
    };

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    internal PadLive Live { get; set; }

    public PadTestPanel()
    {
        DoubleBuffered = true;
        BackColor = SystemColors.Window;
        SetStyle(ControlStyles.ResizeRedraw, true);
        TabStop = false;
    }

    bool Pressed(int control) => ((Live.Buttons >> control) & 1) != 0;

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var g = e.Graphics;
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        var k = (FindForm()?.DeviceDpi ?? DeviceDpi) / 96f;
        int chipW = (int)(54 * k),
            chipH = (int)(22 * k),
            gap = (int)(4 * k);
        var sticksW = (int)(250 * k);
        var perRow = Math.Max(1, (Math.Max(chipW, Width - sticksW) + gap) / (chipW + gap));
        using var border = new Pen(SystemColors.ControlDark);
        using var on = new SolidBrush(SystemColors.Highlight);
        using var off = new SolidBrush(SystemColors.ControlLight);
        using var dot = new SolidBrush(SystemColors.Highlight);
        var n = 0;
        for (var i = 0; i < Catalog.ControlIds.Length; i++)
        {
            if (!Short.TryGetValue(Catalog.ControlIds[i], out var name))
                continue;
            var rect = new Rectangle(
                n % perRow * (chipW + gap),
                n / perRow * (chipH + gap),
                chipW,
                chipH
            );
            n++;
            var down = Pressed(i);
            g.FillRectangle(down ? on : off, rect);
            g.DrawRectangle(border, rect);
            TextRenderer.DrawText(
                g,
                name,
                Font,
                rect,
                down ? SystemColors.HighlightText : SystemColors.GrayText,
                TextFormatFlags.HorizontalCenter
                    | TextFormatFlags.VerticalCenter
                    | TextFormatFlags.NoPadding
            );
        }

        var d = (int)(76 * k);
        var top = (int)(8 * k);
        var x = Width - sticksW + gap;
        DrawStick(g, border, dot, new Rectangle(x, top, d, d), Live.LX, Live.LY);
        DrawStick(g, border, dot, new Rectangle(x + d + gap * 3, top, d, d), Live.RX, Live.RY);
        var barX = x + 2 * (d + gap * 3);
        var barW = (int)(16 * k);
        DrawBar(
            g,
            border,
            on,
            new Rectangle(barX, top, barW, d),
            Live.LT,
            Pressed((int)PadControl.LT)
        );
        DrawBar(
            g,
            border,
            on,
            new Rectangle(barX + barW + gap * 2, top, barW, d),
            Live.RT,
            Pressed((int)PadControl.RT)
        );
        var labelY = top + d + gap;
        TextRenderer.DrawText(g, "Left stick", Font, new Point(x, labelY), SystemColors.GrayText);
        TextRenderer.DrawText(
            g,
            "Right stick",
            Font,
            new Point(x + d + gap * 3, labelY),
            SystemColors.GrayText
        );
        TextRenderer.DrawText(g, "LT   RT", Font, new Point(barX, labelY), SystemColors.GrayText);
    }

    static void DrawStick(Graphics g, Pen border, Brush dot, Rectangle area, short sx, short sy)
    {
        g.DrawEllipse(border, area);
        var r = area.Width / 2f;
        var dotR = r / 6f;
        var cx = area.X + r + Math.Clamp(sx / 32768f, -1f, 1f) * (r - dotR);
        var cy = area.Y + r + Math.Clamp(sy / 32768f, -1f, 1f) * (r - dotR);
        g.FillEllipse(dot, cx - dotR, cy - dotR, dotR * 2, dotR * 2);
    }

    static void DrawBar(
        Graphics g,
        Pen border,
        Brush fill,
        Rectangle area,
        short value,
        bool pressed
    )
    {
        var height = (int)(Math.Clamp(value / 32767f, 0f, 1f) * area.Height);
        g.FillRectangle(fill, area.X, area.Bottom - height, area.Width, height);
        g.DrawRectangle(border, area);
    }
}

enum StepKind
{
    Button,
    DPad,
    Trigger,
    Axis,
}

/// <summary>One thing the wizard asks for: the control (SDL's mapping name Sdl), how it is read, what to tell the user.</summary>
sealed record WizardStep(string Id, string Sdl, StepKind Kind, string Prompt);

/// <summary>
/// What the "map this controller" wizard decides: which raw button, axis or hat moved for a
/// control, and the SDL mapping string at the end (SDL's a/b/x/y are positional: south, east,
/// west, north). A spec is "b3" (button), "h0.4" (hat 0, bit 4), "a2" (a whole axis), "a2~" (the
/// axis inverted), "+a4" or "-a4" (one half of an axis).
/// </summary>
static class MappingWizardLogic
{
    /// <summary>How far an axis must move, of its 65536 steps, to count.</summary>
    public const int AxisMove = 16000;

    public static readonly WizardStep[] Steps =
    {
        new(
            "south",
            "a",
            StepKind.Button,
            "Press the bottom face button (A on Xbox, Cross on PlayStation, B on Switch)."
        ),
        new(
            "east",
            "b",
            StepKind.Button,
            "Press the right face button (B on Xbox, Circle on PlayStation, A on Switch)."
        ),
        new(
            "west",
            "x",
            StepKind.Button,
            "Press the left face button (X on Xbox, Square on PlayStation, Y on Switch)."
        ),
        new(
            "north",
            "y",
            StepKind.Button,
            "Press the top face button (Y on Xbox, Triangle on PlayStation, X on Switch)."
        ),
        new(
            "back",
            "back",
            StepKind.Button,
            "Press the Back button (View, Select or Share, left of the middle)."
        ),
        new(
            "guide",
            "guide",
            StepKind.Button,
            "Press the center button (Xbox, PS or Home). Skip it if there is none."
        ),
        new(
            "start",
            "start",
            StepKind.Button,
            "Press the Start button (Menu or Options, right of the middle)."
        ),
        new("l3", "leftstick", StepKind.Button, "Click the left stick in (L3)."),
        new("r3", "rightstick", StepKind.Button, "Click the right stick in (R3)."),
        new("lb", "leftshoulder", StepKind.Button, "Press the left bumper (LB or L1)."),
        new("rb", "rightshoulder", StepKind.Button, "Press the right bumper (RB or R1)."),
        new("up", "dpup", StepKind.DPad, "Press D-pad up."),
        new("down", "dpdown", StepKind.DPad, "Press D-pad down."),
        new("left", "dpleft", StepKind.DPad, "Press D-pad left."),
        new("right", "dpright", StepKind.DPad, "Press D-pad right."),
        new(
            "lt",
            "lefttrigger",
            StepKind.Trigger,
            "Squeeze the left trigger all the way (LT or L2)."
        ),
        new(
            "rt",
            "righttrigger",
            StepKind.Trigger,
            "Squeeze the right trigger all the way (RT or R2)."
        ),
        new(
            "leftx",
            "leftx",
            StepKind.Axis,
            "Push the left stick all the way to the right, then let go."
        ),
        new("lefty", "lefty", StepKind.Axis, "Push the left stick all the way down, then let go."),
        new(
            "rightx",
            "rightx",
            StepKind.Axis,
            "Push the right stick all the way to the right, then let go."
        ),
        new(
            "righty",
            "righty",
            StepKind.Axis,
            "Push the right stick all the way down, then let go."
        ),
    };

    /// <summary>The spec of what changed between start and raw for this step, or null. Specs in used are not offered again.</summary>
    public static string? Detect(
        WizardStep step,
        RawJoystick raw,
        RawJoystick start,
        IReadOnlyCollection<string> used
    )
    {
        if (step.Kind != StepKind.Axis)
        {
            for (var i = 0; i < raw.Buttons.Length; i++)
            {
                var was = i < start.Buttons.Length && start.Buttons[i];
                if (raw.Buttons[i] && !was && !used.Contains($"b{i}"))
                    return $"b{i}";
            }
        }
        if (step.Kind == StepKind.DPad)
        {
            for (var hat = 0; hat < raw.Hats.Length; hat++)
            {
                int added = raw.Hats[hat] & ~(hat < start.Hats.Length ? start.Hats[hat] : 0);
                if (added == 0)
                    continue;
                var spec = $"h{hat}.{added & -added}";
                if (!used.Contains(spec))
                    return spec;
            }
        }
        if (step.Kind is StepKind.Trigger or StepKind.Axis)
        {
            var best = -1;
            var bestDelta = 0;
            for (var a = 0; a < raw.Axes.Length && a < start.Axes.Length; a++)
            {
                var delta = raw.Axes[a] - start.Axes[a];
                if (Math.Abs(delta) < AxisMove || (step.Kind == StepKind.Axis && AxisUsed(used, a)))
                    continue;
                if (best < 0 || Math.Abs(delta) > Math.Abs(bestDelta))
                {
                    best = a;
                    bestDelta = delta;
                }
            }
            if (best >= 0)
            {
                var rest = start.Axes[best];
                var spec =
                    step.Kind == StepKind.Axis ? (bestDelta > 0 ? $"a{best}" : $"a{best}~")
                    : rest <= -20000 && bestDelta > 0 ? $"a{best}"
                    : rest >= 20000 && bestDelta < 0 ? $"a{best}~"
                    : $"{(bestDelta > 0 ? '+' : '-')}a{best}";
                if (step.Kind == StepKind.Axis || !used.Contains(spec))
                    return spec;
            }
        }
        return null;
    }

    static bool AxisUsed(IReadOnlyCollection<string> used, int axis) =>
        used.Any(s => Regex.IsMatch(s, $@"^[+-]?a{axis}~?$"));

    /// <summary>Nothing is pressed or moved compared with rest (the controller is let go of).</summary>
    public static bool IsNeutral(RawJoystick raw, RawJoystick rest)
    {
        for (var i = 0; i < raw.Buttons.Length; i++)
            if (raw.Buttons[i] != (i < rest.Buttons.Length && rest.Buttons[i]))
                return false;
        for (var i = 0; i < raw.Axes.Length; i++)
            if (Math.Abs(raw.Axes[i] - (i < rest.Axes.Length ? rest.Axes[i] : 0)) >= 6000)
                return false;
        for (var i = 0; i < raw.Hats.Length; i++)
            if (raw.Hats[i] != (i < rest.Hats.Length ? rest.Hats[i] : 0))
                return false;
        return true;
    }

    /// <summary>The SDL mapping string for the controls found (results: step id to spec).</summary>
    public static string Build(
        string guid,
        string name,
        IReadOnlyDictionary<string, string> results
    )
    {
        var parts = new List<string>
        {
            guid,
            name.Replace(',', ' ').Trim() is { Length: > 0 } n ? n : "Controller",
        };
        foreach (var step in Steps)
            if (results.TryGetValue(step.Id, out var spec))
                parts.Add($"{step.Sdl}:{spec}");
        parts.Add("platform:Windows");
        return string.Join(',', parts) + ",";
    }
}

/// <summary>
/// The "map this controller" dialog: after a first look at the controller at rest, it asks for each
/// control in turn, watches Raw() for what moved, and hands SDL the mapping string.
/// </summary>
sealed class MappingWizard : Form
{
    enum Stage
    {
        Intro,
        Run,
        Done,
    }

    readonly ISettingsHost host;
    readonly PadInfo pad;
    readonly System.Windows.Forms.Timer timer = new() { Interval = 33 };
    readonly WrapLabel prompt = new()
    {
        Dock = DockStyle.Fill,
        Font = new Font("Segoe UI", 11F, FontStyle.Bold),
    };
    readonly WrapLabel summary = new() { Dock = DockStyle.Fill, Muted = true };
    readonly ProgressBar progress = new()
    {
        Dock = DockStyle.Fill,
        Maximum = MappingWizardLogic.Steps.Length,
        Margin = new Padding(3, 6, 3, 6),
    };
    readonly Label statusLabel = new() { AutoSize = true, ForeColor = SystemColors.GrayText };
    readonly Button primary = new() { AutoSize = true, MinimumSize = new Size(90, 28) };
    readonly Button secondary = new() { AutoSize = true, MinimumSize = new Size(90, 28) };
    readonly Button skip = new()
    {
        Text = "Skip",
        AutoSize = true,
        MinimumSize = new Size(90, 28),
    };
    readonly Button cancel = new()
    {
        Text = "Cancel",
        AutoSize = true,
        MinimumSize = new Size(90, 28),
        DialogResult = DialogResult.Cancel,
    };
    readonly Dictionary<string, string> results = new();
    Stage stage = Stage.Intro;
    RawJoystick? last,
        rest,
        start;
    int index;
    bool wait;

    public MappingWizard(ISettingsHost host, PadInfo pad)
    {
        this.host = host;
        this.pad = pad;
        Text = "Map this controller";
        Font = new Font("Segoe UI", 9F);
        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(500, 260);
        CancelButton = cancel;

        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.RightToLeft,
            AutoSize = true,
        };
        buttons.Controls.AddRange(new Control[] { cancel, skip, secondary, primary });
        var table = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 5,
            Padding = new Padding(12),
        };
        table.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        table.Controls.Add(prompt, 0, 0);
        table.Controls.Add(summary, 0, 1);
        table.Controls.Add(progress, 0, 2);
        table.Controls.Add(statusLabel, 0, 3);
        table.Controls.Add(buttons, 0, 4);
        Controls.Add(table);

        primary.Click += (_, _) => OnPrimary();
        secondary.Click += (_, _) => OnSecondary();
        skip.Click += (_, _) => Advance();
        timer.Tick += (_, _) => Poll();
        FormClosed += (_, _) => timer.Dispose();
        Render();
        timer.Start();
    }

    static RawJoystick Copy(RawJoystick r) =>
        new(
            r.Id,
            r.Guid,
            r.Name,
            (bool[])r.Buttons.Clone(),
            (short[])r.Axes.Clone(),
            (byte[])r.Hats.Clone()
        );

    void OnPrimary()
    {
        if (stage == Stage.Intro)
        {
            if (last is null)
                return;
            rest = Copy(last);
            start = Copy(last);
            index = 0;
            results.Clear();
            wait = false;
            stage = Stage.Run;
            Render();
            return;
        }
        var mapping = MappingWizardLogic.Build(last?.Guid ?? "", last?.Name ?? pad.Name, results);
        if (host.AddMapping(mapping))
        {
            DialogResult = DialogResult.OK;
            return;
        }
        MessageBox.Show(
            this,
            "SDL refused the mapping. Press Start over and check each button.",
            AppInfo.Name,
            MessageBoxButtons.OK,
            MessageBoxIcon.Warning
        );
    }

    // Back during the run, Start over at the end.
    void OnSecondary()
    {
        if (stage == Stage.Done)
        {
            stage = Stage.Intro;
            results.Clear();
            Render();
            return;
        }
        index = Math.Max(0, index - 1);
        results.Remove(MappingWizardLogic.Steps[index].Id);
        wait = true;
        start = null;
        Render();
    }

    void Advance()
    {
        index++;
        wait = true;
        start = null;
        if (index >= MappingWizardLogic.Steps.Length)
            stage = Stage.Done;
        Render();
    }

    void Poll()
    {
        RawJoystick? raw;
        try
        {
            raw = host.Raw(pad.Id);
        }
        catch (Exception e)
        {
            Log.Warn("Mapping wizard: raw read failed: " + e.Message);
            return;
        }
        if (raw is null)
        {
            var wasThere = last is not null;
            last = null;
            statusLabel.Text = "The controller is not connected.";
            if (wasThere)
                Render();
            return;
        }
        var first = last is null;
        last = raw;
        if (stage == Stage.Intro)
        {
            if (first)
                Render();
            return;
        }
        if (stage == Stage.Done)
            return;
        if (wait)
        {
            if (MappingWizardLogic.IsNeutral(raw, rest!))
            {
                wait = false;
                start = Copy(raw);
            }
            statusLabel.Text = wait ? "Let go of everything..." : "Waiting for your input.";
            return;
        }
        statusLabel.Text = "Waiting for your input.";
        var step = MappingWizardLogic.Steps[index];
        if (MappingWizardLogic.Detect(step, raw, start!, results.Values.ToList()) is { } spec)
        {
            results[step.Id] = spec;
            Advance();
        }
    }

    void Render()
    {
        summary.Visible = stage == Stage.Done;
        progress.Visible = stage == Stage.Run;
        skip.Visible = stage == Stage.Run;
        secondary.Visible = stage != Stage.Intro;
        switch (stage)
        {
            case Stage.Intro:
                prompt.Text =
                    "Leave the controller untouched, then press Start. You will be asked for each button, trigger and stick in turn. Skip anything the controller does not have.";
                primary.Text = "Start";
                primary.Enabled = last is not null;
                statusLabel.Text = last is null
                    ? "Waiting for the controller..."
                    : "Controller ready.";
                break;
            case Stage.Run:
                var step = MappingWizardLogic.Steps[index];
                prompt.Text =
                    $"Step {index + 1} of {MappingWizardLogic.Steps.Length}\r\n{step.Prompt}";
                progress.Value = Math.Min(index, progress.Maximum);
                primary.Visible = false;
                secondary.Text = "Back";
                secondary.Enabled = index > 0;
                break;
            case Stage.Done:
                primary.Visible = true;
                var got = MappingWizardLogic.Steps.Where(s => results.ContainsKey(s.Id)).ToList();
                prompt.Text = $"{got.Count} of {MappingWizardLogic.Steps.Length} controls mapped.";
                summary.Text =
                    "Skipped: "
                    + (
                        MappingWizardLogic
                            .Steps.Where(s => !results.ContainsKey(s.Id))
                            .Select(s => s.Id)
                            .ToList()
                            is { Count: > 0 } skipped
                            ? string.Join(", ", skipped)
                            : "nothing"
                    )
                    + ". Save it, then use the test in the Controllers window to check each control.";
                primary.Text = "Save mapping";
                primary.Enabled = got.Count >= 4;
                secondary.Text = "Start over";
                secondary.Enabled = true;
                statusLabel.Text = "";
                break;
        }
        if (stage == Stage.Intro)
            primary.Visible = true;
    }
}
