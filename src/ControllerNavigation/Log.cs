using System.Collections.Concurrent;

namespace ControllerNavigation;

/// <summary>
/// Plain text log in %LOCALAPPDATA%\ControllerNavigation\logs\app.log (the elevated parts write
/// install.log, update.log and input-helper.log in Program Files' own logs folder, see Installer). Lines are queued and written by a
/// background thread, so logging never blocks the caller (the input thread logs). Past 5 MB the
/// file becomes app.old.log and starts afresh.
/// </summary>
static class Log
{
    const long MaxSize = 5 * 1024 * 1024;
    static string? filePath;
    static readonly BlockingCollection<string> Queue = new();
    static long queued,
        done;

    static Log()
    {
        new Thread(() =>
        {
            long size = -1; // bytes in the file as this thread knows it; -1: read it again
            foreach (var line in Queue.GetConsumingEnumerable())
            {
                try
                {
                    var path = filePath ??= DefaultPath();
                    if (path is null)
                        continue;
                    if (size < 0)
                        size = File.Exists(path) ? new FileInfo(path).Length : 0;
                    if (size > MaxSize)
                        size = Roll(path);
                    File.AppendAllText(path, line);
                    size += System.Text.Encoding.UTF8.GetByteCount(line);
                }
                catch (Exception)
                {
                    size = -1;
                }
                finally
                {
                    Interlocked.Increment(ref done);
                }
            }
        })
        {
            IsBackground = true,
            Name = "Log",
            Priority = ThreadPriority.BelowNormal,
        }.Start();
    }

    /// <summary>
    /// Writes to this file instead of the user's log (the elevated helpers: a folder only
    /// administrators can write, never one the user could plant a link in). Set before the first line.
    /// </summary>
    public static void UseFile(string? path) => filePath = path;

    /// <summary>Waits (2 s at most) until the lines logged so far are written: before an early exit.</summary>
    public static void Flush()
    {
        var until = Environment.TickCount64 + 2000;
        while (
            Interlocked.Read(ref done) < Interlocked.Read(ref queued)
            && Environment.TickCount64 < until
        )
            Thread.Sleep(10);
    }

    static string? DefaultPath()
    {
        try
        {
            var dir = Path.Combine(AppInfo.DataDir, "logs");
            Directory.CreateDirectory(dir);
            return Path.Combine(dir, "app.log");
        }
        catch (Exception)
        {
            return Path.Combine(Path.GetTempPath(), "ControllerNavigation.log");
        }
    }

    // A file that cannot be moved (open elsewhere) is copied and emptied instead.
    static long Roll(string file)
    {
        var length = new FileInfo(file).Length;
        if (length <= MaxSize)
            return length;
        var old = Path.ChangeExtension(file, ".old.log");
        try
        {
            File.Move(file, old, overwrite: true);
        }
        catch (Exception)
        {
            try
            {
                File.Copy(file, old, overwrite: true);
                File.WriteAllText(file, "");
            }
            catch (Exception) { }
        }
        return 0;
    }

    public static void Info(string message) => Write("INFO", message);

    public static void Warn(string message) => Write("WARN", message);

    public static void Error(string message, Exception? e = null) =>
        Write("ERROR", e is null ? message : $"{message}: {e}");

    static void Write(string level, string message)
    {
        Interlocked.Increment(ref queued);
        Queue.Add(
            $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {level, -5} {message}{Environment.NewLine}"
        );
    }
}
