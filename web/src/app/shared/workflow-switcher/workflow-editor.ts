import { Component, OnInit, computed, inject, input, output, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { ApiService } from '../../core/api.service';
import { FileDownloads, workflowFileName } from '../../core/file-downloads';
import { WorkflowsStore } from '../../core/stores';
import { WorkflowDto } from '../../core/models';

/** Create a workflow, or rename / describe / delete an existing one. Deleting takes its agents and resources with it. */
@Component({
  selector: 'app-workflow-editor',
  imports: [FormsModule],
  templateUrl: './workflow-editor.html',
  styleUrl: './workflow-editor.scss',
  host: { '(document:keydown.escape)': 'cancel()' },
})
export class WorkflowEditor implements OnInit {
  private readonly api = inject(ApiService);
  private readonly store = inject(WorkflowsStore);
  private readonly downloads = inject(FileDownloads);

  /** Existing workflow when editing; null when creating. */
  readonly workflow = input<WorkflowDto | null>(null);
  readonly closed = output<void>();

  protected readonly name = signal('');
  protected readonly description = signal('');
  protected readonly saving = signal(false);
  protected readonly exporting = signal(false);
  protected readonly error = signal<string | null>(null);

  protected readonly canSave = computed(() => this.name().trim().length > 0 && !this.saving());
  protected readonly canDelete = computed(() => {
    const w = this.workflow();
    return w !== null && !w.isDefault && !this.saving();
  });

  ngOnInit(): void {
    const existing = this.workflow();
    if (existing) {
      this.name.set(existing.name);
      this.description.set(existing.description);
    }
  }

  protected cancel(): void {
    if (this.saving()) return;
    this.closed.emit();
  }

  protected save(): void {
    if (!this.canSave()) return;
    this.saving.set(true);
    this.error.set(null);
    const body = { name: this.name().trim(), description: this.description().trim() };
    const existing = this.workflow();
    const request = existing ? this.api.updateWorkflow(existing.id, body) : this.api.createWorkflow(body);
    request.subscribe({
      next: saved => {
        this.saving.set(false);
        this.store.upsert(saved);
        if (!existing) this.store.select(saved.id); // a new workflow is where you want to be next
        this.closed.emit();
      },
      error: err => {
        this.saving.set(false);
        this.error.set(err?.error?.title || err?.error?.detail || 'Saving the workflow failed — is the API running?');
      },
    });
  }

  /** Downloads the workflow as a .workflow file another Looper can import. Secrets are stripped server-side. */
  protected exportWorkflow(): void {
    const existing = this.workflow();
    if (!existing || this.exporting()) return;
    this.exporting.set(true);
    this.error.set(null);
    this.api.exportWorkflowFile(existing.id).subscribe({
      next: blob => {
        this.exporting.set(false);
        this.downloads.saveBlob(workflowFileName(existing.name), blob);
      },
      error: err => {
        this.exporting.set(false);
        this.error.set(err?.error?.title || 'Exporting the workflow failed — is the API running?');
      },
    });
  }

  protected remove(): void {
    const existing = this.workflow();
    if (!existing || !this.canDelete()) return;
    const contents = existing.agentCount + existing.resourceCount > 0
      ? ` Its ${existing.agentCount} agent${existing.agentCount === 1 ? '' : 's'} and ${existing.resourceCount} resource${existing.resourceCount === 1 ? '' : 's'} go with it, run history included.`
      : '';
    if (!confirm(`Delete workflow “${existing.name}”?${contents}`)) return;
    this.saving.set(true);
    this.api.deleteWorkflow(existing.id).subscribe({
      next: () => {
        this.saving.set(false);
        this.store.remove(existing.id);
        this.closed.emit();
      },
      error: err => {
        this.saving.set(false);
        this.error.set(err?.error?.title || 'Deleting the workflow failed.');
      },
    });
  }
}
