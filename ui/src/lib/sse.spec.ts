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
  it('round-trips the v2 core frames', () => {
    const loaded = parseFrame<AgentFrame>('event: loaded\ndata: {"turnId":"t1","provider":"azure-openai","model":"gpt-5-mini"}');
    expect(loaded).toEqual({ type: 'loaded', turnId: 't1', provider: 'azure-openai', model: 'gpt-5-mini' });

    const token = parseFrame<AgentFrame>('event: token\ndata: {"delta":"Hel"}');
    expect(token).toEqual({ type: 'token', delta: 'Hel' });

    const tool = parseFrame<AgentFrame>('event: tool\ndata: {"name":"rank_comps","phase":"start","label":"Ranking comparables"}');
    expect(tool).toEqual({ type: 'tool', name: 'rank_comps', phase: 'start', label: 'Ranking comparables' });

    const heartbeat = parseFrame<AgentFrame>('event: heartbeat\ndata: {"seq":3}');
    expect(heartbeat).toEqual({ type: 'heartbeat', seq: 3 });

    const message = parseFrame<AgentFrame>('event: message\ndata: {"text":"Done."}');
    expect(message).toEqual({ type: 'message', text: 'Done.' });

    const done = parseFrame<AgentFrame>('event: done\ndata: {"turnId":"t1"}');
    expect(done).toEqual({ type: 'done', turnId: 't1' });

    const error = parseFrame<AgentFrame>('event: error\ndata: {"turnId":"t1","code":"turn_failed","detail":null}');
    expect(error).toEqual({ type: 'error', turnId: 't1', code: 'turn_failed', detail: null });
  });

  it('returns null for malformed blocks (missing name, missing data, bad JSON)', () => {
    expect(parseFrame('data: {"seq":1}')).toBeNull();
    expect(parseFrame('event: heartbeat')).toBeNull();
    expect(parseFrame('event: heartbeat\ndata: {not json')).toBeNull();
  });

  it('requires an object payload — primitives, arrays and null are rejected', () => {
    expect(parseFrame('event: token\ndata: 42')).toBeNull();
    expect(parseFrame('event: token\ndata: [1,2]')).toBeNull();
    expect(parseFrame('event: token\ndata: null')).toBeNull();
    expect(parseFrame('event: token\ndata: "text"')).toBeNull();
  });

  it('rejects invalid event names', () => {
    expect(parseFrame('event: Bad_Name!\ndata: {}')).toBeNull();
    expect(parseFrame('event: -leading\ndata: {}')).toBeNull();
    expect(parseFrame('event: UPPER\ndata: {}')).toBeNull();
  });

  it('a hostile payload type can never override the event discriminant', () => {
    const frame = parseFrame<{ type: string; delta: string }>('event: token\ndata: {"delta":"x","type":"done"}');
    expect(frame?.type).toBe('token');
    expect(frame?.delta).toBe('x');
  });
});

describe('readSseStream', () => {
  it('emits each parsed frame and skips malformed blocks without dying', async () => {
    const events: AgentFrame[] = [];
    const response = sseResponse(
      'event: loaded\ndata: {"turnId":"t1","provider":"p","model":"m"}\n\n',
      'event: broken\ndata: {oops\n\n', // malformed JSON — parseFrame → null, stream continues
      'event: heartbeat\ndata: {"seq":1}\n\n',
      'event: message\ndata: {"text":"Hi"}\n\n',
      'event: done\ndata: {"turnId":"t1"}\n\n',
    );

    await readSseStream<AgentFrame>(response, (e) => events.push(e));

    expect(events.map((e) => e.type)).toEqual(['loaded', 'heartbeat', 'message', 'done']);
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
