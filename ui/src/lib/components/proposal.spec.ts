import { describe, expect, it } from 'vitest';
import { Type, provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { AgentProposal, AgentProposalCard } from './proposal-card';
import { AgentProposalList } from './proposal-list';

const PENDING: AgentProposal = {
  id: 'p1',
  status: 'pending',
  kindLabel: 'Update field',
  summary: 'Set access to paved.',
  changes: [
    { op: 'update', title: 'Site access', detail: 'gravel → paved' },
    { op: 'add', title: 'Note', detail: 'per photos' },
  ],
};

function create<T>(type: Type<T>, inputs: Record<string, unknown>) {
  TestBed.resetTestingModule(); // some tests create several fixtures back to back
  TestBed.configureTestingModule({ providers: [provideZonelessChangeDetection()] });
  const fixture = TestBed.createComponent(type);
  for (const [key, value] of Object.entries(inputs)) {
    fixture.componentRef.setInput(key, value);
  }
  fixture.detectChanges();
  return fixture;
}

describe('AgentProposalCard', () => {
  it('pending renders summary, decision rows and Accept/Deny intents', () => {
    const fixture = create(AgentProposalCard, { proposal: PENDING });
    const decided: string[] = [];
    fixture.componentInstance.accepted.subscribe(() => decided.push('accept'));
    fixture.componentInstance.denied.subscribe(() => decided.push('deny'));

    expect(fixture.nativeElement.textContent).toContain('Set access to paved.');
    expect(fixture.nativeElement.querySelectorAll('[data-testid="proposal-change"]')).toHaveLength(2);

    (fixture.nativeElement.querySelector('[data-testid="btn-proposal-accept"]') as HTMLButtonElement).click();
    (fixture.nativeElement.querySelector('[data-testid="btn-proposal-deny"]') as HTMLButtonElement).click();
    expect(decided).toEqual(['accept', 'deny']);
  });

  it('busy disables decisions', () => {
    const fixture = create(AgentProposalCard, { proposal: PENDING, busy: true });
    expect((fixture.nativeElement.querySelector('[data-testid="btn-proposal-accept"]') as HTMLButtonElement).disabled).toBe(true);
    expect((fixture.nativeElement.querySelector('[data-testid="btn-proposal-deny"]') as HTMLButtonElement).disabled).toBe(true);
  });

  it('row limit hides overflow behind "Show N more"', () => {
    const many: AgentProposal = {
      ...PENDING,
      changes: Array.from({ length: 9 }, (_, i) => ({ op: 'update' as const, title: `Row ${i + 1}` })),
    };
    const fixture = create(AgentProposalCard, { proposal: many, rowLimit: 6 });

    expect(fixture.nativeElement.querySelectorAll('[data-testid="proposal-change"]')).toHaveLength(6);
    const more = fixture.nativeElement.querySelector('[data-testid="proposal-more"]') as HTMLButtonElement;
    expect(more.textContent).toContain('Show 3 more');
    more.click();
    fixture.detectChanges();
    expect(fixture.nativeElement.querySelectorAll('[data-testid="proposal-change"]')).toHaveLength(9);
  });

  it('accepted shows the done message; denied shows dismissed', () => {
    const accepted = create(AgentProposalCard, { proposal: { ...PENDING, status: 'accepted', doneMessage: 'Field updated.' } });
    expect(accepted.nativeElement.querySelector('[data-testid="proposal-done"]')?.textContent).toContain('Field updated.');
    expect(accepted.nativeElement.querySelector('[data-testid="btn-proposal-accept"]')).toBeNull();

    const denied = create(AgentProposalCard, { proposal: { ...PENDING, status: 'denied' } });
    expect(denied.nativeElement.querySelector('[data-testid="proposal-dismissed"]')).toBeTruthy();
  });

  it('stale is the honest not-decidable state — never accept/deny-able', () => {
    const fixture = create(AgentProposalCard, { proposal: { ...PENDING, status: 'stale' } });
    expect(fixture.nativeElement.querySelector('[data-testid="proposal-stale"]')?.textContent).toContain('Outdated');
    expect(fixture.nativeElement.querySelector('[data-testid="btn-proposal-accept"]')).toBeNull();
    expect(fixture.nativeElement.querySelector('[data-testid="btn-proposal-deny"]')).toBeNull();
  });
});

describe('AgentProposalList', () => {
  it('multiple pending proposals gain accept-all / deny-all batch intents', () => {
    const proposals: AgentProposal[] = [
      PENDING,
      { ...PENDING, id: 'p2' },
      { ...PENDING, id: 'p3', status: 'accepted' },
    ];
    const fixture = create(AgentProposalList, { proposals });
    const batches: AgentProposal[][] = [];
    fixture.componentInstance.acceptedAll.subscribe((b) => batches.push(b));

    const acceptAll = fixture.nativeElement.querySelector('[data-testid="btn-proposal-accept-all"]') as HTMLButtonElement;
    expect(acceptAll).toBeTruthy();
    expect(fixture.nativeElement.textContent).toContain('2 proposed changes');
    acceptAll.click();

    expect(batches).toHaveLength(1);
    expect(batches[0].map((p) => p.id)).toEqual(['p1', 'p2']); // only the pending ones
  });

  it('a single pending proposal shows no batch row and forwards single intents', () => {
    const fixture = create(AgentProposalList, { proposals: [PENDING] });
    const singles: AgentProposal[] = [];
    fixture.componentInstance.accepted.subscribe((p) => singles.push(p));

    expect(fixture.nativeElement.querySelector('[data-testid="btn-proposal-accept-all"]')).toBeNull();
    (fixture.nativeElement.querySelector('[data-testid="btn-proposal-accept"]') as HTMLButtonElement).click();
    expect(singles.map((p) => p.id)).toEqual(['p1']);
  });
});
