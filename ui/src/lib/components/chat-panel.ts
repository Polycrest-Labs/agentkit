import {
  ChangeDetectionStrategy,
  Component,
  computed,
  contentChildren,
  inject,
  input,
  output,
  viewChild,
} from '@angular/core';
import { AGENT_TRANSPORT } from '../tokens';
import {
  AgentQuestionEntry,
  AgentQuestionFormEntry,
  AgentTurnState,
  AgentTurnStateOptions,
  SendAgentTurnOptions,
} from '../turn-state';
import { AgentCardDirective } from './agent-card.directive';
import { AgentComposer } from './composer';
import { AgentFollowupChips } from './followup-chips';
import { AgentTranscript } from './transcript';

/**
 * The assembled chat surface: transcript above, followup chips, composer flex-pinned at the
 * bottom — one `min-h-0` flex column per the layout doctrine (the transcript owns the scroll
 * region; short transcripts bottom-hug via `justify-end`; NEVER a `calc(100vh-…)` magic number).
 *
 * Injects {@link AGENT_TRANSPORT} and constructs its own {@link AgentTurnState} — or accepts a
 * host-constructed one via the `state` input. Hosts wanting different chrome compose the pieces
 * (`agent-transcript` / `agent-followup-chips` / `agent-composer`) directly instead.
 *
 * Followup chips PREFILL the composer and focus it — never auto-send. A visible Stop renders only
 * when the transport advertises `serverCancel`. Question cards submit through the host by default
 * (`autoAnswer` sends the composed text as the next turn; hosts persisting structured
 * parent-linked answers set `autoAnswer=false` and handle `questionAnswered`).
 */
@Component({
  selector: 'agent-chat-panel',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [AgentComposer, AgentFollowupChips, AgentTranscript],
  host: { class: 'agentkit-panel', 'data-agentkit-part': 'panel' },
  template: `
    <agent-transcript
      [state]="turnState()"
      [showUsage]="showUsage()"
      [cardTemplates]="cards()"
      (questionAnswered)="onAnswer($event)"
    />

    <div class="agentkit-panel__foot">
      @if (turnState().canStop()) {
        <div class="agentkit-panel__stop-row">
          <button type="button" class="agentkit-stop" data-testid="btn-stop" (click)="stop()">
            <span class="agentkit-stop__icon" aria-hidden="true">■</span> Stop
          </button>
        </div>
      }
      <agent-followup-chips
        [prompts]="turnState().followups()"
        [starters]="starters()"
        [showStarters]="!hasUserMessage()"
        (prefill)="prefill($event)"
      />
      <agent-composer
        [attachments]="turnState().pendingAttachments()"
        [uploading]="turnState().uploadsInFlight()"
        [disabled]="turnState().composerDisabled()"
        [placeholder]="placeholder()"
        (submitted)="send($event)"
        (filesSelected)="turnState().addFiles($event)"
        (removeAttachment)="turnState().removePending($event)"
      />
    </div>
  `,
})
export class AgentChatPanel {
  /** Host-constructed state (optional — omitted, the panel builds one over AGENT_TRANSPORT). */
  // eslint-disable-next-line @typescript-eslint/no-explicit-any
  readonly state = input<AgentTurnState<any> | null>(null);
  /** Options for the panel-owned state (ignored when `state` is provided). */
  readonly stateOptions = input<AgentTurnStateOptions>({});
  /** Per-send options (domain frame hook, extra body) applied to every panel-initiated send. */
  // eslint-disable-next-line @typescript-eslint/no-explicit-any
  readonly sendOptions = input<SendAgentTurnOptions<any>>({});
  readonly placeholder = input('Reply to assistant');
  readonly showUsage = input(true);
  /** Conversation starters shown until the first user message. */
  readonly starters = input<string[]>([]);
  /** When true (default) a submitted question card sends its composed text as the next turn.
   * Hosts persisting structured parent-linked answers set false and handle `questionAnswered`. */
  readonly autoAnswer = input(true);

  readonly questionAnswered = output<{ entry: AgentQuestionEntry | AgentQuestionFormEntry; text: string }>();

  protected readonly cards = contentChildren(AgentCardDirective, { descendants: true });
  private readonly composer = viewChild(AgentComposer);
  private readonly transport = inject(AGENT_TRANSPORT, { optional: true });

  // eslint-disable-next-line @typescript-eslint/no-explicit-any
  private ownState: AgentTurnState<any> | null = null;

  // eslint-disable-next-line @typescript-eslint/no-explicit-any
  protected readonly turnState = computed<AgentTurnState<any>>(() => {
    const provided = this.state();
    if (provided) {
      return provided;
    }
    if (!this.transport) {
      throw new Error('agent-chat-panel needs either a [state] input or an AGENT_TRANSPORT provider.');
    }
    this.ownState ??= new AgentTurnState(this.transport, this.stateOptions());
    return this.ownState;
  });

  protected readonly hasUserMessage = computed(() =>
    this.turnState().timeline().some((e) => e.kind === 'user'));

  protected send(text: string): void {
    this.turnState().send(text, this.sendOptions());
  }

  protected stop(): void {
    void this.turnState().stop();
  }

  /** Chip tap: prefill + focus — the user reviews and presses send themselves. */
  protected prefill(text: string): void {
    this.composer()?.setDraft(text);
    this.composer()?.focusInput();
  }

  protected onAnswer(event: { entry: AgentQuestionEntry | AgentQuestionFormEntry; text: string }): void {
    this.questionAnswered.emit(event);
    if (this.autoAnswer()) {
      this.turnState().send(event.text, this.sendOptions());
    }
  }
}
