import { Component } from '@angular/core';
import { scriptedTransport } from '../testing/scripted-transport';
import { AgentTurnState } from '../turn-state';
import { AgentCardDirective } from './agent-card.directive';
import { AgentTranscript } from './transcript';

/**
 * Test host for the transcript spec. Lives outside the `.spec.ts` file because the Angular
 * compiler plugin transforms library sources but not spec files — a `@Component` declared inside
 * a spec would reach Node as raw decorator syntax.
 */
@Component({
  imports: [AgentTranscript, AgentCardDirective],
  template: `
    <agent-transcript [state]="state" [showUsage]="showUsage">
      <ng-template agentCard="suggestion" let-data>
        <div class="host-suggestion" data-testid="host-suggestion">{{ data.path }}</div>
      </ng-template>
    </agent-transcript>
  `,
})
export class TranscriptSpecHost {
  state = new AgentTurnState(scriptedTransport([[]]));
  showUsage = true;
}
