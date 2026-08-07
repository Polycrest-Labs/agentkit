import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { AgentFrame } from './frames';
import { AgentTransport, AgentTurnBody, AgentTurnRefusal, UploadedAgentAsset } from './tokens';
import { AgentTurnState } from './turn-state';

type Emit = (frame: AgentFrame | { type: string }) => void;

/** A transport whose stream is driven by the test: emit frames, then settle. */
function scripted(run: (emit: Emit, signal: AbortSignal | undefined, body: AgentTurnBody) => Promise<void>):
  AgentTransport<AgentFrame | { type: string }> {
  return { streamTurn: (body, onFrame, signal) => run(onFrame as Emit, signal, body) };
}

const LOADED: AgentFrame = { type: 'loaded', turnId: 't1', provider: 'azure-openai', model: 'gpt-5-mini' };
const MESSAGE: AgentFrame = { type: 'message', text: 'All set.' };
const DONE: AgentFrame = { type: 'done', turnId: 't1' };

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
  it('idle → streaming → done, with the message text in the transcript and hooks fired', async () => {
    const domain: { type: string }[] = [];
    const state = new AgentTurnState(scripted(async (emit) => {
      await flush(); // the wire is never synchronous — frames land after send() returns
      emit(LOADED);
      emit({ type: 'heartbeat', seq: 1 });
      emit({ type: 'suggestion', section: 'site', path: 'access', value: 'paved' }); // host domain frame now
      emit(MESSAGE);
      emit(DONE);
    }));

    expect(state.phase()).toBe('idle');
    const sent = state.send('rank the comps', { onDomainFrame: (f) => domain.push(f) });
    expect(sent).toBe(true);
    expect(state.phase()).toBe('streaming');
    await flush();

    expect(state.phase()).toBe('done');
    expect(state.loaded()?.model).toBe('gpt-5-mini');
    expect(domain).toHaveLength(1);
    expect(state.messages().map((m) => [m.role, m.text])).toEqual([
      ['user', 'rank the comps'],
      ['assistant', 'All set.'],
    ]);
  });

  it('message is authoritative — streamed tokens are only the fallback', async () => {
    const state = new AgentTurnState(scripted(async (emit) => {
      emit(LOADED);
      emit({ type: 'token', delta: 'streamed ' });
      emit({ type: 'token', delta: 'draft' });
      emit(MESSAGE);
      emit(DONE);
    }));

    state.send('hello');
    await flush();

    expect(state.messages().at(-1)?.text).toBe('All set.');
  });

  it('accumulated tokens stand in when no message frame arrived before done', async () => {
    const state = new AgentTurnState(scripted(async (emit) => {
      emit(LOADED);
      emit({ type: 'token', delta: 'Hel' });
      emit({ type: 'token', delta: 'lo' });
      emit(DONE);
    }));

    state.send('hello');
    await flush();

    expect(state.phase()).toBe('done');
    expect(state.messages().at(-1)?.text).toBe('Hello');
  });

  it('refuses to send while a turn is streaming', async () => {
    const state = new AgentTurnState(scripted(() => new Promise(() => { /* never settles */ })));
    expect(state.send('one')).toBe(true);
    expect(state.send('two')).toBe(false);
  });

  it('a closed turn with nothing visible says so', async () => {
    const state = new AgentTurnState(scripted(async (emit) => {
      emit(LOADED);
      emit(DONE);
    }));

    state.send('anything to add?');
    await flush();

    expect(state.phase()).toBe('done');
    expect(state.messages().at(-1)?.text).toContain('nothing to add');
  });

  it('a turn that only asked a question is a complete answer, not "nothing to add"', async () => {
    const state = new AgentTurnState(scripted(async (emit) => {
      emit(LOADED);
      emit({ type: 'question', question: { id: 'q1', prompt: 'Which rights?' } });
      emit(DONE);
    }));

    state.send('value the property');
    await flush();

    expect(state.phase()).toBe('done');
    expect(state.messages().filter((m) => m.text.includes('nothing to add'))).toHaveLength(0);
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

  it('a terminal error frame renders its safe detail sentence', async () => {
    const state = new AgentTurnState(scripted(async (emit) => {
      emit(LOADED);
      emit({ type: 'error', turnId: 't1', code: 'turn_failed', detail: 'the model refused' });
    }));

    state.send('hello');
    await flush();

    expect(state.phase()).toBe('error');
    expect(state.messages().at(-1)?.text).toBe('The turn failed — the model refused');
  });

  it('a terminal error frame without detail uses the generic line', async () => {
    const state = new AgentTurnState(scripted(async (emit) => {
      emit(LOADED);
      emit({ type: 'error', turnId: 't1', code: 'turn_failed' });
    }));

    state.send('hello');
    await flush();

    expect(state.messages().at(-1)?.text).toBe('The turn failed — try again.');
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
      emit(MESSAGE);
      emit(DONE);
    }));

    state.send('hello', { onDomainFrame: (f) => domain.push(f) });
    await flush();

    expect(domain).toEqual([{ type: 'dataset', name: 'comps' }]);
    expect(state.phase()).toBe('done');
  });

  it('core v2 frames (tool, citation, usage, followups) never leak into onDomainFrame', async () => {
    const domain: { type: string }[] = [];
    const state = new AgentTurnState(scripted(async (emit) => {
      emit(LOADED);
      emit({ type: 'tool', name: 'rank_comps', phase: 'start' });
      emit({ type: 'citation', title: 'TxGIO', url: 'https://example.test' });
      emit({ type: 'usage', inputTokens: 10, outputTokens: 2 });
      emit({ type: 'followups', prompts: ['Find comps'] });
      emit(MESSAGE);
      emit(DONE);
    }));

    state.send('hello', { onDomainFrame: (f) => domain.push(f) });
    await flush();

    expect(domain).toHaveLength(0);
    expect(state.phase()).toBe('done');
  });
});

