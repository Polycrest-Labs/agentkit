import { describe, expect, it } from 'vitest';
import { AgentFrame } from './frames';
import { httpErrorMessage, parseFrame, readSseStream } from './sse';

function sseResponse(...blocks: string[]): Response {
  const encoder = new TextEncoder();
  const stream = new ReadableStream<Uint8Array>({
    start(controller) {
      for (const block of blocks) {
        controller.enqueue(encoder.encode(block));
      }
      controller.close();
    },
  });
  return new Response(stream);
}

describe('parseFrame', () => {
  it('round-trips all five core frames', () => {
    const loaded = parseFrame<AgentFrame>('event: loaded\ndata: {"turnId":"t1","provider":"azure-openai","model":"gpt-4o"}');
    expect(loaded).toEqual({ type: 'loaded', turnId: 't1', provider: 'azure-openai', model: 'gpt-4o' });

    const suggestion = parseFrame<AgentFrame>('event: suggestion\ndata: {"section":"improvements","path":"roof.condition","value":"fair","note":"per photos"}');
    expect(suggestion).toEqual({ type: 'suggestion', section: 'improvements', path: 'roof.condition', value: 'fair', note: 'per photos' });

    const heartbeat = parseFrame<AgentFrame>('event: heartbeat\ndata: {"seq":3,"round":1}');
    expect(heartbeat).toEqual({ type: 'heartbeat', seq: 3, round: 1 });

    const changeset = parseFrame<AgentFrame>('event: changeset\ndata: {"turnId":"t1","text":"Done.","suggestions":2}');
    expect(changeset).toEqual({ type: 'changeset', turnId: 't1', text: 'Done.', suggestions: 2 });

    const error = parseFrame<AgentFrame>('event: error\ndata: {"turnId":"t1","error":"turn-failed","detail":null}');
    expect(error).toEqual({ type: 'error', turnId: 't1', error: 'turn-failed', detail: null });
  });

  it('returns null for malformed blocks (missing name, missing data, bad JSON)', () => {
    expect(parseFrame('data: {"seq":1}')).toBeNull();
    expect(parseFrame('event: heartbeat')).toBeNull();
    expect(parseFrame('event: heartbeat\ndata: {not json')).toBeNull();
  });
});

describe('readSseStream', () => {
  it('emits each parsed frame and skips malformed blocks without dying', async () => {
    const events: AgentFrame[] = [];
    const response = sseResponse(
      'event: loaded\ndata: {"turnId":"t1","provider":"p","model":"m"}\n\n',
      'event: broken\ndata: {oops\n\n', // malformed JSON — parseFrame → null, stream continues
      'event: heartbeat\ndata: {"seq":1}\n\n',
      'event: changeset\ndata: {"turnId":"t1","text":"Hi","suggestions":0}\n\n',
    );

    await readSseStream<AgentFrame>(response, (e) => events.push(e));

    expect(events.map((e) => e.type)).toEqual(['loaded', 'heartbeat', 'changeset']);
  });

  it('reassembles frames split across chunk boundaries', async () => {
    const events: AgentFrame[] = [];
    const response = sseResponse('event: heart', 'beat\ndata: {"se', 'q":7}\n\n');

    await readSseStream<AgentFrame>(response, (e) => events.push(e));

    expect(events).toEqual([{ type: 'heartbeat', seq: 7 }]);
  });
});

describe('httpErrorMessage', () => {
  it('prefers ProblemDetails detail, then title, then raw text, then the fallback', async () => {
    expect(await httpErrorMessage(new Response('{"title":"Conflict","detail":"A turn is already running."}'), 'f'))
      .toBe('A turn is already running.');
    expect(await httpErrorMessage(new Response('{"title":"Conflict"}'), 'f')).toBe('Conflict');
    expect(await httpErrorMessage(new Response('plain refusal'), 'f')).toBe('plain refusal');
    expect(await httpErrorMessage(new Response(''), 'fallback')).toBe('fallback');
  });
});
