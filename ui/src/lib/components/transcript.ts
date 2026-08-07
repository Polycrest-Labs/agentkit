import {
  ChangeDetectionStrategy,
  Component,
  ElementRef,
  computed,
  contentChildren,
  effect,
  inject,
  input,
  output,
  signal,
} from '@angular/core';
import { NgTemplateOutlet } from '@angular/common';
import { AgentMarkdownPipe } from '../markdown.pipe';
import {
  AgentDomainEntry,
  AgentQuestionEntry,
  AgentQuestionFormEntry,
  AgentTranscriptEntry,
  AgentTurnState,
} from '../turn-state';
import { AgentQuestionForm } from '../frames';
import { AgentCardDirective } from './agent-card.directive';
import { AgentQuestionCard } from './question-card';
import { AgentThinking } from './thinking';

/**
 * The transcript: renders the state's ordered timeline — markdown bubbles, the live streaming
 * bubble (thinking dots until the first token, then text + caret), the running tool pill, durable
 * question/form cards, per-message citations footer, an optional usage line, copy-message, and
 * host-domain entries through `*agentCard` template projection.
 *
 * The host element owns this scroll region (`overflow-y: auto`; mount it in a `min-h-0` flex
 * column per the layout doctrine). Scroll sticks to the bottom on new content — but NEVER while
 * the user has scrolled up to read (threshold ~80px from the bottom); a jump-to-latest button
 * brings them back. That suppression is the improvement neither sibling copy has.
 */
@Component({
  selector: 'agent-transcript',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [NgTemplateOutlet, AgentMarkdownPipe, AgentQuestionCard, AgentThinking],
  host: {
    class: 'agentkit-transcript',
    'data-agentkit-part': 'transcript',
    '(scroll)': 'onScroll()',
  },
  template: `
    <div class="agentkit-transcript__inner" data-testid="transcript">
      @for (entry of state().timeline(); track $index) {
        @switch (entry.kind) {
          @case ('user') {
            <div class="agentkit-bubble agentkit-bubble--user" data-testid="msg-user">
              @if (entry.attachments?.length) {
                <div class="agentkit-bubble__attachments">
                  @for (a of entry.attachments; track a.assetId) {
                    <span class="agentkit-bubble__attachment" data-testid="msg-attachment">
                      @if (a.previewUrl && (a.kind === 'image' || a.contentType?.startsWith('image/'))) {
                        <img [src]="a.previewUrl" [alt]="a.originalName || 'attachment'" />
                      } @else {
                        <span class="agentkit-bubble__attachment-icon" aria-hidden="true">📄</span>
                      }
                      <span class="agentkit-bubble__attachment-name">{{ a.originalName || a.assetId }}</span>
                    </span>
                  }
                </div>
              }
              <div class="agentkit-bubble__text">{{ entry.text }}</div>
            </div>
          }
          @case ('assistant') {
            @if (entry.notice) {
              <div class="agentkit-notice" data-testid="msg-notice" role="status">{{ entry.text }}</div>
            } @else {
              <div class="agentkit-bubble agentkit-bubble--assistant" data-testid="msg-assistant">
                <div class="agentkit-prose" [innerHTML]="entry.text | agentMarkdown"></div>
                @if (entry.citations?.length) {
                  <div class="agentkit-citations" data-testid="msg-citations">
                    @for (c of entry.citations; track $index) {
                      @if (c.url) {
                        <a class="agentkit-citations__item" [href]="c.url" target="_blank" rel="noopener noreferrer">{{ c.title || c.url }}</a>
                      } @else if (c.title) {
                        <span class="agentkit-citations__item">{{ c.title }}</span>
                      }
                    }
                  </div>
                }
                <div class="agentkit-bubble__meta">
                  <button type="button" class="agentkit-bubble__copy" data-testid="btn-copy-message"
                          aria-label="Copy message" title="Copy message" (click)="copy(entry.text)">⧉</button>
                  @if (showUsage() && entry.usage; as usage) {
                    <span class="agentkit-usage" data-testid="msg-usage">{{ usageLine(usage.inputTokens, usage.outputTokens) }}</span>
                  }
                </div>
              </div>
            }
          }
          @case ('question') {
            <agent-question-card
              [form]="asForm(entry)"
              [answered]="entry.answered ?? false"
              [answerText]="entry.answerText ?? null"
              (submitted)="questionAnswered.emit({ entry, text: $event.text })"
            />
          }
          @case ('questionForm') {
            <agent-question-card
              [form]="formOf(entry)"
              [answered]="entry.answered ?? false"
              [answerText]="entry.answerText ?? null"
              (submitted)="questionAnswered.emit({ entry, text: $event.text })"
            />
          }
          @case ('domain') {
            @if (cardTemplate(entry.frameType); as tpl) {
              <ng-container *ngTemplateOutlet="tpl.template; context: { $implicit: entry.data, entry: asDomain(entry) }" />
            }
          }
        }
      }

      @if (state().streaming()) {
        <div class="agentkit-bubble agentkit-bubble--assistant agentkit-bubble--live" data-testid="msg-streaming">
          @if (state().streamingText()) {
            <div class="agentkit-prose" [innerHTML]="state().streamingText() | agentMarkdown"></div><span class="agentkit-caret" aria-hidden="true"></span>
          } @else {
            <agent-thinking />
          }
          @if (state().activeTool(); as tool) {
            <div class="agentkit-tool-pill" data-testid="tool-pill" role="status">
              <span class="agentkit-tool-pill__spinner" aria-hidden="true"></span>
              <span>{{ tool.label || tool.name }}</span>
            </div>
          }
        </div>
      }
    </div>

    @if (scrolledUp()) {
      <button type="button" class="agentkit-transcript__jump" data-testid="btn-jump-latest"
              aria-label="Jump to latest" (click)="jumpToLatest()">↓ Latest</button>
    }
  `,
})
export class AgentTranscript {
  // The state's generic parameter is irrelevant to rendering; accept any domain typing.
  // eslint-disable-next-line @typescript-eslint/no-explicit-any
  readonly state = input.required<AgentTurnState<any>>();
  readonly showUsage = input(true);
  /** Extra `*agentCard` templates forwarded by a wrapping component (content projection queries
   * only see DIRECT content — `agent-chat-panel` forwards the host's templates through this). */
  readonly cardTemplates = input<readonly AgentCardDirective[]>([]);

