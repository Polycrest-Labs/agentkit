using System.ClientModel;
using Azure;

namespace AgentKit.Resilience;

/// <summary>Which failures are worth retrying on the next candidate model: transport/provider errors
/// (network, timeouts, 408/429/5xx) plus auth and model-missing — never content-quality problems.</summary>
public class FailoverPolicy
{
    public static FailoverPolicy Default { get; } = new();

    public virtual bool IsEligible(Exception exception) => exception switch
    {
        ClientResultException cre => IsEligibleStatus(cre.Status),
        RequestFailedException rfe => IsEligibleStatus(rfe.Status),
        HttpRequestException or IOException or TimeoutException => true,
        // HttpClient's own timeout (the caller's token is checked before this policy runs).
        TaskCanceledException => true,
        _ => exception.InnerException is { } inner && IsEligible(inner),
    };

    private static bool IsEligibleStatus(int status) =>
        status is 408 or 429 or 401 or 403 or 404 or (>= 500 and <= 599) or 0;
}
