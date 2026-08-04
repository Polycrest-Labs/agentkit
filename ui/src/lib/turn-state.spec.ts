import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { AgentFrame, SuggestionFrame } from './frames';
import { AgentTransport, AgentTurnBody, AgentTurnRefusal, UploadedAgentAsset } from './tokens';
import { AgentTurnState } from './turn-state';

type Emit = (frame: AgentFrame | { type: string }) => void;

/** A transport whose stream is driven by the test: emit frames, then settle. */
function scripted(run: (emit: Emit, signal: AbortSignal | undefined, body: AgentTurnBody) => Promise<void>):
  AgentTransport<AgentFrame | { type: string }> {
  return { streamTurn: (body, onFrame, signal) => run(onFrame as Emit, signal, body) };
}

const LOADED: AgentFrame = { type: 'loaded', turnId: 't1', provider: 'azure-openai', model: 'gpt-4o' };
const CHANGESET: AgentFrame = { type: 'changeset', turnId: 't1', text: 'All set.', suggestions: 1 };

function flush(): Promise<void> {
  return new Promise((resolve) => setTimeout(resolve, 0));
}

// jsdom has no createObjectURL; the attachment flow only needs stable strings.
beforeEach(() => {
  if (!URL.createObjectURL) {
    URL.createObjectURL = () => `blob:test-${Math.random()}`;
    URL.revokeObjectURL = () => undefined;
  }
});

afterEach(() => {
  vi.useRealTimers();
});

describe('AgentTurnState — the happy path', () => {
  it('idle → streaming → done, with the changeset text in the transcript and hooks fired', async () => {
    const suggestions: SuggestionFrame[] = [];
    const state = new AgentTurnState(scripted(async (emit) => {
      await flush(); // the wire is never synchronous — frames land after send() returns
      emit(LOADED);
      emit({ type: 'heartbeat', seq: 1 });
      emit({ type: 'suggestion', section: 'site', path: 'access', value: 'paved' });
      emit(CHANGESET);
    }));

    expect(state.phase()).toBe('idle');
    const sent = state.send('rank the comps', { onSuggestion: (s) => suggestions.push(s) });
    expect(sent).toBe(true);
    expect(state.phase()).toBe('streaming');
    await flush();

    expect(state.phase()).toBe('done');
    expect(state.loaded()?.model).toBe('gpt-4o');
    expect(suggestions).toHaveLength(1);
    expect(state.messages().map((m) => [m.role, m.text])).toEqual([
      ['user', 'rank the comps'],
      ['assistant', 'All set.'],
    ]);
  });

  it('refuses to send while a turn is streaming', async () => {
    const state = new AgentTurnState(scripted(() => new Promise(() => { /* never settles */ })));
    expect(state.send('one')).toBe(true);
    expect(state.send('two')).toBe(false);
  });

  it('a closed turn with nothing visible says so', async () => {
    const state = new AgentTurnState(scripted(async (emit) => {
      emit({ type: 'changeset', turnId: 't1', text: null, suggestions: 0 });
    }));

    state.send('anything to add?');
    await flush();

    expect(state.phase()).toBe('done');
    expect(state.messages().at(-1)?.text).toContain('nothing to add');
  });
});

describe('AgentTurnState — refusals and errors', () => {
  it('409 puts the busy line in the transcript; the composer unlocks', async () => {
    const state = new AgentTurnState(scripted(() =>
      Promise.reject(new AgentTurnRefusal('A turn is already running.', 409))));

    state.send('hello');
    await flush();

    expect(state.phase()).toBe('error');
    expect(state.unavailable()).toBe(false);
    expect(state.messages().at(-1)?.text).toContain('One turn at a time');
    expect(state.composerDisabled()).toBe(false);
  });

  it('503 latches unavailable', async () => {
    const state = new AgentTurnState(scripted(() =>
      Promise.reject(new AgentTurnRefusal('No provider configured.', 503))));

    state.send('hello');
    await flush();

    expect(state.unavailable()).toBe(true);
    expect(state.phase()).toBe('error');
  });

  it('a terminal error frame renders its detail sentence', async () => {
    const state = new AgentTurnState(scripted(async (emit) => {
      emit(LOADED);
      emit({ type: 'error', turnId: 't1', error: 'turn-failed', detail: 'the model refused' });
    }));

    state.send('hello');
    await flush();

    expect(state.phase()).toBe('error');
    expect(state.messages().at(-1)?.text).toBe('The turn failed — the model refused');
  });

  it('a stream that closes without a terminal frame is a failure, not a success', async () => {
    const state = new AgentTurnState(scripted(async (emit) => {
      emit(LOADED);
      emit({ type: 'suggestion', section: 's', path: 'p' }); // half-run turn — cards alone must not read as an answer
    }));

    state.send('hello');
    await flush();

    expect(state.phase()).toBe('error');
    expect(state.messages().at(-1)?.text).toContain('ended before it finished');
  });
});

