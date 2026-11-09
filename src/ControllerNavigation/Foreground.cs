using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace ControllerNavigation;

/// <summary>The window in front as last classified: its process (exe name, full path, start time), app rule, rights.</summary>
sealed record FrontWindow(
    IntPtr Window,
    uint ProcessId,
    string? Exe,
    string? Path,
    long Started,
    AppRule? Rule,
    bool Ours,
    bool Elevated
)
{
    public static readonly FrontWindow None = new(
        IntPtr.Zero,
        0,
        null,
        null,
        0,
        null,
        false,
        false
    );
}

/// <summary>
/// Which window is in front and whether the controller may drive it (SPEC "Which apps get the
/// mouse", "Foreground tracking"): this tool's own windows and "always" apps yes, "never" apps no,
/// every other app unless the GameDetector says it is a game or reads the controller itself.
/// Read on the input thread at every poll: no WinEvent hook, since none fires when the foreground
/// goes NULL or when a UWP app's window attaches to a frame host that is already in front. A window
/// is classified once, when the handle changes; the detector's verdict and a title-rule app's title
/// are read again 4 times a second. Off at once, on only after OnDelayMs of allowed windows: during
/// a game launch or a fullscreen switch the foreground flickers through the desktop, and a click
/// then would pull focus out of the game.
/// </summary>
sealed class ForegroundTracker
{
    [DllImport("user32.dll")]
    static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern int GetWindowText(IntPtr window, StringBuilder text, int max);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern int GetClassName(IntPtr window, StringBuilder name, int max);

    [DllImport("user32.dll")]
    static extern bool EnumChildWindows(IntPtr parent, EnumProc proc, IntPtr data);

    [DllImport("user32.dll")]
    static extern bool EnumWindows(EnumProc proc, IntPtr data);

    [DllImport("user32.dll")]
    static extern bool IsWindowVisible(IntPtr window);

    [DllImport("user32.dll")]
    static extern IntPtr GetWindow(IntPtr window, uint command);

    [DllImport("user32.dll")]
    static extern int GetWindowLong(IntPtr window, int index);

    [DllImport("dwmapi.dll")]
    static extern int DwmGetWindowAttribute(IntPtr window, int attribute, out int value, int size);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern IntPtr OpenProcess(uint access, bool inherit, uint processId);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    static extern bool QueryFullProcessImageName(
        IntPtr process,
        int flags,
        StringBuilder name,
        ref int size
    );

    [DllImport("kernel32.dll")]
    static extern bool CloseHandle(IntPtr handle);

