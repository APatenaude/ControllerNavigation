using System.Net;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace ControllerNavigation;

/// <summary>
/// The update rules that need no network, file or window (lifted from HTPC's UpdateRules.cs;
/// UpdateTests checks them): versions, where GitHub's redirects may go, what a 403 means, when the
/// quiet check is due, what update.json must hold, the updater's state between checks.
/// </summary>
static class UpdateRules
{
    public static readonly TimeSpan CheckEvery = TimeSpan.FromHours(20);

    // A check that failed (offline, GitHub limiting): again after an hour, this many times in a
    // row at most, then at the daily rhythm again.
    public static readonly TimeSpan RetryAfter = TimeSpan.FromHours(1);
    public const int RetriesAfterFailure = 4;

    /// <summary>The largest exe update.json may announce.</summary>
    public const long MaxExeSize = 512L * 1024 * 1024;

    /// <summary>"0.10.2" or "v0.10.2" as a number triple; null otherwise (no pre-releases: stable releases only).</summary>
    public static Version? ParseSemVer(string? text)
    {
        if (text is null)
            return null;
        var m = Regex.Match(text, @"^v?(\d{1,6})\.(\d{1,6})\.(\d{1,6})$");
        return m.Success
            ? new Version(
                int.Parse(m.Groups[1].Value),
                int.Parse(m.Groups[2].Value),
                int.Parse(m.Groups[3].Value)
            )
            : null;
    }

    /// <summary>Both are versions and latest is the newer.</summary>
    public static bool IsNewer(string? latest, string? current) =>
        ParseSemVer(latest) is { } l && ParseSemVer(current) is { } c && l > c;

    /// <summary>
    /// Where a redirect may take a read that started at github.com/&lt;Repo&gt;/: HTTPS on 443 only;
    /// github.com again only under the repository's own path (elsewhere it was renamed or moved: an
    /// error, not followed); or any host under githubusercontent.com, where GitHub serves release
    /// files (TLS and the job's SHA-256 check carry the integrity, not that host's exact name).
    /// Null when allowed, else why not.
    /// </summary>
    public static string? RefusedHop(Uri uri)
    {
        if (
            !uri.IsAbsoluteUri
            || uri.Scheme != "https"
            || uri.Port != 443
            || uri.UserInfo.Length > 0
        )
            return $"Refused to read from {uri.Host}";
        var host = uri.Host.ToLowerInvariant();
        if (host == "github.com")
            return uri.AbsolutePath.StartsWith(
                $"/{AppInfo.Repo}/",
                StringComparison.OrdinalIgnoreCase
            )
                ? null
                : $"{AppInfo.Repo} seems to have moved (GitHub sent {uri.AbsolutePath})";
        return
            host == "githubusercontent.com"
            || host.EndsWith(".githubusercontent.com", StringComparison.Ordinal)
            ? null
            : $"Refused to read from {uri.Host}";
    }

