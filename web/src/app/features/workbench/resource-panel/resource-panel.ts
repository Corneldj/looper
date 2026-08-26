import { Component, OnInit, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { ApiService } from '../../../core/api.service';
import { ResourcesStore } from '../../../core/stores';
import { GeneratedResourceTypeDto, ResourceDto, ResourceType, ResourceTypeDto } from '../../../core/models';
import { ResourceEditor } from './resource-editor';
import { WorkspacesModal } from './workspaces-modal';

@Component({
  selector: 'app-resource-panel',
  imports: [ResourceEditor, FormsModule, WorkspacesModal],
  templateUrl: './resource-panel.html',
  styleUrl: './resource-panel.scss',
})
export class ResourcePanel implements OnInit {
  private readonly api = inject(ApiService);
  readonly store = inject(ResourcesStore);

  /** Type-picker step of the create flow. */
  readonly pickerOpen = signal(false);
  /** Non-null while the editor modal is open (for create and edit alike). */
  readonly editorType = signal<ResourceType | null>(null);
  /** The dynamic type definition when the editor edits a Custom resource. */
  readonly editorTypeDef = signal<ResourceTypeDto | null>(null);
  /** The resource being edited, or null when creating. */
  readonly editing = signal<ResourceDto | null>(null);
  readonly error = signal<string | null>(null);
  /** The WorkspacePool resource whose workspaces modal is open. */
  readonly workspacesFor = signal<ResourceDto | null>(null);

  // ---------- "New type with AI" flow ----------
  readonly aiOpen = signal(false);
  readonly aiDescription = signal('');
  readonly aiBusy = signal(false);
  readonly aiError = signal<string | null>(null);
  readonly aiResult = signal<GeneratedResourceTypeDto | null>(null);

  /** Resources grouped by catalog entry (built-in and dynamic alike); only non-empty groups. */
  readonly groups = computed(() => {
    const resources = this.store.resources();
    return this.store.types().map(meta => ({
      meta,
      resources: resources
        .filter(r => (r.type === 'Custom' ? r.customTypeKey === meta.typeKey : r.type === meta.typeKey))
        .sort((a, b) => a.name.localeCompare(b.name)),
    })).filter(group => group.resources.length > 0);
  });

  ngOnInit(): void {
    this.store.load();
    this.store.loadTypes();
  }

  openPicker(): void {
    this.error.set(null);
    this.pickerOpen.set(true);
  }

  closePicker(): void {
    this.pickerOpen.set(false);
  }

  pickType(entry: ResourceTypeDto): void {
    this.pickerOpen.set(false);
    this.editing.set(null);
    // Field specs present → the generic dynamic form (AI-generated and shipped graph types alike);
    // absent → one of the classic types with a bespoke form. builtIn only governs deletability.
    if (entry.fields) {
      this.editorTypeDef.set(entry);
      this.editorType.set('Custom');
    } else {
      this.editorTypeDef.set(null);
      this.editorType.set(entry.typeKey as ResourceType);
    }
  }

  edit(resource: ResourceDto): void {
    this.error.set(null);
    if (resource.type === 'Custom') {
      const def = this.store.types().find(t => t.typeKey === resource.customTypeKey);
      if (!def) {
        this.error.set(`The resource type '${resource.customTypeKey}' is no longer installed.`);
        return;
      }
      this.editorTypeDef.set(def);
    } else {
      this.editorTypeDef.set(null);
    }
    this.editing.set(resource);
    this.editorType.set(resource.type);
  }

  onEditorClosed(saved: ResourceDto | null): void {
    if (saved) this.store.upsert(saved);
    this.editing.set(null);
    this.editorType.set(null);
    this.editorTypeDef.set(null);
  }

  remove(resource: ResourceDto): void {
    const inUse =
      resource.agentCount > 0
        ? ` It is used by ${resource.agentCount} agent${resource.agentCount === 1 ? '' : 's'}.`
        : '';
    if (!confirm(`Delete “${resource.name}”?${inUse}`)) return;
    this.api.deleteResource(resource.id).subscribe({
      next: () => {
        this.store.remove(resource.id);
        this.error.set(null);
      },
      error: () => this.error.set(`Couldn’t delete “${resource.name}” — is the API running?`),
    });
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

  // ---------- AI type creation ----------

  openAiCreate(): void {
    this.pickerOpen.set(false);
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
  }

  generateType(): void {
    const description = this.aiDescription().trim();
    if (description.length < 10 || this.aiBusy()) return;
    this.aiBusy.set(true);
    this.aiError.set(null);
    this.api.generateResourceType(description).subscribe({
      next: result => {
        this.aiBusy.set(false);
        this.aiResult.set(result);
        this.store.addType(result.type);
      },
      error: err => {
        this.aiBusy.set(false);
        const detail = err?.error?.errors
          ? Object.values(err.error.errors as Record<string, string[]>).flat().join('\n')
          : err?.error?.title;
        this.aiError.set(detail || 'Generation failed — is the API running and the Claude CLI installed?');
      },
    });
  }

  /** From the AI success screen straight into creating the first resource of the new type. */
  useGeneratedType(): void {
    const generated = this.aiResult();
    if (!generated) return;
    this.closeAiCreate();
    this.pickType(generated.type);
  }
}
