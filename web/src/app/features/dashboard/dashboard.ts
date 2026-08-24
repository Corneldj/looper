import { Component, computed, inject, signal } from '@angular/core';
import { RouterLink } from '@angular/router';
import { takeUntilDestroyed, toObservable } from '@angular/core/rxjs-interop';
import { catchError, forkJoin, map, of, switchMap, timer } from 'rxjs';
import { ApiService } from '../../core/api.service';
import {
  AgentBreakdownDto,
  CostSeriesPointDto,
  DashboardSummaryDto,
  ModelUsageDto,
  RecentFailureDto,
} from '../../core/models';
import {
  formatCost,
  formatDuration,
  formatPercent,
  formatTokens,
  modelShortName,
  relativeTime,
} from '../../core/format';
import { SpendChart } from './spend-chart';
import { BarList, BarListRow, OTHER_COLOR, SERIES_COLORS } from './bar-list';

interface Trend {
  dir: 'up' | 'down' | 'flat';
  arrow: string;
  text: string;
}

@Component({
  selector: 'app-dashboard',
  imports: [RouterLink, SpendChart, BarList],
  templateUrl: './dashboard.html',
  styleUrl: './dashboard.scss',
})
export class Dashboard {
  private readonly api = inject(ApiService);

  protected readonly periods: readonly number[] = [7, 14, 30];
  protected readonly days = signal(14);

  protected readonly summary = signal<DashboardSummaryDto | null>(null);
  protected readonly series = signal<CostSeriesPointDto[]>([]);
  protected readonly breakdown = signal<AgentBreakdownDto[]>([]);
  protected readonly models = signal<ModelUsageDto[]>([]);
  protected readonly failures = signal<RecentFailureDto[]>([]);

  /** True while a period change is in flight — dims charts, keeps the frame. */
  protected readonly refreshing = signal(false);
  /** True when the latest fetch failed entirely (previous data stays up). */
  protected readonly apiError = signal(false);
  /** Summary + series have arrived at least once (gates empty-state flash). */
  protected readonly loadedOnce = signal(false);
  /** Breakdown / models / failures have arrived at least once. */
  protected readonly detailsLoaded = signal(false);

  // Shared formatters exposed to the template.
  protected readonly formatCost = formatCost;
  protected readonly formatDuration = formatDuration;
  protected readonly formatPercent = formatPercent;
  protected readonly modelShortName = modelShortName;
  protected readonly relativeTime = relativeTime;

  constructor() {
    // Summary + series: refetch on period change, poll every 30s.
    toObservable(this.days)
      .pipe(
        switchMap(days => timer(0, 30_000).pipe(map(() => days))),
        switchMap(days =>
          forkJoin({
            summary: this.api.getDashboardSummary(days).pipe(catchError(() => of(null))),
            series: this.api.getCostSeries(days).pipe(catchError(() => of(null))),
          }),
        ),
        takeUntilDestroyed(),
      )
      .subscribe(({ summary, series }) => {
        this.refreshing.set(false);
        this.apiError.set(summary === null && series === null);
        if (summary) this.summary.set(summary);
        if (series) this.series.set(series);
        if (summary || series) this.loadedOnce.set(true);
      });

    // Breakdown, model usage and failures: refetch on period change.
    toObservable(this.days)
      .pipe(
        switchMap(() =>
          forkJoin({
            breakdown: this.api.getAgentBreakdown(this.days()).pipe(catchError(() => of(null))),
            models: this.api.getModelUsage(this.days()).pipe(catchError(() => of(null))),
            failures: this.api.getRecentFailures(8).pipe(catchError(() => of(null))),
          }),
        ),
        takeUntilDestroyed(),
      )
      .subscribe(({ breakdown, models, failures }) => {
        if (breakdown) this.breakdown.set(breakdown);
        if (models) this.models.set(models);
        if (failures) this.failures.set(failures);
        if (breakdown || models || failures) this.detailsLoaded.set(true);
      });
  }

  protected setDays(days: number): void {
    if (days === this.days()) return;
    this.refreshing.set(true);
    this.days.set(days);
  }

  // ---------- KPI tiles ----------

  protected readonly spendText = computed(() => {
    const s = this.summary();
    return s ? formatCost(s.totalCostUsd) : '—';
  });

  protected readonly spendTrend = computed(() => trendOf(this.summary()?.costTrendPct));

