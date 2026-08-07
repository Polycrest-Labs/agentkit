import {
  ChangeDetectionStrategy,
  Component,
  DestroyRef,
  ElementRef,
  computed,
  effect,
  inject,
  input,
  output,
  signal,
  viewChild,
} from '@angular/core';

/** A composer attachment chip's display model (the state's PendingChip satisfies it). */
export interface AgentComposerAttachment {
  id: string;
  fileName: string;
  contentType?: string;
  kind?: string;
  previewUrl?: string | null;
}

/**
 * The ChatGPT-style agent composer — ported wholesale from the aws `ai-composer` (the lineage's
 * most evolved copy) and restyled onto the kit's tokened classes (`agentkit-composer__*` in
 * `styles.css`; `data-testid`s are for tests, never styling API).
 *
 * Compact single-line mode keeps +, text, and send in one row. As soon as the draft wraps, the
 * user expands it, or attachments are present, it becomes a taller two-row composer with the
 * textarea above the controls. Intentionally transport-agnostic: parents own uploads/persistence.
 *
 * The hard-won behaviors, preserved exactly: Enter/Shift+Enter with the `isComposing` IME guard;
 * auto-grow with wrap PRE-measurement (the mode flips before the wrap paints, so the composer
 * never flashes); send-on-pointerdown for touch (before the keyboard-close layout shift) with a
 * one-shot ghost-click suppressor; ~48px invisible touch targets; the ＋ menu / drag-drop overlay
 * / clipboard paste all feeding one `filesSelected` pipeline — with the `text/plain` guard so a
 * mixed copy (Word/Excel renders text + bitmap) pastes as TEXT.
 */