    /// <summary>
    /// Where github.com/&lt;Repo&gt;/releases/latest redirected: the newest release's tag, or null when
    /// the repository has none yet (/&lt;Repo&gt;/releases). Anywhere else (a renamed or moved
    /// repository, another host) or an odd tag throws: an error, not "no release".
    /// </summary>
    public static string? LatestTag(Uri to)
    {
        var releases = $"/{AppInfo.Repo}/releases";
        var prefix = $"{releases}/tag/";
        if (
            to.Scheme != "https"
            || !to.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase)
        )
            throw new InvalidOperationException(
                $"releases/latest pointed away from github.com ({to.Host})"
            );
        if (to.AbsolutePath.TrimEnd('/').Equals(releases, StringComparison.OrdinalIgnoreCase))
            return null;
        if (!to.AbsolutePath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                $"{AppInfo.Repo} seems to have moved (its releases/latest points to {to.AbsolutePath})"
            );
        var tag = Uri.UnescapeDataString(to.AbsolutePath[prefix.Length..]);
        return Regex.IsMatch(tag, "^[A-Za-z0-9._-]{1,64}$")
            ? tag
            : throw new InvalidOperationException($"Odd release tag: {tag}");
    }

    /// <summary>GitHub's rate limit: 429, or a 403 that says so (a Retry-After, or X-RateLimit-Remaining: 0). Any other 403 is a refusal.</summary>
    public static bool IsRateLimit(int code, bool retryAfter, string? remaining) =>
        code == 429 || code == 403 && (retryAfter || remaining?.Trim() == "0");

    /// <summary>
    /// Whether the quiet check is due: CheckEvery after the last one that worked, or, after one
    /// failed, RetryAfter after that try (RetriesAfterFailure times in a row, then CheckEvery).
    /// </summary>
    public static bool CheckDue(
        DateTime now,
        DateTime? lastCheckUtc,
        DateTime? lastTryUtc,
        int failedChecks
    )
    {
        if (failedChecks > 0 && lastTryUtc is { } tried)
            return now - tried >= (failedChecks <= RetriesAfterFailure ? RetryAfter : CheckEvery);
        return lastCheckUtc is not { } last || now - last >= CheckEvery;
    }

    /// <summary>
    /// A release's update.json, checked field by field, for the release tagged tag:
    /// {"version":"x.y.z","exe":"ControllerNavigation.exe","sha256":"&lt;64 hex&gt;","size":&lt;bytes&gt;},
    /// its tag v&lt;version&gt;. Throws InvalidDataException on anything else.
    /// </summary>
    public static ReleaseInfo ParseManifest(string text, string tag)
    {
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(text);
        }
        catch (JsonException)
        {
            throw new InvalidDataException("update.json is not valid JSON");
        }
        using (doc)
        {
            var r = doc.RootElement;
            if (r.ValueKind != JsonValueKind.Object)
                throw Bad("not an object");
            string? S(string name) =>
                r.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
                    ? v.GetString()
                    : null;
            var version = S("version");
            if (version is null || version.StartsWith('v') || ParseSemVer(version) is null)
                throw Bad($"version '{version}' is not major.minor.patch");
            if ($"v{version}" != tag)
                throw Bad($"is for version {version}, not release {tag}");
            var exe = S("exe");
            if (
                exe is null
                || !Regex.IsMatch(exe, "^[A-Za-z0-9._-]{1,128}$")
                || !exe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            )
                throw Bad($"odd exe name '{exe}'");
            var sha256 = S("sha256");
            if (sha256 is null || !Regex.IsMatch(sha256, "^[0-9a-fA-F]{64}$"))
                throw Bad("sha256 is not 64 hex digits");
            if (
                !r.TryGetProperty("size", out var s)
                || s.ValueKind != JsonValueKind.Number
                || !s.TryGetInt64(out var size)
                || size <= 0
                || size > MaxExeSize
            )
                throw Bad("size");
            return new ReleaseInfo(tag, version, exe, sha256.ToLowerInvariant(), size);
        }

        static InvalidDataException Bad(string why) => new($"update.json: {why}");
    }

    /// <summary>
    /// The updater's state between checks and installs: notInstalled (not the copy in Program
    /// Files: nothing to update), off (auto-update off), waitingIdle (a newer release, applied once
    /// the pad rests), failed (the newer release's install failed in this run, or the last check
    /// did), upToDate.
    /// </summary>
    public static string RestingState(
        bool installed,
        bool autoUpdate,
        bool newer,
        bool installFailed,
        bool checkFailed
    ) =>
        !installed ? Updater.NotInstalled
        : !autoUpdate ? Updater.Off
        : newer && !installFailed ? Updater.WaitingIdle
        : newer || checkFailed ? Updater.Failed
        : Updater.UpToDate;
}

/// <summary>A release as its update.json describes it (UpdateRules.ParseManifest).</summary>
sealed record ReleaseInfo(string Tag, string Version, string Exe, string Sha256, long Size);

