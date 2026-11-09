namespace ControllerNavigation.Tests;

// The install, update and input-helper rules that need no network, elevation or task: versions,
// GitHub's redirects, releases/latest, when the quiet check is due, update.json, the updater's
// resting state, the elevated starts' command lines and environment, the helper's frames.
static class UpdateTests
{
    public static void Run()
    {
        T.Group("Updates: versions and GitHub's redirects", Redirects);
        T.Group("Updates: releases/latest", Latest);
        T.Group("Updates: rate limits and when to check", WhenToCheck);
        T.Group("Updates: update.json", Manifest);
        T.Group("Updates: state between checks", Resting);
        T.Group("Install: elevated command lines", CommandLines);
        T.Group("Install: clean environment", CleanEnvironment);
        T.Group("Input helper: frames", Frames);
    }

    static void Redirects()
    {
        T.Check(
            UpdateRules.ParseSemVer("0.10.0") > UpdateRules.ParseSemVer("0.9.9"),
            "0.10.0 is newer than 0.9.9"
        );
        T.Check(UpdateRules.ParseSemVer("v1.2.3") == new Version(1, 2, 3), "v1.2.3 is 1.2.3");
        T.Check(
            UpdateRules.ParseSemVer("1.2.3-beta") is null && UpdateRules.ParseSemVer("1.2") is null,
            "a pre-release or 1.2 is not a version here"
        );
        T.Check(
            UpdateRules.IsNewer("0.2.0", "0.1.9") && !UpdateRules.IsNewer("0.1.0", "0.1.0"),
            "newer only when strictly newer"
        );
        T.Check(
            !UpdateRules.IsNewer(null, "0.1.0") && !UpdateRules.IsNewer("0.2.0", null),
            "nothing known: not newer"
        );

        foreach (
            var (url, ok) in new[]
            {
                (
                    "https://github.com/APatenaude/ControllerNavigation/releases/download/v1.0.0/update.json",
                    true
                ),
                (
                    "https://github.com/apatenaude/controllernavigation/releases/download/v1.0.0/update.json",
                    true
                ),
                (
                    "https://github.com/Someone/ControllerNavigation/releases/download/v1.0.0/update.json",
                    false
                ),
                (
                    "https://github.com/APatenaude/ControllerNavigationX/releases/download/v1.0.0/x",
                    false
                ),
                (
                    "https://release-assets.githubusercontent.com/github-production-release-asset/1",
                    true
                ),
                ("https://another-name.githubusercontent.com/x", true),
                ("https://evilgithubusercontent.com/x", false),
                ("https://x.githubusercontent.com.example.net/x", false),
                ("http://release-assets.githubusercontent.com/x", false),
                ("https://release-assets.githubusercontent.com:8443/x", false),
                ("https://user@release-assets.githubusercontent.com/x", false),
                ("https://example.com/x", false),
            }
        )
            T.Check(
                (UpdateRules.RefusedHop(new Uri(url)) is null) == ok,
                $"{(ok ? "followed" : "refused")}: {url}"
            );
        T.Check(
            UpdateRules
                .RefusedHop(new Uri("https://github.com/Someone/ControllerNavigation/releases/x"))
                ?.Contains("moved") == true,
            "another repository's path on github.com says the repository moved"
        );
    }

    static void Latest()
    {
        T.Equal(
            "v0.2.0",
            UpdateRules.LatestTag(
                new Uri("https://github.com/APatenaude/ControllerNavigation/releases/tag/v0.2.0")
            ),
            "a release: its tag"
        );
        T.Check(
            UpdateRules.LatestTag(
                new Uri("https://github.com/APatenaude/ControllerNavigation/releases")
            )
                is null,
            "no release yet: none"
        );
        foreach (
            var moved in new[]
            {
                "https://github.com/NewOwner/ControllerNavigation/releases/tag/v0.2.0",
                "https://github.com/APatenaude/ControllerNavigation-old/releases/tag/v0.2.0",
                "https://example.com/APatenaude/ControllerNavigation/releases/tag/v0.2.0",
                "https://github.com/APatenaude/ControllerNavigation/releases/tag/v0.2.0%20x",
            }
        )
            T.Check(
                Throws<InvalidOperationException>(() => UpdateRules.LatestTag(new Uri(moved))),
                $"an error, not \"no release\": {moved}"
            );
    }

