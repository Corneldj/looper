import { Component, computed, inject, input, signal } from '@angular/core';
import { takeUntilDestroyed, toObservable } from '@angular/core/rxjs-interop';
import { catchError, forkJoin, map, of, switchMap, timer } from 'rxjs';
import { ApiService } from '../../../core/api.service';
import { AUTONOMY_LEVELS, DeliveryMetricsDto, PrStatus, PullRequestDto } from '../../../core/models';
import { formatCost, formatDateTime, formatPercent, relativeTime } from '../../../core/format';
import { LogPrModal } from './log-pr-modal';
import { EditPrModal } from './edit-pr-modal';

/**
 * The delivery section of the dashboard: the five metrics that measure value
 * that stuck (output volume is meaningless — these catch whether agent work
 * actually ships and lasts), plus the autonomy dial and the tracked PRs.
 */
@Component({
  selector: 'app-delivery-section',
  imports: [LogPrModal, EditPrModal],
  templateUrl: './delivery-section.html',
  styleUrl: './delivery-section.scss',
})
export class DeliverySection {
  private readonly api = inject(ApiService);

  /** Reporting window in days — follows the dashboard period selector. */
  readonly days = input<number>(30);

  protected readonly metrics = signal<DeliveryMetricsDto | null>(null);
  protected readonly prs = signal<PullRequestDto[]>([]);
  /** Something has arrived at least once (gates empty-state flash). */
  protected readonly loaded = signal(false);
  /** True when the latest fetch failed entirely (previous data stays up). */
  protected readonly apiError = signal(false);

  protected readonly logPrOpen = signal(false);
  protected readonly editingPr = signal<PullRequestDto | null>(null);
  protected readonly syncingIds = signal<ReadonlySet<string>>(new Set());
  /** Non-fatal row-action failure (sync/delete) — shown above the PR table. */
  protected readonly actionError = signal<string | null>(null);

  // Shared formatters exposed to the template.
  protected readonly formatCost = formatCost;
  protected readonly formatPercent = formatPercent;
  protected readonly formatDateTime = formatDateTime;
  protected readonly relativeTime = relativeTime;

  protected readonly sortedPrs = computed(() =>
    [...this.prs()].sort((a, b) => b.openedAtUtc.localeCompare(a.openedAtUtc)),
  );

  constructor() {
    // Refetch when the period changes, poll every 60s.
    toObservable(this.days)
      .pipe(
        switchMap(days => timer(0, 60_000).pipe(map(() => days))),
        switchMap(days => this.fetch(days)),
        takeUntilDestroyed(),
      )
      .subscribe(result => this.apply(result));
  }

  private fetch(days: number) {
    return forkJoin({
      metrics: this.api.getDeliveryMetrics(days).pipe(catchError(() => of(null))),
      prs: this.api.getPullRequests(days).pipe(catchError(() => of(null))),
    });
  }

  private apply({ metrics, prs }: { metrics: DeliveryMetricsDto | null; prs: PullRequestDto[] | null }): void {
    this.apiError.set(metrics === null && prs === null);
    if (metrics) this.metrics.set(metrics);
    if (prs) this.prs.set(prs);
    if (metrics || prs) this.loaded.set(true);
  }

  /** One-off metrics refresh after a mutation, so the tiles keep step with the table. */
  private refreshMetrics(): void {
    this.api
      .getDeliveryMetrics(this.days())
      .pipe(catchError(() => of(null)))
      .subscribe(metrics => {
        if (metrics) this.metrics.set(metrics);
      });
  }

  private upsert(pr: PullRequestDto): void {
    this.prs.update(list => {
      const index = list.findIndex(p => p.id === pr.id);
      if (index < 0) return [pr, ...list];
      const next = [...list];
      next[index] = pr;
      return next;
    });
  }

  // ---------- Tile tones ----------

  protected firstPassTone(rate: number): string {
    return rate >= 0.8 ? 'good' : rate >= 0.5 ? 'warn' : 'bad';
  }

  protected escalationTone(rate: number): string {
    return rate <= 0.1 ? 'good' : rate <= 0.3 ? 'warn' : 'bad';
  }

  // ---------- Autonomy ----------

  protected autonomyBlurb(level: number): string {
    return AUTONOMY_LEVELS.find(l => l.level === level)?.blurb ?? '';
  }

  // ---------- PR table ----------

  protected statusChip(status: PrStatus): string {
    return status === 'Merged' ? 'chip-green' : status === 'Open' ? 'chip-blue' : 'chip-neutral';
  }

  protected sync(pr: PullRequestDto): void {
    if (this.syncingIds().has(pr.id)) return;
    this.syncingIds.update(ids => new Set(ids).add(pr.id));
    this.api.syncPullRequest(pr.id).subscribe({
      next: updated => {
        this.stopSyncing(pr.id);
        this.actionError.set(null);
        this.upsert(updated);
        this.refreshMetrics();
      },
      error: () => {
        this.stopSyncing(pr.id);
        this.actionError.set(`Couldn’t sync “${pr.title}” — check that the API is running.`);
      },
    });
  }

  private stopSyncing(id: string): void {
    this.syncingIds.update(ids => {
      const next = new Set(ids);
      next.delete(id);
      return next;
    });
  }

  protected remove(pr: PullRequestDto): void {
    if (!confirm(`Stop tracking “${pr.title}”? The PR itself is untouched.`)) return;
    this.api.deletePullRequest(pr.id).subscribe({
      next: () => {
        this.actionError.set(null);
        this.prs.update(list => list.filter(p => p.id !== pr.id));
        this.refreshMetrics();
      },
      error: () => this.actionError.set('Delete failed — check that the API is running.'),
    });
  }

  // ---------- Modals ----------

  protected onLogPrClosed(pr: PullRequestDto | null): void {
    this.logPrOpen.set(false);
    if (pr) {
      this.upsert(pr);
      this.refreshMetrics();
    }
  }

  protected onEditClosed(pr: PullRequestDto | null): void {
    this.editingPr.set(null);
    if (pr) {
      this.upsert(pr);
      this.refreshMetrics();
    }
  }
}
