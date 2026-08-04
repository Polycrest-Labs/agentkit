import { ChangeDetectionStrategy, Component, effect, inject, input, output, signal, untracked } from '@angular/core';
import { AGENT_CONFIRM, AgentChatCapable, AgentChatSummary } from './tokens';

/**
 * The far-left chats rail shared by multi-chat agent surfaces: list (newest activity first),
 * new-chat, delete-with-confirm, an "in progress" pulse on chats with a running turn, and the
 * collapse hamburger. Ported from the aws chat kit (commit 28131fd) with its two couplings cut:
 * the transport is the {@link AgentChatCapable} capability (an input, host-provided) and the
 * delete confirm rides the {@link AGENT_CONFIRM} token (package-owned default). Same DOM and
 * data-testids (chat-list / chat-item / btn-new-chat / btn-delete-chat / btn-collapse-chats).
 * The parent owns which chat is active. Styling is utility-class based (Tailwind hosts pick it
 * up as-is; others restyle via the data-testids).
 */
@Component({
  selector: 'agent-chat-rail',
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: { class: 'contents' },
  template: `
    <aside data-testid="chat-list" class="flex w-56 shrink-0 flex-col rounded-lg border border-slate-200 bg-white shadow-sm print:hidden">
      <div class="flex items-center justify-between border-b border-slate-200 px-3 py-2">
        <h2 class="text-sm font-semibold text-slate-800">Chats</h2>
        <div class="flex items-center gap-1">
          <button
            type="button"
            data-testid="btn-new-chat"
            class="rounded-md bg-blue-600 px-2 py-1 text-xs font-semibold text-white hover:bg-blue-700 focus:outline-none focus:ring-2 focus:ring-blue-600/40 disabled:opacity-50"
            [disabled]="creating()"
            (click)="newChat()"
          >
            + New
          </button>
          <button
            type="button"
            data-testid="btn-collapse-chats"
            class="rounded p-1 text-slate-500 hover:bg-slate-100 hover:text-slate-700 focus:outline-none focus:ring-2 focus:ring-blue-600/40"
            aria-label="Hide chat list"
            title="Hide chat list"
            (click)="collapse.emit()"
          >
            <svg viewBox="0 0 24 24" class="h-4 w-4" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M11 17l-5-5 5-5" /><path d="M18 17l-5-5 5-5" /></svg>
          </button>
        </div>
      </div>
      <ul class="flex-1 overflow-auto py-1">
        @for (c of chats(); track c.chatId) {
          <li class="group relative">
            <button
              type="button"
              data-testid="chat-item"
              class="flex w-full items-center gap-2 px-3 py-2 pr-8 text-left text-sm hover:bg-slate-50 focus:outline-none focus:bg-blue-50"
              [class.bg-blue-50]="activeChatId() === c.chatId"
              [class.font-semibold]="activeChatId() === c.chatId"
              (click)="selected.emit(c.chatId)"
            >
              <span class="flex-1 truncate text-slate-800">{{ c.title }}</span>
              @if (c.inProgress) {
                <span class="h-2 w-2 shrink-0 animate-pulse rounded-full bg-blue-500" role="status" aria-label="Turn in progress"></span>
              }
            </button>
            <button
              type="button"
              data-testid="btn-delete-chat"
              class="absolute right-1.5 top-1/2 hidden -translate-y-1/2 rounded p-1 text-slate-600 hover:bg-red-50 hover:text-red-700 focus:outline-none focus:ring-2 focus:ring-red-400/40 group-hover:block group-focus-within:block"
              [attr.aria-label]="'Delete chat ' + c.title"
              (click)="remove(c)"
            >
              ✕
            </button>
          </li>
        } @empty {
          <li class="px-3 py-4 text-center text-xs text-slate-600">No chats yet — start one.</li>
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
