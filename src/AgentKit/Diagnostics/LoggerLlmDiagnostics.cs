using Microsoft.Extensions.Logging;

namespace AgentKit.Diagnostics;

/// <summary>Default diagnostics: structured error logs + <c>AgentKit</c> meter counters tagged
/// model/provider/feature. Both flow to Application Insights through the host's Azure Monitor
/// OpenTelemetry wiring with no App Insights dependency here.</summary>
public sealed class LoggerLlmDiagnostics(ILogger<LoggerLlmDiagnostics> logger, AgentKitMetrics metrics) : ILlmDiagnostics
{
    public void ModelFailure(string model, string provider, string feature, Exception exception)
    {
        logger.LogError(exception, "AgentKit model failure: {Model} ({Provider}) for {Feature}", model, provider, feature);
        metrics.ModelFailures.Add(1, Tags(model, provider, feature));
    }

    public void Failover(string fromModel, string toModel, string provider, string feature)
    {
        logger.LogError("AgentKit failover: {FromModel} → {ToModel} ({Provider}) for {Feature}", fromModel, toModel, provider, feature);
        metrics.Failovers.Add(1, Tags(fromModel, provider, feature));
    }

    public void FailoverExhausted(string feature, string way, string lastModel, Exception exception)
    {
        logger.LogError(exception, "AgentKit failover exhausted: no candidate for way {Way} succeeded (last: {Model}) for {Feature}", way, lastModel, feature);
        metrics.FailoverExhausted.Add(1, new KeyValuePair<string, object?>("model", lastModel), new("feature", feature), new("way", way));
    }

    private static KeyValuePair<string, object?>[] Tags(string model, string provider, string feature) =>
        [new("model", model), new("provider", provider), new("feature", feature)];
}
