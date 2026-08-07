import { describe, expect, it } from 'vitest';
import { provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { AgentQuestionForm } from '../frames';
import { AgentQuestionCard } from './question-card';

const FORM: AgentQuestionForm = {
  id: 'form-1',
  intro: 'A few details to get started.',
  questions: [
    {
      id: 'q1',
      prompt: 'Which rights are appraised?',
      options: [
        { id: 'o1', label: 'Fee simple' },
        { id: 'o2', label: 'Leased fee', detail: 'Subject to the current lease' },
      ],
      multiSelect: false,
      allowOther: true,
    },
    {
      id: 'q2',
      prompt: 'Site characteristics?',
      options: [
        { id: 'o1', label: 'Paved access' },
        { id: 'o2', label: 'Utilities at road' },
      ],
      multiSelect: true,
      allowOther: false,
    },
  ],
};

function create(inputs: Record<string, unknown>) {
  TestBed.configureTestingModule({ providers: [provideZonelessChangeDetection()] });
  const fixture = TestBed.createComponent(AgentQuestionCard);
  for (const [key, value] of Object.entries(inputs)) {
    fixture.componentRef.setInput(key, value);
  }
  fixture.detectChanges();
  return fixture;
}

function options(fixture: ReturnType<typeof create>): HTMLButtonElement[] {
  return Array.from(fixture.nativeElement.querySelectorAll('button[data-testid="question-option"]'));
}

function submitBtn(fixture: ReturnType<typeof create>): HTMLButtonElement {
  return fixture.nativeElement.querySelector('[data-testid="btn-question-submit"]') as HTMLButtonElement;
}

describe('AgentQuestionCard', () => {
  it('submit unlocks at the FIRST answer; skipped questions post "(skipped)" lines', () => {
    const fixture = create({ form: FORM });
    const answers: { text: string }[] = [];
    fixture.componentInstance.submitted.subscribe((a) => answers.push(a));

    expect(submitBtn(fixture).disabled).toBe(true);
    options(fixture)[0].click(); // q1: Fee simple
    fixture.detectChanges();
    expect(submitBtn(fixture).disabled).toBe(false);

    submitBtn(fixture).click();
    expect(answers).toEqual([{ text: 'Fee simple\n(skipped)' }]);
  });

  it('multi-select accumulates; single-select replaces; answers keep form order', () => {
    const fixture = create({ form: FORM });
    const answers: { text: string }[] = [];
    fixture.componentInstance.submitted.subscribe((a) => answers.push(a));

    const opts = options(fixture);
    opts[0].click(); // q1 Fee simple
    opts[1].click(); // q1 Leased fee — replaces (radio)
    opts[3].click(); // q2 Paved access
    opts[4].click(); // q2 Utilities at road — accumulates (checkbox)
    fixture.detectChanges();
    submitBtn(fixture).click();

    expect(answers).toEqual([{ text: 'Leased fee\nPaved access, Utilities at road' }]);
  });

  it('"Other" free text rides the answer line; multi-question Done label', () => {
    const fixture = create({ form: FORM });
    const answers: { text: string }[] = [];
    fixture.componentInstance.submitted.subscribe((a) => answers.push(a));

    expect(submitBtn(fixture).textContent?.trim()).toBe('Done'); // > 1 question
    const otherToggle = options(fixture)[2]; // q1's Other…
    otherToggle.click();
    fixture.detectChanges();
    const otherInput = fixture.nativeElement.querySelector('.agentkit-qcard__other-input input') as HTMLInputElement;
    otherInput.value = 'Mineral estate only';
    otherInput.dispatchEvent(new Event('input', { bubbles: true }));
    fixture.detectChanges();
    submitBtn(fixture).click();

    expect(answers).toEqual([{ text: 'Mineral estate only\n(skipped)' }]);
  });

  it('a free-text-only question renders one input; Enter submits a 1-question form', () => {
    const single: AgentQuestionForm = {
      id: 'f',
      questions: [{ id: 'q1', prompt: 'Anything unusual about access?', options: [], allowOther: true }],
    };
    const fixture = create({ form: single });
    const answers: { text: string }[] = [];
    fixture.componentInstance.submitted.subscribe((a) => answers.push(a));

    expect(submitBtn(fixture).textContent?.trim()).toBe('Send'); // single question
    const input = fixture.nativeElement.querySelector('input[data-testid="question-option"]') as HTMLInputElement;
    input.value = 'Easement across the north line';
    input.dispatchEvent(new Event('input', { bubbles: true }));
    fixture.detectChanges();
    input.dispatchEvent(new KeyboardEvent('keydown', { key: 'Enter', bubbles: true }));

    expect(answers).toEqual([{ text: 'Easement across the north line' }]);
  });

  it('answered locks every control and shows the durable answer text', () => {
    const fixture = create({ form: FORM, answered: true, answerText: 'Fee simple / Paved access' });

    expect(fixture.nativeElement.querySelector('[data-testid="question-answered"]')).toBeTruthy();
    expect(fixture.nativeElement.textContent).toContain('Answered');
    expect(fixture.nativeElement.textContent).toContain('Fee simple / Paved access');
    expect(fixture.nativeElement.querySelector('[data-testid="btn-question-submit"]')).toBeNull();
    for (const opt of options(fixture)) {
      expect(opt.disabled).toBe(true);
    }
  });
});
