using AgentKit.Agents;

namespace AgentKit.Wire;

/// <summary>Options for one <see cref="AgentTurnSession"/>.</summary>
public sealed class AgentWireOptions
{
    /// <summary>Heartbeat cadence for the merged <c>heartbeat</c> frames (default 15 s; ≤ 0 disables).
    /// The client dead-air ceiling defaults to 90 s, comfortably past this.</summary>
    public TimeSpan HeartbeatInterval { get; init; } = TimeSpan.FromSeconds(15);

    /// <summary>The turn's tool catalog, used to resolve <c>tool.label</c> from
    /// <see cref="AgentTool.DisplayLabel"/>; null/missing → label null (hosts show the raw name).</summary>
    public AgentToolCatalog? Tools { get; init; }

    /// <summary>Maps a host <see cref="CustomEvent"/> payload onto a named domain frame. Payloads the
    /// mapper returns null for are dropped with a diagnostic — never streamed untyped. Kit-native
    /// question/form/followup events are recognized before this mapper runs.</summary>
    public Func<object, DomainFrame?>? DomainFrameMapper { get; init; }

    /// <summary>Emit a <c>usage</c> frame when the runner reports usage (default true). The outcome
    /// carries usage either way.</summary>
    public bool EmitUsage { get; init; } = true;

    /// <summary>Clock for the heartbeat pump — swap for a fake in tests.</summary>
    public TimeProvider TimeProvider { get; init; } = TimeProvider.System;
}