  protected readonly runsText = computed(() => {
    const s = this.summary();
    return s ? s.totalRuns.toLocaleString('en-US') : '—';
  });

  protected readonly runsTrend = computed(() => trendOf(this.summary()?.runsTrendPct));

  protected readonly successText = computed(() => {
    const s = this.summary();
    return s && s.totalRuns > 0 ? formatPercent(s.successRate) : '—';
  });

  protected readonly successKpiClass = computed(() => {
    const s = this.summary();
    return s && s.totalRuns > 0 ? this.successClass(s.successRate) : '';
  });

  protected readonly avgTimeText = computed(() => {
    const s = this.summary();
    return s && s.totalRuns > 0 ? formatDuration(Math.round(s.avgDurationMs)) : '—';
  });

  protected readonly agentsText = computed(() => {
    const s = this.summary();
    return s ? `${s.activeAgents} / ${s.totalAgents}` : '—';
  });

  protected successClass(rate: number): string {
    return rate >= 0.9 ? 'good' : rate >= 0.7 ? 'warn' : 'bad';
  }

  // ---------- Chart + table rows ----------

  protected readonly sortedBreakdown = computed(() =>
    [...this.breakdown()].sort((a, b) => b.costUsd - a.costUsd),
  );

  protected readonly agentRows = computed<BarListRow[]>(() => {
    const list = this.sortedBreakdown();
    const top = list.slice(0, 8);
    const rest = list.slice(8);

    const rows: BarListRow[] = top.map((a, i) => ({
      label: a.name,
      value: a.costUsd,
      display: formatCost(a.costUsd),
      color: SERIES_COLORS[i],
      detail: [
        { label: 'Runs', value: a.runs.toLocaleString('en-US') },
        { label: 'Success rate', value: a.runs > 0 ? formatPercent(a.successRate) : '—' },
        { label: 'Avg duration', value: a.runs > 0 ? formatDuration(Math.round(a.avgDurationMs)) : '—' },
        { label: 'Cost / run', value: a.runs > 0 ? formatCost(a.avgCostPerRunUsd) : '—' },
      ],
    }));

    if (rest.length) {
      rows.push({
        label: `Other (${rest.length})`,
        value: rest.reduce((sum, a) => sum + a.costUsd, 0),
        display: formatCost(rest.reduce((sum, a) => sum + a.costUsd, 0)),
        color: OTHER_COLOR,
        detail: [
          { label: 'Agents', value: `${rest.length}` },
          { label: 'Runs', value: rest.reduce((sum, a) => sum + a.runs, 0).toLocaleString('en-US') },
        ],
      });
    }
    return rows;
  });

  protected readonly modelRows = computed<BarListRow[]>(() => {
    const list = [...this.models()].sort((a, b) => b.costUsd - a.costUsd);
    const top = list.slice(0, 8);
    const rest = list.slice(8);

    const rows: BarListRow[] = top.map((m, i) => ({
      label: modelShortName(m.model),
      value: m.costUsd,
      display: formatCost(m.costUsd),
      color: SERIES_COLORS[i],
      detail: [
        { label: 'Runs', value: m.runs.toLocaleString('en-US') },
        { label: 'Tokens in', value: formatTokens(m.inputTokens) },
        { label: 'Tokens out', value: formatTokens(m.outputTokens) },
      ],
    }));

    if (rest.length) {
      rows.push({
        label: `Other (${rest.length})`,
        value: rest.reduce((sum, m) => sum + m.costUsd, 0),
        display: formatCost(rest.reduce((sum, m) => sum + m.costUsd, 0)),
        color: OTHER_COLOR,
        detail: [
          { label: 'Models', value: `${rest.length}` },
          { label: 'Runs', value: rest.reduce((sum, m) => sum + m.runs, 0).toLocaleString('en-US') },
        ],
      });
    }
    return rows;
  });
}

function trendOf(pct: number | null | undefined): Trend | null {
  if (pct === null || pct === undefined) return null;
  if (Math.abs(pct) < 0.05) return { dir: 'flat', arrow: '', text: 'no change' };
  const abs = Math.abs(pct);
  const text = `${abs < 10 ? abs.toFixed(1) : Math.round(abs).toLocaleString('en-US')}%`;
  return pct > 0 ? { dir: 'up', arrow: '▲', text } : { dir: 'down', arrow: '▼', text };
}
