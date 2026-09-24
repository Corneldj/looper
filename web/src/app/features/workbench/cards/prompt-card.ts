import { Component, OnDestroy, computed, effect, inject, input, output, signal, untracked } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { ApiService } from '../../../core/api.service';
import { apiErrorMessage } from '../../../core/format';
import { MapResourceDto, ResourceDto } from '../../../core/models';

type SaveState = 'idle' | 'pending' | 'saving' | 'error';

/**
 * The prompt box on a one-off prompt's workbench card — the "Instructions / context" box of
 * Ticket Filler's Board page, right on the canvas. It saves itself a moment after typing stops
 * (and on leaving the box), writing the text alone. While nobody is editing it follows the server,
 * so a run taking the prompt shows up as an emptied box within a poll.
 */
@Component({
  selector: 'app-prompt-card',
  imports: [FormsModule],
  templateUrl: './prompt-card.html',
  styleUrl: './prompt-card.scss',
})
export class PromptCard implements OnDestroy {
  private readonly api = inject(ApiService);

  /** Typing pauses this long before the box saves. */
  static readonly saveDelayMs = 700;

  /** Mirrors OneOffPromptModule.MaxLength: the prompt travels on the run's command line. */
  static readonly maxLength = 8_000;

  readonly resource = input.required<MapResourceDto>();
  readonly saved = output<ResourceDto>();

  readonly maxLength = PromptCard.maxLength;
  readonly draft = signal('');
  readonly state = signal<SaveState>('idle');
  readonly error = signal<string | null>(null);

  readonly status = computed(() => {
    switch (this.state()) {
      case 'pending':
      case 'saving':
        return 'Saving…';
      case 'error':
        return this.error() ?? 'Not saved.';
      default:
        return this.draft().trim() ? 'Waiting for the next run.' : 'Nothing waiting for the next run.';
    }
  });

  private focused = false;
  private timer: ReturnType<typeof setTimeout> | null = null;

  constructor() {
    // Follow the server unless someone is mid-edit: their draft is the truth until it is saved.
    effect(() => {
      const server = this.resource().card?.text ?? '';
      untracked(() => {
        if (!this.focused && this.state() === 'idle') this.draft.set(server);
      });
    });
  }

  onFocus(): void {
    this.focused = true;
  }

  onInput(text: string): void {
    this.draft.set(text);
    this.error.set(null);
    this.state.set('pending');
    this.clearTimer();
    this.timer = setTimeout(() => this.save(), PromptCard.saveDelayMs);
  }

  onBlur(): void {
    this.focused = false;
    if (this.state() === 'pending') {
      this.clearTimer();
      this.save();
    } else if (this.state() === 'idle') {
      // Nothing typed: show what the server holds now — a run may have taken the prompt meanwhile.
      this.draft.set(this.resource().card?.text ?? '');
    }
  }

  ngOnDestroy(): void {
    // Leaving the page mid-pause still keeps what was typed.
    if (this.state() === 'pending') {
      this.clearTimer();
      this.save();
    }
  }

  private save(): void {
    this.state.set('saving');
    const text = this.draft();
    this.api.setOneOffPrompt(this.resource().id, text).subscribe({
      next: dto => {
        // A keystroke during the save starts another round; only then is this answer stale.
        if (this.state() === 'saving') this.state.set('idle');
        this.saved.emit(dto);
      },
      error: err => {
        this.state.set('error');
        this.error.set(apiErrorMessage(err, 'Not saved — check that the API is running.'));
      },
    });
  }

  private clearTimer(): void {
    if (this.timer) clearTimeout(this.timer);
    this.timer = null;
  }
}
