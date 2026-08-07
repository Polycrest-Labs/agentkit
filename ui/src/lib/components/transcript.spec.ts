import { describe, expect, it } from 'vitest';
import { provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { AgentTurnState } from '../turn-state';
import { AgentTranscript } from './transcript';
import { TranscriptSpecHost } from './transcript-host.fixture';

function create(): { fixture: ReturnType<typeof TestBed.createComponent<TranscriptSpecHost>>; state: AgentTurnState } {
  TestBed.configureTestingModule({ providers: [provideZonelessChangeDetection()] });
  const fixture = TestBed.createComponent(TranscriptSpecHost);
  fixture.detectChanges();
  return { fixture, state: fixture.componentInstance.state };
}

/** A direct transcript fixture — inputs driven through setInput (signal-input change detection). */
function createDirect(state: AgentTurnState, showUsage = true) {
  TestBed.resetTestingModule();
  TestBed.configureTestingModule({ providers: [provideZonelessChangeDetection()] });
  const fixture = TestBed.createComponent(AgentTranscript);
  fixture.componentRef.setInput('state', state);
  fixture.componentRef.setInput('showUsage', showUsage);
  fixture.detectChanges();
  return fixture;
}

function flush(): Promise<void> {
  return new Promise((resolve) => setTimeout(resolve, 0));
}

describe('AgentTranscript', () => {
  it('renders the hydrated rich timeline in order: bubbles, markdown, cards, domain projection', async () => {
    const { fixture, state } = create();
    state.hydrate([
      { kind: 'user', text: 'value the property', attachments: [{ assetId: 'a-1', originalName: 'site.jpg', contentType: 'image/jpeg', previewUrl: 'blob:x' }] },
      { kind: 'assistant', text: 'Here is **the answer** with a [link](https://x.test).', citations: [{ title: 'TxGIO', url: 'https://txgio.test' }], usage: { inputTokens: 1200, outputTokens: 340 } },
      { kind: 'question', question: { id: 'q1', prompt: 'Which rights?' }, answered: true, answerText: 'Fee simple' },
      { kind: 'domain', frameType: 'suggestion', data: { section: 'site', path: 'access' } },
      { kind: 'assistant', text: 'One turn at a time.', notice: true },
    ]);
    fixture.detectChanges();
    await flush();
    fixture.detectChanges();

    const el = fixture.nativeElement as HTMLElement;
    expect(el.querySelector('[data-testid="msg-user"]')?.textContent).toContain('value the property');
    expect(el.querySelector('[data-testid="msg-attachment"]')?.textContent).toContain('site.jpg');
    const assistant = el.querySelector('[data-testid="msg-assistant"]')!;
    expect(assistant.querySelector('strong')?.textContent).toBe('the answer'); // markdown rendered
    expect((assistant.querySelector('.agentkit-prose a') as HTMLAnchorElement).href).toContain('x.test');
    const citation = el.querySelector('[data-testid="msg-citations"] a') as HTMLAnchorElement;
    expect(citation.href).toContain('txgio.test');
    expect(citation.textContent).toContain('TxGIO');
    expect(el.querySelector('[data-testid="msg-usage"]')?.textContent).toContain('1.2k in · 340 out');
    expect(el.querySelector('[data-testid="question-card"]')?.textContent).toContain('Which rights?');
    expect(el.querySelector('[data-testid="question-answered"]')?.textContent).toContain('Fee simple');
    expect(el.querySelector('[data-testid="host-suggestion"]')?.textContent).toBe('access'); // *agentCard projection
    expect(el.querySelector('[data-testid="msg-notice"]')?.textContent).toContain('One turn at a time.');
  });

  it('usage line hides when showUsage is off', async () => {
    const state = new AgentTurnState({ streamTurn: () => Promise.resolve() });
    state.hydrate([{ kind: 'assistant', text: 'answer', usage: { inputTokens: 10, outputTokens: 2 } }]);
    const fixture = createDirect(state, false);
    await flush();
    fixture.detectChanges();

    expect((fixture.nativeElement as HTMLElement).querySelector('[data-testid="msg-usage"]')).toBeNull();
    expect((fixture.nativeElement as HTMLElement).querySelector('[data-testid="msg-assistant"]')).toBeTruthy();
  });

  it('shows thinking dots before the first token, then the live streaming bubble with tool pill', async () => {
    let emit: (f: { type: string; [k: string]: unknown }) => void = () => undefined;
    const transport = {
      streamTurn: (_b: unknown, onFrame: (f: never) => void) => {
        emit = onFrame as typeof emit;
        return new Promise<void>(() => { /* held open */ });
      },
    };
    const state2 = new AgentTurnState(transport as never);
    const fixture = createDirect(state2);

    state2.send('hello');
    fixture.detectChanges();
    const el = fixture.nativeElement as HTMLElement;
    expect(el.querySelector('[data-testid="msg-streaming"]')).toBeTruthy();
    expect(el.querySelector('[data-testid="thinking"]')).toBeTruthy();

    emit({ type: 'token', delta: 'Hel' });
    emit({ type: 'tool', name: 'rank_comps', phase: 'start', label: 'Ranking comparables' });
    fixture.detectChanges();

    expect(el.querySelector('[data-testid="thinking"]')).toBeNull();
    expect(el.querySelector('[data-testid="msg-streaming"]')?.textContent).toContain('Hel');
    expect(el.querySelector('[data-testid="tool-pill"]')?.textContent).toContain('Ranking comparables');
  });

  it('suppresses auto-stick while the user scrolled up and offers jump-to-latest', async () => {
    const { fixture, state } = create();
    state.hydrate([{ kind: 'assistant', text: 'first' }]);
    fixture.detectChanges();
    await flush(); // drain the initial stick-to-bottom scroll before staging the scrolled-up reader

    const host = (fixture.nativeElement as HTMLElement).querySelector('agent-transcript') as HTMLElement;
    // jsdom has no layout — model a scrollable region and a reader who scrolled up.
    Object.defineProperty(host, 'scrollHeight', { value: 1000, configurable: true });
    Object.defineProperty(host, 'clientHeight', { value: 400, configurable: true });
    host.scrollTop = 100; // 500px from the bottom — reading history
    host.dispatchEvent(new Event('scroll'));
    fixture.detectChanges();

    const jump = (fixture.nativeElement as HTMLElement).querySelector('[data-testid="btn-jump-latest"]') as HTMLButtonElement;
    expect(jump).toBeTruthy();

    // New content while scrolled up must NOT yank the position.
    state.append({ kind: 'assistant', text: 'second' });
    fixture.detectChanges();
    await flush();
    expect(host.scrollTop).toBe(100);

    jump.click();
    fixture.detectChanges();
    expect(host.scrollTop).toBe(1000); // jumped to the latest
    expect((fixture.nativeElement as HTMLElement).querySelector('[data-testid="btn-jump-latest"]')).toBeNull();
  });

  it('copy button writes the message text to the clipboard', async () => {
    const { fixture, state } = create();
    const written: string[] = [];
    Object.defineProperty(navigator, 'clipboard', {
      value: { writeText: (t: string) => { written.push(t); return Promise.resolve(); } },
      configurable: true,
    });
    state.hydrate([{ kind: 'assistant', text: 'copy me' }]);
    fixture.detectChanges();
    await flush();
    fixture.detectChanges();

    ((fixture.nativeElement as HTMLElement).querySelector('[data-testid="btn-copy-message"]') as HTMLButtonElement).click();
    expect(written).toEqual(['copy me']);
  });
});