/// <summary>
/// Reads the releases (lifted from HTPC's UpdateService and setup\lib\UpdateCore.ps1): the newest
/// tag from where github.com/&lt;Repo&gt;/releases/latest redirects (no GitHub API, so no rate
/// limit), then that release's update.json and exe, every redirect checked (UpdateRules.RefusedHop)
/// and followed here, never by HttpClient. The Updater reads with it; the elevated UpdateJob reads
/// everything again on its own.
/// </summary>
static class ReleaseFeed
{
    /// <summary>direct: no proxy, for the elevated job (the user's proxy settings, in HKCU and the environment, are theirs to point anywhere).</summary>
    public static HttpClient Client(bool direct)
    {
        var http = new HttpClient(
            new SocketsHttpHandler
            {
                AllowAutoRedirect = false,
                AutomaticDecompression = DecompressionMethods.None,
                UseProxy = !direct,
                ConnectTimeout = TimeSpan.FromSeconds(20),
            }
        )
        {
            Timeout = TimeSpan.FromSeconds(60),
        };
        http.DefaultRequestHeaders.UserAgent.Add(
            new ProductInfoHeaderValue("ControllerNavigation", AppInfo.Version)
        );
        return http;
    }

    /// <summary>The newest stable release; null when the repository has none yet, or its newest is not a plain v&lt;x.y.z&gt;. Throws when it cannot tell.</summary>
    public static async Task<ReleaseInfo?> LatestAsync(HttpClient http, CancellationToken cancel)
    {
        var latestUri = new Uri($"https://github.com/{AppInfo.Repo}/releases/latest");
        string? tag;
        using (
            var latest = await http.GetAsync(
                latestUri,
                HttpCompletionOption.ResponseHeadersRead,
                cancel
            )
        )
        {
            var code = (int)latest.StatusCode;
            if (code is 403 or 429)
                throw new InvalidOperationException(
                    RateLimited(latest) ?? $"releases/latest answered {code}"
                );
            if (code is not (301 or 302 or 303 or 307 or 308) || latest.Headers.Location is null)
                throw new InvalidOperationException($"releases/latest answered {code}");
            tag = UpdateRules.LatestTag(new Uri(latestUri, latest.Headers.Location));
        }
        if (tag is null || UpdateRules.ParseSemVer(tag) is null)
            return null;
        return UpdateRules.ParseManifest(
            await GetTextAsync(http, ReleaseFile(tag, "update.json"), cancel),
            tag
        );
    }

