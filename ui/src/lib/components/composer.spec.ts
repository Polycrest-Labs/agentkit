import { describe, expect, it } from 'vitest';
import { provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { AgentComposer } from './composer';

interface ComposerInternals {
  onKeydown(e: { key: string; shiftKey: boolean; isComposing: boolean; preventDefault(): void }): void;
  onPaste(e: {
    clipboardData: { files: File[]; getData(type: string): string } | null;
    preventDefault(): void;
  }): void;
}

function create(inputs: Record<string, unknown> = {}) {
  TestBed.configureTestingModule({ providers: [provideZonelessChangeDetection()] });
  const fixture = TestBed.createComponent(AgentComposer);
  for (const [key, value] of Object.entries(inputs)) {
    fixture.componentRef.setInput(key, value);
  }
  fixture.detectChanges();
  return fixture;
}

function typeInto(fixture: ReturnType<typeof create>, text: string): void {
  const textarea = fixture.nativeElement.querySelector('[data-testid="composer-input"]') as HTMLTextAreaElement;
  textarea.value = text;
  textarea.dispatchEvent(new Event('input', { bubbles: true }));
  fixture.detectChanges();
}

describe('AgentComposer — send rules', () => {
  it('Enter submits the trimmed draft; the composer resets', async () => {
    const fixture = create();
    const sent: string[] = [];
    fixture.componentInstance.submitted.subscribe((t) => sent.push(t));

    typeInto(fixture, '  rank the comps  ');
    (fixture.componentInstance as unknown as ComposerInternals).onKeydown({
      key: 'Enter', shiftKey: false, isComposing: false, preventDefault: () => undefined,
    });

    expect(sent).toEqual(['rank the comps']);
  });

  it('Shift+Enter and IME composition Enter never submit', () => {
    const fixture = create();
    const sent: string[] = [];
    fixture.componentInstance.submitted.subscribe((t) => sent.push(t));
    typeInto(fixture, 'line one');

    const internals = fixture.componentInstance as unknown as ComposerInternals;
    internals.onKeydown({ key: 'Enter', shiftKey: true, isComposing: false, preventDefault: () => undefined });
    internals.onKeydown({ key: 'Enter', shiftKey: false, isComposing: true, preventDefault: () => undefined });

    expect(sent).toEqual([]);
  });

  it('send is disabled while empty and while uploads are in flight', () => {
    const fixture = create({ uploading: 1 });
    typeInto(fixture, 'ready to go');
    const send = fixture.nativeElement.querySelector('[data-testid="btn-send"]') as HTMLButtonElement;
    expect(send.disabled).toBe(true); // uploading gates
    expect(fixture.nativeElement.querySelector('[data-testid="composer-uploading"]')).toBeTruthy();

    fixture.componentRef.setInput('uploading', 0);
    fixture.detectChanges();
    expect(send.disabled).toBe(false);

    typeInto(fixture, '   ');
    expect(send.disabled).toBe(true); // whitespace-only draft
  });

  it('attachments alone arm the send button', () => {
    const fixture = create({ attachments: [{ id: 'a-1', fileName: 'site.jpg', contentType: 'image/jpeg', previewUrl: 'blob:x' }] });
    const send = fixture.nativeElement.querySelector('[data-testid="btn-send"]') as HTMLButtonElement;
    expect(send.disabled).toBe(false);
    expect(fixture.nativeElement.querySelector('[data-testid="composer-attachment"]')).toBeTruthy();
  });

  it('setDraft prefills without sending (the followup-chip contract)', () => {
    const fixture = create();
    const sent: string[] = [];
    fixture.componentInstance.submitted.subscribe((t) => sent.push(t));

    fixture.componentInstance.setDraft('Find comps near the subject');
    fixture.detectChanges();

    const textarea = fixture.nativeElement.querySelector('[data-testid="composer-input"]') as HTMLTextAreaElement;
    expect(textarea.value).toBe('Find comps near the subject');
    expect(sent).toEqual([]);
  });
});

describe('AgentComposer — clipboard paste (the text/plain guard)', () => {
  const file = new File(['x'], 'shot.png', { type: 'image/png' });

  it('a files-only paste attaches; preventDefault is called', () => {
    const fixture = create();
    const files: File[][] = [];
    fixture.componentInstance.filesSelected.subscribe((f) => files.push(f));
    let prevented = false;

    (fixture.componentInstance as unknown as ComposerInternals).onPaste({
      clipboardData: { files: [file], getData: () => '' },
      preventDefault: () => { prevented = true; },
    });

    expect(files).toEqual([[file]]);
    expect(prevented).toBe(true);
  });

  it('a mixed copy carrying text/plain pastes as TEXT — no attachment', () => {
    const fixture = create();
    const files: File[][] = [];
    fixture.componentInstance.filesSelected.subscribe((f) => files.push(f));

    (fixture.componentInstance as unknown as ComposerInternals).onPaste({
      clipboardData: { files: [file], getData: () => 'the copied cell text' },
      preventDefault: () => undefined,
    });

    expect(files).toEqual([]);
  });

  it('paste while disabled does nothing', () => {
    const fixture = create({ disabled: true });
    const files: File[][] = [];
    fixture.componentInstance.filesSelected.subscribe((f) => files.push(f));

    (fixture.componentInstance as unknown as ComposerInternals).onPaste({
      clipboardData: { files: [file], getData: () => '' },
      preventDefault: () => undefined,
    });

    expect(files).toEqual([]);
  });
});

describe('AgentComposer — attachment chips', () => {
  it('remove emits the chip; image chips show a preview, documents an icon', () => {
    const image = { id: 'a-1', fileName: 'site.jpg', contentType: 'image/jpeg', previewUrl: 'blob:img' };
    const pdf = { id: 'a-2', fileName: 'lease.pdf', contentType: 'application/pdf', previewUrl: null };
    const fixture = create({ attachments: [image, pdf] });
    const removed: unknown[] = [];
    fixture.componentInstance.removeAttachment.subscribe((a) => removed.push(a));

    const chips = fixture.nativeElement.querySelectorAll('[data-testid="composer-attachment"]');
    expect(chips).toHaveLength(2);
    expect(chips[0].querySelector('img')).toBeTruthy();
    expect(chips[1].querySelector('img')).toBeNull();

    (chips[0].querySelector('[data-testid="btn-attachment-remove"]') as HTMLButtonElement).click();
    expect(removed).toEqual([image]);
  });
});