describe('AgentTurnState — timeline v2', () => {
  it('streams tokens live, records tool/citation/usage state, and folds them into the assistant entry', async () => {
    let emitFrame: Emit = () => undefined;
    let settle: () => void = () => undefined;
    const state = new AgentTurnState(scripted((emit) => {
      emitFrame = emit;
      return new Promise((resolve) => { settle = resolve; });
    }));

    state.send('rank the comps');
    await flush();
    emitFrame(LOADED);
    emitFrame({ type: 'token', delta: 'Ranking' });
    expect(state.streamingText()).toBe('Ranking');
    emitFrame({ type: 'tool', name: 'rank_comps', phase: 'start', label: 'Ranking comparables' });
    expect(state.activeTool()).toEqual({ name: 'rank_comps', label: 'Ranking comparables' });
    emitFrame({ type: 'tool', name: 'rank_comps', phase: 'end', label: 'Ranking comparables' });
    expect(state.activeTool()).toBeNull();
    emitFrame({ type: 'citation', title: 'TxGIO', url: 'https://example.test' });
    emitFrame({ type: 'citation', title: 'TxGIO', url: 'https://example.test' }); // duplicate — deduped
    emitFrame({ type: 'usage', inputTokens: 1200, outputTokens: 340 });
    emitFrame(MESSAGE);
    emitFrame(DONE);
    settle();
    await flush();

    expect(state.phase()).toBe('done');
    expect(state.streamingText()).toBe('');
    const entry = state.timeline().at(-1);
    expect(entry?.kind).toBe('assistant');
    if (entry?.kind === 'assistant') {
      expect(entry.text).toBe('All set.');
      expect(entry.citations).toEqual([{ title: 'TxGIO', url: 'https://example.test' }]);
      expect(entry.usage).toEqual({ inputTokens: 1200, outputTokens: 340 });
      expect(entry.createdAtUtc).toBeTruthy(); // v2 stamps timestamps
    }
  });

  it('question frames append durable entries; a later send locks them', async () => {
    const state = new AgentTurnState(scripted(async (emit) => {
      emit(LOADED);
      emit({ type: 'question', question: { id: 'q1', prompt: 'Which rights?' } });
      emit(DONE);
    }));

    state.send('value the property');
    await flush();

    const question = state.timeline().find((e) => e.kind === 'question');
    expect(question && question.kind === 'question' && question.answered).toBeFalsy();

    state.send('Fee simple');
    await flush();

    const locked = state.timeline().find((e) => e.kind === 'question');
    expect(locked && locked.kind === 'question' && locked.answered).toBe(true);
  });

  it('followup chips arrive with their turn and clear on the next send and on reset', async () => {
    let turn = 0;
    const state = new AgentTurnState(scripted(async (emit) => {
      if (turn++ === 0) {
        emit({ type: 'followups', prompts: ['Find comps', 'Run the workup'] });
        emit(MESSAGE);
      }
      emit(DONE);
    }));

    state.send('hello');
    await flush();
    expect(state.followups()).toEqual(['Find comps', 'Run the workup']);

    state.send('next turn');
    expect(state.followups()).toEqual([]); // dropped at send, before any new frames
    await flush();

    state.reset();
    expect(state.followups()).toEqual([]);
  });

  it('hydrate seeds the ordered rich timeline and live entries append after', async () => {
    const state = new AgentTurnState(scripted(async (emit) => {
      emit(MESSAGE);
      emit(DONE);
    }));

    state.hydrate([
      { kind: 'user', text: 'earlier question', createdAtUtc: '2026-08-01T00:00:00Z', attachments: [{ assetId: 'a-1', originalName: 'site.jpg', contentType: 'image/jpeg' }] },
      { kind: 'assistant', text: 'earlier answer', createdAtUtc: '2026-08-01T00:00:05Z', citations: [{ title: 'Doc', url: 'https://x.test' }], usage: { inputTokens: 10, outputTokens: 5 } },
      { kind: 'question', question: { id: 'q1', prompt: 'County?' }, answered: true, answerText: 'Travis' },
      { kind: 'domain', frameType: 'suggestion', data: { section: 'site', path: 'access' } },
    ]);

    expect(state.timeline().map((e) => e.kind)).toEqual(['user', 'assistant', 'question', 'domain']);
    expect(state.messages().map((m) => m.text)).toEqual(['earlier question', 'earlier answer']);

    state.send('and now?');
    await flush();

    expect(state.timeline().map((e) => e.kind)).toEqual(['user', 'assistant', 'question', 'domain', 'user', 'assistant']);
    const hydratedQuestion = state.timeline()[2];
    expect(hydratedQuestion.kind === 'question' && hydratedQuestion.answerText).toBe('Travis');
  });

  it('append() lets the host add durable domain entries', () => {
    const state = new AgentTurnState(scripted(() => Promise.resolve()));
    state.append({ kind: 'domain', frameType: 'suggestion', data: { path: 'p' } });
    expect(state.timeline()).toHaveLength(1);
    expect(state.timeline()[0].createdAtUtc).toBeTruthy();
  });

  it('working reflects frame recency, not just the streaming phase', async () => {
    vi.useFakeTimers();
    let emitFrame: Emit = () => undefined;
    const state = new AgentTurnState(scripted((emit) => {
      emitFrame = emit;
      return new Promise(() => { /* held open */ });
    }), { deadAirMs: 90_000, workingWindowMs: 30_000 });

    state.send('hello');
    expect(state.working()).toBe(true); // fresh send counts as activity

    await vi.advanceTimersByTimeAsync(31_000);
    expect(state.streaming()).toBe(true);
    expect(state.working()).toBe(false); // no frame for > 2× heartbeat — not "working"

    emitFrame({ type: 'heartbeat', seq: 1 });
    expect(state.working()).toBe(true); // liveness restored by any frame
  });
});