@Component({
  selector: 'agent-composer',
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: {
    class: 'agentkit-composer-host',
    '(document:click)': 'closeMenuOnOutsideClick($event)',
  },
  template: `
    <form
      class="agentkit-composer"
      data-agentkit-part="composer"
      [class.agentkit-composer--compact]="compact()"
      [class.agentkit-composer--drop]="dragActive()"
      (submit)="submit($event)"
      (pointerdown)="onComposerPointerDown($event)"
      (dragenter)="onDragEnter($event)"
      (dragover)="onDragOver($event)"
      (dragleave)="onDragLeave()"
      (drop)="onDrop($event)"
    >
      @if (dragActive()) {
        <div class="agentkit-composer__drop-hint" data-testid="composer-drop-hint" aria-hidden="true">
          Drop to attach
        </div>
      }
      @if (attachments().length || uploading() > 0) {
        <div class="agentkit-composer__attachments">
          @for (a of attachments(); track a.id) {
            <span class="agentkit-composer__attachment" data-testid="composer-attachment">
              @if (isImageAttachment(a)) {
                <img class="agentkit-composer__attachment-preview" [src]="a.previewUrl" [alt]="a.fileName" />
              } @else {
                <span class="agentkit-composer__attachment-icon" aria-hidden="true">
                  <svg viewBox="0 0 24 24"><path d="M14 3H7a2 2 0 0 0-2 2v14a2 2 0 0 0 2 2h10a2 2 0 0 0 2-2V8Z" /><path d="M14 3v5h5" /></svg>
                </span>
              }
              <span class="agentkit-composer__attachment-name">{{ a.fileName }}</span>
              <button type="button" class="agentkit-composer__attachment-remove" data-testid="btn-attachment-remove" (click)="removeAttachment.emit(a)" aria-label="Remove attachment">✕</button>
            </span>
          }
          @if (uploading() > 0) {
            <span class="agentkit-composer__attachment" data-testid="composer-uploading" role="status" aria-live="polite">
              <span class="agentkit-composer__attachment-icon" aria-hidden="true"><span class="agentkit-composer__spinner"></span></span>
              <span class="agentkit-composer__attachment-name">{{ uploading() > 1 ? 'Uploading ' + uploading() + ' files…' : 'Uploading…' }}</span>
            </span>
          }
        </div>
      }

      <div class="agentkit-composer__row">
        <div class="agentkit-composer__attach">
          <button
            type="button"
            class="agentkit-composer__icon-btn"
            data-testid="btn-attach"
            [disabled]="disabled()"
            aria-label="Add photos or files"
            aria-haspopup="menu"
            [attr.aria-expanded]="menuOpen()"
            (click)="toggleMenu()"
          >
            <svg viewBox="0 0 24 24" aria-hidden="true"><path d="M12 5v14M5 12h14" /></svg>
          </button>

          @if (menuOpen()) {
            <div class="agentkit-composer__menu" role="menu">
              @if (referencesLabel()) {
                <button type="button" role="menuitem" data-testid="btn-attach-references" (click)="pickReferences()">
                  <span class="agentkit-composer__menu-at">&#64;</span>
                  {{ referencesLabel() }}
                </button>
              }
              <button type="button" role="menuitem" data-testid="btn-attach-photos" (click)="pick('photos')">
                <span><svg viewBox="0 0 24 24" aria-hidden="true"><rect x="3" y="5" width="18" height="14" rx="2" /><path d="m3 15 4-4 4 4 3-3 7 7" /><circle cx="16" cy="9" r="1.5" /></svg></span>
                Photos
              </button>
              <button type="button" role="menuitem" data-testid="btn-attach-files" (click)="pick('files')">
                <span><svg viewBox="0 0 24 24" aria-hidden="true"><path d="M14 3H7a2 2 0 0 0-2 2v14a2 2 0 0 0 2 2h10a2 2 0 0 0 2-2V8Z" /><path d="M14 3v5h5" /></svg></span>
                Files
              </button>
            </div>
          }
        </div>

        <textarea
          #textInput
          name="ask"
          rows="1"
          wrap="soft"
          autocomplete="off"
          enterkeyhint="send"
          data-testid="composer-input"
          [attr.aria-label]="ariaLabel()"
          [placeholder]="placeholder()"
          [disabled]="disabled()"
          [value]="draft()"
          [class.agentkit-composer__input--expanded]="expanded()"
          (input)="onInput($event)"
          (keydown)="onKeydown($event)"
          (paste)="onPaste($event)"
        ></textarea>

        <div class="agentkit-composer__right">
          @if (showExpand()) {
            <button
              type="button"
              class="agentkit-composer__icon-btn agentkit-composer__expand"
              [class.agentkit-composer__icon-btn--active]="expanded()"
              [disabled]="disabled()"
              [attr.aria-label]="expanded() ? 'Collapse composer' : 'Expand composer'"
              (click)="toggleExpanded()"
            >
              <svg viewBox="0 0 24 24" aria-hidden="true"><path d="M8 3H3v5M3 3l6 6M16 21h5v-5M21 21l-6-6" /></svg>
            </button>
          }
          <button
            type="submit"
            class="agentkit-composer__send"
            data-testid="btn-send"
            [class.agentkit-composer__send--ready]="canSubmit()"
            [disabled]="disabled() || !canSubmit()"
            [attr.title]="uploading() > 0 ? 'Waiting for attachments to finish uploading…' : null"
            aria-label="Send message"
            (pointerdown)="onSendPointerDown($event)"
          >
            <svg viewBox="0 0 24 24" aria-hidden="true"><path d="M12 19V5M5 12l7-7 7 7" /></svg>
          </button>
        </div>
      </div>

      <input #photoInput type="file" multiple accept="image/jpeg,image/png,image/webp,image/gif" hidden (change)="onFiles(photoInput)" />
      <input #fileInput type="file" multiple [accept]="accept()" hidden (change)="onFiles(fileInput)" />
    </form>
  `,
})
export class AgentComposer<TAttachment extends AgentComposerAttachment = AgentComposerAttachment> {
  readonly value = input('');
  readonly attachments = input<TAttachment[]>([]);
  /** Attachment uploads still in flight (count). Send is blocked while > 0 — submitting mid-upload
   * would post the message without the attachment — and an "Uploading…" chip shows the wait. */
  readonly uploading = input(0);
  readonly disabled = input(false);
  readonly placeholder = input('Reply to assistant');
  readonly ariaLabel = input('Ask assistant');
  readonly accept = input('image/jpeg,image/png,image/webp,image/gif,application/pdf,.jpg,.jpeg,.png,.webp,.gif,.pdf');
  /** When set, the + menu gains a top "references" item emitting `references`. */
  readonly referencesLabel = input<string | null>(null);

