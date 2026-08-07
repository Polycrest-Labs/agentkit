import { ChangeDetectionStrategy, Component, input } from '@angular/core';

/**
 * The assistant "thinking" indicator: three bouncing dots (the vacadock pattern, including the
 * prefers-reduced-motion opacity fallback — see `agentkit-thinking` in `styles.css`) plus an
 * sr-only live status so screen readers hear one announcement instead of a token stream.
 */
@Component({
  selector: 'agent-thinking',
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <span class="agentkit-thinking" data-agentkit-part="thinking" data-testid="thinking" aria-hidden="true">
      <span></span><span></span><span></span>
    </span>
    <span class="agentkit-sr-only" role="status">{{ label() }}</span>
  `,
})
export class AgentThinking {
  readonly label = input('Assistant is working');
}
