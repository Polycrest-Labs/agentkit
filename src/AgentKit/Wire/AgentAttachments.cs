namespace AgentKit.Wire;

// ─────────────────────────────────────────────────────────────────────────────────────────────────
// The attachment CONTRACT (types only): the kit standardizes the rich shapes and the scope-generic
// store seam; the HOST owns custody. No route id or tenant id is ever inferred from an asset id —
// the host constructs an authorized TScope and passes it into every store/open call. The narrow
// ASP.NET multipart endpoint helper that validates against AgentAttachmentLimits lives beside the
// SSE writer (AgentAttachmentEndpoint).
// ─────────────────────────────────────────────────────────────────────────────────────────────────

/// <summary>One incoming upload the host store persists (name already reduced to a safe basename
/// by the endpoint helper).</summary>
public sealed record AgentAttachmentUpload
{
    public required string OriginalName { get; init; }
    public required string ContentType { get; init; }
    public required Stream Content { get; init; }
    public required long ByteSize { get; init; }
}

/// <summary>A stored asset, echoed to the composer as a chip. Asset ids are strings — hosts choose
/// their own identity scheme (Groundsworth: an attachment-row uuid).</summary>
public sealed record StoredAgentAsset
{
    public required string AssetId { get; init; }
    public required string OriginalName { get; init; }
    public required string ContentType { get; init; }
    /// <summary>"image" or "document" (see <see cref="AgentAttachmentLimits.KindFor"/>).</summary>
    public required string Kind { get; init; }
    public required long ByteSize { get; init; }
}

/// <summary>An opened asset's content for an authenticated bytes/preview read.</summary>
public sealed record AgentAssetContent
{
    public required Stream Content { get; init; }
    public required string ContentType { get; init; }
    public required string OriginalName { get; init; }
    public required long ByteSize { get; init; }
    public string? Kind { get; init; }
}

/// <summary>
/// The host storage seam. <typeparamref name="TScope"/> is the host's server-resolved authorization
/// scope (e.g. an authorized company/job pair) — created by the host per request and honored on
/// every call, so a leaked asset id alone can never cross a tenant or job wall.
/// </summary>
public interface IAgentAttachmentStore<in TScope>
{
    /// <summary>Persists one validated batch (all-or-nothing) and returns the stored assets in
    /// upload order.</summary>
    Task<IReadOnlyList<StoredAgentAsset>> StoreAsync(TScope scope, IReadOnlyList<AgentAttachmentUpload> files, CancellationToken ct);

    /// <summary>Opens one asset under the scope, or null when it does not exist THERE — an id that
    /// exists in another scope must behave exactly like one that never existed.</summary>
    Task<AgentAssetContent?> OpenAsync(TScope scope, string assetId, CancellationToken ct);
}

/// <summary>Transport-mechanics budgets the endpoint helper enforces before the store runs. Hosts
/// may be stricter (domain payload/model-input limits), never looser.</summary>
public sealed record AgentAttachmentLimits
{
    public int MaxFiles { get; init; } = 4;
    public long MaxBytesPerFile { get; init; } = 25 * 1024 * 1024;
    public long MaxTotalBytes { get; init; } = 25 * 1024 * 1024;

    /// <summary>Exact content-type allowlist (no wildcards, no parameters).</summary>
    public IReadOnlyList<string> AllowedContentTypes { get; init; } =
        ["image/jpeg", "image/png", "image/gif", "image/webp", "application/pdf"];

    /// <summary>The kit's kind taxonomy: images are "image", everything else "document".</summary>
    public static string KindFor(string contentType) =>
        contentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase) ? "image" : "document";
}
