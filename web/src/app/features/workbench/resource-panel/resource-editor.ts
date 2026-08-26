import { Component, OnInit, computed, inject, input, output, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { ApiService } from '../../../core/api.service';
import { FolderPicker } from '../../../shared/folder-picker/folder-picker';
import {
  MODELS,
  ResourceDto,
  ResourceFieldDto,
  ResourceType,
  ResourceTypeDto,
  SECRET_SENTINEL,
  resourceTypeMeta,
} from '../../../core/models';

type McpTransport = 'stdio' | 'http' | 'sse';

/**
 * Modal editor for creating and editing a resource of one fixed type.
 * Builds the type-specific configJson contract expected by the executor.
 */
@Component({
  selector: 'app-resource-editor',
  imports: [FormsModule, FolderPicker],
  templateUrl: './resource-editor.html',
  styleUrl: './resource-editor.scss',
})
export class ResourceEditor implements OnInit {
  private readonly api = inject(ApiService);

  /** Existing resource when editing; null when creating. */
  readonly resource = input<ResourceDto | null>(null);
  /** The resource type this editor edits (fixed for the lifetime of the modal). */
  readonly type = input.required<ResourceType>();
  /** For type 'Custom': the dynamic type definition whose fields drive the form. */
  readonly typeDef = input<ResourceTypeDto | null>(null);
  /** Emits the saved dto, or null when cancelled. */
  readonly closed = output<ResourceDto | null>();

  readonly models = MODELS;
  readonly secretSentinel = SECRET_SENTINEL;
  readonly meta = computed(() => {
    const def = this.typeDef();
    return def
      ? { icon: def.icon, label: def.label, blurb: def.blurb }
      : resourceTypeMeta(this.type());
  });

  // ---------- Custom (dynamic module) ----------
  readonly customFields = computed<ResourceFieldDto[]>(() => this.typeDef()?.fields ?? []);
  readonly customValues = signal<Record<string, unknown>>({});

  setCustomValue(key: string, value: unknown): void {
    this.customValues.update(values => ({ ...values, [key]: value }));
  }

  asText(value: unknown): string {
    return typeof value === 'string' ? value : '';
  }

  /** Which dynamic Path-kind field the folder picker is currently choosing for. */
  readonly customPathFieldKey = signal<string | null>(null);

  openCustomFolderPicker(fieldKey: string): void {
    this.customPathFieldKey.set(fieldKey);
  }

  onCustomFolderPicked(picked: string | null): void {
    const fieldKey = this.customPathFieldKey();
    this.customPathFieldKey.set(null);
    if (picked && fieldKey) this.setCustomValue(fieldKey, picked);
  }

  readonly saving = signal(false);
  readonly saveError = signal<string | null>(null);

  // ---------- Common fields ----------
  readonly name = signal('');
  readonly description = signal('');

  // ---------- McpServer ----------
  readonly transport = signal<McpTransport>('stdio');
  readonly command = signal('');
  readonly argsText = signal('');
  readonly envText = signal('');
  readonly url = signal('');

  // ---------- FileLocation ----------
  readonly path = signal('');
  readonly primary = signal(false);
  readonly folderPickerOpen = signal(false);

  openFolderPicker(): void {
    this.folderPickerOpen.set(true);
  }

  onFolderPicked(picked: string | null): void {
    this.folderPickerOpen.set(false);
    if (picked) this.path.set(picked);
  }

  // ---------- Rag ----------
  readonly instructions = signal('');
  readonly ragPath = signal('');
  readonly ragUrl = signal('');

  // ---------- TestingAction ----------
  readonly testCommand = signal('');
  readonly workingDirectory = signal('');
  readonly timeoutSeconds = signal<number | null>(null);

  // ---------- Rule ----------
  readonly ruleText = signal('');

  // ---------- SubAgent ----------
  readonly subDescription = signal('');
  readonly subPrompt = signal('');
  readonly subTools = signal('');
  readonly subModel = signal('');

  // ---------- AzureConnection ----------
  readonly tenantId = signal('');
  readonly subscriptionId = signal('');
  readonly clientId = signal('');
  readonly clientSecret = signal('');

  // ---------- PatToken ----------
  readonly envVar = signal('');
  readonly tokenValue = signal('');

  /** True while a stored secret is untouched — sending the sentinel back keeps it. */
  readonly clientSecretUnchanged = computed(() => this.clientSecret() === SECRET_SENTINEL);
  readonly tokenValueUnchanged = computed(() => this.tokenValue() === SECRET_SENTINEL);

  readonly canSave = computed(() => {
    if (!this.name().trim()) return false;
    const type = this.type();
    switch (type) {
      case 'McpServer':
        return this.transport() === 'stdio' ? !!this.command().trim() : !!this.url().trim();
      case 'FileLocation':
        return !!this.path().trim();
      case 'Rag':
        return !!this.instructions().trim();
      case 'TestingAction':
        return !!this.testCommand().trim();
      case 'Rule':
        return !!this.ruleText().trim();
      case 'SubAgent':
        return !!this.subDescription().trim() && !!this.subPrompt().trim();
      case 'AzureConnection':
        return (
          !!this.tenantId().trim() &&
          !!this.subscriptionId().trim() &&
          !!this.clientId().trim() &&
          !!this.clientSecret()
        );
      case 'PatToken':
        return !!this.envVar().trim() && !!this.tokenValue();
      case 'Custom':
        return this.customFields().every(field => {
          if (!field.required) return true;
          const value = this.customValues()[field.key];
          if (field.kind === 'Boolean') return true;
          if (field.kind === 'Number') return typeof value === 'number' && !Number.isNaN(value);
          return typeof value === 'string' && value.trim().length > 0;
        });
    }
  });

  ngOnInit(): void {
    const existing = this.resource();
    if (!existing) return;
    this.name.set(existing.name);
    this.description.set(existing.description);
    let config: Record<string, unknown> = {};
    try {
      const parsed = JSON.parse(existing.configJson);
      if (parsed && typeof parsed === 'object' && !Array.isArray(parsed)) config = parsed;
    } catch {
      // Malformed stored config — start from an empty form rather than failing.
    }
    this.populateFrom(config);
  }

  cancel(): void {
    this.closed.emit(null);
  }

  save(): void {
    if (!this.canSave() || this.saving()) return;
    this.saving.set(true);
    this.saveError.set(null);
    const configJson = JSON.stringify(this.buildConfig());
    const name = this.name().trim();
    const description = this.description().trim();
    const existing = this.resource();
    const request = existing
      ? this.api.updateResource(existing.id, { name, description, configJson })
      : this.api.createResource({
          name,
          type: this.type(),
          customTypeKey: this.typeDef()?.typeKey ?? null,
          description,
          configJson,
        });
    request.subscribe({
      next: dto => this.closed.emit(dto),
      error: () => {
        this.saving.set(false);
        this.saveError.set('Save failed — check that the API is running and try again.');
      },
    });
  }

  private populateFrom(config: Record<string, unknown>): void {
    const str = (key: string) => (typeof config[key] === 'string' ? (config[key] as string) : '');
    const type = this.type();
    switch (type) {
      case 'McpServer': {
        const transport = config['transport'];
        this.transport.set(transport === 'http' || transport === 'sse' ? transport : 'stdio');
        this.command.set(str('command'));
        const args = config['args'];
        if (Array.isArray(args)) {
          this.argsText.set(args.filter((a): a is string => typeof a === 'string').join('\n'));
        }
        const env = config['env'];
        if (env && typeof env === 'object' && !Array.isArray(env)) {
          this.envText.set(
            Object.entries(env as Record<string, unknown>)
              .map(([key, value]) => `${key}=${value}`)
              .join('\n'),
          );
        }
        this.url.set(str('url'));
        break;
      }
      case 'FileLocation':
        this.path.set(str('path'));
        this.primary.set(config['primary'] === true);
        break;
      case 'Rag':
        this.instructions.set(str('instructions'));
        this.ragPath.set(str('path'));
        this.ragUrl.set(str('url'));
        break;
      case 'TestingAction':
        this.testCommand.set(str('command'));
        this.workingDirectory.set(str('workingDirectory'));
        this.timeoutSeconds.set(
          typeof config['timeoutSeconds'] === 'number' ? (config['timeoutSeconds'] as number) : null,
        );
        break;
      case 'Rule':
        this.ruleText.set(str('text'));
        break;
      case 'SubAgent':
        this.subDescription.set(str('description'));
        this.subPrompt.set(str('prompt'));
        this.subTools.set(str('tools'));
        this.subModel.set(str('model'));
        break;
      case 'AzureConnection':
        this.tenantId.set(str('tenantId'));
        this.subscriptionId.set(str('subscriptionId'));
        this.clientId.set(str('clientId'));
        this.clientSecret.set(str('clientSecret'));
        break;
      case 'PatToken':
        this.envVar.set(str('envVar'));
        this.tokenValue.set(str('value'));
        break;
      case 'Custom': {
        const values: Record<string, unknown> = {};
        for (const field of this.customFields()) {
          const value = config[field.key];
          if (field.kind === 'Boolean') values[field.key] = value === true;
          else if (field.kind === 'Number') values[field.key] = typeof value === 'number' ? value : null;
          else values[field.key] = typeof value === 'string' ? value : '';
        }
        this.customValues.set(values);
        break;
      }
    }
  }

  private buildConfig(): Record<string, unknown> {
    const type = this.type();
    switch (type) {
      case 'McpServer': {
        const stdio = this.transport() === 'stdio';
        return {
          transport: this.transport(),
          command: stdio ? this.command().trim() : '',
          args: stdio ? this.parseLines(this.argsText()) : [],
          env: stdio ? this.parseEnv(this.envText()) : {},
          url: stdio ? '' : this.url().trim(),
        };
      }
      case 'FileLocation':
        return { path: this.path().trim(), primary: this.primary() };
      case 'Rag': {
        const config: Record<string, unknown> = { instructions: this.instructions().trim() };
        if (this.ragPath().trim()) config['path'] = this.ragPath().trim();
        if (this.ragUrl().trim()) config['url'] = this.ragUrl().trim();
        return config;
      }
      case 'TestingAction': {
        const config: Record<string, unknown> = { command: this.testCommand().trim() };
        if (this.workingDirectory().trim()) config['workingDirectory'] = this.workingDirectory().trim();
        const timeout = this.timeoutSeconds();
        if (timeout != null && timeout > 0) config['timeoutSeconds'] = timeout;
        return config;
      }
      case 'Rule':
        return { text: this.ruleText().trim() };
      case 'SubAgent': {
        const config: Record<string, unknown> = {
          description: this.subDescription().trim(),
          prompt: this.subPrompt().trim(),
        };
        if (this.subTools().trim()) config['tools'] = this.subTools().trim();
        if (this.subModel()) config['model'] = this.subModel();
        return config;
      }
      case 'AzureConnection':
        return {
          tenantId: this.tenantId().trim(),
          subscriptionId: this.subscriptionId().trim(),
          clientId: this.clientId().trim(),
          clientSecret: this.clientSecret(),
        };
      case 'PatToken':
        return { envVar: this.envVar().trim(), value: this.tokenValue() };
      case 'Custom': {
        const config: Record<string, unknown> = {};
        for (const field of this.customFields()) {
          const value = this.customValues()[field.key];
          if (field.kind === 'Boolean') {
            config[field.key] = value === true;
          } else if (field.kind === 'Number') {
            if (typeof value === 'number' && !Number.isNaN(value)) config[field.key] = value;
          } else if (field.kind === 'Password') {
            // Never trim secrets; empty means "not provided".
            if (typeof value === 'string' && value.length > 0) config[field.key] = value;
          } else if (typeof value === 'string' && value.trim().length > 0) {
            config[field.key] = value.trim();
          }
        }
        return config;
      }
    }
  }

  private parseLines(text: string): string[] {
    return text
      .split('\n')
      .map(line => line.trim())
      .filter(line => line.length > 0);
  }

  private parseEnv(text: string): Record<string, string> {
    const env: Record<string, string> = {};
    for (const line of text.split('\n')) {
      const trimmed = line.trim();
      if (!trimmed) continue;
      const eq = trimmed.indexOf('=');
      if (eq <= 0) continue;
      env[trimmed.slice(0, eq).trim()] = trimmed.slice(eq + 1).trim();
    }
    return env;
  }
}
