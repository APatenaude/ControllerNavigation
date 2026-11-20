using System.ComponentModel;
using System.Runtime.InteropServices;

namespace ControllerNavigation;

// The Buttons and Layers dialogs, and the rows they and the app editor are made of (BindingGrid).
sealed partial class SettingsForm
{
    static readonly Dictionary<string, string> StickLabels = new()
    {
        [StickMode.Pointer] = "Mouse pointer",
        [StickMode.Scroll] = "Scrolling",
        [StickMode.Arrows] = "Arrow keys",
        [StickMode.Wasd] = "W A S D keys",
        [StickMode.None] = "Nothing",
    };

    readonly ComboBox leftStick = StickCombo(false);
    readonly ComboBox rightStick = StickCombo(false);
    readonly BindingGrid baseGrid = new() { Dock = DockStyle.Fill };
    Control buttonsPage = null!,
        layersPage = null!;
    readonly ListBox layerList = new() { Dock = DockStyle.Fill, IntegralHeight = false };
    readonly Button layerAdd = new() { Text = "Add", AutoSize = true };
    readonly Button layerRemove = new() { Text = "Remove", AutoSize = true };
    readonly WrapLabel layerEmpty = new()
    {
        Text = "No layers. Add one to give a button a second job.",
        Muted = true,
        Dock = DockStyle.Fill,
    };
    readonly Panel layerDetail = new() { Dock = DockStyle.Fill, AutoScroll = true };
    readonly TextBox layerName = new() { Width = 150 };
    readonly ComboBox layerButton = new QuietComboBox()
    {
        DropDownStyle = ComboBoxStyle.DropDownList,
        Width = 200,
    };
    readonly CheckBox layerEverywhere = new()
    {
        Text = "Everywhere: also in games and every other window",
        AutoSize = true,
    };
    readonly WrapLabel layerGuideNote = new()
    {
        Text =
            "The Guide button is left to Windows: holding it opens Task View on Windows 11. That is why a layer's button is Start by default.",
        Dock = DockStyle.Fill,
    };
    readonly BindingGrid layerAlone = new() { Dock = DockStyle.Fill };
    readonly NumericUpDown layerHoldMs = new QuietNumericUpDown()
    {
        Minimum = 100,
        Maximum = 10000,
        Increment = 100,
        Width = 80,
    };
    readonly BindingGrid layerMap = new() { Dock = DockStyle.Fill };
    readonly ComboBox layerAddControl = new QuietComboBox()
    {
        DropDownStyle = ComboBoxStyle.DropDownList,
        Width = 220,
    };

