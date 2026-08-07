import { describe, expect, it } from 'vitest';
import { provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { AgentFrame } from '../frames';
import { AGENT_TRANSPORT } from '../tokens';
import { scriptedTransport } from '../testing/scripted-transport';
import { AgentTurnState } from '../turn-state';
import { AgentChatPanel } from './chat-panel';

const LOADED: AgentFrame = { type: 'loaded', turnId: 't1', provider: 'p', model: 'm' };

function flush(): Promise<void> {
  return new Promise((resolve) => setTimeout(resolve, 5));
}

function create(providers: unknown[] = [], inputs: Record<string, unknown> = {}) {
  TestBed.configureTestingModule({
    providers: [provideZonelessChangeDetection(), ...providers as []],
  });
  const fixture = TestBed.createComponent(AgentChatPanel);
  for (const [key, value] of Object.entries(inputs)) {
    fixture.componentRef.setInput(key, value);
  }
  fixture.detectChanges();
  return fixture;
}

describe('AgentChatPanel — the scripted round-trip', () => {
  it('a send flows through the transport script into the rendered transcript (frames → timeline → DOM)', async () => {
    const transport = scriptedTransport([[
      LOADED,
      { type: 'token', delta: 'All ' },
      { type: 'token', delta: 'set.' },
      { type: 'followups', prompts: ['Find comps near the subject'] },
      { type: 'message', text: 'All set — **two** comps ranked.' },
      { type: 'done', turnId: 't1' },
    ]]);
    const state = new AgentTurnState(transport);
    const fixture = create([], { state });

    state.send('rank the comps');
    fixture.detectChanges();
    await flush();
    fixture.detectChanges();

    const el = fixture.nativeElement as HTMLElement;
    expect(transport.bodies).toEqual([{ text: 'rank the comps' }]);
    expect(el.querySelector('[data-testid="msg-user"]')?.textContent).toContain('rank the comps');
    // The authoritative message wins over the streamed fallback, markdown-rendered.
    const assistant = el.querySelector('[data-testid="msg-assistant"]');
    expect(assistant?.querySelector('strong')?.textContent).toBe('two');
    // The turn's followups render as chips.
    expect(el.querySelector('[data-testid="followup-chip"]')?.textContent).toContain('Find comps near the subject');
  });

  it('a followup chip PREFILLS the composer and never auto-sends', async () => {
    const transport = scriptedTransport([[
      LOADED,
      { type: 'followups', prompts: ['Run the site workup'] },
      { type: 'message', text: 'Asked.' },
      { type: 'done', turnId: 't1' },
    ]]);
    const state = new AgentTurnState(transport);
    const fixture = create([], { state });

    state.send('hello');
    await flush();
    fixture.detectChanges();

    const el = fixture.nativeElement as HTMLElement;
    (el.querySelector('[data-testid="followup-chip"]') as HTMLButtonElement).click();
    fixture.detectChanges();
    await flush();
    fixture.detectChanges();

    const input = el.querySelector('[data-testid="composer-input"]') as HTMLTextAreaElement;
    expect(input.value).toBe('Run the site workup');
    expect(transport.bodies).toHaveLength(1); // still only the original send — prefill, not send
  });

  it('builds its own state over AGENT_TRANSPORT when no state input is given; starters show until the first message', () => {
    const transport = scriptedTransport([[LOADED, { type: 'done', turnId: 't1' }]]);
    const fixture = create(
      [{ provide: AGENT_TRANSPORT, useValue: transport }],
      { starters: ['What can you do?'] },
    );

    const el = fixture.nativeElement as HTMLElement;
    expect(el.querySelector('[data-testid="followup-chip"]')?.textContent).toContain('What can you do?');
    expect(el.querySelector('[data-testid="composer-input"]')).toBeTruthy();
  });

  it('a submitted question card auto-sends its composed answer (autoAnswer default)', async () => {
    const transport = scriptedTransport([
      [LOADED, { type: 'question', question: { id: 'q1', prompt: 'Which rights?', options: [{ id: 'o1', label: 'Fee simple' }] } }, { type: 'done', turnId: 't1' }],
      [LOADED, { type: 'message', text: 'Noted.' }, { type: 'done', turnId: 't2' }],
    ]);
    const state = new AgentTurnState(transport);
    const fixture = create([], { state });
    const answered: { text: string }[] = [];
    fixture.componentInstance.questionAnswered.subscribe((a) => answered.push({ text: a.text }));

    state.send('value the property');
    await flush();
    fixture.detectChanges();

    const el = fixture.nativeElement as HTMLElement;
    (el.querySelector('button[data-testid="question-option"]') as HTMLButtonElement).click();
    fixture.detectChanges();
    (el.querySelector('[data-testid="btn-question-submit"]') as HTMLButtonElement).click();
    await flush();
    fixture.detectChanges();

    expect(answered).toEqual([{ text: 'Fee simple' }]);
    expect(transport.bodies.map((b) => b.text)).toEqual(['value the property', 'Fee simple']);
  });

  it('Stop renders only on a serverCancel transport and stops through it', async () => {
    // Capability absent → no Stop, ever.
    const plain = new AgentTurnState(scriptedTransport([[LOADED]], {}));
    const withoutStop = create([], { state: plain });
    plain.send('hello');
    withoutStop.detectChanges();
    expect((withoutStop.nativeElement as HTMLElement).querySelector('[data-testid="btn-stop"]')).toBeNull();
    plain.abort();

    // Capability present → Stop appears while streaming and cancels server-side.
    TestBed.resetTestingModule();
    const cancelable = scriptedTransport([[LOADED]], { capabilities: { serverCancel: true } });
    const state = new AgentTurnState(cancelable);
    const fixture = create([], { state });
    state.send('hello');
    fixture.detectChanges();

    const stop = (fixture.nativeElement as HTMLElement).querySelector('[data-testid="btn-stop"]') as HTMLButtonElement;
    expect(stop).toBeTruthy();
    stop.click();
    await flush();
    fixture.detectChanges();

    expect(cancelable.cancels).toBe(1);
    expect(state.phase()).toBe('idle');
    expect((fixture.nativeElement as HTMLElement).querySelector('[data-testid="msg-notice"]')?.textContent).toContain('Stopped.');
  });
});
