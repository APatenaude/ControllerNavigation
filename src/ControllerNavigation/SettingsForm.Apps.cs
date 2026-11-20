namespace ControllerNavigation;

// The app exceptions (Always or Excluded from the mouse): the list on the main window, and the dialog
// that edits one (mode, programs, stick and button overrides, title rules).
sealed partial class SettingsForm
{
    // A ListView with check boxes: only a click on the box turns a rule on or off.
    readonly ListView appList = new()
    {
        Dock = DockStyle.Fill,
        View = View.Details,
        HeaderStyle = ColumnHeaderStyle.Nonclickable,
        CheckBoxes = true,
        FullRowSelect = true,
        HideSelection = false,
        MultiSelect = false,
        ShowItemToolTips = true,
    };
    readonly Button appAddRunning = new() { Text = "Add running app...", AutoSize = true };
    readonly Button appNew = new() { Text = "New...", AutoSize = true };
    readonly Button appEdit = new() { Text = "Edit...", AutoSize = true };
    readonly Button appRemove = new() { Text = "Remove", AutoSize = true };

    readonly Panel appDetail = new() { Dock = DockStyle.Fill, AutoScroll = true };
    readonly TextBox appName = new() { Width = 220 };
    readonly ComboBox appMode = new QuietComboBox
    {
        DropDownStyle = ComboBoxStyle.DropDownList,
        Width = 330,
    };
    readonly WrapLabel appNeverNote = new()
    {
        Text =
            "This app is excluded: it never gets the mouse, so the stick overrides, button overrides and title rules below do not apply. They are for apps set to Always.",
        Dock = DockStyle.Fill,
        Visible = false,
    };
    readonly ListBox appExes = new() { Dock = DockStyle.Fill, IntegralHeight = false };
    readonly TextBox appExeNew = new() { Width = 150, PlaceholderText = "program.exe" };
    readonly Button appExeAdd = new() { Text = "Add", AutoSize = true };
    readonly Button appExeRemove = new() { Text = "Remove", AutoSize = true };
    readonly Button appExeRunning = new() { Text = "Add a running app...", AutoSize = true };
    readonly ComboBox appLeft = StickCombo(true);
    readonly ComboBox appRight = StickCombo(true);
    readonly BindingGrid appMap = new() { Dock = DockStyle.Fill };
    readonly ComboBox appMapAdd = new QuietComboBox
    {
        DropDownStyle = ComboBoxStyle.DropDownList,
        Width = 220,
    };
    readonly TextBox appTitles = new()
    {
        Multiline = true,
        AcceptsReturn = true,
        ScrollBars = ScrollBars.Vertical,
        Dock = DockStyle.Fill,
        WordWrap = false,
    };
    readonly Button appTitleUse = new() { Text = "Use the current title", AutoSize = true };
    int editingIndex = -1;

    AppRule? editingApp =>
        editingIndex >= 0 && editingIndex < edit.Apps.Count ? edit.Apps[editingIndex] : null;

