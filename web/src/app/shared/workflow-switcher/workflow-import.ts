import { Component, computed, inject, output, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { ApiService } from '../../core/api.service';
import { WorkflowsStore } from '../../core/stores';
import { WorkflowImportResultDto, WorkflowPackageSummaryDto } from '../../core/models';

/**
 * Import a .workflow file (exported by any Looper) as a new workflow. The API reads the file back
 * first — checking its manifest and checksums — so the user sees what is inside before anything
 * is created; the import itself installs missing resource types and creates everything in one
 * transaction, then reports what still needs a human: secrets, paths, paused agents.
 */
@Component({
  selector: 'app-workflow-import',
  imports: [FormsModule],
  templateUrl: './workflow-import.html',
  styleUrl: './workflow-import.scss',
  host: { '(document:keydown.escape)': 'cancel()' },
})
export class WorkflowImport {
  private readonly api = inject(ApiService);
  private readonly store = inject(WorkflowsStore);

  readonly closed = output<void>();

  protected readonly file = signal<File | null>(null);
  protected readonly inspecting = signal(false);
  protected readonly summary = signal<WorkflowPackageSummaryDto | null>(null);
  protected readonly name = signal('');
  protected readonly readError = signal<string | null>(null);
  protected readonly importing = signal(false);
  protected readonly error = signal<string | null>(null);
  protected readonly result = signal<WorkflowImportResultDto | null>(null);
  protected readonly dragOver = signal(false);

  protected readonly canImport = computed(() => this.summary() !== null && !this.importing() && this.result() === null);

  /** One sentence for the result screen: "2 resources, 1 agent, 1 resource type installed (SlackHook)." */
  protected readonly doneSummary = computed(() => {
    const done = this.result();
    if (!done) return '';
    const plural = (n: number, word: string) => `${n} ${word}${n === 1 ? '' : 's'}`;
    const parts = [plural(done.resourcesCreated, 'resource'), plural(done.agentsCreated, 'agent')];
    if (done.resourceTypesInstalled.length > 0) {
      parts.push(`${plural(done.resourceTypesInstalled.length, 'resource type')} installed (${done.resourceTypesInstalled.join(', ')})`);
    }
    if (done.resourceTypesReused.length > 0) {
      parts.push(`${done.resourceTypesReused.length} already here (${done.resourceTypesReused.join(', ')})`);
    }
    return parts.join(', ') + '.';
  });

  protected onFileInput(event: Event): void {
    const input = event.target as HTMLInputElement;
    const file = input.files?.[0];
    if (file) this.choose(file);
    input.value = '';
  }

  protected onDrop(event: DragEvent): void {
    event.preventDefault();
    this.dragOver.set(false);
    const file = event.dataTransfer?.files?.[0];
    if (file) this.choose(file);
  }

  protected onDragOver(event: DragEvent): void {
    event.preventDefault();
    this.dragOver.set(true);
  }

  /** Asks the API to read the file back: a wrong or tampered file is refused here, before anything is created. */
  choose(file: File): void {
    this.readError.set(null);
    this.error.set(null);
    this.summary.set(null);
    this.file.set(file);
    if (!file.name.toLowerCase().endsWith('.workflow')) {
      this.readError.set('Choose a .workflow file exported from Looper.');
      return;
    }
    this.inspecting.set(true);
    this.api.inspectWorkflowFile(file).subscribe({
      next: summary => {
        this.inspecting.set(false);
        this.summary.set(summary);
        this.name.set(summary.name);
      },
      error: err => {
        this.inspecting.set(false);
        this.readError.set(err?.error?.title || err?.error?.detail || 'The file could not be read — is the API running?');
      },
    });
  }

  protected import(): void {
    const file = this.file();
    const summary = this.summary();
    if (!file || !summary || !this.canImport()) return;
    this.importing.set(true);
    this.error.set(null);
    const name = this.name().trim();
    this.api.importWorkflowFile(file, name.length > 0 && name !== summary.name ? name : null).subscribe({
      next: result => {
        this.importing.set(false);
        this.result.set(result);
        this.store.upsert(result.workflow);
      },
      error: err => {
        this.importing.set(false);
        const detail = err?.error?.errors
          ? Object.values(err.error.errors as Record<string, string[]>).flat().join('\n')
          : err?.error?.title || err?.error?.detail;
        this.error.set(detail || 'Importing the workflow failed — is the API running?');
      },
    });
  }

  /** Switches the workbench to the imported workflow. */
  protected open(): void {
    const result = this.result();
    if (result) this.store.select(result.workflow.id);
    this.closed.emit();
  }

  protected cancel(): void {
    if (this.importing()) return;
    this.closed.emit();
  }
}