    static ComboBox StickCombo(bool inherit)
    {
        var combo = new QuietComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 150 };
        if (inherit)
            combo.Items.Add(new ComboItem("", "Same as the base map"));
        foreach (var mode in Catalog.StickModes)
            combo.Items.Add(new ComboItem(mode, StickLabels.GetValueOrDefault(mode, mode)));
        return combo;
    }

    static void SelectId(ComboBox combo, string? id)
    {
        for (var i = 0; i < combo.Items.Count; i++)
        {
            if (combo.Items[i] is ComboItem item && item.Id == (id ?? ""))
            {
                combo.SelectedIndex = i;
                return;
            }
        }
        combo.SelectedIndex = -1;
    }

    static string? SelectedId(ComboBox combo) => (combo.SelectedItem as ComboItem)?.Id;

    // ---- Buttons

    Control BuildButtonsPage()
    {
        baseGrid.Changed = Touch;
        baseGrid.KeepNone = false;
        leftStick.SelectionChangeCommitted += (_, _) =>
        {
            edit.Base.LeftStick = SelectedId(leftStick) ?? StickMode.None;
            Touch();
        };
        rightStick.SelectionChangeCommitted += (_, _) =>
        {
            edit.Base.RightStick = SelectedId(rightStick) ?? StickMode.None;
            Touch();
        };
        var sticks = new FlowLayoutPanel
        {
            AutoSize = true,
            Dock = DockStyle.Fill,
            WrapContents = true,
        };
        sticks.Controls.Add(Caption("Left stick"));
        sticks.Controls.Add(leftStick);
        sticks.Controls.Add(Caption("Right stick"));
        sticks.Controls.Add(rightStick);
        var table = new TableLayoutPanel { ColumnCount = 1, RowCount = 3 };
        table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        table.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        table.Controls.Add(sticks, 0, 0);
        table.Controls.Add(
            new WrapLabel
            {
                Text =
                    "What each control does while an app that gets the mouse is in front. A long-press action runs instead when the button is held; the action then runs on a short press. Apps set to Always can override single buttons (Edit... in the app exceptions list).",
                Muted = true,
                Dock = DockStyle.Fill,
            },
            0,
            1
        );
        table.Controls.Add(baseGrid, 0, 2);
        loaders.Add(LoadButtons);
        buttonsPage = table;
        return table;
    }

    static Label Caption(string text) =>
        new()
        {
            Text = text,
            AutoSize = true,
            Anchor = AnchorStyles.Left,
            Margin = new Padding(8, 7, 4, 0),
        };

    void LoadButtons()
    {
        var was = loading;
        loading = true;
        try
        {
            SelectId(leftStick, edit.Base.LeftStick);
            SelectId(rightStick, edit.Base.RightStick);
            baseGrid.TakenHint = LayersButtonText();
            baseGrid.ShowMap(
                edit.Base.Buttons,
                Catalog.MapIds,
                taken: id => edit.Layers.FirstOrDefault(l => l.Button == id)?.Name
            );
        }
        finally
        {
            loading = was;
        }
    }

    // ---- Layers

    Layer? CurrentLayer =>
        layerList.SelectedIndex >= 0 && layerList.SelectedIndex < edit.Layers.Count
            ? edit.Layers[layerList.SelectedIndex]
            : null;

    Control BuildLayersPage()
    {
        layerAlone.Hold = false;
        layerAlone.Repeat = false;
        layerAlone.AutoScroll = false;
        layerMap.Removable = true;
        layerMap.KeepNone = true;
        layerAlone.Changed = Touch;
        layerMap.Changed = Touch;
        layerList.SelectedIndexChanged += (_, _) =>
        {
            if (!loading)
                ShowLayer();
        };
        layerAdd.Click += (_, _) => AddLayer();
        layerRemove.Click += (_, _) => RemoveLayer();
        layerName.TextChanged += (_, _) =>
        {
            if (loading || CurrentLayer is not { } layer)
                return;
            layer.Name = layerName.Text;
            var i = layerList.SelectedIndex;
            var was = loading;
            loading = true;
            layerList.Items[i] = layer.Name;
            layerList.SelectedIndex = i;
            loading = was;
            Touch();
        };
        layerButton.SelectionChangeCommitted += (_, _) => OnLayerButton();
        layerEverywhere.CheckedChanged += (_, _) =>
        {
            if (loading || CurrentLayer is not { } layer)
                return;
            layer.Everywhere = layerEverywhere.Checked;
            Touch();
        };
        layerHoldMs.ValueChanged += (_, _) =>
        {
            if (loading || CurrentLayer is not { } layer)
                return;
            layer.HoldMs = (int)layerHoldMs.Value;
            Touch();
        };
        layerAddControl.SelectionChangeCommitted += (_, _) =>
        {
            if (CurrentLayer is { } layer && SelectedId(layerAddControl) is { Length: > 0 } id)
            {
                layer.Buttons[id] = new PadBinding();
                ShowLayerMap(layer);
                Touch();
            }
        };

        var nameLine = new FlowLayoutPanel
        {
            AutoSize = true,
            Dock = DockStyle.Fill,
            WrapContents = true,
        };
        nameLine.Controls.Add(Caption("Name"));
        nameLine.Controls.Add(layerName);
        nameLine.Controls.Add(Caption("Layer button"));
        nameLine.Controls.Add(layerButton);
        var holdLine = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill };
        holdLine.Controls.Add(Caption("Held alone for"));
        holdLine.Controls.Add(layerHoldMs);
        holdLine.Controls.Add(Caption("ms"));

        var detail = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 1,
        };
        detail.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        void Row(Control c, SizeType type = SizeType.AutoSize, float height = 0)
        {
            detail.RowStyles.Add(new RowStyle(type, height));
            detail.Controls.Add(c, 0, detail.RowStyles.Count - 1);
        }
        Row(nameLine);
        Row(layerEverywhere);
        Row(layerGuideNote);
        Row(
            new Label
            {
                Text = "The layer button on its own",
                AutoSize = true,
                Font = new Font(Font, FontStyle.Bold),
                Margin = new Padding(3, 10, 3, 3),
            }
        );
        Row(layerAlone, SizeType.Absolute, 104);
        Row(holdLine);
        Row(
            new Label
            {
                Text = "While the layer button is held",
                AutoSize = true,
                Font = new Font(Font, FontStyle.Bold),
                Margin = new Padding(3, 10, 3, 3),
            }
        );
        Row(layerMap, SizeType.Absolute, 230);
        Row(layerAddControl);
        layerDetail.Controls.Add(detail);

        // The layers in a short list on top (usually one), the selected layer's editors below.
        var buttons = new FlowLayoutPanel
        {
            AutoSize = true,
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.TopDown,
        };
        buttons.Controls.Add(layerAdd);
        buttons.Controls.Add(layerRemove);
        var table = new TableLayoutPanel { ColumnCount = 2, RowCount = 2 };
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        table.RowStyles.Add(new RowStyle(SizeType.Absolute, 64));
        table.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        table.Controls.Add(layerList, 0, 0);
        table.Controls.Add(buttons, 1, 0);
        var below = new Panel { Dock = DockStyle.Fill };
        below.Controls.Add(layerDetail);
        below.Controls.Add(layerEmpty);
        table.Controls.Add(below, 0, 1);
        table.SetColumnSpan(below, 2);
        loaders.Add(LoadLayers);
        layersPage = table;
        return table;
    }

    void ResetLayers() => edit.Layers = Defaults.Layers();

    void LoadLayers()
    {
        var was = loading;
        loading = true;
        try
        {
            var keep = Math.Max(layerList.SelectedIndex, 0);
            layerList.Items.Clear();
            foreach (var layer in edit.Layers)
                layerList.Items.Add(layer.Name);
            if (edit.Layers.Count > 0)
                layerList.SelectedIndex = Math.Min(keep, edit.Layers.Count - 1);
        }
        finally
        {
            loading = was;
        }
        ShowLayer();
    }

    void ShowLayer()
    {
        var layer = CurrentLayer;
        layerDetail.Visible = layer is not null;
        layerEmpty.Visible = layer is null;
        layerRemove.Enabled = layer is not null;
        if (layer is null)
            return;
        var was = loading;
        loading = true;
        try
        {
            layerName.Text = layer.Name;
            layerButton.Items.Clear();
            foreach (var id in Catalog.ControlIds)
            {
                var other = edit.Layers.FirstOrDefault(l => l != layer && l.Button == id);
                var text =
                    Catalog.Labels.GetValueOrDefault(id, id)
                    + (other is null ? "" : $" (used by {other.Name})");
                layerButton.Items.Add(new ComboItem(id, text));
            }
            SelectId(layerButton, layer.Button);
            layerEverywhere.Checked = layer.Everywhere;
            layerHoldMs.Value = Math.Clamp(layer.HoldMs, 100, 10000);
            layerGuideNote.Visible = layer.Button == "guide";
            layerAlone.ShowRows(
                new[]
                {
                    new RowSpec(
                        "Tap",
                        "A short press of the layer button, when nothing else was pressed with it.",
                        () => layer.Tap,
                        b => layer.Tap = b,
                        null,
                        null
                    ),
                    new RowSpec(
                        "Hold",
                        "The layer button held alone for the time below.",
                        () => layer.Hold,
                        b => layer.Hold = b,
                        null,
                        null
                    ),
                }
            );
            ShowLayerMap(layer);
        }
        finally
        {
            loading = was;
        }
    }

    void ShowLayerMap(Layer layer)
    {
        var ids = Catalog.ControlIds.Where(layer.Buttons.ContainsKey).ToList();
        layerMap.ShowMap(
            layer.Buttons,
            ids,
            remove: id =>
                BeginInvoke(() =>
                {
                    layer.Buttons.Remove(id);
                    ShowLayerMap(layer);
                    Touch();
                })
        );
        var was = loading;
        loading = true;
        layerAddControl.Items.Clear();
        layerAddControl.Items.Add(new ComboItem("", "Add a button..."));
        foreach (var id in Catalog.ControlIds)
            if (!layer.Buttons.ContainsKey(id) && id != layer.Button)
                layerAddControl.Items.Add(
                    new ComboItem(id, Catalog.Labels.GetValueOrDefault(id, id))
                );
        layerAddControl.SelectedIndex = 0;
        loading = was;
    }

    void OnLayerButton()
    {
        if (CurrentLayer is not { } layer || SelectedId(layerButton) is not { } id)
            return;
        var other = edit.Layers.FirstOrDefault(l => l != layer && l.Button == id);
        if (other is not null)
        {
            MessageBox.Show(
                this,
                $"The layer \"{other.Name}\" already uses that button.",
                AppInfo.Name,
                MessageBoxButtons.OK,
                MessageBoxIcon.Information
            );
            SelectId(layerButton, layer.Button);
            return;
        }
        layer.Button = id;
        layer.Buttons.Remove(id);
        layerGuideNote.Visible = id == "guide";
        ShowLayerMap(layer);
        Touch();
    }

    void AddLayer()
    {
        var used = edit.Layers.Select(l => l.Button).ToHashSet();
        var button = new[] { "start", "back", "lb", "rb", "l3", "r3" }
            .Concat(Catalog.ControlIds)
            .FirstOrDefault(id => !used.Contains(id));
        if (button is null)
            return;
        var n = edit.Layers.Count + 1;
        while (edit.Layers.Any(l => l.Name == $"Layer {n}"))
            n++;
        edit.Layers.Add(
            new Layer
            {
                Name = $"Layer {n}",
                Button = button,
                Everywhere = false,
            }
        );
        Touch();
        LoadLayers();
        layerList.SelectedIndex = edit.Layers.Count - 1;
        layerName.Focus();
        layerName.SelectAll();
    }

    void RemoveLayer()
    {
        if (CurrentLayer is not { } layer)
            return;
        var sure = MessageBox.Show(
            this,
            $"Remove the layer \"{layer.Name}\" and its buttons?",
            AppInfo.Name,
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Question
        );
        if (sure != DialogResult.Yes)
            return;
        edit.Layers.Remove(layer);
        Touch();
        LoadLayers();
    }
}