    [DllImport("advapi32.dll", SetLastError = true)]
    static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);

    [DllImport("advapi32.dll", SetLastError = true)]
    static extern bool GetTokenInformation(
        IntPtr token,
        int infoClass,
        out int info,
        int length,
        out int returned
    );

    delegate bool EnumProc(IntPtr window, IntPtr data);

    const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
    const int TitleEveryMs = 250;

    /// <summary>The shell's own windows: never "the app that was in front" (clicking the tray puts the taskbar in front).</summary>
    static readonly HashSet<string> ShellExes = new(StringComparer.OrdinalIgnoreCase)
    {
        "explorer.exe",
        "SearchHost.exe",
        "StartMenuExperienceHost.exe",
        "ShellExperienceHost.exe",
        "TextInputHost.exe",
        "LockApp.exe",
    };

    static readonly uint OwnId = (uint)Environment.ProcessId;

    /// <summary>This process runs elevated itself (User Account Control off): nothing to route through the helper.</summary>
    static readonly bool SelfElevated = IsElevated(OwnId);

    readonly Func<Settings> settings;
    readonly GameDetector detector;
    readonly ConcurrentDictionary<string, string> titles = new(StringComparer.OrdinalIgnoreCase);
    Settings? classifiedWith;
    IntPtr window = (IntPtr)(-1);
    FrontWindow front = FrontWindow.None;
    bool uwpPending,
        titleOff;
    long nextTitle,
        nextVerdict,
        allowedSince = -1;
    Verdict? verdict;
    string? logged;

    public ForegroundTracker(Func<Settings> settings, GameDetector detector)
    {
        this.settings = settings;
        this.detector = detector;
    }

    /// <summary>Why the app in front gets the mouse or not ("always (app exception)", "reads the controller"...), for the tray.</summary>
    public string Reason => reason;
    volatile string reason = "";

    /// <summary>The session is locked or asleep (TrayApp, from SystemEvents): everything off.</summary>
    public volatile bool Suspended;

    /// <summary>The window in front as last classified (input thread writes, anyone reads).</summary>
    public FrontWindow Current => front;

    /// <summary>Whether the last poll let the controller drive the app in front (the tray's icon).</summary>
    public bool LastAllowed => lastAllowed;
    volatile bool lastAllowed;

    /// <summary>The last app in front that was neither the shell nor this tool (the tray's "add the app that was in front").</summary>
    public RunningApp? LastOther => lastOther;
    volatile RunningApp? lastOther;

    /// <summary>The last title seen in front for this exe, or null.</summary>
    public string? LastTitle(string exe) => titles.TryGetValue(exe, out var t) ? t : null;

    /// <summary>One poll (input thread): what is in front, and whether the controller may drive it.</summary>
    public Front Poll(long now)
    {
        var s = settings();
        var hwnd = GetForegroundWindow();
        if (hwnd != window || s != classifiedWith || (uwpPending && now >= nextTitle))
        {
            window = hwnd;
            classifiedWith = s;
            front = Classify(hwnd, s);
            Input.ToElevated = front.Elevated && !SelfElevated;
            nextTitle = nextVerdict = 0;
            verdict = null;
        }
        if (front.Rule is { Mode: AppMode.Always, TitleOff.Count: > 0 } rule && now >= nextTitle)
        {
            nextTitle = now + TitleEveryMs;
            var title = TitleOf(hwnd);
            if (front.Exe is { } exe && title.Length > 0)
                titles[exe] = title;
            var off = rule.TitleOff.Any(p => TitleRules.Matches(title, p));
            if (off != titleOff)
                Log.Info(
                    off ? $"Title rule: off for \"{title}\"" : $"Title rule: on again (\"{title}\")"
                );
            titleOff = off;
        }
        else if (front.Rule is not { Mode: AppMode.Always, TitleOff.Count: > 0 })
            titleOff = false;
        if (uwpPending && nextTitle == 0)
            nextTitle = now + TitleEveryMs;

        var automatic = !front.Ours && front.Rule is null && front.Path is not null;
        if (automatic && now >= nextVerdict)
        {
            verdict = detector.Check(front.ProcessId, front.Started, front.Path);
            nextVerdict = now + TitleEveryMs;
        }

        var why =
            hwnd == IntPtr.Zero ? "nothing in front"
            : front.Ours ? "this tool"
            : front.Rule is { Mode: AppMode.Never } ? "excluded (app exception)"
            : front.Rule is not null ? (titleOff ? "title rule" : "always (app exception)")
            : !automatic ? "could not tell which program"
            : verdict?.Reason ?? "looking into it";
        var allowed =
            !Suspended
            && hwnd != IntPtr.Zero
            && (
                front.Ours
                || front.Rule is { Mode: AppMode.Always } && !titleOff
                || automatic && verdict is { LeftAlone: false }
            );
        if (!allowed)
            allowedSince = -1;
        else if (allowedSince < 0)
            allowedSince = now;
        var on = allowed && now - allowedSince >= s.Feel.OnDelayMs;
        lastAllowed = on;
        reason = why;

        var state =
            $"{front.Exe ?? "(nothing)"}{(front.Elevated ? " (elevated)" : "")}: {(allowed ? "mouse" : "left alone")} ({why})";
        if (state != logged && why != "looking into it")
        {
            logged = state;
            Log.Info("In front: " + state);
        }
        return new Front(on, front.Rule is { Mode: AppMode.Always } ? front.Rule : null);
    }

    FrontWindow Classify(IntPtr hwnd, Settings s)
    {
        uwpPending = false;
        if (hwnd == IntPtr.Zero)
            return FrontWindow.None;
        GetWindowThreadProcessId(hwnd, out var pid);
        if (pid == OwnId)
            return new FrontWindow(
                hwnd,
                pid,
                AppInfo.ExeName,
                Environment.ProcessPath,
                0,
                null,
                true,
                false
            );
        var (path, started) = ProcessOf(pid);
        if (
            path is not null
            && System
                .IO.Path.GetFileName(path)
                .Equals("ApplicationFrameHost.exe", StringComparison.OrdinalIgnoreCase)
        )
        {
            // A UWP app: its own process owns the core window inside the frame (none yet while it starts).
            var app = CoreWindowProcess(hwnd, pid);
            if (app == 0)
            {
                // Not known yet which app it is (it could be a game): left alone until the core window comes.
                uwpPending = true;
                path = null;
            }
            else
            {
                pid = app;
                (path, started) = ProcessOf(pid);
            }
        }
        var exe = path is null ? null : System.IO.Path.GetFileName(path);
        var rule = exe is null
            ? null
            : s.Apps.FirstOrDefault(a =>
                a.Enabled && a.Exes.Contains(exe, StringComparer.OrdinalIgnoreCase)
            );
        var result = new FrontWindow(hwnd, pid, exe, path, started, rule, false, IsElevated(pid));
        if (exe is not null && !ShellExes.Contains(exe))
            lastOther = new RunningApp(exe, TitleOf(hwnd));
        return result;
    }

    static uint CoreWindowProcess(IntPtr frame, uint framePid)
    {
        uint found = 0;
        var name = new StringBuilder(64);
        EnumChildWindows(
            frame,
            (child, _) =>
            {
                name.Clear();
                GetClassName(child, name, name.Capacity);
                if (name.ToString() != "Windows.UI.Core.CoreWindow")
                    return true;
                GetWindowThreadProcessId(child, out var pid);
                if (pid == framePid)
                    return true;
                found = pid;
                return false;
            },
            IntPtr.Zero
        );
        return found;
    }

    /// <summary>The exe file name of a process ("chrome.exe"), or null when it cannot be read (gone, protected).</summary>
    static string? ExeOf(uint pid) =>
        ProcessOf(pid).Path is { } path ? Path.GetFileName(path) : null;

    /// <summary>A process's exe full path (null when it cannot be read) and start time (a reused id is another process).</summary>
    static (string? Path, long Started) ProcessOf(uint pid)
    {
        var h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (h == IntPtr.Zero)
            return (null, 0);
        try
        {
            var name = new StringBuilder(1024);
            var size = name.Capacity;
            var path = QueryFullProcessImageName(h, 0, name, ref size) ? name.ToString() : null;
            return (path, GetProcessTimes(h, out var created, out _, out _, out _) ? created : 0);
        }
        finally
        {
            CloseHandle(h);
        }
    }

    [DllImport("kernel32.dll")]
    static extern bool GetProcessTimes(
        IntPtr process,
        out long creation,
        out long exit,
        out long kernel,
        out long user
    );

    /// <summary>
    /// Whether a process runs with administrator rights (HTPC's Native.IsElevatedProcess): a
    /// standard process cannot open an elevated one's token at all (access denied), which counts as
    /// elevated too. Unknown or gone: false.
    /// </summary>
    static bool IsElevated(uint pid)
    {
        const uint TOKEN_QUERY = 0x8;
        const int TokenElevation = 20,
            ERROR_ACCESS_DENIED = 5;
        var process = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (process == IntPtr.Zero)
            return Marshal.GetLastWin32Error() == ERROR_ACCESS_DENIED;
        try
        {
            if (!OpenProcessToken(process, TOKEN_QUERY, out var token))
                return Marshal.GetLastWin32Error() == ERROR_ACCESS_DENIED;
            try
            {
                return GetTokenInformation(
                        token,
                        TokenElevation,
                        out var elevated,
                        sizeof(int),
                        out _
                    )
                    && elevated != 0;
            }
            finally
            {
                CloseHandle(token);
            }
        }
        finally
        {
            CloseHandle(process);
        }
    }

    static string TitleOf(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero)
            return "";
        var text = new StringBuilder(512);
        GetWindowText(hwnd, text, text.Capacity);
        return text.ToString();
    }

    /// <summary>
    /// The apps with a window on screen (visible, not cloaked on another virtual desktop, not a tool
    /// window, with a title), one entry per exe, this tool left out. Any thread.
    /// </summary>
    public static IReadOnlyList<RunningApp> RunningApps()
    {
        const uint GW_OWNER = 4;
        const int GWL_EXSTYLE = -20,
            WS_EX_TOOLWINDOW = 0x80,
            DWMWA_CLOAKED = 14;
        var apps = new Dictionary<string, RunningApp>(StringComparer.OrdinalIgnoreCase);
        EnumWindows(
            (w, _) =>
            {
                if (
                    !IsWindowVisible(w)
                    || GetWindow(w, GW_OWNER) != IntPtr.Zero
                    || (GetWindowLong(w, GWL_EXSTYLE) & WS_EX_TOOLWINDOW) != 0
                )
                    return true;
                if (
                    DwmGetWindowAttribute(w, DWMWA_CLOAKED, out var cloaked, sizeof(int)) == 0
                    && cloaked != 0
                )
                    return true;
                var title = TitleOf(w);
                if (title.Length == 0)
                    return true;
                GetWindowThreadProcessId(w, out var pid);
                if (pid == OwnId)
                    return true;
                var exe = ExeOf(pid);
                if (
                    exe is not null
                    && exe.Equals("ApplicationFrameHost.exe", StringComparison.OrdinalIgnoreCase)
                    && CoreWindowProcess(w, pid) is var app and not 0
                )
                    exe = ExeOf(app);
                if (exe is not null)
                    apps.TryAdd(exe, new RunningApp(exe, title));
                return true;
            },
            IntPtr.Zero
        );
        return apps.Values.OrderBy(a => a.Exe, StringComparer.OrdinalIgnoreCase).ToList();
    }
}

/// <summary>Title rules: globs (* any run, ? one character) matched against the whole title, ignoring case.</summary>
static class TitleRules
{
    static readonly ConcurrentDictionary<string, Regex> Compiled = new();

    public static bool Matches(string title, string glob) =>
        Compiled
            .GetOrAdd(
                glob,
                g => new Regex(
                    "^" + Regex.Escape(g).Replace(@"\*", ".*").Replace(@"\?", ".") + "$",
                    RegexOptions.IgnoreCase
                        | RegexOptions.Singleline
                        | RegexOptions.CultureInvariant
                )
            )
            .IsMatch(title);
}
