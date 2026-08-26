import { Component, OnInit, inject, input, output, signal } from '@angular/core';
import { ApiService } from '../../../core/api.service';
import { ResourceDto, WorkspaceDto } from '../../../core/models';
import { formatDateTime, relativeTime } from '../../../core/format';

/** Lifecycle view of one Dynamic Workspaces pool: what's active, done, and cleanable. */
@Component({
  selector: 'app-workspaces-modal',
  templateUrl: './workspaces-modal.html',
  styleUrl: './workspaces-modal.scss',
  host: { '(document:keydown.escape)': 'close()' },
})
export class WorkspacesModal implements OnInit {
  private readonly api = inject(ApiService);

  readonly pool = input.required<ResourceDto>();
  readonly closed = output<void>();

  readonly workspaces = signal<WorkspaceDto[]>([]);
  readonly loaded = signal(false);
  readonly error = signal<string | null>(null);
  readonly busyId = signal<string | null>(null);

  protected readonly relativeTime = relativeTime;
  protected readonly formatDateTime = formatDateTime;

  ngOnInit(): void {
    this.reload();
  }

  reload(): void {
    this.api.getWorkspaces(this.pool().id).subscribe({
      next: list => {
        this.workspaces.set(list);
        this.loaded.set(true);
        this.error.set(null);
      },
      error: () => {
        this.loaded.set(true);
        this.error.set('Couldn’t load workspaces — is the API running?');
      },
    });
  }

  close(): void {
    this.closed.emit();
  }

  markDone(workspace: WorkspaceDto): void {
    this.busyId.set(workspace.id);
    this.api.completeWorkspace(workspace.id).subscribe({
      next: () => {
        this.busyId.set(null);
        this.reload();
      },
      error: () => {
        this.busyId.set(null);
        this.error.set('Marking done failed.');
      },
    });
  }

  clean(workspace: WorkspaceDto): void {
    if (!confirm(`Delete the directory for “${workspace.unit}”?\n${workspace.path}`)) return;
    this.busyId.set(workspace.id);
    this.api.cleanWorkspace(workspace.id).subscribe({
      next: () => {
        this.busyId.set(null);
        this.reload();
      },
      error: () => {
        this.busyId.set(null);
        this.error.set('Cleaning failed — see the API log.');
      },
    });
  }
}