  readonly valueChange = output<string>();
  readonly submitted = output<string>();
  readonly filesSelected = output<File[]>();
  readonly removeAttachment = output<TAttachment>();
  readonly references = output<void>();

  private readonly host = inject<ElementRef<HTMLElement>>(ElementRef);
  private readonly textInput = viewChild<ElementRef<HTMLTextAreaElement>>('textInput');
  private readonly photoInput = viewChild<ElementRef<HTMLInputElement>>('photoInput');
  private readonly fileInput = viewChild<ElementRef<HTMLInputElement>>('fileInput');

  protected readonly draft = signal('');
  protected readonly menuOpen = signal(false);
  protected readonly expanded = signal(false);
  /** A files drag is hovering the composer — shows the drop hint + highlight ring. */
  protected readonly dragActive = signal(false);
  /** dragenter/dragleave fire per child element — depth-count so the hint doesn't flicker. */
  private dragDepth = 0;
  private readonly wraps = signal(false);
  private measureNode: HTMLSpanElement | null = null;
  private resizeFrame: number | null = null;
  private removeClickSuppressor: (() => void) | null = null;

  protected readonly canSubmit = computed(
    () => this.uploading() === 0 && (this.draft().trim().length > 0 || this.attachments().length > 0),
  );
  protected readonly showExpand = computed(() => this.wraps() || this.expanded());
  protected readonly compact = computed(
    () => this.attachments().length === 0 && this.uploading() === 0 && !this.expanded() && !this.wraps(),
  );

  constructor() {
    inject(DestroyRef).onDestroy(() => {
      this.removeClickSuppressor?.();
      // The wrap-measurement span lives on document.body — without this, every
      // destroyed composer leaves one hidden node behind for the page's lifetime.
      this.measureNode?.remove();
      this.measureNode = null;
    });
    effect(() => {
      this.draft.set(this.value());
      queueMicrotask(() => {
        const el = this.textInput()?.nativeElement;
        if (el) {
          this.resize(el);
        }
      });
    });
  }

  protected closeMenuOnOutsideClick(e: MouseEvent): void {
    if (!this.host.nativeElement.contains(e.target as Node)) {
      this.menuOpen.set(false);
    }
  }

  protected onInput(e: Event): void {
    const el = e.target as HTMLTextAreaElement;
    this.draft.set(el.value);
    this.valueChange.emit(this.draft());
    this.resize(el);
  }

  protected onKeydown(e: KeyboardEvent): void {
    if (e.key === 'Enter' && !e.shiftKey && !e.isComposing) {
      e.preventDefault();
      this.submit();
    }
  }

  protected submit(e?: Event): void {
    e?.preventDefault();
    if (this.disabled() || !this.canSubmit()) {
      return;
    }
    this.submitted.emit(this.draft().trim());
    queueMicrotask(() => this.reset());
  }

  /**
   * Touch taps anywhere on the composer chrome must not steal focus from the textarea — a
   * pointerdown's default action blurs it, which closes the on-screen keyboard and shifts the
   * layout mid-tap. preventDefault() on pointerdown blocks the focus change but not the later
   * click, so every (click)-driven control (menu, expand, attachment remove) keeps working.
   */
  protected onComposerPointerDown(e: PointerEvent): void {
    if (e.pointerType === 'mouse') {
      return;
    }
    if ((e.target as HTMLElement | null)?.closest('textarea')) {
      return; // caret placement / focusing the input must keep its default behavior
    }
    e.preventDefault();
  }

  /**
   * Touch fast-path: send on pointerdown (before the keyboard-close layout shift), keep the
   * keyboard open, and swallow the browser's trailing synthesized click so it can't activate
   * whatever scrolls under the finger.
   */
  protected onSendPointerDown(e: PointerEvent): void {
    if (e.pointerType === 'mouse' || !e.isPrimary) {
      return; // mouse keeps the native click → form submit path
    }
    e.preventDefault(); // always — even when we can't submit, a tap on send must not blur/close the keyboard
    if (this.disabled() || !this.canSubmit()) {
      return;
    }
    this.suppressNextClick();
    const textarea = this.textInput()?.nativeElement;
    const hadFocus = !!textarea && textarea.ownerDocument.activeElement === textarea;
    this.submit();
    if (hadFocus) {
      textarea.focus();
    }
  }