describe('AgentTurnState — stop', () => {
  it('stop() is refused without the serverCancel capability', async () => {
    const state = new AgentTurnState(scripted(() => new Promise(() => { /* held open */ })));
    state.send('hello');
    expect(state.canStop()).toBe(false);
    expect(await state.stop()).toBe(false);
    expect(state.phase()).toBe('streaming');
  });

  it('stop() cancels server-side, appends "Stopped." only after confirmation', async () => {
    let cancels = 0;
    const transport: AgentTransport<AgentFrame | { type: string }> = {
      streamTurn: () => new Promise(() => { /* held open */ }),
      capabilities: { serverCancel: true },
      cancelTurn: async () => { cancels++; },
    };
    const state = new AgentTurnState(transport);
    state.send('hello');
    expect(state.canStop()).toBe(true);

    expect(await state.stop()).toBe(true);
    expect(cancels).toBe(1);
    expect(state.phase()).toBe('idle');
    const last = state.timeline().at(-1);
    expect(last?.kind === 'assistant' && last.notice && last.text).toBe('Stopped.');
  });

  it('a failed server cancel claims nothing — the turn keeps running', async () => {
    const transport: AgentTransport<AgentFrame | { type: string }> = {
      streamTurn: () => new Promise(() => { /* held open */ }),
      capabilities: { serverCancel: true },
      cancelTurn: () => Promise.reject(new Error('cancel endpoint down')),
    };
    const state = new AgentTurnState(transport);
    state.send('hello');

    expect(await state.stop()).toBe(false);
    expect(state.phase()).toBe('streaming');
    expect(state.timeline().filter((e) => e.kind === 'assistant')).toHaveLength(0);
  });
});