/// <summary>Key names as KeyCombo knows them, for the recorder: a Keys value (a virtual-key code) to its name, and a combo's canonical text.</summary>
static class KeyNames
{
    static readonly Dictionary<int, string> ByVk = Build();

    static Dictionary<int, string> Build()
    {
        var map = new Dictionary<int, string>();
        foreach (var name in KeyCombo.Names)
            if (KeyCombo.Parse(name) is [var vk])
                map[vk] = name;
        map[0x5C] = "Win";
        return map;
    }

    public static string? NameOf(Keys key) => ByVk.GetValueOrDefault((int)key);

    public static bool IsModifier(string? name) => name is "Ctrl" or "Alt" or "Shift" or "Win";

    /// <summary>"shift+ctrl+t" as "Ctrl+Shift+T" (modifiers first, KeyCombo's casing), or null if KeyCombo refuses it.</summary>
    public static string? Canonical(string? text) =>
        KeyCombo.Parse(text) is { } vks ? string.Join('+', vks.Select(v => ByVk[v])) : null;
}

/// <summary>
/// A text box that can also record a key combination: while Recording, every key goes to it (even
/// Tab, Esc and Alt combinations) and the first non-modifier key, or a modifier let go of alone,
/// ends the recording through Recorded. Windows keeps Win combinations for itself: those are typed.
/// </summary>
sealed class KeyRecorderBox : TextBox
{
    const int WM_KEYDOWN = 0x100,
        WM_SYSKEYDOWN = 0x104;

