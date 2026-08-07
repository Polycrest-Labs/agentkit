import { ChangeDetectionStrategy, Component, computed, input, output, signal } from '@angular/core';

/** One decision row inside a proposal (host-built — the kit never infers diffs from requests). */
export interface AgentProposalChange {
  op: 'add' | 'update' | 'remove' | 'keep';
  title: string;
  detail?: string | null;
}

export type AgentProposalStatus = 'pending' | 'accepted' | 'denied' | 'stale';

/**
 * A presentation-only proposal record. Proposals are HOST-DOMAIN data: the host owns the durable
 * records, authorization, freshness, idempotency, persistence and decision endpoints — the kit
 * only renders the states and emits intents. There is deliberately NO core proposal wire frame.
 */
export interface AgentProposal {
  id: string;
  status: AgentProposalStatus;
  /** Short kind label shown in the header pill (e.g. "Update field"). */
  kindLabel?: string | null;
  /** Single-character/emoji icon for the header badge. */
  icon?: string | null;
  summary?: string | null;
  changes?: AgentProposalChange[];
  /** Shown for an accepted proposal (default "Change applied."). */
  doneMessage?: string | null;
  /** Shown for a stale proposal (default wording below). */
  staleMessage?: string | null;
}

/**
 * The proposal card — vacadock's presentation behavior, generic: pending shows summary + decision
 * rows (row-limited with "Show N more") + Accept/Deny; accepted shows the done message; denied
 * shows "dismissed"; stale is the honest not-decidable state (never accept/deny-able — a deny
 * would render as "you declined", which didn't happen). `busy` disables the decision buttons
 * while the host's authorized, idempotent decision request runs.
 */
@Component({
  selector: 'agent-proposal-card',
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    @if (proposal().status === 'accepted') {
      <div class="agentkit-proposal__done" data-testid="proposal-done">
        <span aria-hidden="true">✓</span>
        <span>{{ proposal().doneMessage || 'Change applied.' }}</span>
      </div>
    } @else if (proposal().status === 'denied') {
      <div class="agentkit-proposal__dismissed" data-testid="proposal-dismissed">
        Proposal dismissed
      </div>
    } @else if (proposal().status === 'stale') {
      <div class="agentkit-proposal__stale" data-testid="proposal-stale">
        <span aria-hidden="true">⏳</span>
        <span>{{ proposal().staleMessage || 'Outdated — the underlying data changed since this was proposed.' }}</span>
      </div>
    } @else {
      <section class="agentkit-proposal" data-agentkit-part="proposal-card" data-testid="proposal-card" aria-label="Proposed change">
        <div class="agentkit-proposal__head">
          <span class="agentkit-proposal__icon" aria-hidden="true">{{ proposal().icon || '✎' }}</span>
          <div class="agentkit-proposal__headline">
            <div class="agentkit-proposal__title-row">
              <span class="agentkit-proposal__title">Proposed change</span>
              @if (proposal().kindLabel) {
                <span class="agentkit-proposal__kind">{{ proposal().kindLabel }}</span>
              }
            </div>
            <p class="agentkit-proposal__summary">{{ proposal().summary || 'Review the details below before accepting.' }}</p>
          </div>
        </div>

        <div class="agentkit-proposal__changes">
          @for (ch of visibleChanges(); track $index) {
            <div class="agentkit-proposal__change" data-testid="proposal-change">
              <span class="agentkit-proposal__op" [attr.data-op]="ch.op" aria-hidden="true">{{ opLabel(ch) }}</span>
              <div class="agentkit-proposal__change-body">
                <div class="agentkit-proposal__change-title">{{ ch.title }}</div>
                @if (ch.detail) {
                  <div class="agentkit-proposal__change-detail">{{ ch.detail }}</div>
                }
              </div>
            </div>
          } @empty {
            <div class="agentkit-proposal__no-detail">No diff details were included.</div>
          }
        </div>

        @if (hiddenCount() > 0) {
          <button type="button" class="agentkit-proposal__more" data-testid="proposal-more" (click)="expanded.set(!expanded())">
            {{ expanded() ? 'Show fewer details' : ('Show ' + hiddenCount() + ' more') }}
          </button>
        }

        <div class="agentkit-proposal__actions">
          <button type="button" class="agentkit-proposal__accept" data-testid="btn-proposal-accept"
                  (click)="accepted.emit(proposal())" [disabled]="busy()">
            Accept
          </button>
          <button type="button" class="agentkit-proposal__deny" data-testid="btn-proposal-deny"
                  (click)="denied.emit(proposal())" [disabled]="busy()">
            Deny
          </button>
        </div>
      </section>
    }
  `,
})
export class AgentProposalCard {
  readonly proposal = input.required<AgentProposal>();
  readonly busy = input(false);
  /** Rows shown before "Show N more" expands the rest. */
  readonly rowLimit = input(6);

  readonly accepted = output<AgentProposal>();
  readonly denied = output<AgentProposal>();

  protected readonly expanded = signal(false);

  protected readonly visibleChanges = computed(() => {
    const changes = this.proposal().changes ?? [];
    return this.expanded() ? changes : changes.slice(0, this.rowLimit());
  });

  protected readonly hiddenCount = computed(() => {
    if (this.expanded()) return 0;
    return Math.max(0, (this.proposal().changes ?? []).length - this.visibleChanges().length);
  });

  protected opLabel(change: AgentProposalChange): string {
    switch (change.op) {
      case 'add': return '+';
      case 'remove': return '−';
      case 'keep': return '=';
      default: return '~';
    }
  }
}
