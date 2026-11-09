using System.Text.Json;

namespace ControllerNavigation;

/// <summary>
/// The settings window: a small options window with the frequent settings (the app exceptions, the
/// pointer speed, updates) and buttons that open the rest in dialogs. Everything edits a copy of
/// the settings (edit); every change restarts a 400 ms timer and the copy is then handed to the
/// host, which normalizes and stores it. A host that normalizes to something other than what was
/// sent (it dropped an entry) is told apart from one that changed the settings itself: only the
/// latter, or a failed save, reloads the controls.
/// </summary>
/// <remarks>
/// The dialogs' contents are built once, kept in a hidden panel of this window (so the window's
/// DPI scaling reaches them once) and moved into a dialog while it is open.
/// </remarks>
sealed partial class SettingsForm : Form
{
    readonly ISettingsHost host;
    readonly System.Windows.Forms.Timer saveTimer = new() { Interval = 400 };
    readonly List<Action> loaders = new();
    readonly Panel hiddenHost = new() { Visible = false };
    readonly Label statusLine = new() { AutoSize = true, Margin = new Padding(3, 3, 3, 6) };
    readonly WrapLabel warningText = new()
    {
        ForeColor = Color.FromArgb(180, 80, 0),
        Dock = DockStyle.Fill,
    };
    readonly LinkLabel warningLink = new()
    {
        Text = "Details...",
        AutoSize = true,
        Margin = new Padding(3, 6, 3, 3),
    };

    // Its height is set by hand (ShowWarning): a wrapping label in an auto-sized table is measured too early.
    readonly Panel warningPanel = new() { Dock = DockStyle.Fill, Visible = false };
    readonly TrackBar speedBar = new QuietTrackBar()
    {
        Minimum = 0,
        Maximum = 100,
        TickFrequency = 25,
        SmallChange = 2,
        LargeChange = 10,
        Dock = DockStyle.Fill,
    };
    readonly Label speedValue = new() { AutoSize = true, Anchor = AnchorStyles.None };
    readonly Button buttonsButton = new() { Text = "Buttons...", AutoSize = true };
    readonly Button layersButton = new() { Text = "Hold buttons...", AutoSize = true };
    readonly Button controllersButton = new() { Text = "Controllers...", AutoSize = true };
    readonly Button moreButton = new() { Text = "More...", AutoSize = true };
    readonly Button closeButton = new() { Text = "Close", AutoSize = true };
    readonly Label saveError = new()
    {
        AutoSize = true,
        ForeColor = Color.FromArgb(190, 50, 40),
        Visible = false,
    };
    Settings edit;
    string synced;
    bool loading;

    public SettingsForm(ISettingsHost host)
    {
        this.host = host;
        edit = SettingsStore.Copy(host.Settings);
        synced = Json(host.Settings);

        Text = AppInfo.Name;
        Font = new Font("Segoe UI", 9F);
        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(470, 600);
        try
        {
            Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
        }
        catch (Exception) { }

        var appsGroup = BuildAppsGroup();
        hiddenHost.Controls.Add(BuildControllersPage());
        hiddenHost.Controls.Add(BuildButtonsPage());
        hiddenHost.Controls.Add(BuildLayersPage());
        hiddenHost.Controls.Add(BuildMorePage());
        hiddenHost.Controls.Add(appDetail);
        HookUpdateControls();
        loaders.Add(ShowStatusLine);
        loaders.Add(LoadSpeed);
        loaders.Add(UpdateLayersButton);

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            Padding = new Padding(10),
        };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        void Row(Control c, SizeType type = SizeType.AutoSize)
        {
            root.RowStyles.Add(new RowStyle(type, type == SizeType.Percent ? 100 : 0));
            root.Controls.Add(c, 0, root.RowStyles.Count - 1); // explicit: a hidden row keeps its place
        }
        Row(statusLine);
        warningLink.Dock = DockStyle.Right;
        warningPanel.Controls.Add(warningText);
        warningPanel.Controls.Add(warningLink);
        Row(warningPanel);
        Row(appsGroup, SizeType.Percent);
        Row(BuildSpeedGroup());
        Row(BuildUpdateRow());
        Row(BuildButtonRow());
        Row(saveError);
        Controls.Add(root);
        Controls.Add(hiddenHost);

