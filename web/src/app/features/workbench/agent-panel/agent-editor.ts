import { Component, OnInit, computed, inject, input, output, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { ApiService } from '../../../core/api.service';
import { ResourcesStore } from '../../../core/stores';
import { FolderPicker } from '../../../shared/folder-picker/folder-picker';
import {
  AgentDetailDto,
  EFFORT_LEVELS,
  EffortLevel,
  MODELS,
  SaveAgentRequest,
} from '../../../core/models';

interface AgentForm {
  name: string;
  description: string;
  prompt: string;
  model: string;
  effort: EffortLevel;
  intervalMinutes: number | null;
  maxTurns: number | null;
  maxBudgetUsd: number | null;
  workingDirectory: string;
  allowedTools: string;
  bypassPermissions: boolean;
  dryRun: boolean;
}

@Component({
  selector: 'app-agent-editor',
  imports: [FormsModule, FolderPicker],
  templateUrl: './agent-editor.html',
  styleUrl: './agent-editor.scss',
  host: { '(document:keydown.escape)': 'onEscape()' },
})
export class AgentEditor implements OnInit {
  private readonly api = inject(ApiService);
  protected readonly resourcesStore = inject(ResourcesStore);

  protected readonly folderPickerOpen = signal(false);

  protected openFolderPicker(): void {
    this.folderPickerOpen.set(true);
  }

  /** Escape dismisses the folder picker first, and only then the editor itself. */
  protected onEscape(): void {
    if (this.folderPickerOpen()) {
      this.folderPickerOpen.set(false);
      return;
    }
    this.cancel();
  }

  protected onFolderPicked(picked: string | null): void {
    this.folderPickerOpen.set(false);
    if (picked) this.form.workingDirectory = picked;
  }

  /** Fresh detail when editing; null for create. */
  readonly agent = input<AgentDetailDto | null>(null);
  /** true = saved (the panel refreshes the store), false = dismissed. */
  readonly closed = output<boolean>();

  protected readonly models = MODELS;
  protected readonly effortLevels = EFFORT_LEVELS;
  protected readonly intervalPresets = [
    { label: '15m', minutes: 15 },
    { label: '1h', minutes: 60 },
    { label: '4h', minutes: 240 },
    { label: '24h', minutes: 1440 },
  ];

  protected readonly saving = signal(false);
  protected readonly error = signal<string | null>(null);
  protected readonly selectedIds = signal<ReadonlySet<string>>(new Set<string>());

  // Dry run defaults ON so new loops are free to rehearse.
  protected form: AgentForm = {
    name: '',
    description: '',
    prompt: '',
    model: 'claude-opus-5',
    effort: 'High',
    intervalMinutes: 60,
    maxTurns: 25,
    maxBudgetUsd: null,
    workingDirectory: '',
    allowedTools: '',
    bypassPermissions: true,
    dryRun: true,
  };

  protected readonly resourceGroups = computed(() => {
    const resources = this.resourcesStore.resources();
    return this.resourcesStore.types()
      .map(catalogEntry => ({
        meta: { type: catalogEntry.typeKey, icon: catalogEntry.icon, label: catalogEntry.label },
        items: resources.filter(r =>
          r.type === 'Custom' ? r.customTypeKey === catalogEntry.typeKey : r.type === catalogEntry.typeKey),
      }))
      .filter(group => group.items.length > 0);
  });

  ngOnInit(): void {
    if (!this.resourcesStore.loaded()) this.resourcesStore.load();
    if (!this.resourcesStore.typesLoaded()) this.resourcesStore.loadTypes();

    const existing = this.agent();
    if (existing) {
      this.form = {
        name: existing.name,
        description: existing.description,
        prompt: existing.prompt,
        model: existing.model,
        effort: existing.effort,
        intervalMinutes: existing.intervalMinutes,
        maxTurns: existing.maxTurns,
        maxBudgetUsd: existing.maxBudgetUsd,
        workingDirectory: existing.workingDirectory ?? '',
        allowedTools: existing.allowedTools ?? '',
        bypassPermissions: existing.bypassPermissions,
        dryRun: existing.dryRun,
      };
      this.selectedIds.set(new Set(existing.resourceIds));
    }
  }

  protected get canSave(): boolean {
    return this.form.name.trim().length > 0 && this.form.prompt.trim().length > 0;
  }

  protected setIntervalPreset(minutes: number): void {
    this.form.intervalMinutes = minutes;
  }

  protected isSelected(id: string): boolean {
    return this.selectedIds().has(id);
  }

  protected toggleResource(id: string): void {
    this.selectedIds.update(current => {
      const next = new Set(current);
      if (next.has(id)) next.delete(id);
      else next.add(id);
      return next;
    });
  }

  protected cancel(): void {
    if (this.saving()) return;
    this.closed.emit(false);
  }

  protected save(): void {
    if (!this.canSave || this.saving()) return;
    this.saving.set(true);
    this.error.set(null);

    const body = this.buildRequest();
    const existing = this.agent();
    const request$ = existing ? this.api.updateAgent(existing.id, body) : this.api.createAgent(body);

    request$.subscribe({
      next: () => this.closed.emit(true),
      error: () => {
        this.saving.set(false);
        this.error.set('Saving failed — check that the API is running and try again.');
      },
    });
  }

  private buildRequest(): SaveAgentRequest {
    const f = this.form;
    return {
      name: f.name.trim(),
      description: f.description.trim(),
      prompt: f.prompt.trim(),
      model: f.model,
      effort: f.effort,
      intervalMinutes: this.positiveIntOr(f.intervalMinutes, 60),
      maxTurns: this.positiveIntOr(f.maxTurns, 25),
      maxBudgetUsd: f.maxBudgetUsd != null && f.maxBudgetUsd > 0 ? f.maxBudgetUsd : null,
      workingDirectory: f.workingDirectory.trim() || null,
      allowedTools: f.allowedTools.trim() || null,
      bypassPermissions: f.bypassPermissions,
      dryRun: f.dryRun,
      resourceIds: [...this.selectedIds()],
    };
  }

  private positiveIntOr(value: number | null, fallback: number): number {
    if (value == null || !Number.isFinite(value) || value < 1) return fallback;
    return Math.round(value);
  }
}
