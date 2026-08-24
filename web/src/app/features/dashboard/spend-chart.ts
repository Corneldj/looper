import {
  Component,
  DestroyRef,
  ElementRef,
  afterNextRender,
  computed,
  inject,
  input,
  signal,
} from '@angular/core';
import { CostSeriesPointDto } from '../../core/models';
import { formatCost } from '../../core/format';

interface ChartBar {
  x: number;
  w: number;
  y: number;
  h: number;
  cx: number;
  path: string;
}

/**
 * Single-series daily-cost bar chart (brand indigo — validated ≥3:1 on white),
 * drawn as inline SVG sized to the host via ResizeObserver. Full-height column
 * hit targets drive one shared tooltip; a visually-hidden summary carries the
 * accessible reading.
 */
@Component({
  selector: 'app-spend-chart',
  templateUrl: './spend-chart.html',
  styleUrl: './spend-chart.scss',
})
export class SpendChart {
  readonly points = input.required<CostSeriesPointDto[]>();

  private readonly host = inject(ElementRef);

  protected readonly PAD_TOP = 10;
  protected readonly PAD_LEFT = 50;
  protected readonly PAD_RIGHT = 10;
  protected readonly PLOT_H = 190;
  protected readonly AXIS_H = 26;
  protected readonly TOTAL_H = this.PAD_TOP + this.PLOT_H + this.AXIS_H;

  private readonly width = signal(0);
  protected readonly hovered = signal<number | null>(null);

  constructor() {
    const destroyRef = inject(DestroyRef);
    afterNextRender(() => {
      const observer = new ResizeObserver(entries => {
        this.width.set(Math.floor(entries[0].contentRect.width));
      });
      observer.observe(this.host.nativeElement as HTMLElement);
      destroyRef.onDestroy(() => observer.disconnect());
    });
  }

  protected readonly hasData = computed(() =>
    this.points().some(p => p.costUsd > 0 || p.runs > 0),
  );

  protected readonly layout = computed(() => {
    const pts = this.points();
    const width = this.width();
    if (!pts.length || width < 120) return null;

    const plotW = width - this.PAD_LEFT - this.PAD_RIGHT;
    const n = pts.length;
    const slot = plotW / n;
    const barW = Math.max(2, Math.min(24, slot - 2)); // thin marks, 2px gaps, ≤24px

    const maxCost = Math.max(0, ...pts.map(p => p.costUsd));
    const step = niceStep(maxCost / 3);
    const yMax = step > 0 ? step * 3 : 1;
    const baselineY = this.PAD_TOP + this.PLOT_H;

    const bars: ChartBar[] = pts.map((p, i) => {
      const x = this.PAD_LEFT + i * slot + (slot - barW) / 2;
      let h = (p.costUsd / yMax) * this.PLOT_H;
      if (p.costUsd > 0) h = Math.max(h, 1.5);
      const y = baselineY - h;
      return { x, w: barW, y, h, cx: x + barW / 2, path: roundedTopPath(x, y, barW, h) };
    });

    const ticks =
      step > 0
        ? [1, 2, 3].map(k => ({
            y: baselineY - (k / 3) * this.PLOT_H,
            label: formatCost(step * k),
          }))
        : [];

    const labelStep = Math.ceil(n / 7);
    const xLabels = pts
      .map((p, i) => ({ i, x: this.PAD_LEFT + (i + 0.5) * slot, text: shortDate(p.date) }))
      .filter(l => l.i % labelStep === 0);

    const slots = pts.map((p, i) => ({
      i,
      x: this.PAD_LEFT + i * slot,
      w: slot,
      aria: `${longDate(p.date)}: ${formatCost(p.costUsd)}, ${p.runs} runs, ${p.failures} failures`,
    }));

    return { width, bars, ticks, xLabels, slots, baselineY };
  });

  protected readonly tip = computed(() => {
    const layout = this.layout();
    const i = this.hovered();
    if (!layout || i === null) return null;
    const point = this.points()[i];
    const bar = layout.bars[i];
    if (!point || !bar) return null;

    const estWidth = 185;
    let left = bar.cx + 12;
    if (left + estWidth > layout.width) left = bar.cx - estWidth - 12;
    return {
      left: Math.max(4, left),
      top: this.PAD_TOP + 4,
      date: longDate(point.date),
      cost: formatCost(point.costUsd),
      runs: point.runs,
      failures: point.failures,
    };
  });

  /** Accessible fallback: the chart's story in one visually-hidden sentence. */
  protected readonly summaryText = computed(() => {
    const pts = this.points();
    if (!pts.length) return '';
    const total = pts.reduce((s, p) => s + p.costUsd, 0);
    const runs = pts.reduce((s, p) => s + p.runs, 0);
    const failures = pts.reduce((s, p) => s + p.failures, 0);
    const peak = pts.reduce((a, b) => (b.costUsd > a.costUsd ? b : a), pts[0]);
    return (
      `Daily spend over ${pts.length} days: ${formatCost(total)} total across ` +
      `${runs} runs (${failures} failed). Peak day ${longDate(peak.date)} at ${formatCost(peak.costUsd)}.`
    );
  });
}

/** Round v up to a "nice" tick step (1 / 2 / 2.5 / 5 × 10^k). */
function niceStep(v: number): number {
  if (v <= 0) return 0;
  const exp = Math.floor(Math.log10(v));
  const f = v / 10 ** exp;
  const nf = f <= 1 ? 1 : f <= 2 ? 2 : f <= 2.5 ? 2.5 : f <= 5 ? 5 : 10;
  return nf * 10 ** exp;
}

/** Bar path: 4px rounded top corners, square at the baseline. */
function roundedTopPath(x: number, y: number, w: number, h: number): string {
  if (h <= 0) return '';
  const r = Math.min(4, w / 2, h);
  const b = y + h;
  return (
    `M${x},${b} L${x},${y + r} Q${x},${y} ${x + r},${y} ` +
    `L${x + w - r},${y} Q${x + w},${y} ${x + w},${y + r} L${x + w},${b} Z`
  );
}

function toUtcDate(date: string): Date {
  return new Date(`${date}T00:00:00Z`);
}

function shortDate(date: string): string {
  return toUtcDate(date).toLocaleDateString('en-GB', { day: 'numeric', month: 'short', timeZone: 'UTC' });
}

function longDate(date: string): string {
  return toUtcDate(date).toLocaleDateString('en-GB', {
    weekday: 'short',
    day: 'numeric',
    month: 'short',
    timeZone: 'UTC',
  });
}