        warningLink.LinkClicked += (_, _) => ShowChecksDialog();
        saveTimer.Tick += (_, _) => SaveNow();
        host.Changed += OnHostChanged;
    }

    int Px(int n) => (int)Math.Round(n * DeviceDpi / 96.0);

    GroupBox BuildSpeedGroup()
    {
        speedBar.ValueChanged += (_, _) =>
        {
            speedValue.Text = $"x{SliderToSpeed(speedBar.Value):0.##}";
            if (loading)
                return;
            edit.Feel.PointerSpeed = SliderToSpeed(speedBar.Value);
            Touch();
        };
        var labels = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 3,
            AutoSize = true,
        };
        labels.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33));
        labels.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 34));
        labels.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33));
        labels.Controls.Add(
            new Label
            {
                Text = "Slow",
                AutoSize = true,
                Anchor = AnchorStyles.Left,
            }
        );
        labels.Controls.Add(speedValue);
        labels.Controls.Add(
            new Label
            {
                Text = "Fast",
                AutoSize = true,
                Anchor = AnchorStyles.Right,
            }
        );
        var table = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
            AutoSize = true,
        };
        table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        table.Controls.Add(speedBar, 0, 0);
        table.Controls.Add(labels, 0, 1);
        var group = new GroupBox
        {
            Text = "Pointer speed",
            Dock = DockStyle.Fill,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Padding = new Padding(8, 4, 8, 6),
        };
        group.Controls.Add(table);
        return group;
    }

    // Slow to fast covers a quarter to four times the normal speed, so the middle is 1.
    static double SliderToSpeed(int value) => Math.Round(0.25 * Math.Pow(16, value / 100.0), 2);

    static int SpeedToSlider(double speed) =>
        (int)Math.Round(Math.Clamp(Math.Log(Math.Max(speed, 0.01) / 0.25, 16) * 100, 0, 100));

    void LoadSpeed()
    {
        var was = loading;
        loading = true;
        speedBar.Value = SpeedToSlider(edit.Feel.PointerSpeed);
        speedValue.Text = $"x{edit.Feel.PointerSpeed:0.##}";
        loading = was;
    }

    Control BuildUpdateRow()
    {
        var line = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill };
        line.Controls.Add(autoUpdate);
        line.Controls.Add(checkNow);
        var table = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
            AutoSize = true,
            Margin = new Padding(0, 6, 0, 6),
        };
        table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        table.Controls.Add(line, 0, 0);
        table.Controls.Add(updateLine, 0, 1);
        return table;
    }

    Control BuildButtonRow()
    {
        buttonsButton.Click += (_, _) => OpenPage("buttons");
        layersButton.Click += (_, _) => OpenPage("layers");
        controllersButton.Click += (_, _) => OpenPage("controllers");
        moreButton.Click += (_, _) => OpenPage("more");
        closeButton.Click += (_, _) => Close();
        AcceptButton = closeButton;
        var row = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill };
        row.Controls.AddRange(
            new Control[]
            {
                buttonsButton,
                layersButton,
                controllersButton,
                moreButton,
                closeButton,
            }
        );
        return row;
    }

    // ---- Dialogs

    /// <summary>Opens the dialog for "buttons", "layers", "controllers", "more" or "app" (the app being edited) and returns when it is closed.</summary>
    void OpenPage(string which)
    {
        using var dialog = CreatePageDialog(which);
        var live = which == "controllers";
        controllersOpen = live;
        UpdateLiveTimer();
        try
        {
            WhileModal(() => dialog.ShowDialog(this));
        }
        finally
        {
            controllersOpen = false;
            UpdateLiveTimer();
            ReturnPage(dialog);
        }
        if (which == "more")
            LoadChecks();
        RefreshMain();
    }

    /// <summary>The main window's own controls after a dialog changed what they show.</summary>
    internal void RefreshMain()
    {
        var was = loading;
        loading = true;
        try
        {
            LoadSpeed();
            UpdateLayersButton();
            LoadApps();
        }
        finally
        {
            loading = was;
        }
        ShowStatusLine();
    }

    /// <summary>The dialog for a page, not shown yet. ReturnPage gives the content back when it is closed.</summary>
    internal PageDialog CreatePageDialog(string which)
    {
        if (which == "buttons")
            LoadButtons(); // a layer may have taken or freed a button since it was last filled
        string title;
        Control page;
        Size size;
        Action? reset;
        switch (which)
        {
            case "buttons":
                (title, page, size, reset) = (
                    "Buttons",
                    buttonsPage,
                    new Size(760, 540),
                    ResetButtons
                );
                break;
            case "layers":
                (title, page, size, reset) = (
                    LayersTitle(),
                    layersPage,
                    new Size(760, 620),
                    ResetLayers
                );
                break;
            case "controllers":
                (title, page, size, reset) = (
                    "Controllers",
                    controllersPage,
                    new Size(660, 600),
                    ResetControllers
                );
                break;
            case "app":
                (title, page, size, reset) = (
                    editingApp?.Name ?? "App",
                    appDetail,
                    new Size(760, 560),
                    null
                );
                break;
            default:
                (title, page, size, reset) = (
                    "More settings",
                    morePage,
                    new Size(660, 560),
                    ResetMore
                );
                break;
        }
        var resetText = which == "more" ? "Reset Feel and detection" : "Reset to defaults";
        return new PageDialog(
            title,
            Font,
            page,
            new Size(Px(size.Width), Px(size.Height)),
            reset is null ? null : resetText,
            () =>
            {
                var sure = MessageBox.Show(
                    this,
                    which == "more"
                        ? "Put all the Feel values and the \"Which apps get the mouse\" settings (including the game folders) back to the built-in defaults? The app exceptions, updates and checks are not touched."
                        : $"Put everything in \"{title}\" back to the built-in defaults?",
                    AppInfo.Name,
                    MessageBoxButtons.YesNo,
                    MessageBoxIcon.Question
                );
                if (sure != DialogResult.Yes)
                    return;
                reset!();
                Touch();
                LoadAll();
            }
        );
    }

    internal void ReturnPage(PageDialog dialog) => hiddenHost.Controls.Add(dialog.TakeContent());

    string LayersTitle() => edit.Layers.Count == 1 ? edit.Layers[0].Name : "Hold buttons";

    void ResetButtons() => edit.Base = Defaults.Base();

    // What the main window's layers button says: the layer's name, or "Hold buttons..." for several.
    string LayersButtonText() =>
        edit.Layers.Count == 1 ? $"{edit.Layers[0].Name}..." : "Hold buttons...";

    void UpdateLayersButton() => layersButton.Text = LayersButtonText();

    // While a dialog or a confirm box is open the model it works on must not be swapped for the
    // host's: an outside change waits (reloadPending) until the last one is closed.
    int modalDepth;
    bool reloadPending;

    T WhileModal<T>(Func<T> show)
    {
        modalDepth++;
        try
        {
            return show();
        }
        finally
        {
            modalDepth--;
            if (modalDepth == 0 && reloadPending)
            {
                reloadPending = false;
                BeginInvoke(RefreshFromHost); // after the handler that opened the box has finished
            }
        }
    }

    void ShowChecksDialog()
    {
        using var dialog = new Form
        {
            Text = "Checks",
            Font = Font,
            AutoScaleMode = AutoScaleMode.None,
            ClientSize = new Size(Px(620), Px(420)),
            MinimumSize = new Size(Px(420), Px(300)),
            StartPosition = FormStartPosition.CenterParent,
            ShowInTaskbar = false,
            MinimizeBox = false,
            MaximizeBox = false,
        };
        var panel = new ChecksPanel { Dock = DockStyle.Fill };
        var close = new Button
        {
            Text = "Close",
            AutoSize = true,
            MinimumSize = new Size(Px(90), Px(28)),
            DialogResult = DialogResult.Cancel,
            Dock = DockStyle.Right,
        };
        var bottom = new Panel
        {
            Dock = DockStyle.Bottom,
            Height = Px(42),
            Padding = new Padding(8),
        };
        bottom.Controls.Add(close);
        dialog.Controls.Add(panel);
        dialog.Controls.Add(bottom);
        dialog.CancelButton = close;
        dialog.AcceptButton = close;
        panel.Show(checkResults);
        dialog.ShowDialog(this);
    }

    // ---- The main window's lines

    void ShowStatusLine()
    {
        var connected = pads.Where(p => p.Connected).ToList();
        var inUse = connected.Where(PadEnabled).ToList();
        statusLine.Text = inUse.Count switch
        {
            0 when connected.Count > 0 =>
                "No controller in use: the connected ones are switched off.",
            0 => "No controller connected.",
            1 => $"Controller in use: {inUse[0].Name}",
            _ =>
                $"{inUse.Count} controllers in use: {string.Join(", ", inUse.Select(p => p.Name))}",
        };
    }

    void ShowWarning()
    {
        var bad = checkResults.Where(c => !c.Ok).ToList();
        warningPanel.Visible = bad.Count > 0;
        if (bad.Count == 0)
            return;
        warningText.Text =
            bad[0].Name
            + ": "
            + bad[0].Detail
            + (bad.Count > 1 ? $" (and {bad.Count - 1} more)" : "");
        var width =
            ClientSize.Width - Padding.Horizontal - Px(20) - warningLink.PreferredSize.Width;
        warningPanel.Height = warningText.GetPreferredSize(new Size(width, 0)).Height + Px(6);
    }

    // ---- Loading and saving

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        LoadAll();
        LoadChecks();
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        base.OnFormClosing(e);
        if (saveTimer.Enabled)
            SaveNow();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            host.Changed -= OnHostChanged;
            saveTimer.Dispose();
            liveTimer.Dispose();
        }
        base.Dispose(disposing);
    }

    static string Json(Settings s) => JsonSerializer.Serialize(s, SettingsStore.Json);

    void LoadAll()
    {
        var was = loading;
        loading = true;
        try
        {
            foreach (var load in loaders)
                load();
        }
        finally
        {
            loading = was;
        }
    }

    /// <summary>An edit by the user: saved 400 ms after the last one. Ignored while the controls are being filled.</summary>
    void Touch()
    {
        if (loading)
            return;
        saveTimer.Stop();
        saveTimer.Start();
    }

    void SaveNow()
    {
        saveTimer.Stop();
        var send = Prepared(edit);
        bool ok;
        try
        {
            ok = host.Save(send);
        }
        catch (Exception e)
        {
            Log.Error("Settings window: save failed", e);
            ok = false;
        }
        var expected = Prepared(edit);
        expected.Normalize();
        var now = Json(host.Settings);
        if (ok && Json(expected) == now)
        {
            synced = now;
            ShowSaveError(null);
            return;
        }
        ShowSaveError(
            ok
                ? null
                : "The settings could not be saved. Check that the settings folder can be written."
        );
        if (modalDepth > 0)
        {
            synced = ""; // forces the reload once the dialog is closed
            reloadPending = true;
            return;
        }
        edit = SettingsStore.Copy(host.Settings);
        synced = now;
        LoadAll();
    }

    // The copy that is saved: nothing that does nothing (a base or layer entry for "Nothing").
    // App overrides keep it, there it turns a button off.
    static Settings Prepared(Settings source)
    {
        var s = SettingsStore.Copy(source);
        DropIdle(s.Base.Buttons);
        foreach (var layer in s.Layers)
        {
            DropIdle(layer.Buttons);
            if (layer.Tap is { Action: ActionKind.None })
                layer.Tap = null;
            if (layer.Hold is { Action: ActionKind.None })
                layer.Hold = null;
        }
        return s;
    }

    static void DropIdle(Dictionary<string, PadBinding> map)
    {
        foreach (var (id, b) in map.ToList())
        {
            if (b.Hold is { Action: ActionKind.None })
                b.Hold = null;
            if (b.Action == ActionKind.None && b.Hold is null)
                map.Remove(id);
        }
    }

    void ShowSaveError(string? text)
    {
        saveError.Text = text ?? "";
        saveError.Visible = text is not null;
    }

    // Raised on any thread.
    void OnHostChanged()
    {
        if (IsDisposed || !IsHandleCreated)
            return;
        try
        {
            BeginInvoke(RefreshFromHost);
        }
        catch (InvalidOperationException) { }
    }

    void RefreshFromHost()
    {
        if (IsDisposed)
            return;
        LoadPads();
        ShowStatusLine();
        LoadUpdate();
        if (saveTimer.Enabled)
            return;
        var now = Json(host.Settings);
        if (now == synced)
            return;
        if (modalDepth > 0)
        {
            reloadPending = true;
            return;
        }
        edit = SettingsStore.Copy(host.Settings);
        synced = now;
        LoadAll();
    }

    static PadBinding CloneBinding(PadBinding? b) =>
        b is null
            ? new PadBinding()
            : JsonSerializer.Deserialize<PadBinding>(
                JsonSerializer.Serialize(b, SettingsStore.Json),
                SettingsStore.Json
            )!;

    internal static string Describe(PadBinding? b)
    {
        if (b is null || b.Action == ActionKind.None)
            return "Nothing";
        var label = ActionKind.Labels.GetValueOrDefault(b.Action, b.Action);
        return string.IsNullOrEmpty(b.Arg) ? label : $"{label}: {b.Arg}";
    }
}

