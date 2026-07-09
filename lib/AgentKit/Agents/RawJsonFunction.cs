using System.Text.Json;
using Microsoft.Extensions.AI;

namespace AgentKit.Agents;

/// <summary>An <see cref="AIFunction"/> declared from a raw JSON schema — the bridge that lets tool
/// schemas be authored/ported as literal JSON instead of reflected from .NET signatures. Never invoked
/// through M.E.AI (no <c>FunctionInvokingChatClient</c> in the pipeline): the agent loop dispatches
/// function calls itself, so <see cref="InvokeCoreAsync"/> is unreachable by design.</summary>
public sealed class RawJsonFunction : AIFunction
{
    private readonly string _name;
    private readonly string _description;
    private readonly JsonElement _schema;

    public RawJsonFunction(string name, string description, JsonElement parametersSchema)
    {
        _name = name;
        _description = description;
        _schema = parametersSchema;
    }

    public static RawJsonFunction FromJson(string name, string description, string parametersSchemaJson)
    {
        using var doc = JsonDocument.Parse(parametersSchemaJson);
        return new RawJsonFunction(name, description, doc.RootElement.Clone());
    }

    public override string Name => _name;
    public override string Description => _description;
    public override JsonElement JsonSchema => _schema;

    protected override ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken) =>
        throw new NotSupportedException($"Tool '{_name}' is dispatched by the agent loop, not auto-invoked.");
}
