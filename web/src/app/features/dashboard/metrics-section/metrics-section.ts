import { Component, computed, inject, input, signal } from '@angular/core';
import { takeUntilDestroyed, toObservable } from '@angular/core/rxjs-interop';
import { catchError, map, of, switchMap, timer } from 'rxjs';
import { ApiService } from '../../../core/api.service';
import { ResourcesStore, WorkflowsStore } from '../../../core/stores';
import { METRIC_AGGREGATIONS, MetricSummaryDto, ResourceDto, ResourceTypeDto } from '../../../core/models';
import { formatMetric, relativeTime } from '../../../core/format';
import { ResourceEditor } from '../../workbench/resource-editor/resource-editor';
import { MetricDetailModal } from './metric-detail-modal';

/** Trend as the card shows it: direction-aware tone, because "up" is only good when up is good. */
interface Trend {
  tone: 'good' | 'bad' | 'flat';
  arrow: string;
  text: string;
}

/**
 * The outcomes the user defined, as dashboard cards. Every Metric resource shows up here the
 * moment it exists — with its current reading, a direction-aware trend, target progress and
 * a sparkline — and opens into the measurements behind it.
 */
@Component({
  selector: 'app-metrics-section',
  imports: [ResourceEditor, MetricDetailModal],
  templateUrl: './metrics-section.html',
  styleUrl: './metrics-section.scss',
})
export class MetricsSection {
  private readonly api = inject(ApiService);
  private readonly resourcesStore = inject(ResourcesStore);
  protected readonly workflowsStore = inject(WorkflowsStore);

  /** Reporting window in days — follows the dashboard period selector. */
  readonly days = input<number>(30);
  /** Workflow filter from the dashboard; null = every workflow. */
  readonly workflowId = input<string | null>(null);

  private readonly scope = computed(() => ({ days: this.days(), workflowId: this.workflowId() }));

  protected readonly metrics = signal<MetricSummaryDto[]>([]);
  protected readonly loaded = signal(false);
  protected readonly apiError = signal(false);

  protected readonly editorOpen = signal(false);
  protected readonly detailFor = signal<MetricSummaryDto | null>(null);

  protected readonly formatMetric = formatMetric;
  protected readonly relativeTime = relativeTime;

  /** The Metric type definition drives the generic resource form for "+ New metric". */
  protected readonly metricType = computed<ResourceTypeDto | null>(
    () => this.resourcesStore.types().find(t => t.typeKey === 'Metric') ?? null,
  );

  constructor() {
    this.resourcesStore.loadTypes();
    // Refetch when the period changes, poll every 30s.
    toObservable(this.scope)
      .pipe(
        switchMap(scope => timer(0, 30_000).pipe(map(() => scope))),
        switchMap(({ days, workflowId }) => this.api.getMetrics(days, workflowId).pipe(catchError(() => of(null)))),
        takeUntilDestroyed(),
      )
      .subscribe(list => this.apply(list));
  }

  private apply(list: MetricSummaryDto[] | null): void {
    this.apiError.set(list === null);
    if (list) {
      this.metrics.set(list);
      this.loaded.set(true);
      // Keep an open detail view on the fresh summary.
      const open = this.detailFor();
      if (open) this.detailFor.set(list.find(m => m.resourceId === open.resourceId) ?? null);
    }
  }

  protected refresh(): void {
    this.api
      .getMetrics(this.days(), this.workflowId())
      .pipe(catchError(() => of(null)))
      .subscribe(list => this.apply(list));
  }

  protected onEditorClosed(saved: ResourceDto | null): void {
    this.editorOpen.set(false);
    if (saved) {
      this.resourcesStore.upsert(saved);
      this.refresh();
    }
  }

  // ---------- card readings ----------

  protected currentText(m: MetricSummaryDto): string {
    return m.current === null ? '—' : formatMetric(m.current, m.unit);
  }

  /** The number alone; the unit renders beside it in a smaller face so long units never squeeze the figure. */
  protected currentNumber(m: MetricSummaryDto): string {
    return m.current === null ? '—' : formatMetric(m.current, '');
  }

  protected unitIsPrefix(m: MetricSummaryDto): boolean {
    return m.current !== null && ['$', '€', '£'].includes(m.unit.trim());
  }

  protected aggregationLabel(m: MetricSummaryDto): string {
    return METRIC_AGGREGATIONS.find(a => a.id === m.aggregation)?.label ?? m.aggregation.toLowerCase();
  }

  protected aggregationBlurb(m: MetricSummaryDto): string {
    return METRIC_AGGREGATIONS.find(a => a.id === m.aggregation)?.blurb ?? '';
  }

  protected trend(m: MetricSummaryDto): Trend | null {
    const pct = m.trendPct;
    if (pct === null || m.current === null) return null;
    if (Math.abs(pct) < 0.05) return { tone: 'flat', arrow: '', text: 'no change' };
    const abs = Math.abs(pct);
    const text = `${abs < 10 ? abs.toFixed(1) : Math.round(abs).toLocaleString('en-US')}%`;
    const up = pct > 0;
    const good = m.direction === 'Higher' ? up : !up;
    return { tone: good ? 'good' : 'bad', arrow: up ? '▲' : '▼', text };
  }

  /** Progress towards the target as a 0..1 fraction; null without a target or reading. */
  protected progress(m: MetricSummaryDto): number | null {
    if (m.target === null || m.target === 0 || m.current === null) return null;
    if (m.direction === 'Lower') {
      // For "lower is better" the target is a ceiling: full bar = at or under it.
      return Math.max(0, Math.min(1, m.target / Math.max(m.current, Number.EPSILON)));
    }
    return Math.max(0, Math.min(1, m.current / m.target));
  }

  /** Whole-number progress for the label; null (not 0) when there is nothing to show. */
  protected progressPct(m: MetricSummaryDto): number | null {
    const p = this.progress(m);
    return p === null ? null : Math.max(1, Math.round(p * 100));
  }

  protected progressTone(m: MetricSummaryDto): string {
    const p = this.progress(m);
    if (p === null) return '';
    return p >= 1 ? 'good' : p >= 0.6 ? 'warn' : '';
  }

  /** Sparkline as an SVG polyline over a 120×32 box; null with fewer than two points. */
  protected sparkPoints(m: MetricSummaryDto): string | null {
    const series = m.series;
    if (series.length < 2) return null;
    const values = series.map(p => p.value);
    const min = Math.min(...values);
    const max = Math.max(...values);
    const span = max - min || 1;
    const w = 120;
    const h = 32;
    return series
      .map((p, i) => {
        const x = (i / (series.length - 1)) * (w - 2) + 1;
        const y = h - 2 - ((p.value - min) / span) * (h - 4);
        return `${x.toFixed(1)},${y.toFixed(1)}`;
      })
      .join(' ');
  }

  protected reporters(m: MetricSummaryDto): string {
    if (m.agents.length === 0) return 'not attached to an agent yet';
    if (m.agents.length <= 2) return `reported by ${m.agents.join(' & ')}`;
    return `reported by ${m.agents[0]} +${m.agents.length - 1}`;
  }
}