    /// <summary>
    /// The release's exe into outFile (made new: it must not be there), exactly r.Size bytes (a
    /// Content-Length or a stream that says otherwise fails), its SHA-256 then checked on disk
    /// (a file that fails is deleted). Throws on any mismatch.
    /// </summary>
    public static async Task DownloadAsync(
        HttpClient http,
        ReleaseInfo r,
        string outFile,
        CancellationToken cancel
    )
    {
        using (var response = await GetPinnedAsync(http, ReleaseFile(r.Tag, r.Exe), cancel))
        {
            if (response.Content.Headers.ContentLength is { } declared && declared != r.Size)
                throw new InvalidDataException(
                    $"{r.Exe} is {declared} bytes on the server, update.json says {r.Size}"
                );
            await using var source = await response.Content.ReadAsStreamAsync(cancel);
            await using var file = new FileStream(
                outFile,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                1 << 18
            );
            var buffer = new byte[1 << 18];
            long total = 0;
            int n;
            while ((n = await source.ReadAsync(buffer, cancel)) > 0)
            {
                total += n;
                if (total > r.Size)
                    throw new InvalidDataException(
                        $"{r.Exe} is longer than update.json says ({r.Size} bytes)"
                    );
                await file.WriteAsync(buffer.AsMemory(0, n), cancel);
            }
            if (total != r.Size)
                throw new InvalidDataException($"{r.Exe} ended after {total} of {r.Size} bytes");
            file.Flush(flushToDisk: true);
        }
        string actual;
        await using (var read = File.OpenRead(outFile))
            actual = Convert.ToHexString(await SHA256.HashDataAsync(read, cancel));
        if (!actual.Equals(r.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            File.Delete(outFile);
            throw new InvalidDataException($"{r.Exe} does not match the release's SHA-256");
        }
    }

    static Uri ReleaseFile(string tag, string name) =>
        new(
            $"https://github.com/{AppInfo.Repo}/releases/download/{Uri.EscapeDataString(tag)}/{Uri.EscapeDataString(name)}"
        );

    // A small file (update.json), 256 KB at most.
    static async Task<string> GetTextAsync(HttpClient http, Uri uri, CancellationToken cancel)
    {
        const int most = 262_144;
        using var response = await GetPinnedAsync(http, uri, cancel);
        if (response.Content.Headers.ContentLength > most)
            throw new InvalidDataException($"{uri.AbsolutePath} is larger than {most} bytes");
        await using var stream = await response.Content.ReadAsStreamAsync(cancel);
        using var memory = new MemoryStream();
        var buffer = new byte[16_384];
        int n;
        while ((n = await stream.ReadAsync(buffer, cancel)) > 0)
        {
            memory.Write(buffer, 0, n);
            if (memory.Length > most)
                throw new InvalidDataException($"{uri.AbsolutePath} is larger than {most} bytes");
        }
        return System.Text.Encoding.UTF8.GetString(memory.ToArray());
    }

    // A 2xx response, following redirects (UpdateRules.RefusedHop on every hop) at most 5 times.
    static async Task<HttpResponseMessage> GetPinnedAsync(
        HttpClient http,
        Uri uri,
        CancellationToken cancel
    )
    {
        for (var hop = 0; hop <= 5; hop++)
        {
            if (UpdateRules.RefusedHop(uri) is { } refused)
                throw new InvalidOperationException(refused);
            var response = await http.GetAsync(
                uri,
                HttpCompletionOption.ResponseHeadersRead,
                cancel
            );
            var code = (int)response.StatusCode;
            if (
                code is 301 or 302 or 303 or 307 or 308
                && response.Headers.Location is { } location
            )
            {
                response.Dispose();
                uri = new Uri(uri, location);
                continue;
            }
            if (code is >= 200 and < 300)
                return response;
            var why =
                code is 403 or 429 && RateLimited(response) is { } limited ? limited
                : code == 404 ? $"Not found: {uri.GetLeftPart(UriPartial.Path)}"
                : $"HTTP {code} from {uri.Host}";
            response.Dispose();
            throw new InvalidOperationException(why);
        }
        throw new InvalidOperationException("Too many redirects");
    }

    // GitHub's rate limit (UpdateRules.IsRateLimit): the message, or null when it is not one.
    static string? RateLimited(HttpResponseMessage r)
    {
        var remaining = r.Headers.TryGetValues("X-RateLimit-Remaining", out var left)
            ? left.FirstOrDefault()
            : null;
        return UpdateRules.IsRateLimit(
            (int)r.StatusCode,
            r.Headers.RetryAfter is not null,
            remaining
        )
            ? "GitHub is limiting requests from this PC; try again later"
            : null;
    }
}

/// <summary>What the updater keeps across runs (%LOCALAPPDATA%\ControllerNavigation\update-state.json).</summary>
sealed class UpdateSaved
{
    /// <summary>The last check that worked.</summary>
    public DateTime? LastCheckUtc { get; set; }

    /// <summary>The last try, and how many in a row failed (then tried again after an hour, a few times).</summary>
    public DateTime? LastTryUtc { get; set; }
    public int FailedChecks { get; set; }

    /// <summary>The newest release the last check found (null: none).</summary>
    public string? Latest { get; set; }

    /// <summary>The version that last ran here: an older one means an update was installed since.</summary>
    public string? RanVersion { get; set; }
    public string? UpdatedFrom { get; set; }
    public DateTime? UpdatedUtc { get; set; }

    /// <summary>The version the update job was last started for, until this app learns how it went.</summary>
    public string? InstallingVersion { get; set; }
}

/// <summary>
/// Auto-update in the tray app (SPEC "Install and updates", HTPC's rules): a quiet check about every
/// 20 hours while auto-update is on (an hour after one that failed, a few times), reading only
/// (ReleaseFeed). A newer release, with auto-update on and this copy installed, waits until the pad
/// rests (padIdle), then the elevated \ControllerNavigation\Update task applies it (UpdateJob), which
/// fetches and checks everything again itself and asks this app to exit. This process never
/// changes anything in Program Files. padIdle and autoUpdate are called on a timer thread, and on
/// the threads that call Start and CheckNowAsync.
/// </summary>
sealed class Updater : IDisposable
{
    /// <summary>UpdateStatus.State values.</summary>
    public const string Off = "off",
        NotInstalled = "notInstalled",
        Checking = "checking",
        UpToDate = "upToDate",
        Available = "available",
        WaitingIdle = "waitingIdle",
        Installing = "installing",
        Failed = "failed";

