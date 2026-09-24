import { Component, computed, inject, input, output, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { ApiService } from '../../../core/api.service';
import { apiErrorMessage } from '../../../core/format';
import { ResourceFieldDto, SECRET_SENTINEL, TicketSummaryDto } from '../../../core/models';

/** "12345, #12346 12347" → [12345, 12346, 12347]: as lenient as the API, each id once, in order. */
export function parseTicketIds(text: string): number[] {
  const ids: number[] = [];
  for (const token of text.split(/[\s,;]+/)) {
    const id = Number(token.replace(/^#/, ''));
    if (Number.isInteger(id) && id > 0 && !ids.includes(id)) ids.push(id);
  }
  return ids;
}

/**
 * The bespoke form for Azure DevOps tickets resources — Ticket Filler's Board page: the connection
 * and board filters, then the board itself to tick tickets on. It edits the same config record the
 * generic form would; the selection is the `ticketIds` text, so it can also be typed. The parent
 * editor still owns saving — retrieving the board never persists anything.
 */
@Component({
  selector: 'app-ticket-picker',
  imports: [FormsModule],
  templateUrl: './ticket-picker.html',
  styleUrl: './ticket-picker.scss',
})
export class TicketPicker {
  private readonly api = inject(ApiService);

  readonly values = input.required<Record<string, unknown>>();
  /** The type's field definitions: labels, hints and options come from the API, as for the generic form. */
  readonly fields = input<ResourceFieldDto[]>([]);
  /** The saved resource when editing — the board can then use its stored token. */
  readonly resourceId = input<string | null>(null);
  readonly valuesChange = output<Record<string, unknown>>();

  readonly secretSentinel = SECRET_SENTINEL;

  /** Null until the board is retrieved. */
  readonly tickets = signal<TicketSummaryDto[] | null>(null);
  readonly loading = signal(false);
  readonly error = signal<string | null>(null);
  readonly search = signal('');

  readonly selectedIds = computed(() => parseTicketIds(this.str('ticketIds')));
  readonly canRetrieve = computed(
    () => !!this.str('organization').trim() && !!this.str('project').trim() && !!this.str('pat'),
  );

  /**
   * What the list shows: tickets matching the search, and every selected one — a ticked ticket
   * never hides behind the search. Selected tickets the search does not match sort to the top.
   */
  readonly visible = computed(() => {
    const needle = this.search().trim().toLowerCase();
    const selected = new Set(this.selectedIds());
    const matches = (t: TicketSummaryDto) =>
      !needle || `${t.id} ${t.title} ${t.type} ${t.state} ${t.assignedTo} ${t.tags.join(' ')}`.toLowerCase().includes(needle);
    const pinned = (t: TicketSummaryDto) => (selected.has(t.id) && !matches(t) ? 1 : 0);
    return (this.tickets() ?? []).filter(t => selected.has(t.id) || matches(t)).sort((a, b) => pinned(b) - pinned(a));
  });

  /** Selected ids the board could not read at all: deleted, moved, or invisible to the token. */
  readonly unknownIds = computed(() => {
    const listed = this.tickets();
    return listed ? this.selectedIds().filter(id => !listed.some(t => t.id === id)) : [];
  });

  readonly status = computed(() => {
    const all = this.tickets() ?? [];
    const open = all.filter(t => t.listed).length;
    const selected = this.selectedIds().length;
    const picked = selected > 0 ? ` · ${selected} selected` : '';
    return this.search().trim()
      ? `${this.visible().length} of ${all.length} shown${picked}`
      : `${open} open ticket${open === 1 ? '' : 's'}${picked}`;
  });

  field(key: string): ResourceFieldDto | undefined {
    return this.fields().find(f => f.key === key);
  }

  str(key: string): string {
    const value = this.values()[key];
    return typeof value === 'string' ? value : '';
  }

  set(key: string, value: unknown): void {
    this.valuesChange.emit({ ...this.values(), [key]: value });
  }

  isSelected(id: number): boolean {
    return this.selectedIds().includes(id);
  }

  /** Ticking adds to the end of the selection; the first ticket stays first — it is the one time is booked against. */
  toggle(id: number): void {
    const ids = this.selectedIds();
    this.set('ticketIds', (ids.includes(id) ? ids.filter(i => i !== id) : [...ids, id]).join(', '));
  }

  retrieve(): void {
    if (!this.canRetrieve() || this.loading()) return;
    this.loading.set(true);
    this.error.set(null);
    this.api
      .listBoardTickets({ resourceId: this.resourceId(), configJson: JSON.stringify(this.values()) })
      .subscribe({
        next: tickets => {
          this.tickets.set(tickets);
          this.loading.set(false);
        },
        error: err => {
          this.loading.set(false);
          this.error.set(apiErrorMessage(err, 'The board could not be retrieved — check the organization, project and token.'));
        },
      });
  }
}
