using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;

namespace ControllerNavigation;

/// <summary>
/// The input helper (--input-helper), lifted from HTPC: this exe, elevated, with no window, started
/// by the \ControllerNavigation\Input task when the tray app finds a window in front that runs with
/// administrator rights (ElevatedInput). It listens on a pipe, takes keyboard and mouse records
/// from the installed exe alone and sends them with SendInput, which an elevated process may do into
/// elevated windows. It runs only as the installed exe (Program Files: administrator-write, so
/// nothing the user can replace; Elevation.Enter), takes one connection, serves it and ends with
/// it, and ends by itself when nobody connects. It logs in Program Files' logs folder, not anywhere
/// the user can write.
/// </summary>
static class InputHelper
{
    /// <summary>One helper per signed-in session.</summary>
    public static string PipeName { get; } =
        $"ControllerNavigationInput-{Process.GetCurrentProcess().SessionId}";

    /// <summary>
    /// Whether the server end of the pipe is the helper: the installed exe, and the pipe made by an
    /// elevated process (its owner is Administrators, as an elevated token's default owner is; a
    /// standard process that took the name first owns it as itself).
    /// </summary>
    public static bool IsTheHelper(int processId, NamedPipeClientStream pipe)
    {
        if (!Elevation.SameFile(RunningCopies.ProcessInfo(processId).Path, AppInfo.InstalledExe))
            return false;
        var owner = pipe.GetAccessControl().GetOwner(typeof(SecurityIdentifier));
        return owner is SecurityIdentifier sid
            && sid.IsWellKnown(WellKnownSidType.BuiltinAdministratorsSid);
    }

    /// <summary>The helper's whole run; its exit code (0 served or nobody came, 1 refused).</summary>
    public static int Run()
    {
        try
        {
            return Serve();
        }
        catch (Exception e)
        {
            Log.Error("Input helper: failed", e);
            return 1;
        }
        finally
        {
            Log.Flush();
        }
    }

    static int Serve()
    {
        if (!Elevation.Enter("Input helper", "input-helper.log"))
            return 1;
        var user = WindowsIdentity.GetCurrent().User!;
        var security = new PipeSecurity();
        security.AddAccessRule(
            new PipeAccessRule(
                user,
                PipeAccessRights.Write
                    | PipeAccessRights.Synchronize
                    | PipeAccessRights.ReadPermissions
                    | (PipeAccessRights)
                        0x80 /* FILE_READ_ATTRIBUTES: opening it asks for it */
                ,
                AccessControlType.Allow
            )
        );
        security.AddAccessRule(
            new PipeAccessRule(
                new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
                PipeAccessRights.FullControl,
                AccessControlType.Allow
            )
        );
        security.AddAccessRule(
            new PipeAccessRule(
                new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
                PipeAccessRights.FullControl,
                AccessControlType.Allow
            )
        );
        security.AddAccessRule(
            new PipeAccessRule(
                new SecurityIdentifier(WellKnownSidType.NetworkSid, null),
                PipeAccessRights.FullControl,
                AccessControlType.Deny
            )
        );
        NamedPipeServerStream server;
        try
        {
            server = NamedPipeServerStreamAcl.Create(
                PipeName,
                PipeDirection.In,
                1,
                PipeTransmissionMode.Byte,
                PipeOptions.FirstPipeInstance | PipeOptions.Asynchronous,
                0,
                0,
                security
            );
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Log.Info("Input helper: another one runs; ending");
            return 0;
        }
        using (server)
        {
            Log.Info("Input helper: listening");
            using var waiting = new CancellationTokenSource(TimeSpan.FromMinutes(1));
            try
            {
                server.WaitForConnectionAsync(waiting.Token).GetAwaiter().GetResult();
            }
            catch (OperationCanceledException)
            {
                Log.Info("Input helper: nobody connected; ending");
                return 0;
            }
            if (
                !GetNamedPipeClientProcessId(
                    server.SafePipeHandle.DangerousGetHandle(),
                    out var client
                )
                || !Elevation.SameFile(
                    RunningCopies.ProcessInfo((int)client).Path,
                    AppInfo.InstalledExe
                )
            )
            {
                Log.Warn(
                    "Input helper: a connection from something other than the installed app; ending"
                );
                return 1;
            }
            Log.Info($"Input helper: serving the app (pid {client})");
            var sent = 0;
            while (InputFrame.Read(server) is { } records)
            {
                Input.SendRecords(records);
                sent++;
            }
            Log.Info($"Input helper: the app is gone; ending ({sent} frames sent)");
        }
        return 0;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool GetNamedPipeClientProcessId(IntPtr pipe, out uint processId);
}