    static readonly TimeSpan TickEvery = TimeSpan.FromSeconds(15);

    // The Update task's own time limit: still running past it, it is not coming back.
    static readonly TimeSpan JobLimit = TimeSpan.FromHours(1);

    static string StateFile => Path.Combine(AppInfo.DataDir, "update-state.json");
    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    readonly Func<bool> padIdle,
        autoUpdate;
    readonly bool installed = AppInfo.RunningInstalled;
    readonly object gate = new();
    readonly UpdateSaved saved;
    UpdateStatus status;
    System.Threading.Timer? timer;
    bool checking,
        installing,
        disposed;
    string? lastError; // the last check's failure, until one works
    string? failedVersion; // its install failed in this run: not tried again until Check now
    string? installError;
    string? installVersion;
    DateTime installStartedUtc;

    /// <param name="padIdle">The pad has rested long enough for the app to restart under the user.</param>
    /// <param name="autoUpdate">Settings.AutoUpdate, read at each tick.</param>
    public Updater(Func<bool> padIdle, Func<bool> autoUpdate)
    {
        this.padIdle = padIdle;
        this.autoUpdate = autoUpdate;
        saved = Load();
        NoteInstalledUpdate();
        NoteUnfinishedInstall();
        status = new UpdateStatus(
            AppInfo.Version,
            saved.Latest,
            installed ? UpToDate : NotInstalled,
            UpdatedNote(),
            saved.LastCheckUtc
        );
    }

    public UpdateStatus Status
    {
        get
        {
            lock (gate)
                return status;
        }
    }

    /// <summary>The status changed. Any thread.</summary>
    public event Action? Changed;

    /// <summary>Starts the timer: the quiet check when due, a newer release applied once the pad rests.</summary>
    public void Start()
    {
        lock (gate)
        {
            if (disposed || timer is not null)
                return;
            timer = new System.Threading.Timer(
                _ => OnTick(),
                null,
                TimeSpan.FromMinutes(1),
                TickEvery
            );
        }
        Log.Info(
            $"Updates: {AppInfo.Version}, {(installed ? "installed" : "not installed (nothing to update)")}; newest seen {saved.Latest ?? "none"}"
        );
        Settle();
    }

    /// <summary>Checks now, whatever the schedule, and applies a newer release once the pad rests (if auto-update is on and this copy is installed). Never throws.</summary>
    public Task CheckNowAsync() => CheckAsync(asked: true);

    public void Dispose()
    {
        lock (gate)
        {
            disposed = true;
            timer?.Dispose();
            timer = null;
        }
    }

    void OnTick()
    {
        try
        {
            bool watching,
                due;
            lock (gate)
            {
                if (disposed)
                    return;
                watching = installing;
                due =
                    !checking
                    && UpdateRules.CheckDue(
                        DateTime.UtcNow,
                        saved.LastCheckUtc,
                        saved.LastTryUtc,
                        saved.FailedChecks
                    );
            }
            if (watching)
                WatchInstall();
            else if (due && autoUpdate())
                _ = CheckAsync(asked: false);
            else
                Settle();
        }
        catch (Exception e)
        {
            Log.Error("Updates: timer", e);
        }
    }

