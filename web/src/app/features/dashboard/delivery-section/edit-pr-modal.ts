import { Component, OnInit, computed, inject, input, output, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { ApiService } from '../../../core/api.service';
import { PrStatus, PullRequestDto, UpdatePrBody } from '../../../core/models';

/**
 * Modal editor for the sync-managed fields of a tracked PR. GitHub-hosted PRs
 * get these from gh automatically — manual edits cover local and non-GitHub work.
 */
@Component({
  selector: 'app-edit-pr-modal',
  imports: [FormsModule],
  templateUrl: './edit-pr-modal.html',
  styleUrl: './edit-pr-modal.scss',
})
export class EditPrModal implements OnInit {
  private readonly api = inject(ApiService);

  readonly pr = input.required<PullRequestDto>();
  /** Emits the updated PR, or null when cancelled. */
  readonly closed = output<PullRequestDto | null>();

  readonly title = signal('');
  readonly status = signal<PrStatus>('Open');
  readonly additions = signal<number | null>(0);
  readonly deletions = signal<number | null>(0);
  readonly reviewRounds = signal<number | null>(0);
  readonly reviewComments = signal<number | null>(0);
  readonly humanCommits = signal<number | null>(0);
  readonly repoPath = signal('');
  readonly mergeCommitSha = signal('');

  readonly saving = signal(false);
  readonly saveError = signal<string | null>(null);

  readonly canSave = computed(() => !!this.title().trim());

  ngOnInit(): void {
    const pr = this.pr();
    this.title.set(pr.title);
    this.status.set(pr.status);
    this.additions.set(pr.additions);
    this.deletions.set(pr.deletions);
    this.reviewRounds.set(pr.reviewRounds);
    this.reviewComments.set(pr.reviewComments);
    this.humanCommits.set(pr.humanCommits);
    this.repoPath.set(pr.repoPath ?? '');
    this.mergeCommitSha.set(pr.mergeCommitSha ?? '');
  }

  cancel(): void {
    this.closed.emit(null);
  }

  save(): void {
    if (!this.canSave() || this.saving()) return;
    this.saving.set(true);
    this.saveError.set(null);
    const body: UpdatePrBody = {
      title: this.title().trim(),
      status: this.status(),
      additions: count(this.additions()),
      deletions: count(this.deletions()),
      reviewRounds: count(this.reviewRounds()),
      reviewComments: count(this.reviewComments()),
      humanCommits: count(this.humanCommits()),
      repoPath: this.repoPath().trim() || null,
      mergeCommitSha: this.mergeCommitSha().trim() || null,
    };
    this.api.updatePullRequest(this.pr().id, body).subscribe({
      next: dto => this.closed.emit(dto),
      error: () => {
        this.saving.set(false);
        this.saveError.set('Save failed — check that the API is running and try again.');
      },
    });
  }
}

/** ngModel on a cleared number input yields null — treat as 0, never negative. */
function count(value: number | null): number {
  return value === null || Number.isNaN(value) || value < 0 ? 0 : Math.round(value);
}
