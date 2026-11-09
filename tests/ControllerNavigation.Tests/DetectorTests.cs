namespace ControllerNavigation.Tests;

static class DetectorTests
{
    public static void Run()
    {
        T.Group(
            "Detector: folders, Windows' list, switches",
            () =>
            {
                var s = new Settings();
                using var d = new GameDetector(() => s);
                var forza = d.Check(
                    1,
                    1,
                    @"D:\XboxGames\Forza Horizon 6\Content\forzahorizon6.exe"
                );
                T.Check(
                    forza is { LeftAlone: true } && forza.Value.Reason.Contains("XboxGames"),
                    $"an exe under XboxGames is a game ({forza?.Reason})"
                );
                T.Check(
                    d.Check(1, 1, @"d:\steamlibrary\STEAMAPPS\common\Valheim\valheim.exe")
                        is { LeftAlone: true },
                    "folders match ignoring case"
                );

                s = new Settings
                {
                    Detection = new Detection
                    {
                        ControllerApps = false,
                        KnownGames = false,
                        GameFolders = new(),
                    },
                };
                T.Check(
                    d.Check(1, 1, @"D:\XboxGames\Forza Horizon 6\Content\forzahorizon6.exe")
                        is { LeftAlone: false, Reason: "automatic" },
                    "everything switched off: every app is automatic"
                );
            }
        );

        T.Group(
            "Detector: looking into a process",
            () =>
            {
                var s = new Settings();
                using var d = new GameDetector(() => s);
                var me = System.Diagnostics.Process.GetCurrentProcess();
                var started = me.StartTime.ToFileTimeUtc();
                var first = d.Check((uint)me.Id, started, me.MainModule!.FileName);
                T.Check(
                    first is null,
                    "not known on the first ask (the look is queued, nothing waits)"
                );
                Verdict? later = null;
                for (var i = 0; i < 100 && later is null; i++)
                {
                    Thread.Sleep(50);
                    later = d.Check((uint)me.Id, started, me.MainModule!.FileName);
                }
                T.Check(
                    later is { LeftAlone: false },
                    $"this test process holds no controller: automatic ({later?.Reason ?? "no answer in 5 s"})"
                );
            }
        );
    }
}
