import { Component, OnInit, computed, inject, input, output, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { ApiService } from '../../../core/api.service';
import { MetricSource, MetricSummaryDto, MetricValueDto } from '../../../core/models';
import { formatDateTime, formatMetric } from '../../../core/format';

/**
 * The measurements behind one metric card: every value with who reported it and why,
 * a manual entry form, and the exact reporting snippets for agents and scripts.
 */
@Component({
  selector: 'app-metric-detail-modal',
  imports: [FormsModule],
  templateUrl: './metric-detail-modal.html',
  styleUrl: './metric-detail-modal.scss',
  host: { '(document:keydown.escape)': 'close()' },
})
export class MetricDetailModal implements OnInit {
  private readonly api = inject(ApiService);

  readonly metric = input.required<MetricSummaryDto>();
  /** Fired after a value is added or removed so the cards can refresh. */
  readonly changed = output<void>();
  readonly closed = output<void>();

  protected readonly values = signal<MetricValueDto[]>([]);
  protected readonly loaded = signal(false);
  protected readonly error = signal<string | null>(null);
  protected readonly busy = signal(false);

  protected readonly newValue = signal<number | null>(null);
  protected readonly newNote = signal('');

  protected readonly formatDateTime = formatDateTime;
  protected readonly formatMetric = formatMetric;

  protected readonly slug = computed(
    () => this.metric().name.trim().toLowerCase().replace(/[^a-z0-9]+/g, '-').replace(/^-+|-+$/g, '') || 'metric',
  );

  protected readonly curlSnippet = computed(
    () =>
      `curl -s -X POST "$LOOPER_API_URL/api/metrics/values" -H 'Content-Type: application/json' \\\n` +
      `  -d '{"metric":"${this.metric().resourceId}","runId":"'"$LOOPER_RUN_ID"'","value":<number>,"note":"<what you measured>"}'`,
  );

  protected readonly scriptSnippet = computed(() => `print("@metric ${this.slug()}=<number> <note>")   # any Script resource`);

  ngOnInit(): void {
    this.reload();
  }

  reload(): void {
    this.api.getMetricValues(this.metric().resourceId).subscribe({
      next: list => {
        this.values.set(list);
        this.loaded.set(true);
        this.error.set(null);
      },
      error: () => {
        this.loaded.set(true);
        this.error.set('Couldn’t load the values — is the API running?');
      },
    });
  }

  close(): void {
    this.closed.emit();
  }

  protected sourceChip(source: MetricSource): string {
    switch (source) {
      case 'Agent': return 'chip-accent';
      case 'Script': return 'chip-blue';
      case 'Manual': return 'chip-amber';
      default: return 'chip-neutral';
    }
  }

  protected canAdd(): boolean {
    const v = this.newValue();
    return typeof v === 'number' && Number.isFinite(v) && !this.busy();
  }

  protected add(): void {
    if (!this.canAdd()) return;
    this.busy.set(true);
    this.api
      .recordMetricValue({
        metric: this.metric().resourceId,
        value: this.newValue()!,
        note: this.newNote().trim() || null,
        source: 'Manual',
      })
      .subscribe({
        next: value => {
          this.busy.set(false);
          this.values.update(list => [value, ...list]);
          this.newValue.set(null);
          this.newNote.set('');
          this.error.set(null);
          this.changed.emit();
        },
        error: err => {
          this.busy.set(false);
          const body = err?.error;
          const detail = body?.errors ? Object.values(body.errors as Record<string, string[]>).flat().join('\n') : body?.detail || body?.title;
          this.error.set(detail || 'Adding the value failed.');
        },
      });
  }

  protected remove(value: MetricValueDto): void {
    if (!confirm(`Remove this measurement (${formatMetric(value.value, this.metric().unit)})?`)) return;
    this.api.deleteMetricValue(value.id).subscribe({
      next: () => {
        this.values.update(list => list.filter(v => v.id !== value.id));
        this.changed.emit();
      },
      error: () => this.error.set('Removing the value failed.'),
    });
  }
}
