import { Component, inject, output, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { ApiService } from '../../core/api.service';
import { AgentsStore, ResourcesStore } from '../../core/stores';
import { ArchitectResultDto } from '../../core/models';

/**
 * The AI workflow builder: describe an outcome, the architect composes it from the
 * resource pool (reusing what fits, creating what's missing) and wires the agents.
 * Long-running — the modal stays locked while the build is in flight.
 */
@Component({
  selector: 'app-architect-modal',
  imports: [FormsModule],
  templateUrl: './architect-modal.html',
  styleUrl: './architect-modal.scss',
  host: { '(document:keydown.escape)': 'close()' },
})
export class ArchitectModal {
  private readonly api = inject(ApiService);
  private readonly agentsStore = inject(AgentsStore);
  private readonly resourcesStore = inject(ResourcesStore);

  readonly closed = output<void>();

  readonly description = signal('');
  readonly busy = signal(false);
  /** Transport-level failure (API unreachable, validation) — distinct from a failed build result. */
  readonly error = signal<string | null>(null);
  readonly result = signal<ArchitectResultDto | null>(null);

  close(): void {
    if (this.busy()) return;
    this.closed.emit();
  }

  build(): void {
    const description = this.description().trim();
    if (description.length < 10 || this.busy()) return;
    this.busy.set(true);
    this.error.set(null);
    this.api.buildWorkflow(description).subscribe({
      next: result => {
        this.busy.set(false);
        if (result.success) {
          // Refresh before showing the result so the workbench behind the modal
          // already reflects the new workflow.
          this.agentsStore.refreshNow();
          this.resourcesStore.load();
          this.resourcesStore.loadTypes();
        }
        this.result.set(result);
      },
      error: err => {
        this.busy.set(false);
        const detail = err?.error?.errors
          ? Object.values(err.error.errors as Record<string, string[]>).flat().join('\n')
          : err?.error?.title;
        this.error.set(detail || 'The build couldn’t start — is the API running and the Claude CLI installed?');
      },
    });
  }
}
