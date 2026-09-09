import { Component, OnInit, inject, output } from '@angular/core';
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

  /** The user wants a new workflow. */
  readonly newRequested = output<void>();
  /** The user wants to rename / delete the selected workflow. */
  readonly editRequested = output<WorkflowDto>();

  ngOnInit(): void {
    this.store.load();
  }

  protected pick(id: string): void {
    this.store.select(id);
  }

  protected create(): void {
    this.newRequested.emit();
  }

  protected edit(): void {
    const current = this.store.selected();
    if (current) this.editRequested.emit(current);
  }
}
