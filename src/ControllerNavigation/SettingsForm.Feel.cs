namespace ControllerNavigation;

// The More settings dialog: Feel, updates and checks.
sealed partial class SettingsForm
{
    sealed record FeelField(
        string Name,
        string Help,
        string Unit,
        decimal Min,
        decimal Max,
        decimal Step,
        int Decimals,
        Func<Feel, decimal> Get,
        Action<Feel, decimal> Set
    );

    static readonly (string Group, FeelField[] Fields)[] FeelGroups =
    {
        (
            "Pointer",
            new[]
            {
                new FeelField(
                    "Pointer speed",
                    "How fast the pointer moves at full tilt, as a factor of 0.7 screen heights a second.",
                    "x",
                    0.05m,
                    10,
                    0.05m,
                    2,
                    f => (decimal)f.PointerSpeed,
                    (f, v) => f.PointerSpeed = (double)v
                ),
                new FeelField(
                    "Acceleration",
                    "Held at full tilt a moment, the pointer speeds up to this factor, for long moves across the screen. 1 is off.",
                    "x",
                    1,
                    5,
                    0.1m,
                    1,
                    f => (decimal)f.Acceleration,
                    (f, v) => f.Acceleration = (double)v
                ),
                new FeelField(
                    "Precise pointer speed",
                    "The pointer's speed while precise is on (L3) or a precise button is held, as a factor of the normal speed.",
                    "x",
                    0.01m,
                    1,
                    0.01m,
                    2,
                    f => (decimal)f.PreciseSpeed,
                    (f, v) => f.PreciseSpeed = (double)v
                ),
                new FeelField(
                    "Stick curve",
                    "1 is linear, 2 is squared: small tilts are slow and exact.",
                    "",
                    1,
                    4,
                    0.1m,
                    1,
                    f => (decimal)f.Curve,
                    (f, v) => f.Curve = (double)v
                ),
                new FeelField(
                    "Left stick dead zone",
                    "Stick travel ignored around the centre, of 32767 at full tilt.",
                    "",
                    0,
                    30000,
                    100,
                    0,
                    f => f.LeftDeadzone,
                    (f, v) => f.LeftDeadzone = (int)v
                ),
                new FeelField(
                    "Right stick dead zone",
                    "Stick travel ignored around the centre, of 32767 at full tilt.",
                    "",
                    0,
                    30000,
                    100,
                    0,
                    f => f.RightDeadzone,
                    (f, v) => f.RightDeadzone = (int)v
                ),
            }
        ),
        (
            "Scrolling",
            new[]
            {
                new FeelField(
                    "Scroll speed",
                    "As a factor of 20 notches a second at full tilt.",
                    "x",
                    0.05m,
                    10,
                    0.05m,
                    2,
                    f => (decimal)f.ScrollSpeed,
                    (f, v) => f.ScrollSpeed = (double)v
                ),
            }
        ),
        (
            "Triggers and arrows",
            new[]
            {
                new FeelField(
                    "Trigger threshold",
                    "How far a trigger goes down before it counts as pressed.",
                    "%",
                    5,
                    95,
                    1,
                    0,
                    f => f.TriggerPercent,
                    (f, v) => f.TriggerPercent = (int)v
                ),
                new FeelField(
                    "Stick as arrow keys",
                    "How far a stick used as arrow or WASD keys tilts before it counts.",
                    "%",
                    10,
                    95,
                    1,
                    0,
                    f => f.StickArrowPercent,
                    (f, v) => f.StickArrowPercent = (int)v
                ),
            }
        ),
        (
            "Timing",
            new[]
            {
                new FeelField(
                    "Repeat delay",
                    "How long a held button waits before it starts repeating.",
                    "ms",
                    50,
                    5000,
                    10,
                    0,
                    f => f.RepeatDelayMs,
                    (f, v) => f.RepeatDelayMs = (int)v
                ),
                new FeelField(
                    "Repeat rate",
                    "The time between repeats.",
                    "ms",
                    15,
                    2000,
                    5,
                    0,
                    f => f.RepeatEveryMs,
                    (f, v) => f.RepeatEveryMs = (int)v
                ),
                new FeelField(
                    "Long-press time",
                    "How long a button is held before its long-press action runs.",
                    "ms",
                    100,
                    5000,
                    50,
                    0,
                    f => f.LongPressMs,
                    (f, v) => f.LongPressMs = (int)v
                ),
                new FeelField(
                    "Two-button window",
                    "How long a click on one trigger waits for the other, so both together make a middle click.",
                    "ms",
                    20,
                    300,
                    5,
                    0,
                    f => f.PairWindowMs,
                    (f, v) => f.PairWindowMs = (int)v
                ),
                new FeelField(
                    "Switch-on delay",
                    "A window must stay in front this long before the controller turns on, so a game that is starting is never clicked.",
                    "ms",
                    0,
                    5000,
                    50,
                    0,
                    f => f.OnDelayMs,
                    (f, v) => f.OnDelayMs = (int)v
                ),
            }
        ),
        (
            "Volume",
            new[]
            {
                new FeelField(
                    "Volume step",
                    "How much each volume press or repeat changes the volume. Rounded to 2 % steps while Windows' volume slider is shown.",
                    "%",
                    1,
                    25,
                    1,
                    0,
                    f => f.VolumeStep,
                    (f, v) => f.VolumeStep = (int)v
                ),
                new FeelField(
                    "Volume repeat rate",
                    "The time between steps while a volume button is held (after 400 ms).",
                    "ms",
                    30,
                    2000,
                    5,
                    0,
                    f => f.VolumeRepeatMs,
                    (f, v) => f.VolumeRepeatMs = (int)v
                ),
            }
        ),
    };

