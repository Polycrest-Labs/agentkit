import { ChangeDetectionStrategy, Component, computed, input, output } from '@angular/core';

/**
 * Tappable next-step chips. A tap emits `prefill` — the HOST sets the composer draft and focuses
 * it; a chip must NEVER auto-send (the user reviews and presses send). `prompts` carries the
 * turn's ephemeral followup hints; `starters` are optional conversation openers a host shows
 * until the first user message (pass `[]`/omit to disable).
 */
@Component({
  selector: 'agent-followup-chips',
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    @if (visible().length) {
      <div class="agentkit-chips" data-agentkit-part="followups" role="group" [attr.aria-label]="ariaLabel()">
        @for (prompt of visible(); track prompt) {
          <button
            type="button"
            class="agentkit-chips__chip"
            data-testid="followup-chip"
            (click)="prefill.emit(prompt)"
          >{{ prompt }}</button>
        }
      </div>
    }
  `,
})
export class AgentFollowupChips {
  readonly prompts = input<string[]>([]);
  /** Openers shown while `showStarters` is true and no followups exist. */
  readonly starters = input<string[]>([]);
  /** The host flips this off once the first user message exists. */
  readonly showStarters = input(true);
  readonly ariaLabel = input('Suggested next steps');

  /** Chip taps prefill the composer — never send. */
  readonly prefill = output<string>();

  protected readonly visible = computed(() =>
    this.prompts().length ? this.prompts() : (this.showStarters() ? this.starters() : []));
}