    static void WhenToCheck()
    {
        T.Check(UpdateRules.IsRateLimit(429, false, null), "429: a rate limit");
        T.Check(
            UpdateRules.IsRateLimit(403, false, "0") && UpdateRules.IsRateLimit(403, true, null),
            "403 with X-RateLimit-Remaining: 0 or a Retry-After: a rate limit"
        );
        T.Check(
            !UpdateRules.IsRateLimit(403, false, null)
                && !UpdateRules.IsRateLimit(403, false, "42"),
            "any other 403: not a rate limit"
        );

        var now = new DateTime(2026, 11, 8, 12, 0, 0, DateTimeKind.Utc);
        T.Check(UpdateRules.CheckDue(now, null, null, 0), "never checked: due");
        T.Check(
            !UpdateRules.CheckDue(now, now.AddHours(-19), now.AddHours(-19), 0),
            "checked 19 h ago: not due"
        );
        T.Check(
            UpdateRules.CheckDue(now, now.AddHours(-21), now.AddHours(-21), 0),
            "checked 21 h ago: due"
        );
        T.Check(
            !UpdateRules.CheckDue(now, now.AddHours(-30), now.AddMinutes(-50), 1),
            "failed 50 min ago: not yet"
        );
        T.Check(
            UpdateRules.CheckDue(now, now.AddHours(-30), now.AddMinutes(-61), 1),
            "failed 61 min ago: again"
        );
        T.Check(
            UpdateRules.CheckDue(now, null, now.AddMinutes(-61), UpdateRules.RetriesAfterFailure),
            $"failed {UpdateRules.RetriesAfterFailure} times in a row: still again after an hour"
        );
        T.Check(
            !UpdateRules.CheckDue(now, null, now.AddHours(-2), UpdateRules.RetriesAfterFailure + 1),
            "failed more often than that: back to the daily rhythm"
        );
        T.Check(
            UpdateRules.CheckDue(now, null, now.AddHours(-21), UpdateRules.RetriesAfterFailure + 1),
            "  (due again 20 h after the last try)"
        );
    }

    static void Manifest()
    {
        var sha = new string('a', 64);
        string Json(string version, string exe, string hash, string size) =>
            $$"""{"version":"{{version}}","exe":"{{exe}}","sha256":"{{hash}}","size":{{size}}}""";

        var ok = UpdateRules.ParseManifest(
            Json("0.2.0", "ControllerNavigation.exe", sha.ToUpperInvariant(), "73400320"),
            "v0.2.0"
        );
        T.Check(
            ok
                is {
                    Tag: "v0.2.0",
                    Version: "0.2.0",
                    Exe: "ControllerNavigation.exe",
                    Size: 73400320,
                }
                && ok.Sha256 == sha,
            "a good update.json, its SHA-256 in lower case"
        );
        foreach (
            var (text, why) in new[]
            {
                (Json("0.2.1", "ControllerNavigation.exe", sha, "1"), "another release's version"),
                (Json("v0.2.0", "ControllerNavigation.exe", sha, "1"), "a version with a v"),
                (Json("0.2", "ControllerNavigation.exe", sha, "1"), "a version that is not x.y.z"),
                (Json("0.2.0", "..\\\\evil.exe", sha, "1"), "an exe name with a path"),
                (Json("0.2.0", "ControllerNavigation.zip", sha, "1"), "not an exe"),
                (Json("0.2.0", "ControllerNavigation.exe", sha[..63], "1"), "a short SHA-256"),
                (Json("0.2.0", "ControllerNavigation.exe", sha, "0"), "size 0"),
                (Json("0.2.0", "ControllerNavigation.exe", sha, "\"12\""), "size as text"),
                (
                    Json("0.2.0", "ControllerNavigation.exe", sha, "999999999999"),
                    "a size past 512 MB"
                ),
                ("[1]", "not an object"),
                ("{", "not JSON"),
            }
        )
            T.Check(
                Throws<InvalidDataException>(() => UpdateRules.ParseManifest(text, "v0.2.0")),
                $"refused: {why}"
            );
    }

