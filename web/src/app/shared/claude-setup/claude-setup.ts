import { Component, inject, output, signal } from '@angular/core';
import { ClaudeStatusStore } from '../../core/stores';

/**
 * Modal that explains real runs need the Claude Code CLI and offers three ways out:
 * one-click install through the API, copy-paste commands, or a custom path hint.
 */
@Component({
  selector: 'app-claude-setup',
  templateUrl: './claude-setup.html',
  styleUrl: './claude-setup.scss',
  host: { '(document:keydown.escape)': 'close()' },
})
export class ClaudeSetup {
  protected readonly store = inject(ClaudeStatusStore);
  readonly closed = output<void>();

  protected readonly curlCommand = 'curl -fsSL https://claude.ai/install.sh | bash';
  protected readonly npmCommand = 'npm install -g @anthropic-ai/claude-code';

  protected readonly copiedCommand = signal<string | null>(null);

  protected close(): void {
    if (this.store.installing()) return;
    this.closed.emit();
  }

  protected copy(command: string): void {
    navigator.clipboard?.writeText(command).then(() => {
      this.copiedCommand.set(command);
      setTimeout(() => this.copiedCommand.set(null), 1600);
    });
  }
}