    async Task CheckAsync(bool asked)
    {
        try
        {
            lock (gate)
            {
                if (disposed || checking || installing)
                    return;
                checking = true;
            }
            Set(Checking, null);
            Log.Info($"Updates: checking ({(asked ? "asked" : "quiet")})");
            ReleaseInfo? release = null;
            string? error = null;
            try
            {
                using var http = ReleaseFeed.Client(direct: false);
                using var cancel = new CancellationTokenSource(TimeSpan.FromMinutes(1));
                release = await ReleaseFeed.LatestAsync(http, cancel.Token);
            }
            catch (OperationCanceledException)
            {
                error = "GitHub did not answer in time";
            }
            catch (Exception e)
            {
                error = e.Message;
            }
            bool found;
            lock (gate)
            {
                checking = false;
                saved.LastTryUtc = DateTime.UtcNow;
                if (error is null)
                {
                    saved.LastCheckUtc = saved.LastTryUtc;
                    saved.FailedChecks = 0;
                    saved.Latest = release?.Version;
                    lastError = null;
                    if (asked)
                        failedVersion = installError = null;
                }
                else
                {
                    saved.FailedChecks++;
                    lastError = $"Could not check for updates: {error}";
                }
                found = error is null && UpdateRules.IsNewer(saved.Latest, AppInfo.Version);
            }
            Save();
            Log.Info(
                error is null
                    ? $"Updates: newest release {release?.Version ?? "none"} (this is {AppInfo.Version})"
                    : $"Updates: check failed: {error}"
            );
            if (found && installed && autoUpdate())
                Set(Available, $"Version {release!.Version} is out");
            Settle();
        }
        catch (Exception e)
        {
            lock (gate)
                checking = false;
            Log.Error("Updates: check", e);
        }
    }

    // The state between checks: follows auto-update switched on or off, and starts the update once
    // the pad rests.
    void Settle()
    {
        var auto = autoUpdate();
        string state;
        string? message,
            latest;
        lock (gate)
        {
            if (disposed || checking || installing)
                return;
            latest = saved.Latest;
            var newer = UpdateRules.IsNewer(latest, AppInfo.Version);
            var installFailed = newer && latest == failedVersion;
            state = UpdateRules.RestingState(
                installed,
                auto,
                newer,
                installFailed,
                lastError is not null
            );
            message = state switch
            {
                NotInstalled when newer => $"Version {latest} is out; run it to install it",
                Off when newer => $"Version {latest} is out (auto-update is off)",
                WaitingIdle => $"Version {latest} installs once the controller has rested",
                Failed => installFailed ? installError : lastError,
                UpToDate => UpdatedNote(),
                _ => lastError,
            };
        }
        if (state == WaitingIdle && padIdle())
            StartInstall(latest!);
        else
            Set(state, message);
    }

    void StartInstall(string version)
    {
        lock (gate)
        {
            if (disposed || installing)
                return;
            installing = true;
            installVersion = saved.InstallingVersion = version;
            installStartedUtc = DateTime.UtcNow;
        }
        Save();
        Set(Installing, $"Installing version {version}");
        try
        {
            AppTasks.Run(AppTasks.Update);
            Log.Info($"Updates: the update task started for {version}");
        }
        catch (Exception e)
        {
            InstallFailed(
                version,
                $"Version {version} was not installed: the update task did not start ({e.Message}); run the installer again"
            );
        }
    }

    // The job asks this app to exit once the new exe is checked: still here, it has not got there
    // yet, or it stopped.
    void WatchInstall()
    {
        string version;
        DateTime since;
        lock (gate)
        {
            if (!installing)
                return;
            version = installVersion!;
            since = installStartedUtc;
        }
        var elapsed = DateTime.UtcNow - since;
        bool running;
        int result;
        try
        {
            (running, result) = AppTasks.State(AppTasks.Update);
        }
        catch (Exception e)
        {
            (running, result) = (false, -1);
            Log.Warn($"Updates: the update task's state cannot be read ({e.Message})");
        }
        // Task Scheduler may not show a task it was just asked to run as running yet.
        if (elapsed < TimeSpan.FromSeconds(20) || running && elapsed < JobLimit)
            return;
        var why = result switch
        {
            UpdateJob.NothingNewer => "the update job found no release newer than this one",
            UpdateJob.Busy => "an install or another update was running",
            _ =>
                $"the update job ended with code {result}; its log is {Path.Combine(Elevation.LogDir, "update.log")}",
        };
        InstallFailed(version, $"Version {version} was not installed: {why}");
    }

