import { Component, ElementRef, computed, inject, input, signal } from '@angular/core';

/**
 * Categorical series palette (light mode, validated against #ffffff with the
 * dataviz palette validator): all hard gates pass — lightness band, chroma
 * floor, CVD separation (worst adjacent ΔE 9.1) and normal-vision floor
 * (worst adjacent ΔE 19.6). Three slots sit below 3:1 contrast on white,
 * which is legal here because every bar carries visible direct labels and
 * the agent breakdown table doubles as the accessible view (relief rule).
 * Slot order is the CVD-safety mechanism — never reorder or extend it.
 */
export const SERIES_COLORS: readonly string[] = [
  '#4a3aa7', // violet (brand-adjacent lead)
  '#eb6834', // orange
  '#1baf7a', // aqua
  '#eda100', // yellow
  '#e87ba4', // magenta
  '#008300', // green
  '#2a78d6', // blue
  '#e34948', // red
];

/** De-emphasis tone for the folded "Other" tail — never an identity hue. */
export const OTHER_COLOR = 'var(--text-3)';

export interface BarListRow {
  label: string;
  value: number;
  /** Formatted value shown to the right of the bar (text token color). */
  display: string;
  color: string;
  /** Tooltip lines; `alert` renders the value in red. */
  detail: { label: string; value: string; alert?: boolean }[];
}

/**
 * Horizontal bar list with direct labels (name left, value right) and a
 * shared hover tooltip. Used for "Cost by agent" and "Cost by model".
 */
@Component({
  selector: 'app-bar-list',
  templateUrl: './bar-list.html',
  styleUrl: './bar-list.scss',
})
export class BarList {
  readonly rows = input.required<BarListRow[]>();
  readonly emptyGlyph = input('▦');
  readonly emptyTitle = input('Nothing to chart yet');
  readonly emptyText = input('Costs land here once loops run in this period.');

  private readonly host = inject(ElementRef);

  protected readonly hovered = signal<number | null>(null);
  private readonly tipPos = signal({ x: 0, y: 0 });

  protected readonly hasData = computed(() => this.rows().some(r => r.value > 0));

  private readonly max = computed(() => Math.max(0, ...this.rows().map(r => r.value)));

  protected readonly tip = computed(() => {
    const i = this.hovered();
    if (i === null) return null;
    const row = this.rows()[i];
    return row ? { row, ...this.tipPos() } : null;
  });

  protected widthPct(value: number): number {
    const max = this.max();
    if (max <= 0 || value <= 0) return 0;
    return Math.max((value / max) * 100, 1);
  }

  protected onMove(event: PointerEvent, index: number): void {
    const rect = (this.host.nativeElement as HTMLElement).getBoundingClientRect();
    const estWidth = 210;
    let x = event.clientX - rect.left + 14;
    if (x + estWidth > rect.width) x = event.clientX - rect.left - estWidth - 14;
    const y = Math.min(event.clientY - rect.top + 16, Math.max(rect.height - 40, 0));
    this.tipPos.set({ x: Math.max(4, x), y });
    this.hovered.set(index);
  }

  protected onFocus(event: FocusEvent, index: number): void {
    const hostRect = (this.host.nativeElement as HTMLElement).getBoundingClientRect();
    const rowRect = (event.target as HTMLElement).getBoundingClientRect();
    this.tipPos.set({
      x: Math.max(4, rowRect.left - hostRect.left + 8),
      y: rowRect.bottom - hostRect.top + 6,
    });
    this.hovered.set(index);
  }

  protected clear(): void {
    this.hovered.set(null);
  }

  protected ariaFor(row: BarListRow): string {
    const details = row.detail.map(d => `${d.label} ${d.value}`).join(', ');
    return `${row.label}: ${row.display}. ${details}`;
  }
}
