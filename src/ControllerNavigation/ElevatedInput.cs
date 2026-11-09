using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Principal;

namespace ControllerNavigation;

/// <summary>
/// Input for windows that run with administrator rights (Device Manager, an installer, a command
/// prompt), lifted from HTPC: Windows does not deliver SendInput from a standard process to them,
/// and the tray app never runs elevated. While such a window is in front (Input.ToElevated) Input
/// hands its records to TrySend (Input.ElevatedSink), which passes them through a pipe to a small
/// elevated copy of this exe, the input helper (InputHelper), that sends them. The first records
/// start the helper (the \ControllerNavigation\Input task, made at install) and go out the usual way
/// meanwhile. The helper is let go after a few minutes with nothing to send, and when this is
/// disposed (it then ends). It is trusted by what it is, not by its name: the pipe's other end must
/// be the installed exe and the pipe made by an elevated process (a standard program that took the
/// name first could not be). Anything that runs as this user and can run the installed exe can
/// still use the helper: as the app, it is the same program. A copy that is not installed (a dev
/// build) has no helper.
/// </summary>
sealed class ElevatedInput : IDisposable
{
    const int IdleMinutes = 3;
    readonly bool installed = AppInfo.RunningInstalled;
    readonly object gate = new();
    readonly System.Threading.Timer idleCheck;
    NamedPipeClientStream? pipe;
    long lastUse; // tick count (the clock can jump)
    long nextStart; // no new start of the helper before this tick count
    bool connecting,
        warnedMissing,
        warnedNotInstalled,
        disposed;

    public ElevatedInput()
    {
        idleCheck = new System.Threading.Timer(_ => DropIfIdle(), null, 30_000, 30_000);
    }

    /// <summary>
    /// The records (whole INPUT records, as Input makes them) went through the helper: true. False
    /// when it is not connected (it is started then, in the background) or there is none: the caller
    /// sends them itself. Called on the input thread.
    /// </summary>
    public bool TrySend(byte[] records)
    {
        if (!installed)
        {
            if (!warnedNotInstalled)
            {
                warnedNotInstalled = true;
                Log.Info(
                    "Input helper: this copy is not installed, so windows running as administrator get no input"
                );
            }
            return false;
        }
        if (records.Length == 0 || records.Length % InputFrame.RecordSize != 0)
            return false;
        lock (gate)
        {
            if (disposed)
                return false;
            lastUse = Environment.TickCount64;
            if (pipe is { IsConnected: true })
            {
                try
                {
                    // The helper drops a connection whose frame is too long: a long text goes in parts.
                    const int most = InputFrame.MaxRecords * InputFrame.RecordSize;
                    for (var at = 0; at < records.Length; at += most)
                        pipe.Write(
                            InputFrame.Encode(
                                records.AsSpan(at, Math.Min(most, records.Length - at))
                            )
                        );
                    return true;
                }
                catch (Exception e) when (e is IOException or ObjectDisposedException)
                {
                    Drop("the pipe broke");
                }
            }
            if (connecting || Environment.TickCount64 < nextStart)
                return false;
            connecting = true;
            nextStart = Environment.TickCount64 + 5000;
        }
        _ = Task.Run(Connect);
        return false;
    }

    public void Dispose()
    {
        lock (gate)
        {
            if (disposed)
                return;
            disposed = true;
            Drop("the app is closing");
        }
        idleCheck.Dispose();
    }

    void DropIfIdle()
    {
        lock (gate)
            if (pipe is not null && Environment.TickCount64 - lastUse > IdleMinutes * 60_000L)
                Drop("no administrator window for a while");
    }

    void Drop(string why)
    {
        if (pipe is null)
            return;
        pipe.Dispose();
        pipe = null;
        Log.Info($"Input helper: let go ({why})");
    }

    void Connect()
    {
        NamedPipeClientStream? client = null;
        try
        {
            if (!StartHelperTask())
            {
                lock (gate)
                    nextStart = Environment.TickCount64 + 60_000;
                return;
            }
            client = new NamedPipeClientStream(
                ".",
                InputHelper.PipeName,
                PipeDirection.Out,
                PipeOptions.None,
                TokenImpersonationLevel.Identification
            );
            var clock = Stopwatch.StartNew();
            while (true)
            {
                try
                {
                    client.Connect(500);
                    break;
                }
                catch (TimeoutException) when (clock.Elapsed < TimeSpan.FromSeconds(8)) { }
            }
            // The other end must be the installed exe, elevated: a program that took the pipe's
            // name first would otherwise be handed what is typed (a password).
            if (
                !GetNamedPipeServerProcessId(
                    client.SafePipeHandle.DangerousGetHandle(),
                    out var server
                ) || !InputHelper.IsTheHelper((int)server, client)
            )
            {
                Log.Warn(
                    "Input helper: the pipe's other end is not the installed app, elevated; not used"
                );
                lock (gate)
                    nextStart = Environment.TickCount64 + 60_000;
                return;
            }
            lock (gate)
            {
                if (disposed)
                    return;
                pipe?.Dispose();
                pipe = client;
                client = null;
                lastUse = Environment.TickCount64;
            }
            Log.Info(
                $"Input helper: connected after {clock.ElapsedMilliseconds} ms (an administrator window is in front)"
            );
        }
        catch (Exception e)
        {
            // A helper that refuses to run (a standard account's task) would otherwise be started every few seconds.
            lock (gate)
                nextStart = Environment.TickCount64 + 60_000;
            Log.Warn($"Input helper: could not connect: {e.Message}");
        }
        finally
        {
            client?.Dispose();
            lock (gate)
                connecting = false;
        }
    }

    // The \ControllerNavigation\Input task: elevated, as this user, made at install; the user may
    // run it, not change it.
    bool StartHelperTask()
    {
        try
        {
            AppTasks.Run(AppTasks.Input);
            return true;
        }
        catch (Exception e)
        {
            if (!warnedMissing)
            {
                warnedMissing = true;
                Log.Warn(
                    $"Input helper: could not start the \\ControllerNavigation\\Input task ({e.Message}); run the installer again"
                );
            }
            return false;
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool GetNamedPipeServerProcessId(IntPtr pipe, out uint processId);
}
