import { Component, ElementRef, computed, effect, inject, input, output, signal, untracked } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { FormsModule } from '@angular/forms';
import { Subject, catchError, debounceTime, of, switchMap } from 'rxjs';
import { ApiService } from '../../../core/api.service';
import { apiErrorMessage } from '../../../core/format';
import { JiraIssueDto, MapResourceDto, ResourceDto } from '../../../core/models';

/**
 * The issue chooser on a Jira time-tracking resource's workbench card — the "time goes against"
 * picker of Ticket Filler's Board page, right on the canvas. It searches Jira through the
 * resource's stored address and token, and a pick (or ✕) writes the issue alone. The connection
 * and booking choices stay in the edit modal.
 */
@Component({
  selector: 'app-jira-issue-card',
  imports: [FormsModule],
  templateUrl: './jira-issue-card.html',
  styleUrl: './jira-issue-card.scss',
  host: { '(focusout)': 'onFocusOut($event)' },
})
export class JiraIssueCard {
  private readonly api = inject(ApiService);
  private readonly host = inject<ElementRef<HTMLElement>>(ElementRef);

  /** Keystrokes wait this long before they search, so typing a phrase is one request, not ten. */
  static readonly debounceMs = 300;

  readonly resource = input.required<MapResourceDto>();
  readonly saved = output<ResourceDto>();

  readonly query = signal('');
  readonly results = signal<JiraIssueDto[]>([]);
  readonly searching = signal(false);
  readonly saving = signal(false);
  readonly error = signal<string | null>(null);
  /** A pick the next poll has not confirmed yet — shown meanwhile so the card answers at once. */
  private readonly picked = signal<JiraIssueDto | null>(null);

  readonly connected = computed(() => this.resource().card?.connected ?? false);
  readonly issue = computed<JiraIssueDto | null>(() => {
    const pending = this.picked();
    if (pending) return pending.key ? pending : null;
    const key = this.resource().card?.issueKey ?? '';
    return key ? { key, summary: this.resource().card?.issueSummary ?? '' } : null;
  });

  private readonly queries = new Subject<string>();

  constructor() {
    // Once the server says what was picked, it speaks for itself again.
    effect(() => {
      const key = this.resource().card?.issueKey ?? '';
      untracked(() => {
        if (this.picked()?.key === key) this.picked.set(null);
      });
    });

    // switchMap drops a search still in flight when a newer one starts: the list only ever shows the latest answer.
    this.queries
      .pipe(
        debounceTime(JiraIssueCard.debounceMs),
        switchMap(text => {
          if (text.length < 2 || !this.connected()) return of(null);
          return this.api.searchJiraIssues({ resourceId: this.resource().id, configJson: null, query: text }).pipe(
            catchError(err => {
              this.error.set(apiErrorMessage(err, 'Jira could not be searched — check the address and token.'));
              return of([] as JiraIssueDto[]);
            }),
          );
        }),
        takeUntilDestroyed(),
      )
      .subscribe(issues => {
        this.searching.set(false);
        this.results.set(issues ?? []);
      });
  }

  onQuery(text: string): void {
    const trimmed = text.trim();
    this.query.set(text);
    this.error.set(null);
    // "Searching" from the first keystroke: the debounce wait is part of the search, not "no matches".
    const searchable = trimmed.length >= 2 && this.connected();
    this.searching.set(searchable);
    if (!searchable) this.results.set([]);
    this.queries.next(trimmed);
  }

  /** Enter takes the top result; Escape closes the list. */
  onKey(event: KeyboardEvent): void {
    if (event.key === 'Enter' && this.results().length > 0) {
      event.preventDefault();
      this.pick(this.results()[0]);
    } else if (event.key === 'Escape') {
      this.close();
    }
  }

  pick(issue: JiraIssueDto): void {
    this.close();
    this.write(issue);
  }

  clear(): void {
    this.write({ key: '', summary: '' });
  }

  /** The list closes when focus leaves the card — but not when it moves to one of the card's own results. */
  onFocusOut(event: FocusEvent): void {
    const next = event.relatedTarget as Node | null;
    if (!next || !this.host.nativeElement.contains(next)) this.close();
  }

  private close(): void {
    this.query.set('');
    this.results.set([]);
    this.searching.set(false);
    this.queries.next('');
  }

  private write(issue: JiraIssueDto): void {
    this.saving.set(true);
    this.error.set(null);
    this.picked.set(issue);
    this.api.setJiraIssue(this.resource().id, issue.key, issue.summary).subscribe({
      next: dto => {
        this.saving.set(false);
        this.saved.emit(dto);
      },
      error: err => {
        this.saving.set(false);
        this.picked.set(null);
        this.error.set(apiErrorMessage(err, 'The issue could not be saved — check that the API is running.'));
      },
    });
  }
}