    [DllImport("user32.dll")]
    static extern short GetAsyncKeyState(int key);

    readonly HashSet<string> held = new();
    bool recording,
        mainPressed;

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    internal bool Recording
    {
        get => recording;
        set
        {
            recording = value;
            held.Clear();
            mainPressed = false;
        }
    }

    public event Action<string>? Recorded;

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (!recording || (msg.Msg != WM_KEYDOWN && msg.Msg != WM_SYSKEYDOWN))
            return base.ProcessCmdKey(ref msg, keyData);
        var name = KeyNames.NameOf(keyData & Keys.KeyCode);
        if (name is null)
            return true;
        if (KeyNames.IsModifier(name))
        {
            held.Add(name);
            return true;
        }
        mainPressed = true;
        if ((keyData & Keys.Control) != 0)
            held.Add("Ctrl");
        if ((keyData & Keys.Alt) != 0)
            held.Add("Alt");
        if ((keyData & Keys.Shift) != 0)
            held.Add("Shift");
        if (GetAsyncKeyState(0x5B) < 0 || GetAsyncKeyState(0x5C) < 0)
            held.Add("Win");
        Finish(name);
        return true;
    }

    protected override void OnKeyUp(KeyEventArgs e)
    {
        base.OnKeyUp(e);
        if (
            !recording
            || mainPressed
            || held.Count == 0
            || !KeyNames.IsModifier(KeyNames.NameOf(e.KeyCode))
        )
            return;
        e.Handled = true;
        Finish(null);
    }

    void Finish(string? key)
    {
        var parts = new[] { "Ctrl", "Alt", "Shift", "Win" }.Where(held.Contains).ToList();
        if (key is not null)
            parts.Add(key);
        held.Clear();
        Recorded?.Invoke(string.Join('+', parts));
    }
}

enum ArgKind
{
    None,
    Keys,
    Command,
}

/// <summary>
/// The argument of an action: a recorder for a key action (typed or recorded, committed once
/// KeyCombo accepts it), a plain box for a program, file or address.
/// </summary>
sealed class ArgEditor : Panel
{
    static readonly Color Bad = Color.FromArgb(255, 205, 205);
    readonly KeyRecorderBox text = new() { Dock = DockStyle.Fill };
    readonly CheckBox record = new()
    {
        Appearance = Appearance.Button,
        Text = "Rec",
        Dock = DockStyle.Right,
        TextAlign = ContentAlignment.MiddleCenter,
    };
    readonly ToolTip tips;
    ArgKind kind;
    string committed = "";
    bool setting;

