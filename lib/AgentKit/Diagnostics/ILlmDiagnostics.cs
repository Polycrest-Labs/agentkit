namespace AgentKit.Diagnostics;

/// <summary>The alerting seam: notified on every model failure, failover, and exhaustion. The default
/// <see cref="LoggerLlmDiagnostics"/> writes structured <c>ILogger</c> errors + <c>Meter("AgentKit")</c>
/// counters, which the web host exports to Application Insights; hosts can substitute richer sinks.</summary>
public interface ILlmDiagnostics
{
    /// <summary>A completion faulted on <paramref name="model"/> (it may or may not fail over next).</summary>
    void ModelFailure(string model, string provider, string feature, Exception exception);

    /// <summary>An eligible failure is being retried on the next candidate.</summary>
    void Failover(string fromModel, string toModel, string provider, string feature);

    /// <summary>Every candidate failed — the call is surfacing the final error to its caller.</summary>
    void FailoverExhausted(string feature, string way, string lastModel, Exception exception);
}
