import { Directive, TemplateRef, inject, input } from '@angular/core';
import { AgentDomainEntry } from '../turn-state';

/** The template context an `*agentCard` template receives. */
export interface AgentCardContext {
  /** The domain entry's data payload. */
  $implicit: unknown;
  /** The full timeline entry (id, timestamps). */
  entry: AgentDomainEntry;
}

/**
 * Host-domain card projection: the host registers a template per domain frame type and the
 * transcript renders matching {@link AgentDomainEntry} rows through it —
 *
 * ```html
 * <agent-transcript [state]="state">
 *   <ng-template agentCard="suggestion" let-data let-entry="entry">…host card…</ng-template>
 * </agent-transcript>
 * ```
 *
 * Domain entries with no registered template render nothing (the host owns their meaning).
 */
@Directive({ selector: 'ng-template[agentCard]' })
export class AgentCardDirective {
  /** The domain frame type this template renders (e.g. `suggestion`). */
  readonly agentCard = input.required<string>();
  readonly template = inject(TemplateRef<AgentCardContext>);

  static ngTemplateContextGuard(_dir: AgentCardDirective, ctx: unknown): ctx is AgentCardContext {
    return true;
  }
}
