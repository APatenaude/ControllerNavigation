using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace ControllerNavigation;

/// <summary>Whether an app with no rule is left alone, and why (for the log and the tray).</summary>
readonly record struct Verdict(bool LeftAlone, string Reason);

/// <summary>
/// Tells whether an app with no rule is a game or reads the controller itself, so the controller is
/// left to it (SPEC "Which apps get the mouse"). Three signals, any one is enough:
/// <list type="bullet">
/// <item>its exe sits under a game folder (Detection.GameFolders): games that read the controller
/// through a Windows service (Xbox app games) or cannot be looked into (anti-cheat);</item>
/// <item>Windows recognized it as a game (HKCU\System\GameConfigStore\Children, the list behind Game
/// Bar and Game Mode);</item>
/// <item>it has a game controller open: any program that reads the pad (a game, Moonlight, an
/// emulator, Steam) holds a handle to the controller's device. Found by listing the process's
/// handles and asking each file handle's kernel name, the way Process Explorer does, and comparing
/// with the controllers' device names (XInput devices and HID game controllers, virtual ones
/// included). Browsers, Discord and Explorer hold the pad open too: they need an "always" rule.</item>
/// </list>
/// The handle look runs on a thread of its own (it can take tens of milliseconds in a big game);
/// Check never waits: an app not looked into yet is "not known" (null), and the controller stays
/// off for it meanwhile. A process found reading the pad stays a reader until it exits; one that
/// does not is looked at again every 2 s while in front (a game opens the pad some time after start).
/// A process that cannot be opened (elevated, protected) counts as not reading: the folders and
/// Windows' list cover most protected games.
/// </summary>
sealed class GameDetector : IDisposable
{
    const int RecheckMs = 2000,
        SlowRecheckMs = 15_000,
        KnownGamesEveryMs = 60_000,
        DevicesEveryMs = 30_000,
        NameTimeoutMs = 150;

    readonly Func<Settings> settings;
    readonly Thread thread;
    readonly AutoResetEvent wake = new(false);
    readonly ConcurrentDictionary<(uint Pid, long Started), Inspection> inspected = new();
    readonly ConcurrentQueue<(uint Pid, long Started)> wanted = new();
    volatile HashSet<string> knownGames = new(StringComparer.OrdinalIgnoreCase);
    volatile HashSet<string> devices = new(StringComparer.OrdinalIgnoreCase);
    volatile bool devicesStale = true,
        stopping;
    ushort fileType;
    NameQuery names = new();

    // Handles whose name query never answered: never asked again (each would strand another thread).
    readonly HashSet<(uint Pid, long Started, IntPtr Handle)> hung = new();

    /// <param name="At">When it was last looked into, or last found in use as a reader (Prune keeps it while so).</param>
    /// <param name="FirstAt">When it was first looked into: looked at less often after its first 30 s.</param>
    sealed record Inspection(bool Reads, bool Readable, long At, long FirstAt);

    public GameDetector(Func<Settings> settings)
    {
        this.settings = settings;
        thread = new Thread(Run)
        {
            IsBackground = true,
            Name = "Game detector",
            Priority = ThreadPriority.BelowNormal,
        };
        thread.Start();
    }

    public void Dispose()
    {
        stopping = true;
        wake.Set();
    }

    /// <summary>Controllers came or went: their device names are read again.</summary>
    public void DevicesChanged()
    {
        devicesStale = true;
        wake.Set();
    }

    /// <summary>
    /// The verdict for a process (identified by id and start time, so a reused id is a new process),
    /// or null while it has not been looked into yet. Never blocks: the look is queued.
    /// </summary>
    public Verdict? Check(uint pid, long started, string? path)
    {
        var d = settings().Detection;
        if (
            path is not null
            && d.GameFolders.FirstOrDefault(f =>
                path.Contains(f, StringComparison.OrdinalIgnoreCase)
            )
                is { } folder
        )
            return new Verdict(true, $"in a game folder ({folder.Trim('\\')})");
        if (d.KnownGames && path is not null && knownGames.Contains(path))
            return new Verdict(true, "a game Windows knows");
        if (!d.ControllerApps)
            return new Verdict(false, "automatic");
        var key = (pid, started);
        if (!inspected.TryGetValue(key, out var seen))
        {
            Ask(key);
            return null;
        }
        var now = Environment.TickCount64;
        if (seen.Reads)
        {
            if (now - seen.At > 60_000)
                inspected[key] = seen with { At = now };
            return new Verdict(true, "reads the controller");
        }
        // Games open the pad within their first seconds; after that, fewer handle looks into a
        // process that may watch for them (anti-cheat).
        if (now - seen.At > (now - seen.FirstAt < 30_000 ? RecheckMs : SlowRecheckMs))
            Ask(key);
        return new Verdict(
            false,
            seen.Readable ? "automatic" : "automatic (could not look into it)"
        );
    }