    static void Resting()
    {
        T.Equal(
            Updater.NotInstalled,
            UpdateRules.RestingState(false, true, true, false, false),
            "not installed: nothing to update, whatever is out"
        );
        T.Equal(
            Updater.Off,
            UpdateRules.RestingState(true, false, true, false, false),
            "auto-update off: off"
        );
        T.Equal(
            Updater.WaitingIdle,
            UpdateRules.RestingState(true, true, true, false, true),
            "newer: waits for the pad to rest, even when a later check failed"
        );
        T.Equal(
            Updater.Failed,
            UpdateRules.RestingState(true, true, true, true, false),
            "newer, but its install failed: failed (not tried again by itself)"
        );
        T.Equal(
            Updater.Failed,
            UpdateRules.RestingState(true, true, false, false, true),
            "the last check failed: failed"
        );
        T.Equal(
            Updater.UpToDate,
            UpdateRules.RestingState(true, true, false, false, false),
            "nothing newer: up to date"
        );
    }

    static void CommandLines()
    {
        var dir = @"C:\Program Files\ControllerNavigation";
        var target = $@"{dir}\ControllerNavigation.exe";
        var line = Elevation.Trampoline(
            @"C:\Users\me\Downloads\ControllerNavigation.exe",
            ["--install", "--elevated", "--user", "S-1-5-21-1-2-3-1001"],
            dir,
            "abc123",
            copy: true
        );
        T.Check(line is not null, "a trampoline for a download");
        if (line is null)
            return;
        T.Check(
            line.StartsWith("/d /e:on /v:off /s /c \""),
            "cmd: no AutoRun, extensions on, delayed expansion off"
        );
        T.Check(
            line.Contains($"set \"DOTNET_BUNDLE_EXTRACT_BASE_DIR={dir}\\bundle\""),
            ".NET unpacks into Program Files"
        );
        T.Check(
            line.Contains("set \"DOTNET_STARTUP_HOOKS=\"")
                && line.Contains("set \"CORECLR_ENABLE_PROFILING=\"")
                && new[]
                {
                    "DOTNET_GCPath",
                    "COMPlus_GCPath",
                    "DOTNET_GCName",
                    "COMPlus_GCName",
                }.All(v => line.Contains($"set \"{v}=\"")),
            "startup hooks, profilers and a standalone GC cleared"
        );
        T.Check(line.Contains($"set \"TEMP={dir}\\temp\""), "TEMP in Program Files");
        T.Check(
            line.Contains($"move /y \"{target}\" \"{target}.abc123.old\""),
            "a running copy is renamed aside first"
        );
        T.Check(
            line.Contains(
                $"copy /b /y \"C:\\Users\\me\\Downloads\\ControllerNavigation.exe\" \"{target}\" >nul && start \"\" /wait /d \"{dir}\" \"{target}\" --install --elevated --user S-1-5-21-1-2-3-1001"
            ),
            "copied, then the copy started and waited for"
        );
        T.Check(!line.Contains('%'), "no %variable% for cmd to fill in from the user's registry");

        var again = Elevation.Trampoline(
            target,
            ["--uninstall", "--elevated"],
            dir,
            "abc",
            copy: true
        );
        T.Check(
            again is not null
                && !again.Contains("copy /b")
                && again.Contains("--uninstall --elevated"),
            "the installed exe itself: started, not copied"
        );
        T.Check(
            Elevation.Trampoline(@"C:\%TEMP%\x.exe", ["--install"], dir, "abc", copy: true) is null,
            "a % in the path: refused"
        );
        T.Check(
            Elevation.Trampoline(@"C:\x.exe", ["--install", "a&b"], dir, "abc", copy: true) is null,
            "an argument that is not plain: refused"
        );
        T.Check(
            Elevation.Trampoline(@"C:\x.exe", ["--install"], dir, "a.b", copy: true) is null,
            "a suffix that is not plain: refused"
        );

        var script = Elevation.TaskScript(dir, "--input-helper");
        T.Check(script is not null, "a task script");
        if (script is null)
            return;
        var lines = script.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        T.Equal("@echo off", lines[0], "the script echoes nothing");
        T.Equal($"\"{target}\" --input-helper", lines[^1], "the script ends by running the exe");
        T.Check(
            script.Contains($"set \"DOTNET_BUNDLE_EXTRACT_BASE_DIR={dir}\\bundle\""),
            "the script points .NET at Program Files"
        );
        T.Check(
            Elevation.TaskScript(@"C:\Program Files\Contr%ol", "--input-helper") is null,
            "a % in the folder: no script"
        );

        T.Equal(
            "a \"b c\" \"d\\\"e\" f\\ \"g h\\\\\"",
            Elevation.CommandLine(["a", "b c", "d\"e", "f\\", "g h\\"]),
            "arguments quoted as Windows splits them"
        );
    }

