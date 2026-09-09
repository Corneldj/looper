import { Component, DestroyRef, computed, inject, output, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { Subscription, switchMap, takeWhile, timer } from 'rxjs';
import { ApiService } from '../../core/api.service';
import { ResourcesStore } from '../../core/stores';
import { GeneratedResourceTypeDto, GenerationJobDto, ResourceTypeDto } from '../../core/models';

/**
 * "New resource" step one: pick a type from the catalog (built-in and dynamic alike),
 * or have Claude write a brand-new type. Emits the chosen type; the caller opens the editor.
 */
@Component({
  selector: 'app-resource-type-picker',
  imports: [FormsModule],
  templateUrl: './resource-type-picker.html',
  styleUrl: './resource-type-picker.scss',
  host: { '(document:keydown.escape)': 'onEscape()' },
})
export class ResourceTypePicker {
  private readonly api = inject(ApiService);
  readonly store = inject(ResourcesStore);
  private readonly destroyRef = inject(DestroyRef);
  private poll: Subscription | null = null;

  readonly picked = output<ResourceTypeDto>();
  readonly closed = output<void>();

  readonly error = signal<string | null>(null);

  // ---------- "New type, written by Claude" flow ----------
  readonly aiOpen = signal(false);
  readonly aiDescription = signal('');
  readonly aiBusy = signal(false);
  readonly aiError = signal<string | null>(null);
  readonly aiResult = signal<GeneratedResourceTypeDto | null>(null);
  /** The generation job being watched, with its attempt count and last compiler errors. */
  readonly aiJob = signal<GenerationJobDto | null>(null);
  readonly cancelling = signal(false);

  /** What Claude is doing right now, in words. */
  readonly aiProgress = computed(() => {
    const job = this.aiJob();
    if (!job) return 'Starting…';
    const attempt = job.maxAttempts > 1 ? ` (attempt ${job.attempt} of ${job.maxAttempts})` : '';
    switch (job.phase) {
      case 'Generating': return 'Claude is writing the module…';
      case 'Compiling': return `Compiling${attempt}…`;
      case 'Repairing': return `Attempt ${job.attempt} of ${job.maxAttempts} — Claude is fixing ${job.lastErrors.length} compiler error${job.lastErrors.length === 1 ? '' : 's'}…`;
      case 'Installing': return 'Compiled — installing the module…';
      default: return job.phase;
    }
  });

  onEscape(): void {
    if (this.aiOpen()) this.closeAiCreate();
    else this.closed.emit();
  }

  pick(entry: ResourceTypeDto): void {
    this.picked.emit(entry);
  }

  removeType(entry: ResourceTypeDto, event: Event): void {
    event.stopPropagation();
    if (!confirm(`Remove the resource type “${entry.label}”?`)) return;
    this.api.deleteResourceType(entry.typeKey).subscribe({
      next: () => this.store.removeType(entry.typeKey),
      error: err =>
        this.error.set(
          err?.status === 409
            ? `“${entry.label}” is still used by existing resources — delete those first.`
            : `Couldn’t remove “${entry.label}”.`,
        ),
    });
  }

  openAiCreate(): void {
    this.aiError.set(null);
    this.aiResult.set(null);
    this.aiOpen.set(true);
  }

  closeAiCreate(): void {
    if (this.aiBusy()) return;
    this.aiOpen.set(false);
    this.aiDescription.set('');
    this.aiResult.set(null);
    this.aiError.set(null);
    this.aiJob.set(null);
  }

  /** Starts a generation job and follows it: every compile failure goes back to Claude, up to the cap, until cancelled. */
  generateType(): void {
    const description = this.aiDescription().trim();
    if (description.length < 10 || this.aiBusy()) return;
    this.aiBusy.set(true);
    this.aiError.set(null);
    this.aiJob.set(null);
    this.cancelling.set(false);
    this.api.startResourceTypeGeneration(description).subscribe({
      next: job => {
        this.aiJob.set(job);
        this.follow(job.id);
      },
      error: err => {
        this.aiBusy.set(false);
        const detail = err?.error?.errors
          ? Object.values(err.error.errors as Record<string, string[]>).flat().join('\n')
          : err?.error?.title;
        this.aiError.set(detail || 'Generation could not be started — is the API running?');
      },
    });
  }

  /** Stops the job: the Claude process is killed and nothing is installed. */
  cancelGeneration(): void {
    const job = this.aiJob();
    if (!job || this.cancelling()) return;
    this.cancelling.set(true);
    this.api.cancelResourceTypeGeneration(job.id).subscribe({
      next: updated => this.apply(updated),
      error: () => this.cancelling.set(false),
    });
  }

  private follow(id: string): void {
    this.poll?.unsubscribe();
    this.poll = timer(1500, 2000)
      .pipe(
        switchMap(() => this.api.getResourceTypeGeneration(id)),
        takeWhile(job => !this.isTerminal(job), true),
      )
      .subscribe({
        next: job => this.apply(job),
        error: () => {
          this.aiBusy.set(false);
          this.aiError.set('Lost track of the generation — is the API still running?');
        },
      });
    this.destroyRef.onDestroy(() => this.poll?.unsubscribe());
  }

  private apply(job: GenerationJobDto): void {
    this.aiJob.set(job);
    if (!this.isTerminal(job)) return;
    this.poll?.unsubscribe();
    this.aiBusy.set(false);
    this.cancelling.set(false);
    if (job.phase === 'Installed' && job.result) {
      this.aiResult.set(job.result);
      this.store.addType(job.result.type);
    } else {
      this.aiError.set(job.error || (job.phase === 'Cancelled' ? 'Cancelled — nothing was installed.' : 'Generation failed.'));
    }
  }

  private isTerminal(job: GenerationJobDto): boolean {
    return job.phase === 'Installed' || job.phase === 'Failed' || job.phase === 'Cancelled';
  }

  /** From the AI success screen straight into creating the first resource of the new type. */
  useGeneratedType(): void {
    const generated = this.aiResult();
    if (!generated) return;
    this.aiOpen.set(false);
    this.picked.emit(generated.type);
  }
}
