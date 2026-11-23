using System.Diagnostics;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;

namespace BehaviorLock.LoadTest;

/// <summary>
/// Measures the guard's own work per call on one thread, without HTTP or a database.
/// These are rough numbers from a plain loop, good enough to see where the time goes.
/// </summary>
public static class CallCosts
{
    private const string EfQuery =
        "SELECT \"p\".\"Id\", \"p\".\"Name\", \"p\".\"Price\"\nFROM \"Products\" AS \"p\"\nWHERE \"p\".\"Id\" = @p\nLIMIT 1";

    private const string RawQuery = "SELECT * FROM Products WHERE Name LIKE '%Lap%'";

    public static void Print()
    {
        var normalizer = new SqlNormalizer();

        var access = ResourceAccess.Sql(normalizer.Normalize(EfQuery)[0]);
        var profile = new EndpointProfile();
        profile.Allow("GET /products/{id:int}", access);
        var accessor = new GuardContextAccessor { Current = new GuardContext("GET /products/{id:int}") };
        var guard = new AccessGuard(
            accessor, profile, [], new GlobalMode(GuardMode.Enforce), Options.Create(new BehaviorLockOptions()));

        Console.WriteLine("## Cost per call (one thread, no HTTP, no database)");
        Console.WriteLine();
        Console.WriteLine("| Operation | Time | Allocated |");
        Console.WriteLine("|---|---:|---:|");
        Row("Normalize an EF Core query (95 chars)", () => normalizer.Normalize(EfQuery));
        Row("Normalize a raw query with a literal (46 chars)", () => normalizer.Normalize(RawQuery));
        Row("Check one allowed access in Enforce mode", () => guard.Check(access));

        // The whole work for one command before it runs: the shape of the text, then the check.
        using var command = new SqliteCommand(EfQuery);
        var interceptor = new GuardDbCommandInterceptor(guard, normalizer);
        Row("Check one EF Core command before it runs", () => interceptor.ReaderExecuting(command, null!, default));
        Console.WriteLine();
    }

    private static void Row(string name, Action action)
    {
        const int iterations = 400_000;

        for (var i = 0; i < iterations; i++)
        {
            action();
        }

        var bestNs = double.MaxValue;
        var bytes = 0L;
        for (var run = 0; run < 5; run++)
        {
            var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
            var start = Stopwatch.GetTimestamp();
            for (var i = 0; i < iterations; i++)
            {
                action();
            }

            bestNs = Math.Min(bestNs, Stopwatch.GetElapsedTime(start).TotalNanoseconds / iterations);
            bytes = (GC.GetAllocatedBytesForCurrentThread() - allocatedBefore) / iterations;
        }

        Console.WriteLine($"| {name} | {bestNs:F0} ns | {bytes} B |");
    }
}