    /// <summary>The "App exceptions" group of the main window.</summary>
    GroupBox BuildAppsGroup()
    {
        appList.Columns.Add("App", 300);
        appList.Columns.Add("Mode", Px(70));
        appList.Resize += (_, _) => FitAppColumn();
        // The ListView raises ItemChecked for every item when its handle is created: only a
        // change from what the app already says is an edit.
        appList.ItemChecked += (_, e) =>
        {
            if (loading || e.Item.Index >= edit.Apps.Count)
                return;
            var app = edit.Apps[e.Item.Index];
            if (app.Enabled == e.Item.Checked)
                return;
            app.Enabled = e.Item.Checked;
            Touch();
        };
        appList.DoubleClick += (_, _) => EditSelectedApp();
        appList.SelectedIndexChanged += (_, _) => UpdateAppButtons();
        appAddRunning.Click += (_, _) =>
        {
            if (PickRunningApp(true) is { } picked)
                AddApp(picked.Exe, picked.Mode);
        };
        appNew.Click += (_, _) => AddApp(null, AppMode.Always);
        appEdit.Click += (_, _) => EditSelectedApp();
        appRemove.Click += (_, _) => RemoveApp();
        BuildAppEditor();

        var buttons = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill };
        buttons.Controls.AddRange(new Control[] { appAddRunning, appNew, appEdit, appRemove });
        var table = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 3,
        };
        table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        table.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        table.Controls.Add(
            new WrapLabel
            {
                Text =
                    "Every app gets the mouse automatically, except games and apps that read the controller. Exceptions: Always = gets the mouse even though it keeps the controller open (the desktop, browsers and Discord do). Excluded = never gets the mouse.",
                Muted = true,
                Dock = DockStyle.Fill,
            },
            0,
            0
        );
        table.Controls.Add(appList, 0, 1);
        table.Controls.Add(buttons, 0, 2);
        var group = new GroupBox
        {
            Text = "App exceptions",
            Dock = DockStyle.Fill,
            Padding = new Padding(8, 4, 8, 8),
        };
        group.Controls.Add(table);
        loaders.Add(LoadApps);
        return group;
    }

    void BuildAppEditor()
    {
        appName.TextChanged += (_, _) =>
        {
            if (loading || editingApp is not { } app)
                return;
            app.Name = appName.Text;
            Touch();
        };
        appExeAdd.Click += (_, _) => AddExe(appExeNew.Text);
        // Without this the dialog's Close button (its default button) takes the Enter key.
        appExeNew.PreviewKeyDown += (_, e) => e.IsInputKey |= e.KeyCode == Keys.Enter;
        appExeNew.KeyDown += (_, e) =>
        {
            if (e.KeyCode != Keys.Enter)
                return;
            e.SuppressKeyPress = true;
            AddExe(appExeNew.Text);
        };
        appExeRemove.Click += (_, _) =>
        {
            if (editingApp is not { } app || appExes.SelectedIndex < 0)
                return;
            app.Exes.RemoveAt(appExes.SelectedIndex);
            Touch();
            ShowApp();
        };
        appExeRunning.Click += (_, _) =>
        {
            if (PickRunningApp(false) is { } picked)
                AddExe(picked.Exe);
        };
        appMode.Items.Add(
            new ComboItem(AppMode.Always, "Always: gets the mouse, even if it reads the controller")
        );
        appMode.Items.Add(new ComboItem(AppMode.Never, "Excluded: never gets the mouse"));
        appMode.SelectionChangeCommitted += (_, _) =>
        {
            if (editingApp is not { } app)
                return;
            app.Mode = SelectedId(appMode) ?? AppMode.Always;
            UpdateModeUi(app);
            Touch();
        };
        appLeft.SelectionChangeCommitted += (_, _) =>
        {
            if (editingApp is { } app)
            {
                app.LeftStick = SelectedId(appLeft) is { Length: > 0 } id ? id : null;
                Touch();
            }
        };
        appRight.SelectionChangeCommitted += (_, _) =>
        {
            if (editingApp is { } app)
            {
                app.RightStick = SelectedId(appRight) is { Length: > 0 } id ? id : null;
                Touch();
            }
        };
        appMap.KeepNone = true;
        appMap.Removable = true;
        appMap.Changed = Touch;
        appMapAdd.SelectionChangeCommitted += (_, _) =>
        {
            if (editingApp is { } app && SelectedId(appMapAdd) is { Length: > 0 } id)
            {
                app.Buttons[id] = CloneBinding(edit.Base.Buttons.GetValueOrDefault(id));
                ShowAppMap(app);
                Touch();
            }
        };
        appTitles.TextChanged += (_, _) =>
        {
            if (loading || editingApp is not { } app)
                return;
            app.TitleOff = appTitles.Lines.Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
            Touch();
        };
        appTitleUse.Click += (_, _) => UseCurrentTitle();

        var detail = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 1,
        };
        detail.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        void Row(Control c, float height = 0)
        {
            detail.RowStyles.Add(
                new RowStyle(height > 0 ? SizeType.Absolute : SizeType.AutoSize, height)
            );
            detail.Controls.Add(c, 0, detail.RowStyles.Count - 1);
        }
        FlowLayoutPanel Line(params Control[] controls)
        {
            var line = new FlowLayoutPanel
            {
                AutoSize = true,
                Dock = DockStyle.Fill,
                WrapContents = true,
            };
            line.Controls.AddRange(controls);
            return line;
        }
        Row(Line(Caption("Name"), appName));
        Row(Line(Caption("Mode"), appMode));
        Row(appNeverNote);
        Row(Heading("Programs"));
        Row(
            new WrapLabel
            {
                Text = "Matched by file name, so an update that moves the program keeps working.",
                Muted = true,
                Dock = DockStyle.Fill,
            }
        );
        Row(appExes, 70);
        Row(Line(appExeNew, appExeAdd, appExeRemove, appExeRunning));
        Row(Heading("Sticks in this app"));
        Row(Line(Caption("Left stick"), appLeft, Caption("Right stick"), appRight));
        Row(Heading("Button overrides"));
        Row(
            new WrapLabel
            {
                Text =
                    "Only the buttons that differ from the base map. Hover a row's name to see the base map's action.",
                Muted = true,
                Dock = DockStyle.Fill,
            }
        );
        Row(appMap, 200);
        Row(appMapAdd);
        Row(Heading("Turn the controller off for some windows"));
        Row(
            new WrapLabel
            {
                Text =
                    "One pattern per line. While a window title matches one, the controller does nothing in this app (for example a cloud-gaming tab in Chrome). A pattern must match the whole title: * stands for any text and ? for one character. \"*Xbox Cloud Gaming - Google Chrome\" matches every title that ends that way; \"GeForce NOW\" alone would not match a page called \"GeForce NOW review\".",
                Muted = true,
                Dock = DockStyle.Fill,
            }
        );
        Row(appTitles, 70);
        Row(appTitleUse);
        appDetail.Controls.Add(detail);
        loaders.Add(ShowApp);
    }

    Label Heading(string text) =>
        new()
        {
            Text = text,
            AutoSize = true,
            Font = new Font(Font, FontStyle.Bold),
            Margin = new Padding(3, 10, 3, 3),
        };

    int SelectedApp =>
        appList.SelectedIndices.Count > 0 && appList.SelectedIndices[0] < edit.Apps.Count
            ? appList.SelectedIndices[0]
            : -1;

    void FitAppColumn() =>
        appList.Columns[0].Width = Math.Max(
            Px(100),
            appList.ClientSize.Width
                - appList.Columns[1].Width
                - SystemInformation.VerticalScrollBarWidth
                - 4
        );

    // Overrides and title rules only matter for an app that gets the mouse.
    void UpdateModeUi(AppRule app)
    {
        var always = app.Mode != AppMode.Never;
        appNeverNote.Visible = !always;
        foreach (
            var c in new Control[] { appLeft, appRight, appMap, appMapAdd, appTitles, appTitleUse }
        )
            c.Enabled = always;
    }

    void LoadApps()
    {
        var was = loading;
        loading = true;
        try
        {
            var keep = Math.Max(SelectedApp, 0);
            appList.BeginUpdate();
            appList.Items.Clear();
            foreach (var app in edit.Apps)
            {
                var item = new ListViewItem(app.Name.Length > 0 ? app.Name : "(no name)")
                {
                    Checked = app.Enabled,
                    ToolTipText =
                        app.Exes.Count > 0 ? string.Join(", ", app.Exes) : "No program yet",
                };
                item.SubItems.Add(app.Mode == AppMode.Never ? "Excluded" : "Always");
                appList.Items.Add(item);
            }
            appList.EndUpdate();
            FitAppColumn();
            if (edit.Apps.Count > 0)
                appList.Items[Math.Clamp(keep, 0, edit.Apps.Count - 1)].Selected = true;
        }
        finally
        {
            loading = was;
        }
        UpdateAppButtons();
    }

    void UpdateAppButtons()
    {
        var any = SelectedApp >= 0;
        appEdit.Enabled = any;
        appRemove.Enabled = any;
    }

    // The editor works on the app that was open when the loaders last ran; the dialog closing
    // sends the list back to the main window.
    void ShowApp()
    {
        var app = editingApp;
        appDetail.Enabled = app is not null;
        if (app is null)
            return;
        var was = loading;
        loading = true;
        try
        {
            appName.Text = app.Name;
            SelectId(appMode, app.Mode == AppMode.Never ? AppMode.Never : AppMode.Always);
            UpdateModeUi(app);
            appExes.Items.Clear();
            foreach (var exe in app.Exes)
                appExes.Items.Add(exe);
            SelectId(appLeft, app.LeftStick);
            SelectId(appRight, app.RightStick);
            appTitles.Text = string.Join(Environment.NewLine, app.TitleOff);
            ShowAppMap(app);
        }
        finally
        {
            loading = was;
        }
    }

    void ShowAppMap(AppRule app)
    {
        appMap.ShowMap(
            app.Buttons,
            Catalog.MapIds.Where(app.Buttons.ContainsKey).ToList(),
            baseMap: edit.Base.Buttons,
            remove: id =>
                BeginInvoke(() =>
                {
                    app.Buttons.Remove(id);
                    ShowAppMap(app);
                    Touch();
                })
        );
        var was = loading;
        loading = true;
        appMapAdd.Items.Clear();
        appMapAdd.Items.Add(new ComboItem("", "Add an override..."));
        foreach (var id in Catalog.MapIds)
            if (!app.Buttons.ContainsKey(id))
                appMapAdd.Items.Add(new ComboItem(id, Catalog.Labels.GetValueOrDefault(id, id)));
        appMapAdd.SelectedIndex = 0;
        loading = was;
    }

    /// <summary>The file name of what the user typed, with ".exe" added when it has no extension ("vlc" is "vlc.exe": the foreground check compares exact exe file names).</summary>
    internal static string CleanExe(string text)
    {
        var name = text.Trim().Split('\\', '/').Last().Trim();
        return name.Length > 0 && !name.Contains('.') ? name + ".exe" : name;
    }

    void AddExe(string text)
    {
        if (editingApp is not { } app)
            return;
        var exe = CleanExe(text);
        if (exe.Length == 0)
            return;
        if (app.Exes.Any(e => e.Equals(exe, StringComparison.OrdinalIgnoreCase)))
        {
            MessageBox.Show(
                this,
                $"{exe} is already in the list.",
                AppInfo.Name,
                MessageBoxButtons.OK,
                MessageBoxIcon.Information
            );
            return;
        }
        // One rule per exe: the first rule that lists it wins, so a second one would do nothing.
        if (
            edit.Apps.FirstOrDefault(a =>
                a != app && a.Exes.Contains(exe, StringComparer.OrdinalIgnoreCase)
            ) is
            { } other
        )
        {
            if (
                MessageBox.Show(
                    this,
                    $"{exe} is already in \"{other.Name}\". Move it to \"{app.Name}\"?",
                    AppInfo.Name,
                    MessageBoxButtons.YesNo,
                    MessageBoxIcon.Question
                ) != DialogResult.Yes
            )
                return;
            other.Exes.RemoveAll(e => e.Equals(exe, StringComparison.OrdinalIgnoreCase));
        }
        app.Exes.Add(exe);
        appExeNew.Clear();
        Touch();
        ShowApp();
    }

    void EditSelectedApp()
    {
        if (SelectedApp < 0)
            return;
        EditApp(SelectedApp);
    }

    void EditApp(int index)
    {
        editingIndex = index;
        ShowApp();
        OpenPage("app");
        editingIndex = -1;
        LoadApps();
    }

    void AddApp(string? exe, string mode)
    {
        if (exe is not null)
        {
            var known = edit.Apps.FirstOrDefault(a =>
                a.Exes.Any(e => e.Equals(exe, StringComparison.OrdinalIgnoreCase))
            );
            if (known is not null)
            {
                MessageBox.Show(
                    this,
                    $"{exe} is already in \"{known.Name}\".",
                    AppInfo.Name,
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information
                );
                return;
            }
        }
        var app = new AppRule
        {
            Name = exe is null
                ? "New app"
                : exe.Replace(".exe", "", StringComparison.OrdinalIgnoreCase),
            Mode = mode,
        };
        if (exe is not null)
            app.Exes.Add(exe);
        edit.Apps.Add(app);
        Touch();
        LoadApps();
        appList.SelectedIndices.Clear();
        appList.Items[^1].Selected = true;
        if (exe is null)
            EditApp(edit.Apps.Count - 1);
    }

    void RemoveApp()
    {
        if (SelectedApp < 0)
            return;
        var app = edit.Apps[SelectedApp];
        var sure = WhileModal(() =>
            MessageBox.Show(
                this,
                $"Remove the rule for \"{app.Name}\"? The app becomes automatic again: it gets the mouse unless it reads the controller itself or is a game.",
                AppInfo.Name,
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Question
            )
        );
        if (sure != DialogResult.Yes)
            return;
        edit.Apps.Remove(app);
        Touch();
        LoadApps();
    }

    void UseCurrentTitle()
    {
        if (editingApp is not { } app)
            return;
        foreach (var exe in app.Exes)
        {
            if (host.LastTitle(exe) is not { Length: > 0 } title)
                continue;
            appTitles.Text += (appTitles.TextLength > 0 ? Environment.NewLine : "") + title;
            MessageBox.Show(
                this,
                $"Added the last title seen for {exe}. Edit it so it matches what you want, with * for the parts that change.",
                AppInfo.Name,
                MessageBoxButtons.OK,
                MessageBoxIcon.Information
            );
            return;
        }
        MessageBox.Show(
            this,
            "No window title has been seen yet for this app. Open it, then try again.",
            AppInfo.Name,
            MessageBoxButtons.OK,
            MessageBoxIcon.Information
        );
    }

    /// <summary>The exe the user picks from the open windows, and the mode if askMode (two buttons: Always, Exclude); null when cancelled.</summary>
    (string Exe, string Mode)? PickRunningApp(bool askMode)
    {
        using var dialog = new RunningAppDialog(
            host.RunningApps(),
            edit.Apps.SelectMany(a => a.Exes).ToHashSet(StringComparer.OrdinalIgnoreCase),
            askMode
        );
        return
            WhileModal(() => dialog.ShowDialog(this)) == DialogResult.OK
            && dialog.Selected is { } exe
            ? (exe, dialog.Mode)
            : null;
    }
}