    /// <summary>The user changed the value (a key combination KeyCombo accepts, or any text for a command).</summary>
    public event Action<string>? Committed;

    public ArgEditor(ToolTip tips, int recordWidth)
    {
        this.tips = tips;
        record.Width = recordWidth;
        Controls.Add(text);
        Controls.Add(record);
        text.TextChanged += (_, _) => OnText();
        text.Leave += (_, _) =>
        {
            // A click on the button moves the focus here first: its Click then stops the recording.
            if (!record.ClientRectangle.Contains(record.PointToClient(Cursor.Position)))
                SetRecording(false);
            if (kind == ArgKind.Keys && KeyNames.Canonical(text.Text) is null)
                SetText(committed);
        };
        text.Recorded += combo =>
        {
            SetRecording(false);
            SetText(combo);
            OnText();
        };
        record.AutoCheck = false;
        record.Click += (_, _) => SetRecording(!record.Checked);
        tips.SetToolTip(
            record,
            "Record: press the keys, or just a modifier. Windows keeps Win combinations for itself: type those, like Win+E."
        );
    }

    public void Configure(ArgKind kind, string? value)
    {
        this.kind = kind;
        Visible = kind != ArgKind.None;
        record.Visible = kind == ArgKind.Keys;
        committed = value ?? "";
        SetText(committed);
    }

    void SetRecording(bool on)
    {
        record.Checked = on;
        text.Recording = on;
        record.Text = on ? "..." : "Rec";
        if (on)
            text.Focus();
    }

    void SetText(string value)
    {
        setting = true;
        text.Text = value;
        text.BackColor = SystemColors.Window;
        tips.SetToolTip(
            text,
            kind == ArgKind.Keys
                ? "Ctrl, Alt, Shift or Win, then one key: Ctrl+Shift+T, F11, Win+E"
                : "A program, file or web address"
        );
        setting = false;
    }

    void OnText()
    {
        if (setting)
            return;
        if (kind == ArgKind.Command)
        {
            committed = text.Text;
            Committed?.Invoke(committed);
            return;
        }
        if (KeyNames.Canonical(text.Text) is { } combo)
        {
            text.BackColor = SystemColors.Window;
            committed = combo;
            Committed?.Invoke(combo);
        }
        else
        {
            text.BackColor = text.Text.Length == 0 ? SystemColors.Window : Bad;
        }
    }
}

/// <summary>What one row of a BindingGrid edits.</summary>
/// <param name="Get">The binding now, or null.</param>
/// <param name="Set">Stores the binding, or removes it (null).</param>
/// <param name="TakenBy">A layer that owns this control: the row shows that instead of editors.</param>
/// <param name="Remove">Shows a remove button that calls this.</param>
/// <param name="NoHold">No long-press cells even in a grid that has them (a pair runs at once).</param>
sealed record RowSpec(
    string Label,
    string? Tip,
    Func<PadBinding?> Get,
    Action<PadBinding?> Set,
    string? TakenBy,
    Action? Remove,
    bool NoHold = false
);

/// <summary>
/// A scrolling table of bindings, one row each: control, action, its argument, repeat, and an
/// optional long-press action with its argument. Rows are laid out by hand (Layout) so that the
/// columns line up and the table scrolls sideways when the window is narrow.
/// </summary>
sealed class BindingGrid : Panel
{
    const int RowHeight = 30;
    readonly ToolTip tips = new();
    readonly List<Control> rows = new();
    Control? header;

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    internal bool Hold { get; set; } = true;

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    internal bool Repeat { get; set; } = true;

    /// <summary>A row set to "Nothing" stays (an app override that turns a button off; a row just added).</summary>
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    internal bool KeepNone { get; set; }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    internal bool Removable { get; set; }

    /// <summary>The text of the main window's layers button, named in the "taken by a layer" rows.</summary>
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    internal string TakenHint { get; set; } = "Hold buttons...";

    /// <summary>An edit: the form saves after a pause.</summary>
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    internal Action? Changed { get; set; }

    public BindingGrid()
    {
        AutoScroll = true;
        BorderStyle = BorderStyle.FixedSingle;
        BackColor = SystemColors.Window;
    }

    int Px(int n) => (int)Math.Round(n * (FindForm()?.DeviceDpi ?? DeviceDpi) / 96.0);