  /**
   * One-shot capture-phase click swallower for the ghost click that follows a touch-initiated
   * submit: the browser dispatches it at pointerup coordinates AFTER submit has mutated layout,
   * so it can land on a link that moved under the finger. Self-removes on the first click or
   * after 700ms (pointercancel / no click at all).
   */
  private suppressNextClick(): void {
    this.removeClickSuppressor?.();
    const doc = this.host.nativeElement.ownerDocument;
    const onClick = (ev: MouseEvent) => {
      ev.preventDefault();
      ev.stopImmediatePropagation();
      cleanup();
    };
    const cleanup = () => {
      doc.removeEventListener('click', onClick, true);
      clearTimeout(timer);
      this.removeClickSuppressor = null;
    };
    const timer = setTimeout(cleanup, 700);
    doc.addEventListener('click', onClick, true);
    this.removeClickSuppressor = cleanup;
  }

  protected isImageAttachment(attachment: AgentComposerAttachment): boolean {
    return !!attachment.previewUrl && (attachment.kind === 'image' || attachment.contentType?.startsWith('image/') === true);
  }

  protected toggleMenu(): void {
    if (this.disabled()) {
      return;
    }
    this.menuOpen.update((v) => !v);
  }

  protected pick(kind: 'photos' | 'files'): void {
    this.menuOpen.set(false);
    const input = kind === 'photos' ? this.photoInput() : this.fileInput();
    input?.nativeElement.click();
  }

  protected pickReferences(): void {
    this.menuOpen.set(false);
    this.references.emit();
    this.textInput()?.nativeElement.focus();
  }

  /** Focus the textarea and place the caret at the end — used after a prefill. */
  focusInput(): void {
    const el = this.textInput()?.nativeElement;
    if (!el) {
      return;
    }
    el.focus();
    el.setSelectionRange(el.value.length, el.value.length);
  }

  /** Prefill the draft (followup chips: prefill, focus — NEVER auto-send). */
  setDraft(text: string): void {
    this.draft.set(text);
    this.valueChange.emit(text);
    queueMicrotask(() => {
      const el = this.textInput()?.nativeElement;
      if (el) {
        this.resize(el);
      }
    });
  }

  protected onFiles(input: HTMLInputElement): void {
    const files = input.files ? Array.from(input.files) : [];
    input.value = '';
    if (files.length) {
      this.filesSelected.emit(files);
      this.textInput()?.nativeElement.focus();
    }
  }

  // ── drag & drop / clipboard paste — both feed the same filesSelected pipeline as the + menu ──

  protected onDragEnter(e: DragEvent): void {
    if (this.disabled() || !e.dataTransfer?.types.includes('Files')) {
      return;
    }
    e.preventDefault();
    this.dragDepth++;
    this.dragActive.set(true);
  }

  protected onDragOver(e: DragEvent): void {
    if (this.disabled() || !e.dataTransfer?.types.includes('Files')) {
      return;
    }
    e.preventDefault(); // required — without it the browser navigates to the dropped file
    e.dataTransfer.dropEffect = 'copy';
  }

  protected onDragLeave(): void {
    if (this.dragDepth > 0 && --this.dragDepth === 0) {
      this.dragActive.set(false);
    }
  }

  protected onDrop(e: DragEvent): void {
    this.dragDepth = 0;
    this.dragActive.set(false);
    const files = e.dataTransfer?.files ? Array.from(e.dataTransfer.files) : [];
    if (this.disabled() || !files.length) {
      return;
    }
    e.preventDefault();
    this.filesSelected.emit(files);
    this.textInput()?.nativeElement.focus();
  }

