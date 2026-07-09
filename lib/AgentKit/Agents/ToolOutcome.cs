namespace AgentKit.Agents;

/// <summary>What a tool handler hands back: the string fed to the model, opaque domain events for the
/// host's stream (emitted as <see cref="CustomEvent"/>s before the tool-finished event), and any images
/// the read surfaced (attached as follow-up vision input, since images can't ride a function output).</summary>
public sealed record ToolOutcome(string OutputForModel, IReadOnlyList<object> DomainEvents, bool IsError = false)
{
    public ToolOutcome(string outputForModel) : this(outputForModel, [])
    {
    }

    public IReadOnlyList<LlmImage> Images { get; init; } = [];
}