    void Ask((uint, long) key)
    {
        if (!wanted.Contains(key))
            wanted.Enqueue(key);
        wake.Set();
    }

    void Run()
    {
        long nextKnown = 0,
            nextDevices = 0;
        try
        {
            fileType = FileTypeIndex();
        }
        catch (Exception e)
        {
            Log.Error(
                "Game detector: the file handle type could not be found; apps are not looked into",
                e
            );
        }
        while (!stopping)
        {
            try
            {
                var now = Environment.TickCount64;
                if (now >= nextKnown)
                {
                    knownGames = ReadKnownGames();
                    Prune(now);
                    nextKnown = now + KnownGamesEveryMs;
                }
                if (devicesStale || now >= nextDevices)
                {
                    devicesStale = false;
                    var found = ReadControllerDevices();
                    if (!found.SetEquals(devices))
                    {
                        Log.Info(
                            $"Game detector: {found.Count} controller device(s): {string.Join(", ", found)}"
                        );
                        devices = found;
                        // A "not reading" seen with other controllers (none, while the pad was off)
                        // says nothing now: those apps are looked into again before they get the mouse.
                        foreach (var (key, value) in inspected)
                            if (!value.Reads)
                                inspected.TryRemove(key, out _);
                    }
                    nextDevices = now + DevicesEveryMs;
                }
                while (wanted.TryDequeue(out var key))
                {
                    var before = inspected.TryGetValue(key, out var old) ? old : null;
                    var result = Inspect(key);
                    inspected[key] = result with { FirstAt = before?.FirstAt ?? result.At };
                    if (result.Reads && before?.Reads != true)
                        Log.Info($"Game detector: process {key.Pid} has a controller open");
                }
            }
            catch (Exception e)
            {
                Log.Error("Game detector", e);
            }
            wake.WaitOne(1000);
        }
    }

    /// <summary>Forgets processes not seen for 10 minutes (gone, or long in the background).</summary>
    void Prune(long now)
    {
        foreach (var (key, value) in inspected)
            if (now - value.At > 600_000)
                inspected.TryRemove(key, out _);
        hung.RemoveWhere(h => !inspected.ContainsKey((h.Pid, h.Started)));
    }

    // --- The process's handles --------------------------------------------------------------

    [StructLayout(LayoutKind.Sequential)]
    struct HandleEntry
    {
        public IntPtr Handle;
        public UIntPtr HandleCount,
            PointerCount;
        public uint GrantedAccess,
            ObjectTypeIndex,
            Attributes,
            Reserved;
    }

    [DllImport("ntdll.dll")]
    static extern int NtQueryInformationProcess(
        IntPtr process,
        int infoClass,
        IntPtr info,
        int length,
        out int returned
    );

    [DllImport("ntdll.dll")]
    static extern int NtQueryObject(
        IntPtr handle,
        int infoClass,
        IntPtr info,
        int length,
        out int returned
    );

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool DuplicateHandle(
        IntPtr sourceProcess,
        IntPtr source,
        IntPtr targetProcess,
        out IntPtr target,
        uint access,
        bool inherit,
        uint options
    );

    [DllImport("kernel32.dll")]
    static extern IntPtr GetCurrentProcess();

    [DllImport("kernel32.dll")]
    static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll")]
    static extern uint GetFileType(IntPtr handle);

    const int ProcessHandleInformation = 51,
        ObjectNameInformation = 1;
    const uint PROCESS_QUERY_INFORMATION = 0x400,
        PROCESS_DUP_HANDLE = 0x40,
        DUPLICATE_SAME_ACCESS = 2;
    const uint FILE_TYPE_DISK = 1,
        FILE_TYPE_PIPE = 3;
    const int STATUS_INFO_LENGTH_MISMATCH = unchecked((int)0xC0000004);

