import { Component, OnInit, inject, output, signal } from '@angular/core';
import { ApiService } from '../../core/api.service';
import { FileDownloads, workflowFileName } from '../../core/file-downloads';
import { FormsModule } from '@angular/forms';
import { WorkflowsStore } from '../../core/stores';
import { WorkflowDto } from '../../core/models';

/**
 * The topbar's workflow control: which workbench you are looking at. The selection is
 * global — the workbench shows that workflow, the dashboard defaults to it — and it
 * survives reloads. "+" and "✎" only ask for the editor: the topbar's backdrop-filter would
 * trap a fixed-position modal inside the header, so the app shell renders it outside.
 */
@Component({
  selector: 'app-workflow-switcher',
  imports: [FormsModule],
  templateUrl: './workflow-switcher.html',
  styleUrl: './workflow-switcher.scss',
})
export class WorkflowSwitcher implements OnInit {
  protected readonly store = inject(WorkflowsStore);
  private readonly api = inject(ApiService);
  private readonly downloads = inject(FileDownloads);

  protected readonly exporting = signal(false);
  protected readonly exportError = signal<string | null>(null);

  /** The user wants a new workflow. */
  readonly newRequested = output<void>();
  /** The user wants to rename / delete the selected workflow. */
  readonly editRequested = output<WorkflowDto>();
  /** The user wants to import a workflow package. */
  readonly importRequested = output<void>();

  ngOnInit(): void {
    this.store.load();
  }

  protected pick(id: string): void {
    this.store.select(id);
  }

  protected create(): void {
    this.newRequested.emit();
  }

  /** Downloads the selected workflow as a .workflow file. */
  protected exportSelected(): void {
    const current = this.store.selected();
    if (!current || this.exporting()) return;
    this.exporting.set(true);
    this.exportError.set(null);
    this.api.exportWorkflowFile(current.id).subscribe({
      next: blob => {
        this.exporting.set(false);
        this.downloads.saveBlob(workflowFileName(current.name), blob);
      },
      error: () => {
        this.exporting.set(false);
        this.exportError.set('Export failed — is the API running?');
        setTimeout(() => this.exportError.set(null), 4000);
      },
    });
  }

  protected importPackage(): void {
    this.importRequested.emit();
  }

  protected edit(): void {
    const current = this.store.selected();
    if (current) this.editRequested.emit(current);
  }
}
