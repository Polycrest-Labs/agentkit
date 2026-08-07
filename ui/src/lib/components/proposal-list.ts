import { ChangeDetectionStrategy, Component, computed, input, output } from '@angular/core';
import { AgentProposal, AgentProposalCard } from './proposal-card';

/**
 * A stack of proposal cards plus batch intents: when more than one proposal is pending, an
 * Accept all / Deny all row appears. The kit only emits the intents — the host performs the
 * authorized, idempotent decisions (singly or batched) against its own records and re-projects
 * the updated statuses.
 */
@Component({
  selector: 'agent-proposal-list',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [AgentProposalCard],
  template: `
    <div class="agentkit-proposal-list" data-agentkit-part="proposal-list" data-testid="proposal-list">
      @if (pending().length > 1) {
        <div class="agentkit-proposal-list__batch">
          <span class="agentkit-proposal-list__count">{{ pending().length }} proposed changes</span>
          <button type="button" class="agentkit-proposal__accept" data-testid="btn-proposal-accept-all"
                  (click)="acceptedAll.emit(pending())" [disabled]="busy()">
            Accept all
          </button>
          <button type="button" class="agentkit-proposal__deny" data-testid="btn-proposal-deny-all"
                  (click)="deniedAll.emit(pending())" [disabled]="busy()">
            Deny all
          </button>
        </div>
      }
      @for (p of proposals(); track p.id) {
        <agent-proposal-card
          [proposal]="p"
          [busy]="busy()"
          (accepted)="accepted.emit($event)"
          (denied)="denied.emit($event)"
        />
      }
    </div>
  `,
})
export class AgentProposalList {
  readonly proposals = input<AgentProposal[]>([]);
  readonly busy = input(false);

  readonly accepted = output<AgentProposal>();
  readonly denied = output<AgentProposal>();
  readonly acceptedAll = output<AgentProposal[]>();
  readonly deniedAll = output<AgentProposal[]>();

  protected readonly pending = computed(() => this.proposals().filter((p) => p.status === 'pending'));
}