  /**
   * Attach files pasted from the clipboard (screenshots, copied files). Mixed copies that also
   * carry plain text (e.g. Word/Excel selections render as text + bitmap) paste as TEXT — the
   * file is only attached when the clipboard has no text, so ordinary text paste never changes.
   */
  protected onPaste(e: ClipboardEvent): void {
    if (this.disabled() || !e.clipboardData) {
      return;
    }
    const files = Array.from(e.clipboardData.files);
    if (!files.length || e.clipboardData.getData('text/plain')) {
      return; // no files, or a mixed copy — keep the native text paste
    }
    e.preventDefault();
    this.filesSelected.emit(files);
  }

  protected toggleExpanded(): void {
    this.expanded.update((v) => !v);
    queueMicrotask(() => {
      const el = this.textInput()?.nativeElement;
      if (el) {
        this.resize(el);
        el.focus();
      }
    });
  }

  private resize(el: HTMLTextAreaElement): void {
    el.style.height = 'auto';
    const style = getComputedStyle(el);
    const lineHeight = Number.parseFloat(style.lineHeight) || 22;
    const paddingY = Number.parseFloat(style.paddingTop) + Number.parseFloat(style.paddingBottom);
    const singleLineHeight = Math.ceil(lineHeight + paddingY);
    const hasMultipleLines = this.needsMultiline(el, style, singleLineHeight);
    const maxHeight = this.expanded() ? 240 : 132;
    const changedMode = this.wraps() !== hasMultipleLines;

    this.wraps.set(hasMultipleLines);
    el.style.height = `${Math.min(Math.max(el.scrollHeight, singleLineHeight), maxHeight)}px`;

    if (changedMode) {
      this.scheduleResize(el);
    }
  }

  private reset(): void {
    this.draft.set('');
    this.expanded.set(false);
    this.wraps.set(false);
    const el = this.textInput()?.nativeElement;
    if (el) {
      el.value = '';
      el.style.height = '';
    }
  }

  private needsMultiline(
    el: HTMLTextAreaElement,
    style: CSSStyleDeclaration,
    singleLineHeight: number,
  ): boolean {
    const value = el.value;
    if (!value) {
      return false;
    }

    if (value.includes('\n') || el.scrollHeight > singleLineHeight + 1) {
      return true;
    }

    const line = value.slice(value.lastIndexOf('\n') + 1);
    const textWidth = this.measureTextWidth(line, style);
    const compactWidth = this.compactTextWidth(el, style);

    return textWidth >= compactWidth - 8;
  }

  private compactTextWidth(el: HTMLTextAreaElement, style: CSSStyleDeclaration): number {
    const paddingX = Number.parseFloat(style.paddingLeft) + Number.parseFloat(style.paddingRight);
    if (this.compact()) {
      return Math.max(0, el.clientWidth - paddingX);
    }

    const form = el.closest('.agentkit-composer') as HTMLElement | null;
    if (!form) {
      return Math.max(0, el.clientWidth - paddingX);
    }

    // Compact mode has one plus button, one send button, two grid gaps, and 8px side padding.
    const compactControlWidth = 34 + 34;
    const compactGaps = 16;
    const compactPaddingX = 16;
    return Math.max(80, form.clientWidth - compactPaddingX - compactControlWidth - compactGaps - paddingX);
  }

  private measureTextWidth(text: string, style: CSSStyleDeclaration): number {
    const doc = this.host.nativeElement.ownerDocument;
    const body = doc.body;
    if (!body) {
      return text.length * 8;
    }

    this.measureNode ??= doc.createElement('span');
    const node = this.measureNode;
    if (!node.isConnected) {
      body.appendChild(node);
    }

    node.style.position = 'fixed';
    node.style.left = '-9999px';
    node.style.top = '0';
    node.style.visibility = 'hidden';
    node.style.pointerEvents = 'none';
    node.style.whiteSpace = 'pre';
    node.style.font = style.font;
    node.style.letterSpacing = style.letterSpacing;
    node.textContent = text || ' ';

    return node.getBoundingClientRect().width;
  }

  private scheduleResize(el: HTMLTextAreaElement): void {
    if (this.resizeFrame !== null) {
      cancelAnimationFrame(this.resizeFrame);
    }

    this.resizeFrame = requestAnimationFrame(() => {
      this.resizeFrame = null;
      if (el.isConnected) {
        this.resize(el);
      }
    });
  }
}
