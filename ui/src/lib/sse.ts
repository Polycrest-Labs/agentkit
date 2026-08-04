/**
 * The chat-generic SSE plumbing every agent surface shares: the frame parser and the
 * fetch-reader loop (ported near-verbatim from the aws chat kit's `chat-types.ts`, commit
 * 28131fd), plus the ProblemDetails-friendly refusal reader. Frame unions are typed in
 * `frames.ts`; unknown event types fall through each consumer's domain-frame hook.
 */

/** One `event:`/`data:` SSE block → a typed event ({ type: name, ...payload }); null when malformed. */
export function parseFrame<TEvent extends { type: string }>(block: string): TEvent | null {
  let name = '';
  let data = '';
  for (const line of block.split('\n')) {
    if (line.startsWith('event:')) name = line.slice(6).trim();
    else if (line.startsWith('data:')) data += line.slice(5).trim();
  }
  if (!name || !data) return null;
  try {
    const payload = JSON.parse(data);
    return { type: name, ...payload } as TEvent;
  } catch {
    return null;
  }
}

/** Best-effort ProblemDetails message from a failed response — so users see "A turn is already
 * running for this job." instead of "HTTP 409". Falls back to the caller's generic message. */
export async function httpErrorMessage(response: Response, fallback: string): Promise<string> {
  const raw = (await response.text().catch(() => '')).trim();
  try {
    const parsed = JSON.parse(raw) as { detail?: string; title?: string } | string;
    if (typeof parsed === 'string' && parsed) return parsed;
    if (typeof parsed === 'object' && parsed?.detail) return parsed.detail;
    if (typeof parsed === 'object' && parsed?.title) return parsed.title;
  } catch { /* not JSON — fall through */ }
  return raw || fallback;
}

/** Reads a fetch Response's SSE body to completion, emitting each parsed frame. */
export async function readSseStream<TEvent extends { type: string }>(
  response: Response,
  onEvent: (event: TEvent) => void,
): Promise<void> {
  if (!response.body) return;
  const reader = response.body.getReader();
  const decoder = new TextDecoder();
  let buffer = '';
  while (true) {
    const { value, done } = await reader.read();
    if (done) break;
    buffer += decoder.decode(value, { stream: true });

    let sep: number;
    // SSE events are separated by a blank line.
    while ((sep = buffer.indexOf('\n\n')) >= 0) {
      const block = buffer.slice(0, sep);
      buffer = buffer.slice(sep + 2);
      const event = parseFrame<TEvent>(block);
      if (event) onEvent(event);
    }
  }
}