  /** A question/form card was submitted — the HOST posts the text as the next user turn
   * (with the structured parent link where it persists answers). */
  readonly questionAnswered = output<{ entry: AgentQuestionEntry | AgentQuestionFormEntry; text: string }>();

  private readonly hostEl = inject<ElementRef<HTMLElement>>(ElementRef);
  private readonly cards = contentChildren(AgentCardDirective);

  /** True while the user has scrolled up to read — auto-stick is suppressed. */
  protected readonly scrolledUp = signal(false);
  private readonly cardsByType = computed(() => {
    const map = new Map<string, AgentCardDirective>();
    for (const card of [...this.cards(), ...this.cardTemplates()]) {
      map.set(card.agentCard(), card);
    }
    return map;
  });

  constructor() {
    // Stick to the bottom on new content/tokens — unless the user scrolled up (then the jump
    // button brings them back). queueMicrotask lets the DOM paint the appended row first.
    effect(() => {
      this.state().timeline();
      this.state().streamingText();
      if (!this.scrolledUp()) {
        queueMicrotask(() => this.scrollToBottom());
      }
    });
  }

  protected cardTemplate(frameType: string): AgentCardDirective | undefined {
    return this.cardsByType().get(frameType);
  }

  protected asDomain(entry: AgentTranscriptEntry): AgentDomainEntry {
    return entry as AgentDomainEntry;
  }

  /** A single question renders as a 1-question form (the aws idiom — one card, one Submit). */
  protected asForm(entry: AgentQuestionEntry): AgentQuestionForm {
    return { id: entry.question.id, questions: [entry.question] };
  }

  protected formOf(entry: AgentQuestionFormEntry): AgentQuestionForm {
    return entry.form;
  }

  protected usageLine(input?: number | null, output?: number | null): string {
    const parts: string[] = [];
    if (input != null) parts.push(`${this.compact(input)} in`);
    if (output != null) parts.push(`${this.compact(output)} out`);
    return parts.join(' · ');
  }

  private compact(n: number): string {
    return n >= 1000 ? `${(n / 1000).toFixed(n >= 10_000 ? 0 : 1)}k` : String(n);
  }

  protected copy(text: string): void {
    void navigator.clipboard?.writeText(text).catch(() => { /* clipboard denied — nothing to break */ });
  }

  protected onScroll(): void {
    const el = this.hostEl.nativeElement;
    const fromBottom = el.scrollHeight - el.scrollTop - el.clientHeight;
    this.scrolledUp.set(fromBottom > 80);
  }

  protected jumpToLatest(): void {
    this.scrolledUp.set(false);
    this.scrollToBottom();
  }

  private scrollToBottom(): void {
    const el = this.hostEl.nativeElement;
    el.scrollTop = el.scrollHeight;
  }
}