    /// <summary>Whether the process holds a handle to one of the controllers' devices.</summary>
    Inspection Inspect((uint Pid, long Started) key)
    {
        var now = Environment.TickCount64;
        var controllers = devices;
        if (fileType == 0 || controllers.Count == 0)
            return new Inspection(false, true, now, now);
        var process = OpenProcess(PROCESS_QUERY_INFORMATION | PROCESS_DUP_HANDLE, false, key.Pid);
        if (process == IntPtr.Zero)
            return new Inspection(false, false, now, now);
        try
        {
            if (Handles(process) is not { } entries)
                return new Inspection(false, false, now, now);
            foreach (var entry in entries)
            {
                if (
                    entry.ObjectTypeIndex != fileType
                    || hung.Contains((key.Pid, key.Started, entry.Handle))
                )
                    continue;
                if (
                    !DuplicateHandle(
                        process,
                        entry.Handle,
                        GetCurrentProcess(),
                        out var copy,
                        0,
                        false,
                        DUPLICATE_SAME_ACCESS
                    )
                )
                    continue;
                try
                {
                    // Disk files are not devices, and a pipe can block the name query for good.
                    if (GetFileType(copy) is FILE_TYPE_DISK or FILE_TYPE_PIPE)
                        continue;
                    if (!NameOf(copy, out var name))
                        hung.Add((key.Pid, key.Started, entry.Handle));
                    else if (name is not null && controllers.Contains(name))
                        return new Inspection(true, true, now, now);
                }
                finally
                {
                    CloseHandle(copy);
                }
            }
            return new Inspection(false, true, now, now);
        }
        finally
        {
            CloseHandle(process);
        }
    }

    static List<HandleEntry>? Handles(IntPtr process)
    {
        var size = 64 * 1024;
        for (var attempt = 0; attempt < 8; attempt++)
        {
            var buffer = Marshal.AllocHGlobal(size);
            try
            {
                var status = NtQueryInformationProcess(
                    process,
                    ProcessHandleInformation,
                    buffer,
                    size,
                    out var needed
                );
                if (status == STATUS_INFO_LENGTH_MISMATCH)
                {
                    size = Math.Max(size * 2, needed + 4096);
                    continue;
                }
                if (status != 0)
                    return null;
                var count = (long)(ulong)Marshal.ReadIntPtr(buffer);
                var stride = Marshal.SizeOf<HandleEntry>();
                var list = new List<HandleEntry>((int)Math.Min(count, 1_000_000));
                for (long i = 0; i < count; i++)
                    list.Add(
                        Marshal.PtrToStructure<HandleEntry>(
                            buffer + IntPtr.Size * 2 + (nint)(i * stride)
                        )
                    );
                return list;
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
        return null;
    }

    /// <summary>The type index of File objects in this Windows: the type of a file this process opens.</summary>
    static ushort FileTypeIndex()
    {
        using var file = File.OpenRead(Environment.ProcessPath!);
        var mine = file.SafeFileHandle.DangerousGetHandle();
        var entries =
            Handles(GetCurrentProcess())
            ?? throw new InvalidOperationException("this process's handles could not be listed");
        return (ushort)entries.First(e => e.Handle == mine).ObjectTypeIndex;
    }

    /// <summary>
    /// A handle's kernel object name ("\Device\000001ab", or null when it has none), asked on a helper
    /// thread; false when that query did not answer within 150 ms.
    /// </summary>
    bool NameOf(IntPtr handle, out string? name)
    {
        if (names.Ask(handle, NameTimeoutMs, out name))
            return true;
        // Stuck in the kernel (a device that does not answer): that helper thread is left behind.
        Log.Warn(
            "Game detector: a handle name query did not answer; a new helper thread takes over"
        );
        names = new NameQuery();
        return false;
    }

    /// <summary>One thread that asks NtQueryObject for names, so a query that never returns cannot block the detector.</summary>
    sealed class NameQuery
    {
        readonly AutoResetEvent go = new(false),
            done = new(false);
        IntPtr handle;
        string? result;

        public NameQuery() =>
            new Thread(Loop) { IsBackground = true, Name = "Handle names" }.Start();

        public bool Ask(IntPtr h, int timeoutMs, out string? name)
        {
            handle = h;
            go.Set();
            var answered = done.WaitOne(timeoutMs);
            name = answered ? result : null;
            return answered;
        }

        void Loop()
        {
            var buffer = Marshal.AllocHGlobal(4096);
            while (true)
            {
                go.WaitOne();
                result =
                    NtQueryObject(handle, ObjectNameInformation, buffer, 4096, out _) == 0
                    && Marshal.ReadInt16(buffer) > 0
                        ? Marshal.PtrToStringUni(
                            Marshal.ReadIntPtr(buffer, IntPtr.Size),
                            Marshal.ReadInt16(buffer) / 2
                        )
                        : null;
                done.Set();
            }
        }
    }

    // --- The controllers' device names ------------------------------------------------------

    [StructLayout(LayoutKind.Sequential)]
    struct DevInfoData
    {
        public uint Size;
        public Guid ClassGuid;
        public uint DevInst;
        public IntPtr Reserved;
    }

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode)]
    static extern IntPtr SetupDiGetClassDevsW(
        ref Guid guid,
        string? enumerator,
        IntPtr parent,
        uint flags
    );