    static void CleanEnvironment()
    {
        var machine = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Path"] = @"%SystemRoot%\system32;%USERPROFILE%\bin;%Extra%",
            ["Extra"] = @"C:\Tools",
            ["OS"] = "Windows_NT",
            ["DOTNET_STARTUP_HOOKS"] = @"C:\hook.dll",
            ["COR_ENABLE_PROFILING"] = "1",
        };
        var env = Elevation.CleanEnvironment(machine);
        var win = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        T.Equal(
            $@"{win}\system32;%USERPROFILE%\bin;C:\Tools",
            env["Path"],
            "Path from the machine's, with Windows' folders and the machine's own variables, never a user's"
        );
        T.Check(
            !env.ContainsKey("DOTNET_STARTUP_HOOKS") && !env.ContainsKey("COR_ENABLE_PROFILING"),
            ".NET switches dropped, the machine's included"
        );
        T.Equal(Elevation.TempDir, env["TEMP"], "TEMP in Program Files");
        T.Equal(win, env["SystemRoot"], "SystemRoot from Windows itself");
        T.Check(env.ContainsKey("OS"), "the machine's other variables kept");
    }

    static void Frames()
    {
        byte[] Records(params uint[] types)
        {
            var bytes = new byte[types.Length * InputFrame.RecordSize];
            for (var i = 0; i < types.Length; i++)
                BitConverter.TryWriteBytes(bytes.AsSpan(i * InputFrame.RecordSize), types[i]);
            return bytes;
        }

        var keys = Records(1, 1, 0);
        var stream = new MemoryStream();
        stream.Write(InputFrame.Encode(keys));
        stream.Write(InputFrame.Encode(Records(0)));
        stream.Position = 0;
        T.Check(
            InputFrame.Read(stream) is { } first && first.SequenceEqual(keys),
            "a frame read back whole"
        );
        T.Check(
            InputFrame.Read(stream) is { Length: InputFrame.RecordSize },
            "the next frame after it"
        );
        T.Check(InputFrame.Read(stream) is null, "the end of the stream: none");

        T.Check(!InputFrame.Valid(Records(2)), "hardware input refused");
        T.Check(!InputFrame.Valid(keys.AsSpan(0, 39)), "part of a record refused");
        T.Check(
            InputFrame.Valid(Records(new uint[InputFrame.MaxRecords]))
                && !InputFrame.Valid(Records(new uint[InputFrame.MaxRecords + 1])),
            $"{InputFrame.MaxRecords} records at most"
        );
        var tooLong = new MemoryStream(
            BitConverter.GetBytes(InputFrame.MaxRecords * InputFrame.RecordSize + 1)
        );
        T.Check(InputFrame.Read(tooLong) is null, "a length past the most: the connection drops");
    }

    static bool Throws<TException>(Action action)
        where TException : Exception
    {
        try
        {
            action();
            return false;
        }
        catch (TException)
        {
            return true;
        }
    }
}
