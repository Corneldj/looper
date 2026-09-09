import { Component, OnInit, computed, inject, input, output, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { ApiService } from '../../../core/api.service';
import { FolderPicker } from '../../../shared/folder-picker/folder-picker';
import {
  SCRIPT_TRIGGERS,
  ScriptAssistResultDto,
  ScriptLanguage,
  ScriptRunResultDto,
  ScriptTrigger,
} from '../../../core/models';

/**
 * The bespoke form for Script resources: a code editor with a run panel and an in-place
 * Claude assistant. It edits the same config record the generic dynamic form would
 * (language, code, trigger, args, timeoutSeconds, workingDirectory) — the parent editor
 * still owns saving, so a run or an assist never persists anything by itself.
 */
@Component({
  selector: 'app-script-editor',
  imports: [FormsModule, FolderPicker],
  templateUrl: './script-editor.html',
  styleUrl: './script-editor.scss',
})
export class ScriptEditor implements OnInit {
  private readonly api = inject(ApiService);

  readonly values = input.required<Record<string, unknown>>();
  /** Resource name/description from the parent form — the assistant uses them as context. */
  readonly name = input('');
  readonly description = input('');
  readonly valuesChange = output<Record<string, unknown>>();

  readonly triggers = SCRIPT_TRIGGERS;

  readonly language = computed<ScriptLanguage>(() => (this.values()['language'] === 'bash' ? 'bash' : 'python'));
  readonly trigger = computed<ScriptTrigger>(() => (this.values()['trigger'] === 'after' ? 'after' : 'before'));
  readonly code = computed(() => this.str('code'));
  readonly args = computed(() => this.str('args'));
  readonly workingDirectory = computed(() => this.str('workingDirectory'));
  readonly timeoutSeconds = computed(() => {
    const value = this.values()['timeoutSeconds'];
    return typeof value === 'number' ? value : null;
  });
  readonly lineCount = computed(() => (this.code() ? this.code().split('\n').length : 0));
  readonly triggerBlurb = computed(() => this.triggers.find(t => t.id === this.trigger())?.blurb ?? '');
  readonly slug = computed(
    () => this.name().trim().toLowerCase().replace(/[^a-z0-9]+/g, '-').replace(/^-+|-+$/g, '') || 'script',
  );
  readonly fileName = computed(() => `${this.slug()}.${this.language() === 'bash' ? 'sh' : 'py'}`);
  /** Mirrors ScriptResources.EnvVarName on the API: the variable the agent finds the path in. */
  readonly envVarName = computed(() => `LOOPER_SCRIPT_${this.slug().replace(/-/g, '_').toUpperCase()}`);

  // ---------- Run panel ----------
  readonly runBusy = signal(false);
  readonly runResult = signal<ScriptRunResultDto | null>(null);
  readonly runError = signal<string | null>(null);

  // ---------- Claude assistant ----------
  readonly assistOpen = signal(false);
  readonly assistInstruction = signal('');
  readonly assistAllowRun = signal(true);
  readonly assistBusy = signal(false);
  readonly assistError = signal<string | null>(null);
  readonly assistResult = signal<ScriptAssistResultDto | null>(null);

  readonly pickerOpen = signal(false);

  ngOnInit(): void {
    // A fresh script starts with sensible defaults so the required selects are never blank.
    // One emit: the input only refreshes on the next change detection, so two sequential
    // set() calls would each start from the same stale record and the second would win.
    const values = this.values();
    const patch: Record<string, unknown> = {};
    if (values['language'] !== 'python' && values['language'] !== 'bash') patch['language'] = 'python';
    if (!['before', 'after'].includes(String(values['trigger']))) patch['trigger'] = 'before';
    if (Object.keys(patch).length > 0) this.valuesChange.emit({ ...values, ...patch });
    if (!this.code() && !this.name()) this.assistOpen.set(true); // brand new: lead with the assistant
  }

  str(key: string): string {
    const value = this.values()[key];
    return typeof value === 'string' ? value : '';
  }

  set(key: string, value: unknown): void {
    this.valuesChange.emit({ ...this.values(), [key]: value });
  }

  setTimeout(raw: unknown): void {
    const value = typeof raw === 'number' && !Number.isNaN(raw) && raw > 0 ? Math.round(raw) : null;
    this.set('timeoutSeconds', value);
  }

  /** Tab indents instead of leaving the editor — the one thing a code textarea must do. */
  onCodeKeydown(event: KeyboardEvent): void {
    if (event.key !== 'Tab') return;
    event.preventDefault();
    const box = event.target as HTMLTextAreaElement;
    const indent = this.language() === 'python' ? '    ' : '  ';
    const start = box.selectionStart;
    const end = box.selectionEnd;
    const next = box.value.slice(0, start) + indent + box.value.slice(end);
    box.value = next;
    box.selectionStart = box.selectionEnd = start + indent.length;
    this.set('code', next);
  }

  openPicker(): void {
    this.pickerOpen.set(true);
  }

  onFolderPicked(picked: string | null): void {
    this.pickerOpen.set(false);
    if (picked) this.set('workingDirectory', picked);
  }

  runNow(): void {
    if (this.runBusy() || !this.code().trim()) return;
    this.runBusy.set(true);
    this.runError.set(null);
    this.api
      .runScript({
        language: this.language(),
        code: this.code(),
        args: this.args() || null,
        workingDirectory: this.workingDirectory() || null,
        timeoutSeconds: this.timeoutSeconds(),
      })
      .subscribe({
        next: result => {
          this.runBusy.set(false);
          this.runResult.set(result);
        },
        error: err => {
          this.runBusy.set(false);
          this.runError.set(this.detail(err) ?? 'The run could not be started — is the API running?');
        },
      });
  }

  toggleAssist(): void {
    this.assistOpen.update(open => !open);
  }

  askClaude(): void {
    const instruction = this.assistInstruction().trim();
    if (instruction.length < 5 || this.assistBusy()) return;
    this.assistBusy.set(true);
    this.assistError.set(null);
    this.assistResult.set(null);
    this.api
      .assistScript({
        name: this.name().trim() || 'script',
        description: this.description().trim() || null,
        language: this.language(),
        code: this.code(),
        instruction,
        allowRun: this.assistAllowRun(),
      })
      .subscribe({
        next: result => {
          this.assistBusy.set(false);
          this.assistResult.set(result);
          this.set('code', result.code);
          this.runResult.set(null); // the old run no longer describes this code
        },
        error: err => {
          this.assistBusy.set(false);
          this.assistError.set(this.detail(err) ?? 'Claude could not be reached — is the API running and the Claude CLI installed?');
        },
      });
  }

  private detail(err: unknown): string | null {
    const body = (err as { error?: { errors?: Record<string, string[]>; detail?: string; title?: string } })?.error;
    if (!body) return null;
    if (body.errors) return Object.values(body.errors).flat().join('\n');
    return body.detail || body.title || null;
  }
}