/// <summary>A list of the open windows' programs to pick one from.</summary>
sealed class RunningAppDialog : Form
{
    readonly ListView list = new()
    {
        View = View.Details,
        FullRowSelect = true,
        MultiSelect = false,
        HideSelection = false,
        Dock = DockStyle.Fill,
        ShowItemToolTips = true,
    };
    readonly Button ok = new()
    {
        Text = "Add",
        AutoSize = true,
        MinimumSize = new Size(90, 28),
        DialogResult = DialogResult.OK,
        Enabled = false,
    };
    readonly Button cancel = new()
    {
        Text = "Cancel",
        AutoSize = true,
        MinimumSize = new Size(90, 28),
        DialogResult = DialogResult.Cancel,
    };

    readonly Button never = new()
    {
        Text = "Exclude",
        AutoSize = true,
        MinimumSize = new Size(90, 28),
        Enabled = false,
    };

    /// <summary>The picked program's file name.</summary>
    internal string? Selected { get; private set; }

    /// <summary>AppMode.Always, or AppMode.Never when the Exclude button was used.</summary>
    internal string Mode { get; private set; } = AppMode.Always;

    public RunningAppDialog(IReadOnlyList<RunningApp> apps, ISet<string> known, bool askMode)
    {
        Text = "Add a running app";
        Font = new Font("Segoe UI", 9F);
        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(520, 360);
        MinimumSize = new Size(360, 240);
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        CancelButton = cancel;
        AcceptButton = ok;
        list.Columns.Add("Program", 160);
        list.Columns.Add("Window", 330);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var app in apps.OrderBy(a => a.Exe, StringComparer.OrdinalIgnoreCase))
        {
            if (!seen.Add(app.Exe))
                continue;
            var item = new ListViewItem(app.Exe) { Tag = app.Exe, ToolTipText = app.Title };
            item.SubItems.Add(app.Title);
            if (known.Contains(app.Exe))
            {
                item.Text += " (already has a rule)";
                item.ForeColor = SystemColors.GrayText;
            }
            list.Items.Add(item);
        }
        list.SelectedIndexChanged += (_, _) =>
        {
            Selected = list.SelectedItems.Count > 0 ? list.SelectedItems[0].Tag as string : null;
            ok.Enabled = never.Enabled = Selected is not null;
        };
        never.Click += (_, _) =>
        {
            Mode = AppMode.Never;
            DialogResult = DialogResult.OK;
        };
        if (askMode)
            ok.Text = "Always";
        list.DoubleClick += (_, _) =>
        {
            if (ok.Enabled)
                ok.PerformClick();
        };
        var table = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 3,
            Padding = new Padding(8),
        };
        table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        table.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        table.Controls.Add(
            new Label
            {
                Text =
                    list.Items.Count == 0 ? "No open windows were found."
                    : askMode
                        ? "Pick the program, then choose Always (the mouse, even if it reads the controller) or Exclude (never the mouse):"
                    : "Pick the program:",
                AutoSize = true,
            },
            0,
            0
        );
        table.Controls.Add(list, 0, 1);
        var buttons = new FlowLayoutPanel
        {
            AutoSize = true,
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.RightToLeft,
        };
        buttons.Controls.Add(cancel);
        if (askMode)
            buttons.Controls.Add(never);
        buttons.Controls.Add(ok);
        table.Controls.Add(buttons, 0, 2);
        Controls.Add(table);
    }
}