    /// <summary>One row per control in ids, bound to the map. baseMap: the bindings an override differs from (shown in the tooltip).</summary>
    public void ShowMap(
        Dictionary<string, PadBinding> map,
        IEnumerable<string> ids,
        Func<string, string?>? taken = null,
        Dictionary<string, PadBinding>? baseMap = null,
        Action<string>? remove = null
    ) =>
        ShowRows(
            ids.Select(id => new RowSpec(
                    Catalog.Labels.GetValueOrDefault(id, id),
                    baseMap is null
                        ? Catalog.Labels.GetValueOrDefault(id, id)
                        : $"Base map: {SettingsForm.Describe(baseMap.GetValueOrDefault(id))}",
                    () => map.GetValueOrDefault(id),
                    b =>
                    {
                        if (b is null)
                            map.Remove(id);
                        else
                            map[id] = b;
                    },
                    taken?.Invoke(id),
                    remove is null ? null : () => remove(id),
                    NoHold: Catalog.PairOf(id) is not null
                ))
                .ToList()
        );

    public void ShowRows(IReadOnlyList<RowSpec> specs)
    {
        SuspendLayout();
        var scroll = -AutoScrollPosition.Y;
        AutoScrollPosition = Point.Empty;
        foreach (var row in rows)
            row.Dispose();
        rows.Clear();
        header?.Dispose();
        header = MakeHeader();
        Controls.Add(header);
        foreach (var spec in specs)
        {
            var row = new BindingRow(spec, this, tips, Px);
            rows.Add(row);
            Controls.Add(row);
        }
        LayoutRows();
        ResumeLayout(true);
        AutoScrollPosition = new Point(0, scroll);
    }

    protected override void OnResize(EventArgs eventargs)
    {
        base.OnResize(eventargs);
        if (rows.Count > 0 || header is not null)
        {
            var scroll = -AutoScrollPosition.Y;
            AutoScrollPosition = Point.Empty;
            LayoutRows();
            AutoScrollPosition = new Point(0, scroll);
        }
    }

    void LayoutRows()
    {
        var minWidth = Px(Hold ? 660 : 430);
        var width = Math.Max(ClientSize.Width, minWidth);
        var y = 0;
        if (header is not null)
        {
            header.SetBounds(0, y, width, Px(RowHeight - 6));
            LayoutHeader(header.Controls.Cast<Control>().ToArray(), width);
            y += header.Height;
        }
        foreach (var row in rows)
        {
            row.SetBounds(0, y, width, Px(RowHeight));
            ((BindingRow)row).LayoutCells(width);
            y += row.Height;
        }
        AutoScrollMinSize = new Size(minWidth, y);
    }

    Control MakeHeader()
    {
        var panel = new Panel { BackColor = SystemColors.Control };
        var names = new List<string> { "Control", "Action", "Keys or program" };
        if (Repeat)
            names.Add("Repeat");
        if (Hold)
        {
            names.Add("Long press");
            names.Add("Long-press keys");
        }
        foreach (var name in names)
            panel.Controls.Add(
                new Label
                {
                    Text = name,
                    AutoEllipsis = true,
                    TextAlign = ContentAlignment.MiddleLeft,
                    ForeColor = SystemColors.ControlText,
                    Font = new Font(Font, FontStyle.Bold),
                }
            );
        return panel;
    }

    // Columns: control, action, argument, repeat, long-press action, long-press argument, remove.
    // The same cells for the header (labels in order) and the rows (the controls named in cells).
    internal Rectangle[] Cells(int width, int height)
    {
        var pad = Px(4);
        var label = Px(130);
        var repeat = Repeat ? Px(48) : 0;
        var remove = Removable ? Px(30) : 0;
        var flexible = width - pad * 2 - label - repeat - remove;
        double[] shares = Hold ? new[] { 0.30, 0.24, 0.24, 0.22 } : new[] { 0.42, 0.58 };
        var x = pad;
        var cells = new List<Rectangle> { new(x, 0, label, height) };
        x += label;
        var a = new List<int>();
        for (var i = 0; i < shares.Length; i++)
            a.Add((int)(flexible * shares[i]));
        cells.Add(new Rectangle(x, 0, a[0] - pad, height));
        x += a[0];
        cells.Add(new Rectangle(x, 0, a[1] - pad, height));
        x += a[1];
        if (Repeat)
        {
            cells.Add(new Rectangle(x, 0, repeat, height));
            x += repeat;
        }
        if (Hold)
        {
            cells.Add(new Rectangle(x, 0, a[2] - pad, height));
            x += a[2];
            cells.Add(new Rectangle(x, 0, a[3] - pad, height));
            x += a[3];
        }
        cells.Add(new Rectangle(width - pad - remove, 0, remove, height));
        return cells.ToArray();
    }

