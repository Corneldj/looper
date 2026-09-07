import { Component, OnInit, inject, input, output, signal } from '@angular/core';
import { ApiService } from '../../../core/api.service';
import { ResourceDto, ScriptRunResultDto } from '../../../core/models';

/** Runs a saved Script resource right from its card and shows what it printed. */
@Component({
  selector: 'app-script-run-modal',
  templateUrl: './script-run-modal.html',
  styleUrl: './script-run-modal.scss',
  host: { '(document:keydown.escape)': 'close()' },
})
export class ScriptRunModal implements OnInit {
  private readonly api = inject(ApiService);

  readonly script = input.required<ResourceDto>();
  readonly closed = output<void>();

  readonly busy = signal(false);
  readonly result = signal<ScriptRunResultDto | null>(null);
  readonly error = signal<string | null>(null);

  ngOnInit(): void {
    this.run();
  }

  run(): void {
    if (this.busy()) return;
    this.busy.set(true);
    this.error.set(null);
    this.api.runScript({ resourceId: this.script().id }).subscribe({
      next: result => {
        this.busy.set(false);
        this.result.set(result);
      },
      error: err => {
        this.busy.set(false);
        const body = err?.error;
        const detail = body?.errors ? Object.values(body.errors as Record<string, string[]>).flat().join('\n') : body?.detail || body?.title;
        this.error.set(detail || 'The script could not be started — is the API running?');
      },
    });
  }

  close(): void {
    if (this.busy()) return;
    this.closed.emit();
  }
}
