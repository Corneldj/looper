import { Component, computed, inject, output, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { ApiService } from '../../../core/api.service';
import { AgentsStore } from '../../../core/stores';
import { PullRequestDto } from '../../../core/models';

/**
 * Modal to hand-register a PR that wasn't reported by a real run —
 * work on a non-GitHub remote, or done outside the loop.
 */
@Component({
  selector: 'app-log-pr-modal',
  imports: [FormsModule],
  templateUrl: './log-pr-modal.html',
  styleUrl: './log-pr-modal.scss',
})
export class LogPrModal {
  private readonly api = inject(ApiService);
  protected readonly agentsStore = inject(AgentsStore);

  /** Emits the registered PR, or null when cancelled. */
  readonly closed = output<PullRequestDto | null>();

  readonly agentId = signal('');
  readonly url = signal('');
  readonly title = signal('');
  readonly repoPath = signal('');

  readonly saving = signal(false);
  readonly saveError = signal<string | null>(null);

  readonly canSave = computed(() => !!this.agentId() && (!!this.url().trim() || !!this.title().trim()));

  cancel(): void {
    this.closed.emit(null);
  }

  save(): void {
    if (!this.canSave() || this.saving()) return;
    this.saving.set(true);
    this.saveError.set(null);
    this.api
      .registerPullRequest({
        agentId: this.agentId(),
        url: this.url().trim() || null,
        title: this.title().trim() || null,
        repoPath: this.repoPath().trim() || null,
      })
      .subscribe({
        next: dto => this.closed.emit(dto),
        error: () => {
          this.saving.set(false);
          this.saveError.set('Couldn’t log the PR — check that the API is running and try again.');
        },
      });
  }
}
