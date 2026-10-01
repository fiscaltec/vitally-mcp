using System.Diagnostics.Metrics;
using Microsoft.Extensions.DependencyInjection;

namespace VitallyMcp.Tests;

/// <summary>
/// Records the measurements one <see cref="IMeterFactory"/>'s meters publish, for asserting on the
/// counters in <see cref="VitallyMetrics"/>.
/// </summary>
/// <remarks>
/// Filtered on the factory itself, not only the meter name. A meter created through
/// <see cref="IMeterFactory"/> carries that factory as its <see cref="Meter.Scope"/>, so tests running in
/// parallel — each with its own factory and its own <c>VitallyMcp</c> meter — cannot count each other's
/// measurements. A name-only filter would make every count here flaky under parallel execution.
/// </remarks>
public sealed class MetricCapture : IDisposable
{
    private readonly MeterListener _listener = new();
    private readonly List<(string Instrument, long Value, Dictionary<string, object?> Tags)> _measurements = [];

    public IMeterFactory Factory { get; }

    /// <summary>A <see cref="VitallyMetrics"/> over <see cref="Factory"/>, for unit tests to inject.</summary>
    public VitallyMetrics Metrics { get; }

    private readonly ServiceProvider? _services;

    /// <summary>A capture over a factory of its own, for unit tests.</summary>
    public MetricCapture()
        : this(BuildFactory(out var services)) => _services = services;

    /// <summary>
    /// A capture over an existing factory — a composed host's — so a test can observe what the real
    /// wiring records. <see cref="MeterListener.Start"/> replays instruments published before it, so the
    /// host's singleton may already exist.
    /// </summary>
    public MetricCapture(IMeterFactory factory)
    {
        Factory = factory;
        Metrics = new VitallyMetrics(Factory);

        _listener.InstrumentPublished = (instrument, listener) =>
        {
            if (ReferenceEquals(instrument.Meter.Scope, Factory))
            {
                listener.EnableMeasurementEvents(instrument);
            }
        };
        _listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) =>
        {
            var map = new Dictionary<string, object?>(StringComparer.Ordinal);
            foreach (var tag in tags)
            {
                map[tag.Key] = tag.Value;
            }

            lock (_measurements)
            {
                _measurements.Add((instrument.Name, value, map));
            }
        });
        _listener.Start();
    }

    /// <summary>The sum of every measurement on <paramref name="instrument"/> whose tags include all of <paramref name="tags"/>.</summary>
    public long Total(string instrument, params (string Key, string Value)[] tags)
    {
        lock (_measurements)
        {
            return _measurements
                .Where(m => m.Instrument == instrument
                    && tags.All(t => m.Tags.TryGetValue(t.Key, out var v) && Equals(v, t.Value)))
                .Sum(m => m.Value);
        }
    }

    private static IMeterFactory BuildFactory(out ServiceProvider services)
    {
        services = new ServiceCollection().AddMetrics().BuildServiceProvider();
        return services.GetRequiredService<IMeterFactory>();
    }

    public void Dispose()
    {
        _listener.Dispose();
        _services?.Dispose();
    }
}
