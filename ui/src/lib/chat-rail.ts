import { ChangeDetectionStrategy, Component, effect, inject, input, output, signal, untracked } from '@angular/core';
import { AGENT_CONFIRM, AgentChatCapable, AgentChatSummary } from './tokens';

/**
 * The far-left chats rail shared by multi-chat agent surfaces: list (newest activity first),
 * new-chat, delete-with-confirm, an "in progress" pulse on chats with a running turn, and the
 * collapse hamburger. Ported from the aws chat kit (commit 28131fd) with its two couplings cut:
 * the transport is the {@link AgentChatCapable} capability (an input, host-provided) and the
 * delete confirm rides the {@link AGENT_CONFIRM} token (package-owned default). Same DOM and
 * data-testids (chat-list / chat-item / btn-new-chat / btn-delete-chat / btn-collapse-chats).
 * The parent owns which chat is active. Styled by the kit stylesheet's `agentkit-rail__*`
 * classes on `--agentkit-*` tokens (data-testids are test API, never styling API).
 */
@Component({
  selector: 'agent-chat-rail',
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: { class: 'agentkit-rail-host' },
  template: `
    <aside data-testid="chat-list" class="agentkit-rail" data-agentkit-part="chat-rail">
      <div class="agentkit-rail__head">
        <h2 class="agentkit-rail__title">Chats</h2>
        <div class="agentkit-rail__head-actions">
          <button
            type="button"
            data-testid="btn-new-chat"
            class="agentkit-rail__new"
            [disabled]="creating()"
            (click)="newChat()"
          >
            + New
          </button>
          <button
            type="button"
            data-testid="btn-collapse-chats"
            class="agentkit-rail__collapse"
            aria-label="Hide chat list"
            title="Hide chat list"
            (click)="collapse.emit()"
          >
            <svg viewBox="0 0 24 24" aria-hidden="true"><path d="M11 17l-5-5 5-5" /><path d="M18 17l-5-5 5-5" /></svg>
          </button>
        </div>
      </div>
      <ul class="agentkit-rail__list">
        @for (c of chats(); track c.chatId) {
          <li class="agentkit-rail__row">
            <button
              type="button"
              data-testid="chat-item"
              class="agentkit-rail__item"
              [class.agentkit-rail__item--active]="activeChatId() === c.chatId"
              (click)="selected.emit(c.chatId)"
            >
              <span class="agentkit-rail__item-title">{{ c.title }}</span>
              @if (c.inProgress) {
                <span class="agentkit-rail__pulse" role="status" aria-label="Turn in progress"></span>
              }
            </button>
            <button
              type="button"
              data-testid="btn-delete-chat"
              class="agentkit-rail__delete"
              [attr.aria-label]="'Delete chat ' + c.title"
              (click)="remove(c)"
            >
              ✕
            </button>
          </li>
        } @empty {
          <li class="agentkit-rail__empty">No chats yet — start one.</li>
        }
      </ul>
    </aside>
  `,
})
export class AgentChatRail {
  private readonly confirmDialog = inject(AGENT_CONFIRM);

  readonly transport = input.required<AgentChatCapable>();
  readonly activeChatId = input<number | null>(null);
  /** Sentence appended to the delete confirm ("Delete chat X? <detail>"). */
  readonly deleteDetail = input.required<string>();
  readonly selected = output<number>();
  /** Emits after a delete so the parent can clear/move the active selection. */
  readonly deleted = output<number>();
  /** Emits when the user hides the rail (the parent shows a reopen hamburger). */
  readonly collapse = output<void>();

  protected readonly chats = signal<AgentChatSummary[]>([]);
  protected readonly creating = signal(false);

  constructor() {
    // Load once the transport input lands (inputs aren't set at construction time).
    effect(() => {
      this.transport();
      untracked(() => this.refresh());
    });
  }

  /** Reload summaries (parent calls this after a turn retitles a chat). */
  refresh(): void {
    this.transport().listChats()
      .then((list) => this.chats.set(list))
      .catch(() => this.chats.set([]));
  }

  protected newChat(): void {
    this.creating.set(true);
    this.transport().createChat()
      .then((chat) => {
        this.chats.update((list) => [chat, ...list]);
        this.selected.emit(chat.chatId);
      })
      .catch(() => { /* list stays as-is; the user can retry */ })
      .finally(() => this.creating.set(false));
  }

  protected async remove(chat: AgentChatSummary): Promise<void> {
    const confirmed = await this.confirmDialog.ask({
      title: 'Delete chat',
      message: `Delete chat "${chat.title}"? ${this.deleteDetail()}`,
    });
    if (!confirmed) {
      return;
    }
    this.transport().deleteChat(chat.chatId)
      .then(() => {
        this.chats.update((list) => list.filter((c) => c.chatId !== chat.chatId));
        this.deleted.emit(chat.chatId);
      })
      .catch(() => { /* keep the row; the user can retry */ });
  }
}
