using System.Diagnostics;

namespace BehaviorLock.LoadTest;

/// <summary>The result of one timed run of one scenario against one variant.</summary>
public sealed record RunResult(double RequestsPerSecond, long[] LatencyTicks, int WrongStatus);

public static class LoadRunner
{
    /// <summary>
    /// Sends the scenario from <paramref name="connections"/> parallel loops for the given time.
    /// Each loop sends its next request as soon as the answer arrives (closed loop).
    /// </summary>
    public static async Task<RunResult> RunAsync(ApiHost host, Scenario scenario, TimeSpan duration, int connections)
    {
        var expected = scenario.ExpectedStatus(host.Variant);
        var start = Stopwatch.GetTimestamp();
        var end = start + (long)(duration.TotalSeconds * Stopwatch.Frequency);

        var loops = Enumerable.Range(0, connections).Select(_ => Task.Run(async () =>
        {
            var latencies = new List<long>(capacity: 16_384);
            var wrongStatus = 0;

            while (Stopwatch.GetTimestamp() < end)
            {
                using var request = scenario.CreateRequest();
                var sent = Stopwatch.GetTimestamp();
                using var response = await host.Client.SendAsync(request);
                latencies.Add(Stopwatch.GetTimestamp() - sent);

                if ((int)response.StatusCode != expected)
                {
                    wrongStatus++;
                }
            }

            return (latencies, wrongStatus);
        })).ToArray();

        var results = await Task.WhenAll(loops);
        var elapsed = Stopwatch.GetElapsedTime(start);

        var all = results.SelectMany(result => result.latencies).ToArray();
        return new RunResult(all.Length / elapsed.TotalSeconds, all, results.Sum(result => result.wrongStatus));
    }

    /// <summary>Returns a latency percentile in milliseconds. The ticks must be sorted.</summary>
    public static double PercentileMs(long[] sortedTicks, double percentile)
    {
        if (sortedTicks.Length == 0)
        {
            return double.NaN;
        }

        var index = (int)Math.Ceiling(percentile / 100 * sortedTicks.Length) - 1;
        return sortedTicks[Math.Clamp(index, 0, sortedTicks.Length - 1)] * 1000.0 / Stopwatch.Frequency;
    }
}