    void InstallFailed(string version, string why)
    {
        lock (gate)
        {
            installing = false;
            failedVersion = version;
            installError = why;
            saved.InstallingVersion = null;
        }
        Save();
        Log.Warn($"Updates: {why}");
        Settle();
    }

    void Set(string state, string? message)
    {
        string? before;
        lock (gate)
        {
            var next = new UpdateStatus(
                AppInfo.Version,
                saved.Latest,
                state,
                message,
                saved.LastCheckUtc
            );
            if (next == status)
                return;
            before = status.State;
            status = next;
        }
        if (before != state)
            Log.Info($"Updates: {state}{(message is null ? "" : $" ({message})")}");
        try
        {
            Changed?.Invoke();
        }
        catch (Exception e)
        {
            Log.Error("Updates: a Changed handler", e);
        }
    }

    // An update installed since the last run: the version that ran here last is older than this one.
    void NoteInstalledUpdate()
    {
        if (!installed || saved.RanVersion == AppInfo.Version)
            return;
        if (UpdateRules.IsNewer(AppInfo.Version, saved.RanVersion))
        {
            saved.UpdatedFrom = saved.RanVersion;
            saved.UpdatedUtc = DateTime.UtcNow;
            Log.Info($"Updates: updated from {saved.RanVersion} to {AppInfo.Version}");
        }
        saved.RanVersion = AppInfo.Version;
        Save();
    }

    // The update job stops this app before it swaps the exe: back here still older than the
    // version it was started for, the swap failed (a restart right away would start it again).
    // Not tried again by itself in this run.
    void NoteUnfinishedInstall()
    {
        if (saved.InstallingVersion is not { } version)
            return;
        if (UpdateRules.IsNewer(version, AppInfo.Version))
        {
            failedVersion = version;
            installError =
                $"Version {version} was not installed; its log is {Path.Combine(Elevation.LogDir, "update.log")}";
            Log.Warn($"Updates: {installError}");
        }
        saved.InstallingVersion = null;
        Save();
    }

    string? UpdatedNote() =>
        saved.UpdatedFrom is { } from && saved.UpdatedUtc is { } at
            ? $"Updated from {from} on {at.ToLocalTime().ToString("d MMM yyyy", System.Globalization.CultureInfo.InvariantCulture)}"
            : null;

    static UpdateSaved Load()
    {
        try
        {
            if (File.Exists(StateFile))
                return JsonSerializer.Deserialize<UpdateSaved>(File.ReadAllText(StateFile), Json)
                    ?? new();
        }
        catch (Exception e)
        {
            Log.Warn($"Updates: {StateFile} unreadable ({e.Message}); starting afresh");
        }
        return new();
    }

    void Save()
    {
        try
        {
            string text;
            lock (gate)
                text = JsonSerializer.Serialize(saved, Json);
            Directory.CreateDirectory(AppInfo.DataDir);
            // One temporary file per save: "Check now" and the timer can save at the same moment.
            var temp = $"{StateFile}.{Guid.NewGuid():N}.tmp";
            File.WriteAllText(temp, text);
            File.Move(temp, StateFile, overwrite: true);
        }
        catch (Exception e)
        {
            Log.Warn($"Updates: {StateFile} not saved ({e.Message})");
        }
    }
}

/// <summary>
/// The update job (--apply-update): this exe, elevated, run by the \ControllerNavigation\Update
/// task as the user, in their session (where Program.ExitEvent is). It trusts nothing from the tray
/// app: it reads the newest release and its update.json itself (ReleaseFeed, no proxy), compares it
/// with the installed exe, downloads the new exe into Program Files (admin-only), checks its size,
/// SHA-256 and file version, asks the app to exit, swaps the exe and starts the app again through
/// the Start task. A running exe can be renamed but not overwritten, this job's own included: the
/// current one is renamed aside (*.old) and the new one moved in; the old file goes at a later run
/// (Installer.RemoveSetAside). Logs to Program Files\ControllerNavigation\logs\update.log.
/// </summary>
static class UpdateJob
{
    /// <summary>Exit codes (the Updater reads them as the task's last result).</summary>
    public const int Ok = 0,
        Failed = 1,
        Refused = 2,
        NothingNewer = 3,
        Busy = 4;

