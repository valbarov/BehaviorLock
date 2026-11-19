using System.Runtime;
using Microsoft.Data.Sqlite;

namespace BehaviorLock.LoadTest;

/// <summary>
/// Measures what BehaviorLock costs. It runs the sample API four ways (without the package, and in Off,
/// Audit and Enforce mode) on real Kestrel ports and sends the same load to each one.
///
/// Usage: dotnet run -c Release --project perf/BehaviorLock.LoadTest -- [--duration 4] [--rounds 4] [--connections 16] [--filter text]
///
/// Use a multiple of 4 for the rounds. Then every variant runs in every position of a round equally
/// often, and a machine that slows down during a round does not favor one variant.
/// </summary>
public static class LoadTest
{
    public static async Task Main(string[] args)
    {
        var duration = TimeSpan.FromSeconds(Argument(args, "--duration", 4));
        var rounds = (int)Argument(args, "--rounds", 4);
        var connections = (int)Argument(args, "--connections", 16);
        var filterIndex = Array.IndexOf(args, "--filter");
        var filter = filterIndex >= 0 && filterIndex + 1 < args.Length ? args[filterIndex + 1] : "";
        var scenarios = Scenario.All.Where(scenario => scenario.Name.Contains(filter, StringComparison.OrdinalIgnoreCase)).ToList();
        var processWarmup = TimeSpan.FromSeconds(Math.Min(3, duration.TotalSeconds));
        var warmup = TimeSpan.FromSeconds(0.5);

        // SQLite calls block their thread. Without enough threads from the start, the thread pool grows
        // during the test and the throughput drifts upward from round to round.
        var minThreads = Math.Max(64, connections * 4);
        ThreadPool.SetMinThreads(minThreads, minThreads);

        var profilePath = Path.Combine(FindRepositoryRoot(), "samples", "SampleApi", "behavior.lock.json");
        var workDirectory = Directory.CreateTempSubdirectory("behaviorlock-load-").FullName;

        Console.WriteLine("# BehaviorLock load test");
        Console.WriteLine();
        Console.WriteLine($"- Machine: {Environment.ProcessorCount} logical cores, {System.Runtime.InteropServices.RuntimeInformation.OSDescription}");
        Console.WriteLine($"- Runtime: .NET {Environment.Version}, server GC: {GCSettings.IsServerGC}");
        Console.WriteLine($"- Load: {connections} connections, {rounds} rounds of {duration.TotalSeconds:0.#} s per scenario and variant, after {warmup.TotalSeconds:0.#} s warm-up");
        Console.WriteLine("- Database: SQLite file. The load client runs in the same process as the servers.");
#if DEBUG
        Console.WriteLine("- WARNING: this is a Debug build. Use `-c Release` for real numbers.");
#endif
        Console.WriteLine();

        CallCosts.Print();

        var hosts = new List<ApiHost>();
        try
        {
            foreach (var variant in Enum.GetValues<Variant>())
            {
                var databasePath = Path.Combine(workDirectory, $"{variant}.db");
                hosts.Add(await ApiHost.StartAsync(variant, profilePath, databasePath, connections));
            }

            // The first requests pay for JIT compilation and EF Core query caches. All servers share one
            // process, so without this step the variant that runs first would look slower than the others.
            foreach (var scenario in scenarios)
            {
                foreach (var host in hosts)
                {
                    await LoadRunner.RunAsync(host, scenario, processWarmup, Math.Min(connections, scenario.MaxConnections ?? connections));
                }
            }

            Console.WriteLine("## Load test");
            Console.WriteLine();
            Console.WriteLine("Requests per second is the median of the rounds. \"vs NoPackage\" compares each run with the baseline");
            Console.WriteLine("run of the same round and gives the median of the rounds. When the range of the rounds reaches");
            Console.WriteLine("well across 0% on both sides, the test did not find a clear difference.");
            Console.WriteLine();

            foreach (var scenario in scenarios)
            {
                await MeasureAsync(scenario, hosts, rounds, warmup, duration, connections);
            }
        }
        finally
        {
            foreach (var host in hosts)
            {
                await host.DisposeAsync();
            }

            SqliteConnection.ClearAllPools();
            Directory.Delete(workDirectory, recursive: true);
        }
    }

    private static async Task MeasureAsync(
        Scenario scenario, List<ApiHost> hosts, int rounds, TimeSpan warmup, TimeSpan duration, int connections)
    {
        connections = Math.Min(connections, scenario.MaxConnections ?? connections);
        var runs = hosts.ToDictionary(host => host.Variant, _ => new List<RunResult>());

        // The variants take turns in every round and the starting variant changes,
        // so that slow drift of the machine hits all of them in the same way.
        for (var round = 0; round < rounds; round++)
        {
            for (var turn = 0; turn < hosts.Count; turn++)
            {
                var host = hosts[(round + turn) % hosts.Count];
                await LoadRunner.RunAsync(host, scenario, warmup, connections);
                runs[host.Variant].Add(await LoadRunner.RunAsync(host, scenario, duration, connections));
            }
        }

        Console.WriteLine($"### {scenario.Name}");
        Console.WriteLine();
        Console.WriteLine("| Variant | Requests/s | vs NoPackage | Range of the rounds | p50 ms | p95 ms | p99 ms | Wrong status |");
        Console.WriteLine("|---|---:|---:|---:|---:|---:|---:|---:|");

        var baseline = runs[Variant.NoPackage];
        foreach (var variant in Enum.GetValues<Variant>())
        {
            var results = runs[variant];
            var latencies = results.SelectMany(result => result.LatencyTicks).ToArray();
            Array.Sort(latencies);

            // Compare each run with the baseline run of the same round. The machine changes speed over
            // minutes (heat, other programs), and runs of one round are only seconds apart.
            // The median ignores a single round that was disturbed by something else on the machine.
            var ratios = results.Select((result, round) => result.RequestsPerSecond / baseline[round].RequestsPerSecond).ToArray();
            var comparison = variant == Variant.NoPackage
                ? "baseline | "
                : $"{Percent(Median(ratios))} | {Percent(ratios.Min())} to {Percent(ratios.Max())}";

            Console.WriteLine(
                $"| {variant} | {Median(results.Select(result => result.RequestsPerSecond)):N0} | {comparison} " +
                $"| {LoadRunner.PercentileMs(latencies, 50):0.00} | {LoadRunner.PercentileMs(latencies, 95):0.00} | {LoadRunner.PercentileMs(latencies, 99):0.00} " +
                $"| {results.Sum(result => result.WrongStatus)} |");
        }

        Console.WriteLine();
    }

    private static string Percent(double ratio) => $"{(ratio - 1) * 100:+0.0;-0.0}%";

    private static double Median(IEnumerable<double> values)
    {
        var sorted = values.Order().ToArray();
        var middle = sorted.Length / 2;
        return sorted.Length % 2 == 1 ? sorted[middle] : (sorted[middle - 1] + sorted[middle]) / 2;
    }

    private static double Argument(string[] args, string name, double defaultValue)
    {
        var index = Array.IndexOf(args, name);
        return index >= 0 && index + 1 < args.Length
            ? double.Parse(args[index + 1], System.Globalization.CultureInfo.InvariantCulture)
            : defaultValue;
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "BehaviorLock.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("BehaviorLock.slnx was not found.");
    }
}
