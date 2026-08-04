import { Pipe, PipeTransform } from '@angular/core';
import { marked } from 'marked';

/**
 * Renders the assistant's markdown answer to an HTML string. The result is bound via `[innerHTML]`,
 * which runs through Angular's HTML sanitizer (formatting tags kept, scripts/handlers stripped) — so
 * we never bypass sanitization for chat text. Synchronous parse returns a string directly.
 */
@Pipe({ name: 'agentMarkdown' })
export class AgentMarkdownPipe implements PipeTransform {
  transform(md: string | null | undefined): string {
    return marked.parse(md ?? '', { async: false, gfm: true, breaks: true }) as string;
  }
}