    public static int Run(string[] args)
    {
        try
        {
            if (!Elevation.Enter("Update", "update.log"))
                return Refused;
            var setup = Elevation.TakeSetupLock();
            if (setup is null)
            {
                Log.Warn("Update: an install or another update runs; ending");
                return Busy;
            }
            try
            {
                return Apply().GetAwaiter().GetResult();
            }
            finally
            {
                setup.ReleaseMutex();
                setup.Dispose();
            }
        }
        catch (Exception e)
        {
            Log.Error("Update: failed", e);
            return Failed;
        }
        finally
        {
            Log.Flush();
        }
    }

    static async Task<int> Apply()
    {
        Installer.RemoveSetAside();
        var installed = Installer.ExeVersion(AppInfo.InstalledExe);
        using var http = ReleaseFeed.Client(direct: true);
        using var cancel = new CancellationTokenSource(TimeSpan.FromMinutes(40));
        var release = await ReleaseFeed.LatestAsync(http, cancel.Token);
        if (
            release is null
            || installed is null
            || UpdateRules.ParseSemVer(release.Version) is not { } next
            || next <= installed
        )
        {
            Log.Info(
                $"Update: nothing newer (installed {installed?.ToString() ?? "unknown"}, newest release {release?.Version ?? "none"})"
            );
            return NothingNewer;
        }
        Log.Info($"Update: {installed} to {release.Version}");
        var download = Path.Combine(
            AppInfo.InstallDir,
            $"{AppInfo.ExeName}.{Guid.NewGuid():N}.download"
        );
        try
        {
            await ReleaseFeed.DownloadAsync(http, release, download, cancel.Token);
            Log.Info($"Update: downloaded {release.Size} bytes, SHA-256 as update.json says");
            if (Installer.ExeVersion(download) is var version && version != next)
            {
                Log.Warn($"Update: the downloaded exe says it is {version}, not {next}: refused");
                return Refused;
            }
            var startApp = AppTasks.AppStarter();
            RunningCopies.Stop(everything: false);
            var swapped = Swap(download);
            if (File.Exists(AppInfo.InstalledExe)) // not after a swap that lost it (logged)
                startApp(swapped ? $"Update to {release.Version}" : "Update (old version back)");
            return swapped ? Ok : Failed;
        }
        finally
        {
            try
            {
                File.Delete(download);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
    }

    // The running exe (this job's own image too) renamed aside, the new one moved in; the old one
    // put back when that fails. While the exe's path is empty this process cannot load an assembly
    // it has not loaded yet (AppTasks.AppStarter): the move back comes before any message, and the
    // messages use only Marshal, loaded with this method.
    static bool Swap(string download)
    {
        var exe = AppInfo.InstalledExe;
        var aside = $"{exe}.{Guid.NewGuid().ToString("N")[..8]}.old";
        if (!MoveFileEx(exe, aside, MOVEFILE_WRITE_THROUGH))
        {
            Log.Error(
                $"Update: {exe} could not be set aside ({Marshal.GetPInvokeErrorMessage(Marshal.GetLastPInvokeError())})"
            );
            return false;
        }
        if (MoveFileEx(download, exe, MOVEFILE_WRITE_THROUGH))
        {
            Log.Info(
                $"Update: swapped (the old exe is {Path.GetFileName(aside)} until a later run)"
            );
            return true;
        }
        var why = Marshal.GetLastPInvokeError();
        var back = MoveFileEx(aside, exe, MOVEFILE_WRITE_THROUGH);
        var backWhy = Marshal.GetLastPInvokeError();
        if (back)
            Log.Error(
                $"Update: the new exe could not be moved in ({Marshal.GetPInvokeErrorMessage(why)}); the old one is back"
            );
        else
            Log.Error(
                $"Update: the new exe could not be moved in ({Marshal.GetPInvokeErrorMessage(why)}), nor the old one put back ({Marshal.GetPInvokeErrorMessage(backWhy)}): run the installer again"
            );
        return false;
    }

    const int MOVEFILE_WRITE_THROUGH = 0x8;

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern bool MoveFileEx(string existing, string replacement, int flags);
}