describe('AgentTurnState — dead air', () => {
  it('heartbeat starvation transitions to error EXACTLY once and hangs up', async () => {
    vi.useFakeTimers();
    let aborted = false;
    const state = new AgentTurnState(scripted((emit, signal) => {
      emit(LOADED);
      signal?.addEventListener('abort', () => { aborted = true; });
      return new Promise(() => { /* the stream never speaks again */ });
    }), { deadAirMs: 90_000 });

    state.send('hello');
    await vi.advanceTimersByTimeAsync(89_000);
    expect(state.phase()).toBe('streaming');

    await vi.advanceTimersByTimeAsync(2_000);
    expect(state.phase()).toBe('error');
    expect(aborted).toBe(true);
    const failures = state.messages().filter((m) => m.text.includes('ended before it finished'));
    expect(failures).toHaveLength(1);

    // No second firing, no second message.
    await vi.advanceTimersByTimeAsync(200_000);
    expect(state.messages().filter((m) => m.text.includes('ended before it finished'))).toHaveLength(1);
  });

  it('every frame resets the timer — a chatty stream never starves', async () => {
    vi.useFakeTimers();
    let emitFrame: Emit = () => undefined;
    const state = new AgentTurnState(scripted((emit) => {
      emitFrame = emit;
      return new Promise(() => { /* held open */ });
    }), { deadAirMs: 90_000 });

    state.send('hello');
    for (let i = 0; i < 5; i++) {
      await vi.advanceTimersByTimeAsync(60_000); // each under the ceiling
      emitFrame({ type: 'heartbeat', seq: i + 1 });
    }
    expect(state.phase()).toBe('streaming');
  });
});

describe('AgentTurnState — abort and fall-through', () => {
  it('abort mid-stream is quiet: idle, no failure message', async () => {
    const state = new AgentTurnState(scripted((emit, signal) => {
      emit(LOADED);
      return new Promise((_, reject) => {
        signal?.addEventListener('abort', () => reject(new DOMException('aborted', 'AbortError')));
      });
    }));

    state.send('hello');
    state.abort();
    await flush();

    expect(state.phase()).toBe('idle');
    expect(state.messages()).toHaveLength(1); // just the optimistic user bubble
  });

  it('unknown frame types fall through to onDomainFrame, never the error path', async () => {
    const domain: { type: string }[] = [];
    const state = new AgentTurnState(scripted(async (emit) => {
      emit({ type: 'dataset', name: 'comps' } as { type: string });
      emit(CHANGESET);
    }));

    state.send('hello', { onDomainFrame: (f) => domain.push(f) });
    await flush();

    expect(domain).toEqual([{ type: 'dataset', name: 'comps' }]);
    expect(state.phase()).toBe('done');
  });
});

describe('AgentTurnState — attachments', () => {
  function uploadingTransport(assets: UploadedAgentAsset[], settle: { resolve?: () => void } = {}) {
    let resolveUpload: (a: UploadedAgentAsset[]) => void = () => undefined;
    const transport = {
      ...scripted(async (emit, _signal, body) => {
        bodies.push(body);
        emit(CHANGESET);
      }),
      uploadAttachments: () => new Promise<UploadedAgentAsset[]>((resolve) => {
        resolveUpload = () => resolve(assets);
        settle.resolve = () => resolve(assets);
      }),
    };
    const bodies: AgentTurnBody[] = [];
    return { transport, bodies, finishUpload: () => resolveUpload(assets) };
  }

  it('send is refused while an upload is in flight; the asset ids ride the next turn', async () => {
    const { transport, bodies, finishUpload } = uploadingTransport([{ assetId: 41 }, { assetId: 42 }]);
    const state = new AgentTurnState(transport);

    state.addFiles([new File(['%PDF'], 'lease.pdf', { type: 'application/pdf' })]);
    expect(state.uploadsInFlight()).toBe(1);
    expect(state.send('classify this')).toBe(false); // gated — the chip only lands on upload response

    finishUpload();
    await flush();
    expect(state.uploadsInFlight()).toBe(0);
    const chip = state.pendingAttachments()[0];
    expect(chip.fileName).toBe('lease.pdf (2 pages)');
    expect(chip.id).toBe('41+42');

    expect(state.send('classify this')).toBe(true);
    await flush();
    expect(bodies[0].attachmentAssetIds).toEqual([41, 42]);
    expect(state.pendingAttachments()).toHaveLength(0);
  });

  it('source-kind assets never ride a turn', async () => {
    const { transport, finishUpload } = uploadingTransport([
      { assetId: 1, kind: 'source' },
      { assetId: 2, kind: 'page' },
    ]);
    const state = new AgentTurnState(transport);

    state.addFiles([new File(['%PDF'], 'doc.pdf', { type: 'application/pdf' })]);
    finishUpload();
    await flush();

    expect(state.pendingAttachments()[0].id).toBe('2');
  });

  it('removing a chip drops every joined asset id', async () => {
    const { transport, bodies, finishUpload } = uploadingTransport([{ assetId: 7 }, { assetId: 8 }]);
    const state = new AgentTurnState(transport);
    state.addFiles([new File(['%PDF'], 'doc.pdf', { type: 'application/pdf' })]);
    finishUpload();
    await flush();

    state.removePending(state.pendingAttachments()[0]);

    expect(state.pendingAttachments()).toHaveLength(0);
    expect(state.send('no attachments now')).toBe(true);
    await flush();
    expect(bodies[0].attachmentAssetIds).toBeUndefined();
  });
});