    static readonly Dictionary<string, string> UpdateText = new()
    {
        ["off"] = "Automatic updates are off.",
        ["notInstalled"] =
            "This copy does not run from Program Files, so there is nothing to update here.",
        ["checking"] = "Looking for a newer version...",
        ["upToDate"] = "You have the latest version.",
        ["available"] = "A newer version is available.",
        ["waitingIdle"] =
            "A new version is ready. It installs once the controller has been idle for a few minutes.",
        ["installing"] = "Installing the new version. The tool restarts by itself.",
        ["failed"] = "The last update did not work.",
    };

    readonly List<(FeelField Field, NumericUpDown Box)> feelBoxes = new();
    readonly CheckBox showVolumeSlider = new()
    {
        Text = "Show Windows' volume slider (uses the volume keys, steps of 2 %)",
        AutoSize = true,
        Margin = new Padding(3, 6, 3, 6),
    };
    readonly CheckBox autoUpdate = new() { Text = "Update automatically", AutoSize = true };
    readonly LinkLabel checkNow = new()
    {
        Text = "Check now",
        AutoSize = true,
        Margin = new Padding(12, 6, 3, 3),
    };
    readonly Label updateLine = new()
    {
        AutoEllipsis = true,
        Dock = DockStyle.Fill,
        ForeColor = SystemColors.GrayText,
    };
    readonly WrapLabel updateState = new() { Dock = DockStyle.Fill };
    readonly Label updateVersion = new() { AutoSize = true };
    readonly Label updateLatest = new() { AutoSize = true };
    readonly Label updateLast = new() { AutoSize = true };
    readonly WrapLabel updateMessage = new() { Dock = DockStyle.Fill, Muted = true };
    readonly ChecksPanel checksPanel = new() { Dock = DockStyle.Fill };
    Control morePage = null!;

    // ---- More: Feel, update details and checks, one under the other

