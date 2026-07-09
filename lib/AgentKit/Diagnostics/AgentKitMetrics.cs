using System.Diagnostics.Metrics;

namespace AgentKit.Diagnostics;

/// <summary>The library's meter + counters. The host makes these alertable by adding the meter to its
/// OpenTelemetry pipeline (<c>WithMetrics(m =&gt; m.AddMeter(AgentKitMetrics.MeterName))</c>) — the
/// library itself has no Application Insights dependency.</summary>
public sealed class AgentKitMetrics : IDisposable
{
    public const string MeterName = "AgentKit";

    private readonly Meter _meter;

    public AgentKitMetrics(IMeterFactory? meterFactory = null)
    {
        _meter = meterFactory?.Create(MeterName) ?? new Meter(MeterName);
        ModelFailures = _meter.CreateCounter<long>("llm.model.failures", description: "Completions that faulted on a model");
        Failovers = _meter.CreateCounter<long>("llm.failovers", description: "Completions retried on a fallback model");
        FailoverExhausted = _meter.CreateCounter<long>("llm.failover.exhausted", description: "Completions where every candidate model failed");
    }

    public Counter<long> ModelFailures { get; }
    public Counter<long> Failovers { get; }
    public Counter<long> FailoverExhausted { get; }

    public void Dispose() => _meter.Dispose();
}
