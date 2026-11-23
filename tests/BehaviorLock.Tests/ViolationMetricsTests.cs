using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using System.Net;
using Microsoft.Extensions.DependencyInjection;

namespace BehaviorLock.Tests;

/// <summary>
/// The guard counts each violation in the counter <c>behaviorlock.violations</c> of the meter
/// <c>BehaviorLock</c>, with the endpoint, the kind of the access and whether it was blocked.
/// See <see cref="ModeTestApi"/>.
/// </summary>
public sealed class ViolationMetricsTests
{
    [Fact]
    public async Task Audit_mode_counts_a_violation_that_was_not_blocked()
    {
        await using var api = await ModeTestApi.StartAsync(GuardMode.Audit);
        using var counted = new CountedViolations(api.Services);

        await Send(api, "/default", HttpStatusCode.OK);

        Assert.Equal([Tags("GET /default", "sql", blocked: false)], counted.Items);
    }

    [Fact]
    public async Task Enforce_mode_counts_a_violation_that_was_blocked()
    {
        await using var api = await ModeTestApi.StartAsync(GuardMode.Enforce);
        using var counted = new CountedViolations(api.Services);

        await Send(api, "/default", HttpStatusCode.Forbidden);

        Assert.Equal([Tags("GET /default", "sql", blocked: true)], counted.Items);
    }

    [Fact]
    public async Task Each_violation_counts_one()
    {
        await using var api = await ModeTestApi.StartAsync(GuardMode.Audit);
        using var counted = new CountedViolations(api.Services);

        await Send(api, "/default", HttpStatusCode.OK);
        await Send(api, "/default", HttpStatusCode.OK);
        await Send(api, "/enforce", HttpStatusCode.Forbidden);

        Assert.Equal(
            [Tags("GET /default", "sql", blocked: false), Tags("GET /default", "sql", blocked: false), Tags("GET /enforce", "sql", blocked: true)],
            counted.Items);
    }

    [Theory]
    [InlineData(GuardMode.Off, "/default")]
    [InlineData(GuardMode.Learn, "/default")]
    [InlineData(GuardMode.Enforce, "/off")]
    public async Task A_request_without_a_violation_counts_nothing(GuardMode mode, string path)
    {
        await using var api = await ModeTestApi.StartAsync(mode);
        using var counted = new CountedViolations(api.Services);

        await Send(api, path, HttpStatusCode.OK);

        Assert.Empty(counted.Items);
    }

    [Fact]
    public async Task The_counter_has_a_unit_and_a_description()
    {
        await using var api = await ModeTestApi.StartAsync(GuardMode.Audit);
        using var counted = new CountedViolations(api.Services);

        await Send(api, "/default", HttpStatusCode.OK);

        var counter = Assert.Single(counted.Instruments);
        Assert.Equal("behaviorlock.violations", counter.Name);
        Assert.Equal("{violation}", counter.Unit);
        Assert.False(string.IsNullOrEmpty(counter.Description));
    }

    private static async Task Send(ModeTestApi api, string path, HttpStatusCode expected)
    {
        using var response = await api.Client.GetAsync(path);

        Assert.Equal(expected, response.StatusCode);
    }

    /// <summary>The tags of one measurement, in the form that <see cref="CountedViolations"/> keeps.</summary>
    private static string Tags(string endpoint, string kind, bool blocked) =>
        $"1: behaviorlock.blocked={blocked}, behaviorlock.endpoint={endpoint}, behaviorlock.kind={kind}";

    /// <summary>
    /// Listens to the counter of one app. The tests run in parallel, and every app has a meter with the
    /// same name. So the listener takes only the meter of the given services.
    /// </summary>
    private sealed class CountedViolations : IDisposable
    {
        private readonly MeterListener _listener = new();

        public CountedViolations(IServiceProvider services)
        {
            var meters = services.GetRequiredService<IMeterFactory>();

            _listener.InstrumentPublished = (instrument, listener) =>
            {
                if (instrument.Meter.Name == "BehaviorLock" && ReferenceEquals(instrument.Meter.Scope, meters))
                {
                    Instruments.Enqueue(instrument);
                    listener.EnableMeasurementEvents(instrument);
                }
            };
            _listener.SetMeasurementEventCallback<long>((_, value, tags, _) =>
            {
                var sorted = tags.ToArray().Select(tag => $"{tag.Key}={tag.Value}").Order(StringComparer.Ordinal);
                Items.Enqueue($"{value}: {string.Join(", ", sorted)}");
            });
            _listener.Start();
        }

        /// <summary>One text per measurement: the value, and all tags sorted by name.</summary>
        public ConcurrentQueue<string> Items { get; } = new();

        public ConcurrentQueue<Instrument> Instruments { get; } = new();

        public void Dispose() => _listener.Dispose();
    }
}
