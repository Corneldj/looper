import { Component, computed, inject, input, signal } from '@angular/core';
import { HttpErrorResponse } from '@angular/common/http';
import { RouterLink } from '@angular/router';
import { takeUntilDestroyed, toObservable } from '@angular/core/rxjs-interop';
import { EMPTY, Subject, catchError, forkJoin, merge, of, switchMap, takeWhile, timer } from 'rxjs';
import { ApiService } from '../../core/api.service';
import {
  AgentDetailDto,
  AUTONOMY_LEVELS,
  EFFORT_LEVELS,
  EffortLevel,
  RunDetailDto,
  RunStatus,
  RunSummaryDto,
} from '../../core/models';
import {
  formatCost,
  formatDateTime,
  formatDuration,
  formatInterval,
  formatTokens,
  modelShortName,
  relativeTime,
} from '../../core/format';

@Component({
  selector: 'app-agent-detail',
  imports: [RouterLink],
  templateUrl: './agent-detail.html',
  styleUrl: './agent-detail.scss',
  host: { '(document:keydown.escape)': 'closeDrawer()' },
})
export class AgentDetail {
  /** Route param /agents/:id — bound by the router via component input binding. */
  readonly id = input.required<string>();

  private readonly api = inject(ApiService);

  /** Emits to trigger an immediate refresh outside the 5s cadence (after Run now / Cancel). */
  private readonly refresh$ = new Subject<void>();

  protected readonly agent = signal<AgentDetailDto | null>(null);
  protected readonly runs = signal<RunSummaryDto[]>([]);
  protected readonly loaded = signal(false);
  private readonly missing = signal(false);
  protected readonly notFound = computed(() => this.missing());

  protected readonly selectedRunId = signal<string | null>(null);
  protected readonly runDetail = signal<RunDetailDto | null>(null);
  protected readonly runDetailError = signal(false);

  protected readonly actionBusy = signal(false);

  // Shared formatters exposed to the template.
  protected readonly formatCost = formatCost;
  protected readonly formatDateTime = formatDateTime;
  protected readonly formatDuration = formatDuration;
  protected readonly formatInterval = formatInterval;
  protected readonly formatTokens = formatTokens;
  protected readonly modelShortName = modelShortName;
  protected readonly relativeTime = relativeTime;

  constructor() {
    // Agent + run history: poll every 5s (plus on-demand refreshes), restarting when the route id changes.
    // Errors are swallowed per tick so the last good data stays on screen.
    toObservable(this.id)
      .pipe(
        switchMap(id => {
          this.resetForAgent();
          return merge(timer(0, 5_000), this.refresh$).pipe(
            switchMap(() =>
              forkJoin({
                agent: this.api.getAgent(id).pipe(
                  catchError((err: unknown) => {
                    if (err instanceof HttpErrorResponse && err.status === 404) this.missing.set(true);
                    return of(null);
                  }),
                ),
                runs: this.api.getAgentRuns(id, 50).pipe(catchError(() => of(null))),
              }),
            ),
          );
        }),
        takeUntilDestroyed(),
      )
      .subscribe(({ agent, runs }) => {
        if (agent) {
          this.agent.set(agent);
          this.missing.set(false);
        }
        if (runs) this.runs.set(runs);
        this.loaded.set(true);
      });

    // Selected run drill-down: fetch immediately, then every 3s while the run is
    // still in flight so logs stream in; stop once it reaches a terminal state.
    toObservable(this.selectedRunId)
      .pipe(
        switchMap(runId => {
          if (!runId) return EMPTY;
          return timer(0, 3_000).pipe(
            switchMap(() => this.api.getRun(runId).pipe(catchError(() => of(null)))),
            takeWhile(detail => detail === null || detail.status === 'Running', true),
          );
        }),
        takeUntilDestroyed(),
      )
      .subscribe(detail => {
        if (detail) {
          this.runDetail.set(detail);
          this.runDetailError.set(false);
        } else if (!this.runDetail()) {
          this.runDetailError.set(true);
        }
      });
  }

  protected selectRun(run: RunSummaryDto): void {
    if (this.selectedRunId() === run.id) return;
    this.runDetail.set(null);
    this.runDetailError.set(false);
    this.selectedRunId.set(run.id);
  }

  protected closeDrawer(): void {
    this.selectedRunId.set(null);
    this.runDetail.set(null);
    this.runDetailError.set(false);
  }

  protected runNow(): void {
    const agent = this.agent();
    if (!agent || agent.isRunning || this.actionBusy()) return;
    this.actionBusy.set(true);
    this.api.runAgentNow(agent.id).subscribe({
      next: () => {
        this.actionBusy.set(false);
        this.refresh$.next();
      },
      error: () => this.actionBusy.set(false),
    });
  }

  protected cancelRun(): void {
    const agent = this.agent();
    if (!agent || this.actionBusy()) return;
    this.actionBusy.set(true);
    this.api.cancelAgentRun(agent.id).subscribe({
      next: () => {
        this.actionBusy.set(false);
        this.refresh$.next();
      },
      error: () => this.actionBusy.set(false),
    });
  }

  protected runChipClass(status: RunStatus): string {
    switch (status) {
      case 'Succeeded':
        return 'chip-green';
      case 'Failed':
      case 'TimedOut':
        return 'chip-red';
      case 'Cancelled':
        return 'chip-amber';
      case 'Running':
        return 'chip-green';
    }
  }

  protected runStatusLabel(status: RunStatus): string {
    return status === 'TimedOut' ? 'Timed out' : status;
  }

  protected effortLabel(effort: EffortLevel): string {
    return EFFORT_LEVELS.find(e => e.id === effort)?.label ?? effort;
  }

  protected autonomyBlurb(level: number): string {
    return AUTONOMY_LEVELS.find(l => l.level === level)?.blurb ?? '';
  }

  /** HH:mm:ss for log lines (format.ts has no time-only helper). */
  protected logTime(iso: string): string {
    const date = new Date(iso.endsWith('Z') || iso.includes('+') ? iso : iso + 'Z');
    return date.toLocaleTimeString('en-GB', { hour: '2-digit', minute: '2-digit', second: '2-digit' });
  }

  private resetForAgent(): void {
    this.agent.set(null);
    this.runs.set([]);
    this.loaded.set(false);
    this.missing.set(false);
    this.closeDrawer();
  }
}
