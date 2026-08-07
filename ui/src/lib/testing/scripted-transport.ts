import { AgentFrame } from '../frames';
import { AgentTransport, AgentTransportCapabilities, AgentTurnBody } from '../tokens';

export interface ScriptedTransportOptions {
  /** Delay between replayed frames (default 0 — a microtask between frames either way). */
  delayMs?: number;
  /** Capabilities the stub advertises (e.g. `{serverCancel: true}` to exercise Stop). */
  capabilities?: AgentTransportCapabilities;
}

/** The scripted stub transport returned by {@link scriptedTransport} — also records the bodies
 * it was sent, so specs and demos can assert on them. */
export interface ScriptedAgentTransport extends AgentTransport<AgentFrame | { type: string }> {
  /** Every turn body received, in order. */
  readonly bodies: AgentTurnBody[];
  /** How many times cancelTurn was invoked (when the capability was declared). */
  readonly cancels: number;
}

/**
 * A fixture-replaying stub transport: each `streamTurn` call replays the next script (an array of
 * frames — the same shapes as `protocol/fixtures/`), then resolves. The whole kit runs key-less
 * on it: demos, vitest component specs, and host prototyping all exercise the REAL frame
 * contract rather than hand-mocked callbacks. Scripts beyond the provided list replay the last
 * script. An abort mid-replay stops remaining frames and resolves quietly (the kit's abort
 * semantics).
 */
export function scriptedTransport(
  scripts: (AgentFrame | { type: string })[][],
  options: ScriptedTransportOptions = {},
): ScriptedAgentTransport {
  let turn = 0;
  let cancels = 0;
  const bodies: AgentTurnBody[] = [];
  const delay = (): Promise<void> => new Promise((resolve) =>
    options.delayMs ? setTimeout(resolve, options.delayMs) : queueMicrotask(resolve));

  const transport: ScriptedAgentTransport = {
    bodies,
    get cancels() {
      return cancels;
    },
    capabilities: options.capabilities,
    async streamTurn(body, onFrame, signal): Promise<void> {
      bodies.push(body);
      const script = scripts[Math.min(turn++, scripts.length - 1)] ?? [];
      for (const frame of script) {
        await delay();
        if (signal?.aborted) {
          return; // a caller hanging up is not a failure — resolve quietly
        }
        onFrame(frame);
      }
    },
  };
  if (options.capabilities?.serverCancel) {
    transport.cancelTurn = async () => {
      cancels++;
    };
  }
  return transport;
}