    void LayoutHeader(Control[] labels, int width)
    {
        var cells = Cells(width, Px(RowHeight - 6));
        for (var i = 0; i < labels.Length && i < cells.Length; i++)
            labels[i].Bounds = cells[i];
    }

    internal void RaiseChanged() => Changed?.Invoke();

    // One menu of actions for the whole grid, shared by every row's action buttons (a combo box
    // in each row cost far too much window-handle time). Built at the first use.
    ContextMenuStrip? actionMenu,
        holdMenu;
    Action<string>? picked;

    /// <summary>Shows the action menu under a row's button; the item chosen is passed to onPicked (a different action than currentId).</summary>
    internal void PickAction(
        Control anchor,
        bool withNone,
        string currentId,
        Action<string> onPicked
    )
    {
        var menu = withNone ? (holdMenu ??= BuildMenu(true)) : (actionMenu ??= BuildMenu(false));
        picked = onPicked;
        foreach (ToolStripMenuItem item in menu.Items)
            item.Checked = (string)item.Tag! == currentId;
        menu.Show(anchor, new Point(0, anchor.Height));
    }

    // withNone: the long-press menu, whose first entry "(none)" has the id "" and which has no "Nothing".
    ContextMenuStrip BuildMenu(bool withNone)
    {
        var menu = new ContextMenuStrip { ShowCheckMargin = true, ShowImageMargin = false };
        if (withNone)
            menu.Items.Add(NewItem("", "(none)"));
        foreach (var id in ActionKind.All)
            if (!withNone || id != ActionKind.None)
                menu.Items.Add(NewItem(id, ActionKind.Labels.GetValueOrDefault(id, id)));
        return menu;
    }

    ToolStripMenuItem NewItem(string id, string text)
    {
        var item = new ToolStripMenuItem(text) { Tag = id };
        item.Click += (_, _) => picked?.Invoke(id);
        return item;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            tips.Dispose();
            actionMenu?.Dispose();
            holdMenu?.Dispose();
        }
        base.Dispose(disposing);
    }
}

/// <summary>
/// A button that looks like a drop-down list: the chosen text and an arrow. It opens a menu
/// instead of a list because a real combo box takes tens of milliseconds to get its window.
/// </summary>
sealed class PickerButton : Button
{
    public PickerButton()
    {
        TextAlign = ContentAlignment.MiddleLeft;
        AutoEllipsis = true;
        UseVisualStyleBackColor = true;
    }

    // Keeps the text clear of the arrow.
    protected override void OnSizeChanged(EventArgs e)
    {
        base.OnSizeChanged(e);
        Padding = new Padding(0, 0, Height * 3 / 4, 0);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var size = Math.Max(4, Height / 6);
        var cx = Width - size * 2;
        var cy = Height / 2;
        using var brush = new SolidBrush(
            Enabled ? SystemColors.ControlText : SystemColors.GrayText
        );
        e.Graphics.FillPolygon(
            brush,
            new[]
            {
                new Point(cx - size, cy - size / 2),
                new Point(cx + size, cy - size / 2),
                new Point(cx, cy + size / 2 + 1),
            }
        );
    }
}

/// <summary>One row of a BindingGrid. Edits go straight into the binding (created when the first one is made, removed when it does nothing).</summary>
sealed class BindingRow : Panel
{
    static readonly string[] NoRepeat =
    {
        ActionKind.None,
        ActionKind.Precise,
        ActionKind.PreciseToggle,
    };
    readonly RowSpec spec;
    readonly BindingGrid grid;
    readonly Label name = new() { AutoEllipsis = true, TextAlign = ContentAlignment.MiddleLeft };
    readonly PickerButton action = new();
    string actionId = ActionKind.None;
    readonly ArgEditor arg;
    readonly CheckBox repeat = new()
    {
        AutoSize = false,
        CheckAlign = ContentAlignment.MiddleCenter,
    };
    readonly PickerButton holdAction = new();
    string holdId = "";
    readonly ArgEditor holdArg;
    readonly Button remove = new() { Text = "X", TabStop = true };
    readonly Label taken = new()
    {
        AutoEllipsis = true,
        TextAlign = ContentAlignment.MiddleLeft,
        ForeColor = SystemColors.GrayText,
    };
    bool filling;

