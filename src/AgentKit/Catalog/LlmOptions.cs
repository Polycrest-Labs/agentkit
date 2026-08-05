namespace AgentKit.Catalog;

/// <summary>Root options bound from the <c>Llm</c> config section: providers, the model catalog,
/// routing preferences, and completion logging.</summary>
public sealed class LlmOptions
{
    public Dictionary<string, LlmProviderOptions> Providers { get; } = new(StringComparer.OrdinalIgnoreCase);
    public List<ModelCard> Models { get; } = [];
    public LlmRoutingOptions Routing { get; set; } = new();
    public LlmLoggingOptions Logging { get; set; } = new();
}

/// <summary>How to reach one provider endpoint. <c>Kind</c> is <c>azure-openai</c> (the Responses API
/// on an Azure deployment, key or Entra auth), <c>openai-responses</c> (the same Responses API on the
/// OpenAI platform + key — the only path where OpenAI allows function tools TOGETHER WITH reasoning),
/// <c>openai-compat</c> (any OpenAI-compatible chat-completions endpoint + API key: NeuralWatt, OpenAI
/// itself, …), or <c>gemini-native</c> (Gemini's <c>generateContent</c> API + key — the path that
/// supports Search grounding, unlike Gemini's openai-compat endpoint).</summary>
public sealed class LlmProviderOptions
{
    public string Kind { get; set; } = "openai-compat";
    public string? Endpoint { get; set; }
    public string? ApiKey { get; set; }

    /// <summary>Token-credential mode for <c>azure-openai</c> providers when no API key is set.</summary>
    public CredentialMode CredentialMode { get; set; } = CredentialMode.Auto;

    /// <summary>What "reachable" means per kind. Endpoints: every kind needs one EXCEPT
    /// <c>openai-responses</c>, whose SDK already knows where OpenAI lives — requiring one there would
    /// make an otherwise-complete config silently unroutable (routing skips unconfigured providers, so
    /// the symptom would be a missing model rather than an error). Keys: only <c>azure-openai</c> can
    /// authenticate without one, via a token credential.</summary>
    public bool IsConfigured =>
        (!string.IsNullOrWhiteSpace(Endpoint) || string.Equals(Kind, "openai-responses", StringComparison.OrdinalIgnoreCase))
        && (string.Equals(Kind, "azure-openai", StringComparison.OrdinalIgnoreCase) || !string.IsNullOrWhiteSpace(ApiKey));
}

/// <summary>Azure token-credential selection, replacing the old <c>IHostEnvironment.IsDevelopment()</c>
/// coupling in <c>FoundryClientFactory</c>: <c>Auto</c> = plain <c>DefaultAzureCredential</c> (deployed);
/// <c>DevSafe</c> excludes the managed/workload identity probes that hang or fault on Azure
/// Arc-enrolled dev boxes; <c>ManagedIdentity</c> forces the site identity.</summary>
public enum CredentialMode
{
    Auto,
    DevSafe,
    ManagedIdentity,
}

public sealed class LlmRoutingOptions
{
    /// <summary>Per-tier model preference, keyed <c>"high"</c>/<c>"low"</c>. The FIRST capable entry is
    /// the primary; the rest (then any remaining capable models, cheapest first) are the failover chain.</summary>
    public Dictionary<string, List<string>> Prefer { get; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class LlmLoggingOptions
{
    /// <summary>Which built-in completion sink to register: <c>none</c> (default), <c>jsonl</c>
    /// (local files under <see cref="Directory"/>), or <c>memory</c>. Hosts can register their own
    /// <c>ICompletionSink</c> (e.g. the web app's blob sink) before calling <c>AddAgentKit</c>.</summary>
    public string Sink { get; set; } = "none";

    /// <summary>Directory for the <c>jsonl</c> sink (default <c>llm-logs</c> under the working dir).</summary>
    public string? Directory { get; set; }
}