/// <summary>
/// A modal dialog around a page of the settings window: the page, a "Reset" button and "Close".
/// It does not own the page: TakeContent gives it back before the dialog is disposed.
/// </summary>
sealed class PageDialog : Form
{
    readonly Control content;

    public PageDialog(
        string title,
        Font font,
        Control content,
        Size clientSize,
        string? resetText,
        Action reset
    )
    {
        this.content = content;
        Text = title;
        Font = font;
        AutoScaleMode = AutoScaleMode.None;
        StartPosition = FormStartPosition.CenterParent;
        ShowInTaskbar = false;
        MinimizeBox = false;
        ClientSize = clientSize;
        MinimumSize = new Size(clientSize.Width * 3 / 4, clientSize.Height * 3 / 4);
        var close = new Button
        {
            Text = "Close",
            AutoSize = true,
            MinimumSize = new Size(Scaled(90), Scaled(28)),
            DialogResult = DialogResult.Cancel,
            Anchor = AnchorStyles.Right,
        };
        CancelButton = close;
        AcceptButton = close;
        var bottom = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            AutoSize = true,
        };
        bottom.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        bottom.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        if (resetText is not null)
        {
            var button = new Button
            {
                Text = resetText,
                AutoSize = true,
                Anchor = AnchorStyles.Left,
            };
            button.Click += (_, _) => reset();
            bottom.Controls.Add(button, 0, 0);
        }
        bottom.Controls.Add(close, 1, 0);
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
            Padding = new Padding(8),
        };
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        content.Dock = DockStyle.Fill;
        root.Controls.Add(content, 0, 0);
        root.Controls.Add(bottom, 0, 1);
        Controls.Add(root);
    }

    int Scaled(int n) => (int)Math.Round(n * DeviceDpi / 96.0);

    // A page that was scrolled when it was last closed opens at its top.
    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        if (content is ScrollableControl scrollable)
            scrollable.AutoScrollPosition = Point.Empty;
    }

    /// <summary>Takes the page out of the dialog so that disposing the dialog leaves it alone.</summary>
    internal Control TakeContent()
    {
        content.Parent?.Controls.Remove(content);
        return content;
    }
}

