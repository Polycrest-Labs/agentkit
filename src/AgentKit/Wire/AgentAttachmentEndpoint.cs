using Microsoft.AspNetCore.Http;

namespace AgentKit.Wire;

/// <summary>A typed validation refusal the host maps onto ProblemDetails (the code is stable).</summary>
public sealed record AgentAttachmentError(string Code, string Detail);

/// <summary>One upload batch's result: stored assets, or one typed validation error.</summary>
public sealed record AgentAttachmentResult
{
    public IReadOnlyList<StoredAgentAsset>? Assets { get; init; }
    public AgentAttachmentError? Error { get; init; }
    public bool Succeeded => Error is null;

    public static AgentAttachmentResult Ok(IReadOnlyList<StoredAgentAsset> assets) => new() { Assets = assets };
    public static AgentAttachmentResult Fail(string code, string detail) => new() { Error = new AgentAttachmentError(code, detail) };
}

/// <summary>Magic-byte signatures for the kit's default allowlist. Hosts pass
/// <see cref="Matches"/> as the endpoint's agreement check (or supply their own).</summary>
public static class AgentMagicBytes
{
    /// <summary>Delegate-friendly overload (the endpoint's agreement check takes Memory).</summary>
    public static bool Matches(string contentType, ReadOnlyMemory<byte> header) => Matches(contentType, header.Span);

    /// <summary>True when <paramref name="header"/> (the file's first bytes) matches the declared
    /// <paramref name="contentType"/>'s signature. Unknown content types return false — an
    /// allowlisted-but-unsniffable type must be rejected, never waved through.</summary>
    public static bool Matches(string contentType, ReadOnlySpan<byte> header)
    {
        return contentType.ToLowerInvariant() switch
        {
            "image/jpeg" => StartsWith(header, [0xFF, 0xD8, 0xFF]),
            "image/png" => StartsWith(header, [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]),
            "image/gif" => StartsWith(header, "GIF87a"u8) || StartsWith(header, "GIF89a"u8),
            // RIFF....WEBP
            "image/webp" => header.Length >= 12 && StartsWith(header, "RIFF"u8) && header.Slice(8, 4).SequenceEqual("WEBP"u8),
            "application/pdf" => StartsWith(header, "%PDF-"u8),
            _ => false,
        };
    }

    private static bool StartsWith(ReadOnlySpan<byte> header, ReadOnlySpan<byte> signature) =>
        header.Length >= signature.Length && header[..signature.Length].SequenceEqual(signature);
}

/// <summary>
/// The narrow ASP.NET multipart helper: reads ONE upload batch, validates EVERY file against the
/// limits (count, per-file bytes, aggregate bytes, exact content-type allowlist, host-provided
/// magic-byte agreement, basename sanitization) BEFORE any persistence, then calls the host store
/// once (the store's contract is all-or-nothing). The host owns the route, its auth, and the
/// server-resolved <typeparamref name="TScope"/> — no tenant or route id is ever inferred here.
/// </summary>
public static class AgentAttachmentEndpoint
{
    private const int HeaderSniffBytes = 16;

    public static async Task<AgentAttachmentResult> HandleAsync<TScope>(
        HttpRequest request,
        TScope scope,
        IAgentAttachmentStore<TScope> store,
        AgentAttachmentLimits limits,
        Func<string, ReadOnlyMemory<byte>, bool> magicBytesAgree,
        CancellationToken ct)
    {
        if (!request.HasFormContentType)
        {
            return AgentAttachmentResult.Fail("not_multipart", "The request must be multipart/form-data.");
        }
        var form = await request.ReadFormAsync(ct);
        var files = form.Files;
        if (files.Count == 0)
        {
            return AgentAttachmentResult.Fail("no_files", "The batch contains no files.");
        }
        if (files.Count > limits.MaxFiles)
        {
            return AgentAttachmentResult.Fail("too_many_files", $"At most {limits.MaxFiles} files per batch.");
        }

        // Validate ALL files before any store write — a batch with one bad file stores nothing.
        long totalBytes = 0;
        var uploads = new List<AgentAttachmentUpload>(files.Count);
        var openStreams = new List<Stream>(files.Count);
        try
        {
            foreach (var file in files)
            {
                var displayName = SafeBaseName(file.FileName);
                if (file.Length <= 0)
                {
                    return AgentAttachmentResult.Fail("empty_file", $"'{displayName}' is empty.");
                }
                if (file.Length > limits.MaxBytesPerFile)
                {
                    return AgentAttachmentResult.Fail("file_too_large", $"'{displayName}' exceeds {limits.MaxBytesPerFile} bytes.");
                }
                totalBytes += file.Length;
                if (totalBytes > limits.MaxTotalBytes)
                {
                    return AgentAttachmentResult.Fail("total_too_large", $"The batch exceeds {limits.MaxTotalBytes} bytes.");
                }
                var contentType = (file.ContentType ?? string.Empty).Trim();
                if (!limits.AllowedContentTypes.Contains(contentType, StringComparer.OrdinalIgnoreCase))
                {
                    return AgentAttachmentResult.Fail("unsupported_type", $"'{displayName}' has unsupported type '{contentType}'.");
                }

                var stream = file.OpenReadStream();
                openStreams.Add(stream);
                var header = new byte[HeaderSniffBytes];
                var read = await ReadUpToAsync(stream, header, ct);
                if (!magicBytesAgree(contentType, header.AsMemory(0, read)))
                {
                    return AgentAttachmentResult.Fail("type_mismatch", $"'{displayName}' does not look like '{contentType}'.");
                }
                stream.Position = 0;

                uploads.Add(new AgentAttachmentUpload
                {
                    OriginalName = displayName,
                    ContentType = contentType,
                    Content = stream,
                    ByteSize = file.Length,
                });
            }

            var stored = await store.StoreAsync(scope, uploads, ct);
            return AgentAttachmentResult.Ok(stored);
        }
        finally
        {
            foreach (var stream in openStreams)
            {
                await stream.DisposeAsync();
            }
        }
    }

    /// <summary>Reduce a client-supplied filename to a safe display basename: path separators
    /// stripped (either flavor), control characters removed, length-capped, never empty.</summary>
    public static string SafeBaseName(string? fileName)
    {
        var name = (fileName ?? string.Empty).Replace('\\', '/');
        name = name[(name.LastIndexOf('/') + 1)..];
        name = new string(name.Where(c => !char.IsControl(c)).ToArray()).Trim();
        if (name is "" or "." or "..")
        {
            return "file";
        }
        return name.Length <= 160 ? name : name[^160..];
    }

    private static async Task<int> ReadUpToAsync(Stream stream, byte[] buffer, CancellationToken ct)
    {
        var total = 0;
        while (total < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(total), ct);
            if (read == 0)
            {
                break;
            }
            total += read;
        }
        return total;
    }
}
