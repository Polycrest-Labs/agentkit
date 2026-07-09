namespace AgentKit.Agents;

/// <summary>One function tool: name, description, a raw JSON parameters schema (authored/ported as
/// literal JSON, not reflected), and the handler the loop dispatches to with the model's raw arguments
/// JSON. Handler exceptions are converted by the runner into recoverable error outputs fed back to the
/// model — a handler never needs its own catch-all.</summary>
public sealed class AgentTool
{
    public required string Name { get; init; }
    public required string Description { get; init; }

    /// <summary>The JSON schema of the arguments object, as raw JSON text.</summary>
    public required string ParametersSchema { get; init; }

    public required Func<string, CancellationToken, Task<ToolOutcome>> Handler { get; init; }

    /// <summary>A tool whose arguments schema is generated from <typeparamref name="TArgs"/>
    /// (<see cref="Schema.DtoJsonSchema"/>) and whose handler receives the tolerantly-bound args —
    /// the schema constrains generation provider-side; <see cref="AgentJson"/>'s converters stay as
    /// the app-side safety net for models that ignore it.</summary>
    public static AgentTool Typed<TArgs>(
        string name, string description,
        Func<TArgs, CancellationToken, Task<ToolOutcome>> handler,
        Schema.DtoSchemaOptions? schema = null) => new()
    {
        Name = name,
        Description = description,
        ParametersSchema = Schema.DtoJsonSchema.For<TArgs>(schema),
        Handler = (argsJson, ct) => handler(AgentJson.Deserialize<TArgs>(argsJson), ct),
    };
}

/// <summary>A named collection of <see cref="AgentTool"/>s for one turn.</summary>
public sealed class AgentToolCatalog
{
    private readonly Dictionary<string, AgentTool> _byName;

    public AgentToolCatalog(IEnumerable<AgentTool> tools)
    {
        Tools = [.. tools];
        _byName = Tools.ToDictionary(t => t.Name, StringComparer.Ordinal);
    }

    public IReadOnlyList<AgentTool> Tools { get; }

    public AgentTool? Find(string name) => _byName.GetValueOrDefault(name);
}