    Control BuildMorePage()
    {
        var checks = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            Height = 330,
            ColumnCount = 1,
            RowCount = 2,
        };
        checks.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        checks.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        var again = new Button
        {
            Text = "Check again",
            AutoSize = true,
            Margin = new Padding(3, 3, 3, 6),
        };
        again.Click += (_, _) => LoadChecks();
        checks.Controls.Add(again, 0, 0);
        checks.Controls.Add(checksPanel, 0, 1);
        // The sections sit straight in the scrolling panel, docked to its top; the last one added
        // is the top one. (Wrapping text inside nested auto-sized boxes was measured too early.)
        var scroller = new Panel { AutoScroll = true };
        scroller.Controls.Add(checks);
        scroller.Controls.Add(
            SectionHeading("Checks: things on this PC that would fight the tool")
        );
        scroller.Controls.Add(BuildUpdateDetails());
        scroller.Controls.Add(SectionHeading("Updates"));
        scroller.Controls.Add(BuildFeelSection());
        scroller.Controls.Add(SectionHeading("Feel"));
        scroller.Controls.Add(BuildDetectionSection());
        scroller.Controls.Add(SectionHeading("Which apps get the mouse"));
        // Added bottom-up, so the tab order (and the first focus) runs top-down.
        for (var i = 0; i < scroller.Controls.Count; i++)
            scroller.Controls[i].TabIndex = scroller.Controls.Count - 1 - i;
        morePage = scroller;
        return scroller;
    }

    Label SectionHeading(string text) =>
        new()
        {
            Text = text,
            AutoSize = true,
            Dock = DockStyle.Top,
            Font = new Font(Font, FontStyle.Bold),
            Padding = new Padding(0, 10, 0, 4),
        };

    void ResetMore()
    {
        edit.Feel = new Feel();
        edit.Detection = new Detection();
    }

    // ---- Which apps get the mouse (automatic detection)

    readonly CheckBox detectControllerApps = new()
    {
        Text = "Leave apps that read the controller alone (games, Moonlight, emulators, Steam)",
        AutoSize = true,
    };
    readonly CheckBox detectKnownGames = new()
    {
        Text = "Leave games Windows recognizes alone",
        AutoSize = true,
    };
    readonly ListBox gameFolders = new() { Dock = DockStyle.Fill, IntegralHeight = false };
    readonly TextBox gameFolderNew = new() { Width = 220, PlaceholderText = @"\XboxGames\" };
    readonly Button gameFolderAdd = new() { Text = "Add", AutoSize = true };
    readonly Button gameFolderRemove = new() { Text = "Remove", AutoSize = true };

    Control BuildDetectionSection()
    {
        detectControllerApps.CheckedChanged += (_, _) =>
        {
            if (loading)
                return;
            edit.Detection.ControllerApps = detectControllerApps.Checked;
            Touch();
        };
        detectKnownGames.CheckedChanged += (_, _) =>
        {
            if (loading)
                return;
            edit.Detection.KnownGames = detectKnownGames.Checked;
            Touch();
        };
        gameFolderAdd.Click += (_, _) => AddGameFolder();
        gameFolderNew.PreviewKeyDown += (_, e) => e.IsInputKey |= e.KeyCode == Keys.Enter;
        gameFolderNew.KeyDown += (_, e) =>
        {
            if (e.KeyCode != Keys.Enter)
                return;
            e.SuppressKeyPress = true;
            AddGameFolder();
        };
        gameFolderRemove.Click += (_, _) =>
        {
            if (
                gameFolders.SelectedIndex < 0
                || gameFolders.SelectedIndex >= edit.Detection.GameFolders.Count
            )
                return;
            edit.Detection.GameFolders.RemoveAt(gameFolders.SelectedIndex);
            Touch();
            LoadDetection();
        };
        var table = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 1,
        };
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        void Row(Control c, float height = 0)
        {
            table.RowStyles.Add(
                new RowStyle(height > 0 ? SizeType.Absolute : SizeType.AutoSize, height)
            );
            table.Controls.Add(c, 0, table.RowStyles.Count - 1);
        }
        Row(
            new WrapLabel
            {
                Text =
                    "Every app gets the mouse unless one of these says it reads the controller itself or is a game. An app listed under App exceptions (Always or Excluded) skips this.",
                Muted = true,
                Dock = DockStyle.Fill,
            }
        );
        Row(detectControllerApps);
        Row(detectKnownGames);
        Row(
            new WrapLabel
            {
                Text =
                    "Game folders: any program whose path contains one of these is treated as a game.",
                Dock = DockStyle.Fill,
            }
        );
        Row(gameFolders, 80);
        var line = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill };
        line.Controls.AddRange(new Control[] { gameFolderNew, gameFolderAdd, gameFolderRemove });
        Row(line);
        loaders.Add(() =>
        {
            detectControllerApps.Checked = edit.Detection.ControllerApps;
            detectKnownGames.Checked = edit.Detection.KnownGames;
            LoadDetection();
        });
        return table;
    }

    void LoadDetection()
    {
        var was = loading;
        loading = true;
        gameFolders.Items.Clear();
        foreach (var folder in edit.Detection.GameFolders)
            gameFolders.Items.Add(folder);
        loading = was;
    }

    void AddGameFolder()
    {
        var folder = gameFolderNew.Text.Trim();
        if (folder.Length == 0)
            return;
        if (!edit.Detection.GameFolders.Contains(folder, StringComparer.OrdinalIgnoreCase))
        {
            edit.Detection.GameFolders.Add(folder);
            Touch();
            LoadDetection();
        }
        gameFolderNew.Clear();
    }

    // ---- Feel

    Control BuildFeelSection()
    {
        var defaults = new Feel();
        var table = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 3,
        };
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 160));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 130));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        foreach (var (group, fields) in FeelGroups)
        {
            var heading = Heading(group);
            table.Controls.Add(heading);
            table.SetColumnSpan(heading, 3);
            foreach (var field in fields)
            {
                var box = new QuietNumericUpDown
                {
                    Minimum = field.Min,
                    Maximum = field.Max,
                    Increment = field.Step,
                    DecimalPlaces = field.Decimals,
                    Width = 80,
                    Anchor = AnchorStyles.Left,
                };
                var captured = field;
                box.ValueChanged += (_, _) =>
                {
                    if (loading)
                        return;
                    captured.Set(edit.Feel, box.Value);
                    Touch();
                };
                feelBoxes.Add((field, box));
                var unit = field.Unit.Length > 0 ? " " + field.Unit : "";
                table.Controls.Add(
                    new Label
                    {
                        Text = field.Name,
                        AutoSize = true,
                        Anchor = AnchorStyles.Left,
                        Margin = new Padding(3, 6, 3, 6),
                    }
                );
                var cell = new FlowLayoutPanel { AutoSize = true, Margin = new Padding(0) };
                cell.Controls.Add(box);
                cell.Controls.Add(
                    new Label
                    {
                        Text = field.Unit,
                        AutoSize = true,
                        Margin = new Padding(0, 6, 0, 0),
                    }
                );
                table.Controls.Add(cell);
                table.Controls.Add(
                    new WrapLabel
                    {
                        Text =
                            $"{field.Help} Default: {field.Get(defaults).ToString("0.##", System.Globalization.CultureInfo.InvariantCulture)}{unit}.",
                        Muted = true,
                        Dock = DockStyle.Fill,
                    }
                );
            }
            if (group == "Volume")
            {
                table.Controls.Add(showVolumeSlider);
                table.SetColumnSpan(showVolumeSlider, 3);
            }
        }
        showVolumeSlider.CheckedChanged += (_, _) =>
        {
            if (loading)
                return;
            edit.Feel.ShowVolumeSlider = showVolumeSlider.Checked;
            Touch();
        };
        loaders.Add(() =>
        {
            foreach (var (field, box) in feelBoxes)
                box.Value = Math.Clamp(field.Get(edit.Feel), field.Min, field.Max);
            showVolumeSlider.Checked = edit.Feel.ShowVolumeSlider;
        });
        return table;
    }

    // ---- Updates: the automatic-update box and "Check now" are on the main window

    void HookUpdateControls()
    {
        autoUpdate.CheckedChanged += (_, _) =>
        {
            if (loading)
                return;
            edit.AutoUpdate = autoUpdate.Checked;
            Touch();
        };
        checkNow.LinkClicked += async (_, _) =>
        {
            checkNow.Enabled = false;
            try
            {
                await host.CheckForUpdate();
            }
            catch (Exception e)
            {
                Log.Warn("Settings window: update check failed: " + e.Message);
            }
            if (!IsDisposed)
                LoadUpdate();
        };
        loaders.Add(() =>
        {
            autoUpdate.Checked = edit.AutoUpdate;
            LoadUpdate();
        });
    }

    Control BuildUpdateDetails()
    {
        var info = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            ColumnCount = 2,
        };
        info.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        info.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        void Pair(string name, Control value)
        {
            info.Controls.Add(
                new Label
                {
                    Text = name,
                    AutoSize = true,
                    ForeColor = SystemColors.GrayText,
                    Margin = new Padding(3, 3, 16, 3),
                }
            );
            info.Controls.Add(value);
        }
        Pair("Version in use", updateVersion);
        Pair("Newest version", updateLatest);
        Pair("Last check", updateLast);
        var table = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 1,
        };
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        table.Controls.Add(
            new WrapLabel
            {
                Text =
                    "New versions come from the project's GitHub releases. The tool looks about once a day and installs a new one when the controller has been idle for a few minutes.",
                Muted = true,
                Dock = DockStyle.Fill,
            }
        );
        table.Controls.Add(updateState);
        table.Controls.Add(info);
        table.Controls.Add(updateMessage);
        return table;
    }

    void LoadUpdate()
    {
        var u = host.Update;
        updateState.Text = UpdateText.GetValueOrDefault(u.State, u.State);
        updateLine.Text =
            updateState.Text + (u.Latest is { } l && u.State == "available" ? $" ({l})" : "");
        updateVersion.Text = u.Current;
        updateLatest.Text = u.Latest ?? "Not known yet";
        updateLast.Text = u.LastCheckUtc is { } t ? t.ToLocalTime().ToString("g") : "Never";
        updateMessage.Text = u.Message ?? "";
        updateMessage.Visible = !string.IsNullOrEmpty(u.Message);
        checkNow.Enabled = u.State is not ("checking" or "installing");
        checkNow.Text = u.State == "checking" ? "Checking..." : "Check now";
    }

    // ---- Checks

    IReadOnlyList<CheckResult> checkResults = Array.Empty<CheckResult>();

    void LoadChecks()
    {
        try
        {
            checkResults = host.Checks();
        }
        catch (Exception e)
        {
            Log.Warn("Settings window: checks failed: " + e.Message);
            return;
        }
        checksPanel.Show(checkResults);
        ShowWarning();
    }
}