/// <summary>A label that wraps its text to the width its parent gives it, and grows in height to fit.</summary>
sealed class WrapLabel : Control
{
    public WrapLabel()
    {
        AutoSize = true;
        SetStyle(ControlStyles.SupportsTransparentBackColor | ControlStyles.ResizeRedraw, true);
        Margin = new Padding(3, 3, 3, 3);
    }

    [System.ComponentModel.DesignerSerializationVisibility(
        System.ComponentModel.DesignerSerializationVisibility.Hidden
    )]
    internal bool Muted { get; set; }

    public override Size GetPreferredSize(Size proposedSize)
    {
        var width = proposedSize.Width is > 0 and < 100_000
            ? proposedSize.Width
            : Math.Max(Parent?.ClientSize.Width ?? 400, 100);
        var inner = Math.Max(width - Padding.Horizontal, 40);
        var text = TextRenderer.MeasureText(
            string.IsNullOrEmpty(Text) ? " " : Text,
            Font,
            new Size(inner, 0),
            TextFormatFlags.WordBreak | TextFormatFlags.NoPadding
        );
        return new Size(width, text.Height + Padding.Vertical);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var color = !Enabled || Muted ? SystemColors.GrayText : ForeColor;
        TextRenderer.DrawText(
            e.Graphics,
            Text,
            Font,
            new Rectangle(
                Padding.Left,
                Padding.Top,
                ClientSize.Width - Padding.Horizontal,
                ClientSize.Height - Padding.Vertical
            ),
            color,
            TextFormatFlags.WordBreak | TextFormatFlags.NoPadding
        );
    }

