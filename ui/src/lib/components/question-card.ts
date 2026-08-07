import { ChangeDetectionStrategy, Component, computed, input, output, signal } from '@angular/core';
import { AgentQuestion, AgentQuestionForm } from '../frames';

interface QState {
  selected: ReadonlySet<string>;
  other: string;
  otherOpen: boolean;
}

/**
 * The transport-agnostic question card — the assistant's "pick one / pick many / or tell me"
 * prompt (ported from the aws `ai-question-card`, behavior kept 1:1; Tailwind restyled onto the
 * kit's tokened classes). Renders a form of stacked fieldsets with one Submit; on submit it
 * composes one human-readable answer — one line per question, in form order, selected labels plus
 * any "Other" text — which the host posts as the next user message. The host locks the card once
 * a later message exists (`answered`), optionally showing the durable `answerText`.
 */
@Component({
  selector: 'agent-question-card',
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <section data-testid="question-card" class="agentkit-qcard" data-agentkit-part="question-card" [attr.aria-busy]="busy()">
      @if (form().intro; as intro) {
        <p class="agentkit-qcard__intro">
          <span class="agentkit-qcard__intro-badge" aria-hidden="true">?</span>
          <span>{{ intro }}</span>
        </p>
      }

      @for (q of form().questions ?? []; track q.id) {
        <fieldset class="agentkit-qcard__fieldset">
          <legend class="agentkit-qcard__legend">{{ q.prompt }}</legend>
          @if (freeTextOnly(q)) {
            @if (!locked()) {
              <input type="text"
                     data-testid="question-option"
                     class="agentkit-qcard__text-input"
                     [value]="otherText(q.id)"
                     (input)="setOther(q, $event)"
                     (keydown.enter)="onFreeTextEnter($event)"
                     [attr.aria-label]="q.prompt"
                     [placeholder]="q.otherPlaceholder || 'Type your answer…'" />
            } @else {
              <p class="agentkit-qcard__locked-text">{{ otherText(q.id) || '—' }}</p>
            }
          } @else {
            <div class="agentkit-qcard__options" [attr.role]="q.multiSelect ? 'group' : 'radiogroup'" [attr.aria-label]="q.prompt">
              @for (opt of q.options ?? []; track opt.id) {
                <button type="button"
                        data-testid="question-option"
                        class="agentkit-qcard__option"
                        [class.agentkit-qcard__option--selected]="isSelected(q.id, opt.id)"
                        [attr.role]="q.multiSelect ? 'checkbox' : 'radio'"
                        [attr.aria-checked]="isSelected(q.id, opt.id)"
                        [disabled]="locked()"
                        (click)="toggle(q, opt.id)">
                  <span aria-hidden="true"
                        class="agentkit-qcard__mark"
                        [class.agentkit-qcard__mark--round]="!q.multiSelect"
                        [class.agentkit-qcard__mark--on]="isSelected(q.id, opt.id)">
                    {{ isSelected(q.id, opt.id) ? '✓' : '' }}
                  </span>
                  <span class="agentkit-qcard__option-text">
                    <span class="agentkit-qcard__option-label">{{ opt.label }}</span>
                    @if (opt.detail) {
                      <span class="agentkit-qcard__option-detail">{{ opt.detail }}</span>
                    }
                  </span>
                </button>
              }

              @if (q.allowOther ?? true) {
                <div class="agentkit-qcard__other" [class.agentkit-qcard__other--open]="otherOpen(q.id)">
                  <button type="button"
                          data-testid="question-option"
                          class="agentkit-qcard__option agentkit-qcard__option--bare"
                          [attr.aria-expanded]="otherOpen(q.id)"
                          [disabled]="locked()"
                          (click)="toggleOther(q)">
                    <span aria-hidden="true"
                          class="agentkit-qcard__mark"
                          [class.agentkit-qcard__mark--on]="otherOpen(q.id)">
                      {{ otherOpen(q.id) ? '✓' : '' }}
                    </span>
                    <span class="agentkit-qcard__option-label">Other…</span>
                  </button>
                  @if (otherOpen(q.id) && !locked()) {
                    <div class="agentkit-qcard__other-input">
                      <input type="text"
                             class="agentkit-qcard__text-input"
                             [value]="otherText(q.id)"
                             (input)="setOther(q, $event)"
                             [attr.aria-label]="'Other answer for: ' + q.prompt"
                             [placeholder]="q.otherPlaceholder || 'Type your answer…'" />
                    </div>
                  }
                </div>
              }
            </div>
          }
        </fieldset>
      }

      @if (locked()) {
        <div class="agentkit-qcard__answered" data-testid="question-answered">
          <span aria-hidden="true">✓</span><span>Answered</span>
          @if (answerText()) {
            <span class="agentkit-qcard__answer-text">{{ answerText() }}</span>
          }
        </div>
      } @else {
        <div class="agentkit-qcard__actions">
          <button type="button"
                  data-testid="btn-question-submit"
                  class="agentkit-qcard__submit"
                  (click)="submit()"
                  [disabled]="!canSubmit() || busy()">
            {{ (form().questions ?? []).length > 1 ? 'Done' : 'Send' }}
          </button>
        </div>
      }
    </section>
  `,
})
export class AgentQuestionCard {
  readonly form = input.required<AgentQuestionForm>();
  readonly busy = input(false);
  /** Lock the card (read-only) once a later message exists — the answer already went. */
  readonly answered = input(false);
  /** The durable answer text (host-hydrated), shown on the locked card. */
  readonly answerText = input<string | null>(null);

  /** One human-readable answer: one line per question, in form order. */
  readonly submitted = output<{ text: string }>();

  /** Per-question selection state, keyed by question id. */
  protected readonly state = signal<ReadonlyMap<string, QState>>(new Map());

  protected readonly locked = computed(() => this.answered());
  /** Submit unlocks at the FIRST answer — real intakes have unknowns, and forcing "unknown"
   * placeholders into every field was an e2e review finding. Skipped questions post as
   * "(skipped)" so the one-line-per-question mapping stays aligned. */
  protected readonly canSubmit = computed(() =>
    (this.form().questions ?? []).some((q) => {
      const s = this.state().get(q.id);
      return (s?.selected.size ?? 0) > 0 || (s?.other.trim().length ?? 0) > 0;
    }),
  );

  protected isSelected(qid: string, oid: string): boolean {
    return this.state().get(qid)?.selected.has(oid) ?? false;
  }

  protected otherOpen(qid: string): boolean {
    return this.state().get(qid)?.otherOpen ?? false;
  }

  protected otherText(qid: string): string {
    return this.state().get(qid)?.other ?? '';
  }

  protected freeTextOnly(q: AgentQuestion): boolean {
    return (q.allowOther ?? true) && (q.options ?? []).length === 0;
  }

  protected toggle(q: AgentQuestion, oid: string): void {
    if (this.locked()) {
      return;
    }
    this.update(q.id, (st) => {
      const selected = new Set(st.selected);
      if (q.multiSelect) {
        if (selected.has(oid)) {
          selected.delete(oid);
        } else {
          selected.add(oid);
        }
        return { ...st, selected };
      }
      const had = selected.has(oid);
      selected.clear();
      if (!had) {
        selected.add(oid);
      }
      return { ...st, selected, other: '', otherOpen: false };
    });
  }

  protected toggleOther(q: AgentQuestion): void {
    if (this.locked()) {
      return;
    }
    this.update(q.id, (st) => ({
      ...st,
      selected: q.multiSelect ? st.selected : new Set<string>(),
      otherOpen: !st.otherOpen,
      other: st.otherOpen ? '' : st.other,
    }));
  }

  protected setOther(q: AgentQuestion, event: Event): void {
    const value = (event.target as HTMLInputElement).value;
    this.update(q.id, (st) => ({ ...st, other: value }));
  }

  /** Enter in a free-text field submits ONLY a single-question form. In a multi-question intake,
   * canSubmit() is already true after the first answer, so Enter would post every other question
   * as "(skipped)" and lock the card prematurely — use the Done button instead. */
  protected onFreeTextEnter(event: Event): void {
    event.preventDefault();
    if ((this.form().questions ?? []).length === 1) {
      this.submit();
    }
  }

  protected submit(): void {
    if (this.locked() || !this.canSubmit() || this.busy()) {
      return;
    }

    // One line per question (in form order): the selected labels + any "Other" text; an
    // unanswered question posts "(skipped)" so the line↔question mapping stays aligned.
    const lines = (this.form().questions ?? []).map((q) => {
      const s = this.state().get(q.id);
      const byId = new Map((q.options ?? []).map((o) => [o.id, o.label]));
      const labels = (s ? [...s.selected] : []).map((id) => byId.get(id) ?? id);
      const other = (s?.other ?? '').trim();
      if (other) {
        labels.push(other);
      }
      return labels.length ? labels.join(', ') : '(skipped)';
    });

    this.submitted.emit({ text: lines.join('\n') });
  }

  private update(qid: string, fn: (st: QState) => QState): void {
    this.state.update((m) => {
      const next = new Map(m);
      next.set(qid, fn(next.get(qid) ?? { selected: new Set<string>(), other: '', otherOpen: false }));
      return next;
    });
  }
}
