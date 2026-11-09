using System.Diagnostics;
using System.Reflection;

namespace ControllerNavigation.Tests;

/// <summary>
/// Runs every static class named *Tests that has a public static void Run(): each calls T.Group
/// once per area. Exit code 0 when all checks passed (and some ran).
/// </summary>
static class Program
{
    static int Main(string[] args)
    {
        T.Start(args);
        var suites = typeof(Program)
            .Assembly.GetTypes()
            .Where(t =>
                t.IsAbstract && t.IsSealed && t.Name.EndsWith("Tests", StringComparison.Ordinal)
            )
            .Select(t =>
                t.GetMethod("Run", BindingFlags.Public | BindingFlags.Static, Type.EmptyTypes)
            )
            .Where(m => m is not null)
            .OrderBy(m => m!.DeclaringType!.Name, StringComparer.Ordinal);
        foreach (var run in suites)
            run!.Invoke(null, null);
        return T.Summary();
    }
}

/// <summary>
/// The checks' bookkeeping (as HTPC's launcher tests). Each area is a group, run in a try of its
/// own (a throw is one FAIL that names the group, and the next group still runs) and timed. -v
/// adds each check as it passes; any other argument runs only the groups whose name has it.
/// </summary>
static class T
{
    public static bool Verbose { get; private set; }
    static string[] only = [];
    static int passed,
        failed;
    static readonly List<string> failures = new();

    public static void Start(string[] args)
    {
        Verbose = args.Contains("-v");
        only = args.Where(a => a != "-v").ToArray();
    }

    public static void Check(bool ok, string what)
    {
        if (ok)
        {
            Interlocked.Increment(ref passed);
            if (Verbose)
                Console.WriteLine("  ok   " + what);
            return;
        }
        Interlocked.Increment(ref failed);
        if (Verbose)
            Console.WriteLine("  FAIL " + what);
        else
            lock (failures)
                failures.Add("  FAIL " + what);
    }

    /// <summary>Two values equal, the check's text showing both when they are not.</summary>
    public static void Equal<TValue>(TValue expected, TValue actual, string what) =>
        Check(
            EqualityComparer<TValue>.Default.Equals(expected, actual),
            EqualityComparer<TValue>.Default.Equals(expected, actual)
                ? what
                : $"{what}: expected {expected}, got {actual}"
        );

    public static void Group(string name, Action body)
    {
        if (only.Length > 0 && !only.Any(o => name.Contains(o, StringComparison.OrdinalIgnoreCase)))
            return;
        if (Verbose)
            Console.WriteLine($"== {name}");
        var clock = Stopwatch.StartNew();
        try
        {
            body();
        }
        catch (Exception e)
        {
            var at = e
                .StackTrace?.Split('\n')
                .Select(l => l.Trim())
                .FirstOrDefault(l => l.Contains("Tests"));
            Check(false, $"{name}: {e.GetType().Name}: {e.Message}{(at is null ? "" : " " + at)}");
        }
        Console.WriteLine(
            Verbose
                ? $"   ({clock.ElapsedMilliseconds} ms)"
                : $"== {name} ({clock.ElapsedMilliseconds} ms)"
        );
        lock (failures)
        {
            foreach (var f in failures)
                Console.WriteLine(f);
            failures.Clear();
        }
    }

    public static void GroupAsync(string name, Func<Task> body) =>
        Group(name, () => body().GetAwaiter().GetResult());

    public static int Summary()
    {
        Console.WriteLine($"{passed} passed, {failed} failed");
        if (passed + failed == 0)
            Console.WriteLine(
                $"FAIL no check ran: no group's name has {string.Join(" or ", only.Select(o => $"'{o}'"))}"
            );
        return failed == 0 && passed > 0 ? 0 : 1;
    }
}