    protected override void OnTextChanged(EventArgs e)
    {
        base.OnTextChanged(e);
        Invalidate();
        Parent?.PerformLayout(this, nameof(Text));
    }

    protected override void OnSizeChanged(EventArgs e)
    {
        base.OnSizeChanged(e);
        Invalidate();
    }
}

/// <summary>
/// The mouse wheel over a control that does not have the focus scrolls the page behind it instead
/// of changing the control (a combo box would pick another item, a number box move three steps,
/// and the change would be saved).
/// </summary>
static class WheelGuard
{
    /// <summary>True when the wheel event was taken (the control is not in use); the page has scrolled.</summary>
    public static bool Swallow(Control control, MouseEventArgs e, bool open)
    {
        if (open || control.ContainsFocus)
            return false;
        if (e is HandledMouseEventArgs handled)
            handled.Handled = true;
        for (var p = control.Parent; p is not null; p = p.Parent)
        {
            if (p is ScrollableControl { VerticalScroll.Visible: true } scrollable)
            {
                scrollable.AutoScrollPosition = new Point(
                    -scrollable.AutoScrollPosition.X,
                    -scrollable.AutoScrollPosition.Y - e.Delta / 2
                );
                break;
            }
        }
        return true;
    }
}

sealed class QuietComboBox : ComboBox
{
    protected override void OnMouseWheel(MouseEventArgs e)
    {
        if (!WheelGuard.Swallow(this, e, DroppedDown))
            base.OnMouseWheel(e);
    }
}

sealed class QuietNumericUpDown : NumericUpDown
{
    protected override void OnMouseWheel(MouseEventArgs e)
    {
        if (!WheelGuard.Swallow(this, e, false))
            base.OnMouseWheel(e);
    }
}

sealed class QuietTrackBar : TrackBar
{
    protected override void OnMouseWheel(MouseEventArgs e)
    {
        if (!WheelGuard.Swallow(this, e, false))
            base.OnMouseWheel(e);
    }
}

/// <summary>A combo box entry: an id and the text it shows.</summary>
sealed record ComboItem(string Id, string Text)
{
    public override string ToString() => Text;
}