    public BindingRow(RowSpec spec, BindingGrid grid, ToolTip tips, Func<int, int> px)
    {
        this.spec = spec;
        this.grid = grid;
        arg = new ArgEditor(tips, px(44));
        holdArg = new ArgEditor(tips, px(44));
        BackColor = SystemColors.Window;
        name.Text = spec.Label;
        tips.SetToolTip(name, spec.Tip ?? spec.Label);
        remove.Width = px(26);
        tips.SetToolTip(remove, "Remove this row");
        tips.SetToolTip(repeat, "Keep repeating while the button is held");

        if (spec.TakenBy is { } layer)
        {
            taken.Text =
                $"Taken by the layer \"{layer}\": this button opens it. Set what it does alone with the \"{grid.TakenHint}\" button of the main window.";
            Controls.Add(name);
            Controls.Add(taken);
            return;
        }
        Controls.Add(name);
        Controls.Add(action);
        Controls.Add(arg);
        if (grid.Repeat)
            Controls.Add(repeat);
        if (grid.Hold && !spec.NoHold)
        {
            Controls.Add(holdAction);
            Controls.Add(holdArg);
        }
        if (spec.Remove is not null)
            Controls.Add(remove);

        action.Click += (_, _) => grid.PickAction(action, false, actionId, OnAction);
        arg.Committed += v => Edit(b => b.Arg = v);
        repeat.CheckedChanged += (_, _) => Edit(b => b.Repeat = repeat.Checked);
        holdAction.Click += (_, _) => grid.PickAction(holdAction, true, holdId, OnHoldAction);
        holdArg.Committed += v =>
            Edit(b =>
            {
                if (b.Hold is not null)
                    b.Hold.Arg = v;
            });
        remove.Click += (_, _) => spec.Remove?.Invoke();
        Fill();
    }

    static ArgKind KindOf(string? action) =>
        action == ActionKind.Key ? ArgKind.Keys
        : action == ActionKind.Run ? ArgKind.Command
        : ArgKind.None;

    static bool Repeatable(string action) =>
        !NoRepeat.Contains(action) && !action.StartsWith("mouse", StringComparison.Ordinal);

    void Fill()
    {
        filling = true;
        var b = spec.Get();
        actionId = b?.Action ?? ActionKind.None;
        action.Text = ActionLabel(actionId);
        arg.Configure(KindOf(actionId), b?.Arg);
        repeat.Checked = b?.Repeat ?? false;
        holdId = b?.Hold?.Action ?? "";
        holdAction.Text = holdId.Length == 0 ? "(none)" : ActionLabel(holdId);
        holdArg.Configure(KindOf(b?.Hold?.Action), b?.Hold?.Arg);
        UpdateVisibility(actionId);
        filling = false;
    }

    void UpdateVisibility(string actionId) => repeat.Visible = Repeatable(actionId);

    static string ActionLabel(string id) => ActionKind.Labels.GetValueOrDefault(id, id);

    void OnAction(string id)
    {
        if (filling || id == actionId)
            return;
        actionId = id;
        action.Text = ActionLabel(id);
        Edit(b =>
        {
            b.Action = id;
            b.Arg = null;
            if (!Repeatable(id))
                b.Repeat = false;
        });
        filling = true;
        arg.Configure(KindOf(id), null);
        repeat.Checked = spec.Get()?.Repeat ?? false;
        UpdateVisibility(id);
        filling = false;
    }

    void OnHoldAction(string id)
    {
        if (filling || id == holdId)
            return;
        holdId = id;
        holdAction.Text = id.Length == 0 ? "(none)" : ActionLabel(id);
        Edit(b =>
        {
            if (id.Length == 0)
                b.Hold = null;
            else
                b.Hold = new PadBinding { Action = id };
        });
        holdArg.Configure(KindOf(id), null);
    }

    void Edit(Action<PadBinding> change)
    {
        if (filling)
            return;
        var b = spec.Get() ?? new PadBinding();
        change(b);
        var idle = b.Action == ActionKind.None && b.Hold is null;
        spec.Set(idle && !grid.KeepNone ? null : b);
        grid.RaiseChanged();
    }

    internal void LayoutCells(int width)
    {
        var cells = grid.Cells(width, Height);
        void Place(Control c, Rectangle cell) =>
            c.SetBounds(cell.X, cell.Y + 2, cell.Width, cell.Height - 4);
        name.Bounds = cells[0];
        if (spec.TakenBy is not null)
        {
            taken.Bounds = new Rectangle(cells[1].X, 0, width - cells[1].X - cells[0].X, Height);
            return;
        }
        Place(action, cells[1]);
        Place(arg, cells[2]);
        var i = 3;
        if (grid.Repeat)
            repeat.Bounds = cells[i++];
        if (grid.Hold)
        {
            Place(holdAction, cells[i++]);
            Place(holdArg, cells[i++]);
        }
        if (spec.Remove is not null)
        {
            var cell = cells[i];
            remove.SetBounds(cell.X, cell.Y + 2, cell.Width, cell.Height - 4);
        }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        e.Graphics.DrawLine(SystemPens.ControlLight, 0, Height - 1, Width, Height - 1);
    }
}
