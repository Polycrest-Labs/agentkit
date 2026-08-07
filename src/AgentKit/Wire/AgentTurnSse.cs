using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace AgentKit.Wire;

/// <summary>What the writer does when the CLIENT disconnects mid-stream. Request abort always
/// stops response writes; the difference is what happens to the turn's server-side work.</summary>
public enum AgentClientDisconnect
{
    /// <summary>Keep consuming the session so the runner completes and the host finalizer still
    /// persists the turn (the aws drain discipline). The turn's own token is untouched.</summary>
    Drain,

    /// <summary>Link request abort to turn execution: the writer cancels
    /// <see cref="AgentSseOptions.TurnCancellation"/>, the runner stops, and the finalizer still
    /// runs (on its own token) to persist the canceled outcome.</summary>
    Cancel,
}

/// <summary>What the host finalizer hands back on success (both optional).</summary>
public sealed record AgentTurnFinalization
{
    /// <summary>Optional title for the <c>done</c> frame (e.g. a chat retitle).</summary>
    public string? Title { get; init; }

    /// <summary>Overrides the <c>message</c> text (rare — the outcome's FinalText is the default).</summary>
    public string? FinalTextOverride { get; init; }
}

/// <summary>
/// The REQUIRED host finalizer: runs after the nonterminal frames finish and BEFORE any terminal
/// frame, for every outcome — success, fault and cancel alike. The host atomically persists its
/// AI-turn provenance and durable transcript here; only when this returns does the writer emit
/// <c>message + done</c>. A throw becomes a safe terminal <c>error</c> — a finalization failure
/// can never look like success. <paramref name="finalizationCt"/> is the bounded server-owned
/// token, independent of the (possibly aborted) request.
/// </summary>
public delegate Task<AgentTurnFinalization?> AgentTurnFinalizer(AgentTurnOutcome outcome, CancellationToken finalizationCt);

public sealed class AgentSseOptions
{
    public AgentClientDisconnect ClientDisconnect { get; init; } = AgentClientDisconnect.Drain;

    /// <summary>REQUIRED for <see cref="AgentClientDisconnect.Cancel"/>: the source controlling
    /// turn/runner execution (the same token the host passed to <c>AgentTurnSession.Start</c>).
    /// The writer cancels it when the request aborts. Deliberately explicit — the request-write
    /// token and the turn-execution token are never the same by accident.</summary>
    public CancellationTokenSource? TurnCancellation { get; init; }

    /// <summary>Maps a failed/canceled outcome to the safe terminal error. Return detail ONLY for
    /// safe/domain-facing text — infrastructure exception messages stay in server logs. Default:
    /// stable codes <c>turn_canceled</c>/<c>turn_failed</c> with no detail.</summary>
    public Func<AgentTurnOutcome, ErrorFrame>? SafeError { get; init; }
}