/// <summary>
/// The check results: a status word, the name, what is wrong and what to do, one block each. Laid
/// out by hand because the text wraps to the width and nested tables measured it too early.
/// </summary>
sealed class ChecksPanel : Panel
{
    readonly List<(Label Status, Label Name, WrapLabel Detail, WrapLabel? Fix)> items = new();

    public ChecksPanel()
    {
        AutoScroll = true;
    }

    int Px(int n) => (int)Math.Round(n * (FindForm()?.DeviceDpi ?? DeviceDpi) / 96.0);

    public void Show(IReadOnlyList<CheckResult> results)
    {
        SuspendLayout();
        foreach (var (status, name, detail, fix) in items)
        {
            status.Dispose();
            name.Dispose();
            detail.Dispose();
            fix?.Dispose();
        }
        items.Clear();
        Controls.Clear();
        var bold = new Font(Font, FontStyle.Bold);
        foreach (var check in results)
        {
            var (text, color) =
                !check.Ok ? ("Needs attention", Color.FromArgb(205, 110, 20))
                : check.Fix is not null ? ("Reminder", SystemColors.HotTrack)
                : ("OK", Color.FromArgb(40, 150, 70));
            var status = new Label
            {
                Text = text,
                ForeColor = color,
                Font = bold,
                AutoSize = false,
            };
            var name = new Label
            {
                Text = check.Name,
                Font = bold,
                AutoSize = false,
                AutoEllipsis = true,
            };
            var detail = new WrapLabel { Text = check.Detail, AutoSize = false };
            WrapLabel? fix = check.Fix is null
                ? null
                : new WrapLabel
                {
                    Text = (check.Ok ? "What to do: " : "How to fix: ") + check.Fix,
                    Muted = true,
                    AutoSize = false,
                };
            Controls.AddRange(
                new Control?[] { status, name, detail, fix }
                    .OfType<Control>()
                    .ToArray()
            );
            items.Add((status, name, detail, fix));
        }
        LayoutItems();
        ResumeLayout(true);
    }

    protected override void OnResize(EventArgs eventargs)
    {
        base.OnResize(eventargs);
        if (items.Count > 0)
            LayoutItems();
    }

    void LayoutItems()
    {
        var scroll = -AutoScrollPosition.Y;
        AutoScrollPosition = Point.Empty;
        int pad = Px(8),
            statusW = Px(120),
            lineH = Px(22);
        var x = pad + statusW;
        var width = Math.Max(Px(200), ClientSize.Width - x - pad);
        var y = pad;
        foreach (var (status, name, detail, fix) in items)
        {
            status.SetBounds(pad, y, statusW, lineH);
            name.SetBounds(x, y, width, lineH);
            y += lineH;
            y = Place(detail, x, y, width);
            if (fix is not null)
                y = Place(fix, x, y, width);
            y += pad * 2;
        }
        AutoScrollMinSize = new Size(0, y);
        AutoScrollPosition = new Point(0, scroll);
    }

    static int Place(WrapLabel label, int x, int y, int width)
    {
        var height = label.GetPreferredSize(new Size(width, 0)).Height;
        label.SetBounds(x, y, width, height);
        return y + height;
    }
}
