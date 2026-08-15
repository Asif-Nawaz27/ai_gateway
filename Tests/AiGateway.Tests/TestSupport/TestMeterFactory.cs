using System.Diagnostics.Metrics;

namespace AiGateway.Tests.TestSupport;

/// <summary>Minimal IMeterFactory for tests — AiGatewayMetrics needs one, and tests don't care
/// about actually collecting the emitted measurements, just that recording them doesn't throw.</summary>
internal sealed class TestMeterFactory : IMeterFactory
{
    private readonly List<Meter> _meters = new();

    public Meter Create(MeterOptions options)
    {
        var meter = new Meter(options.Name, options.Version);
        _meters.Add(meter);
        return meter;
    }

    public void Dispose()
    {
        foreach (var meter in _meters)
        {
            meter.Dispose();
        }
    }
}