describe('AgentTurnState — attachments', () => {
  function uploadingTransport(assets: UploadedAgentAsset[], settle: { resolve?: () => void } = {}) {
    let resolveUpload: (a: UploadedAgentAsset[]) => void = () => undefined;
    const transport = {
      ...scripted(async (emit, _signal, body) => {
        bodies.push(body);
        emit(MESSAGE);
        emit(DONE);
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
    const { transport, bodies, finishUpload } = uploadingTransport([{ assetId: 'a-41' }, { assetId: 'a-42' }]);
    const state = new AgentTurnState(transport);

    state.addFiles([new File(['%PDF'], 'lease.pdf', { type: 'application/pdf' })]);
    expect(state.uploadsInFlight()).toBe(1);
    expect(state.send('classify this')).toBe(false); // gated — the chip only lands on upload response

    finishUpload();
    await flush();
    expect(state.uploadsInFlight()).toBe(0);
    const chip = state.pendingAttachments()[0];
    expect(chip.fileName).toBe('lease.pdf (2 pages)');
    expect(chip.id).toBe('a-41+a-42');

    expect(state.send('classify this')).toBe(true);
    await flush();
    expect(bodies[0].attachmentAssetIds).toEqual(['a-41', 'a-42']);
    expect(state.pendingAttachments()).toHaveLength(0);
  });

  it('source-kind assets never ride a turn', async () => {
    const { transport, finishUpload } = uploadingTransport([
      { assetId: 'a-1', kind: 'source' },
      { assetId: 'a-2', kind: 'page' },
    ]);
    const state = new AgentTurnState(transport);

    state.addFiles([new File(['%PDF'], 'doc.pdf', { type: 'application/pdf' })]);
    finishUpload();
    await flush();

    expect(state.pendingAttachments()[0].id).toBe('a-2');
  });

  it('removing a chip drops every joined asset id', async () => {
    const { transport, bodies, finishUpload } = uploadingTransport([{ assetId: 'a-7' }, { assetId: 'a-8' }]);
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