    [DllImport("setupapi.dll")]
    static extern bool SetupDiEnumDeviceInfo(IntPtr set, uint index, ref DevInfoData data);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode)]
    static extern bool SetupDiGetDeviceRegistryPropertyW(
        IntPtr set,
        ref DevInfoData data,
        uint property,
        out uint type,
        byte[]? buffer,
        uint size,
        out uint required
    );

    [DllImport("setupapi.dll")]
    static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);

    const uint DIGCF_PRESENT = 0x2,
        DIGCF_DEVICEINTERFACE = 0x10;
    const uint SPDRP_COMPATIBLEIDS = 0x2,
        SPDRP_PHYSICAL_DEVICE_OBJECT_NAME = 0xE;

    // XInput's device interface (every XInput pad, Steam's and ViGEm's virtual ones included).
    static readonly Guid XusbInterface = new("EC87F1E3-C13B-4100-B5F7-8B84D54260CB");
    static readonly Guid HidClass = new("745A17A0-74D3-11D0-B6FE-00A0C90F57DA");

    /// <summary>The kernel names of every connected controller: XInput devices and HID game controllers (DirectInput, SDL, Steam).</summary>
    static HashSet<string> ReadControllerDevices()
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var xusb = XusbInterface;
        foreach (var (pdo, _) in Devices(ref xusb, DIGCF_PRESENT | DIGCF_DEVICEINTERFACE))
            names.Add(pdo);
        var hid = HidClass;
        foreach (var (pdo, compatible) in Devices(ref hid, DIGCF_PRESENT))
            if (compatible.Contains("HID_DEVICE_SYSTEM_GAME", StringComparer.OrdinalIgnoreCase))
                names.Add(pdo);
        return names;
    }

    static List<(string Pdo, string[] Compatible)> Devices(ref Guid guid, uint flags)
    {
        var list = new List<(string, string[])>();
        var set = SetupDiGetClassDevsW(ref guid, null, IntPtr.Zero, flags);
        if (set == IntPtr.Zero || set == new IntPtr(-1))
            return list;
        try
        {
            var data = new DevInfoData { Size = (uint)Marshal.SizeOf<DevInfoData>() };
            for (uint i = 0; SetupDiEnumDeviceInfo(set, i, ref data); i++)
                if (Property(set, ref data, SPDRP_PHYSICAL_DEVICE_OBJECT_NAME) is [var pdo, ..])
                    list.Add(
                        (pdo, Property(set, ref data, SPDRP_COMPATIBLEIDS) ?? Array.Empty<string>())
                    );
        }
        finally
        {
            SetupDiDestroyDeviceInfoList(set);
        }
        return list;
    }

    /// <summary>A string or multi-string device property, as its strings; null if the device has none.</summary>
    static string[]? Property(IntPtr set, ref DevInfoData data, uint property)
    {
        SetupDiGetDeviceRegistryPropertyW(set, ref data, property, out _, null, 0, out var size);
        if (size == 0)
            return null;
        var buffer = new byte[size];
        if (!SetupDiGetDeviceRegistryPropertyW(set, ref data, property, out _, buffer, size, out _))
            return null;
        return System
            .Text.Encoding.Unicode.GetString(buffer)
            .Split('\0', StringSplitOptions.RemoveEmptyEntries);
    }

    // --- Windows' own list of games ---------------------------------------------------------

    /// <summary>The exes Windows recognized as games (Game Bar, Game Mode): full paths.</summary>
    static HashSet<string> ReadKnownGames()
    {
        var games = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using var children = Registry.CurrentUser.OpenSubKey(@"System\GameConfigStore\Children");
        if (children is null)
            return games;
        foreach (var name in children.GetSubKeyNames())
        {
            using var child = children.OpenSubKey(name);
            if (child?.GetValue("MatchedExeFullPath") is string path && path.Length > 0)
                games.Add(path);
        }
        return games;
    }
}