/// <summary>
/// The narrow ASP.NET SSE writer: headers + one flushed <c>event:/data:</c> block per frame
/// (single-line JSON; <c>X-Accel-Buffering: no</c> defeats proxy buffering), the drain-or-cancel
/// disconnect discipline, and OUTCOME-AWARE terminal finality — the required host finalizer runs
/// before any terminal frame, so success is impossible before the host's durable commit.
/// </summary>
public static class AgentTurnSse
{
    /// <param name="response">The HTTP response (headers not yet sent).</param>
    /// <param name="session">The started turn session whose frames/outcome this writer serves.</param>
    /// <param name="finalizeAsync">The REQUIRED host finalizer — see <see cref="AgentTurnFinalizer"/>.</param>
    /// <param name="options">Disconnect semantics + safe-error mapping.</param>
    /// <param name="requestAborted">The request-WRITE token (HttpContext.RequestAborted): stops
    /// socket writes, never the turn.</param>
    /// <param name="finalizationCt">Bounded server-owned token for the finalizer — independent of
    /// the request so a disconnect can't abandon persistence. Default: none (host should bound it).</param>
    /// <param name="logger">Server-side diagnostics (full exception detail lives here, never the wire).</param>
    public static async Task RunAsync(
        HttpResponse response,
        AgentTurnSession session,
        AgentTurnFinalizer finalizeAsync,
        AgentSseOptions? options = null,
        CancellationToken requestAborted = default,
        CancellationToken finalizationCt = default,
        ILogger? logger = null)
    {
        options ??= new AgentSseOptions();
        if (options.ClientDisconnect == AgentClientDisconnect.Cancel && options.TurnCancellation is null)
        {
            throw new ArgumentException(
                "AgentClientDisconnect.Cancel requires AgentSseOptions.TurnCancellation — the writer cannot cancel a turn it was never given.",
                nameof(options));
        }

        response.Headers.ContentType = "text/event-stream";
        response.Headers.CacheControl = "no-cache";
        response.Headers["X-Accel-Buffering"] = "no"; // defeat proxy/App Service response buffering

        var clientGone = false;
        var coreSerializationFailed = false;

        async Task<bool> WriteFrameAsync(AgentFrame frame)
        {
            if (clientGone || requestAborted.IsCancellationRequested)
            {
                OnClientGone();
                return false;
            }
            string payload;
            try
            {
                // Single-line JSON — one data: line per frame is valid SSE.
                payload = AgentWireJson.Serialize(frame);
            }
            catch (AgentWireException ex)
            {
                // A CORE frame that cannot serialize is a kit/host bug: log fully, stop streaming
                // content, and let the terminal path emit one safe error — never a false success.
                logger?.LogError(ex, "Core frame '{Event}' failed to serialize; the turn will terminate with a safe error", frame.Event);
                coreSerializationFailed = true;
                return false;
            }
            try
            {
                await response.WriteAsync($"event: {frame.Event}\ndata: {payload}\n\n", requestAborted);
                await response.Body.FlushAsync(requestAborted);
                return true;
            }
            catch (Exception ex) when (ex is OperationCanceledException or IOException or ObjectDisposedException)
            {
                OnClientGone(); // disconnected mid-write; keep draining so the turn still persists
                return false;
            }
        }

        void OnClientGone()
        {
            if (clientGone)
            {
                return;
            }
            clientGone = true;
            if (options.ClientDisconnect == AgentClientDisconnect.Cancel)
            {
                options.TurnCancellation!.Cancel();
            }
        }

        // React to a disconnect IMMEDIATELY — not at the next frame. Without this, a hung model
        // round with Cancel semantics would wait for a heartbeat before the abort reached the
        // runner (and a heartbeat-less test would deadlock outright).
        using var abortReaction = requestAborted.Register(OnClientGone);

        // Flush the headers immediately (tokens must start arriving before the first frame).
        try
        {
            await response.Body.FlushAsync(requestAborted);
        }
        catch (OperationCanceledException)
        {
            OnClientGone();
        }

        // ── nonterminal frames ─────────────────────────────────────────────────────────────────
        await foreach (var frame in session.Frames)
        {
            if (requestAborted.IsCancellationRequested)
            {
                OnClientGone();
            }
            if (coreSerializationFailed || clientGone)
            {
                continue; // drain silently so the session pump never blocks and the turn persists
            }
            await WriteFrameAsync(frame);
        }

        var outcome = await session.Outcome;

        // ── the required host finalizer — BEFORE any terminal frame, for every outcome ─────────
        AgentTurnFinalization? finalization = null;
        Exception? finalizeFailure = null;
        try
        {
            finalization = await finalizeAsync(outcome, finalizationCt);
        }
        catch (Exception ex)
        {
            finalizeFailure = ex;
            logger?.LogError(ex, "Turn {TurnId} finalization failed — emitting a safe error, never done", session.Descriptor.TurnId);
        }

        // ── terminal finality ──────────────────────────────────────────────────────────────────
        if (finalizeFailure is not null)
        {
            await WriteFrameAsync(new ErrorFrame("finalize_failed", TurnId: session.Descriptor.TurnId));
            return;
        }
        if (!outcome.Succeeded)
        {
            if (outcome.Exception is { } fault)
            {
                logger?.LogError(fault, "Turn {TurnId} failed", session.Descriptor.TurnId);
            }
            var safeError = options.SafeError?.Invoke(outcome)
                ?? new ErrorFrame(outcome.WasCanceled ? "turn_canceled" : "turn_failed", TurnId: session.Descriptor.TurnId);
            await WriteFrameAsync(safeError);
            return;
        }
        if (coreSerializationFailed)
        {
            // Persistence landed, but the stream the client saw is corrupt — an honest safe error
            // beats a done that vouches for missing frames.
            await WriteFrameAsync(new ErrorFrame("turn_failed", TurnId: session.Descriptor.TurnId));
            return;
        }

        await WriteFrameAsync(new MessageFrame(finalization?.FinalTextOverride ?? outcome.FinalText));
        await WriteFrameAsync(new DoneFrame(session.Descriptor.TurnId, finalization?.Title));
    }
}
