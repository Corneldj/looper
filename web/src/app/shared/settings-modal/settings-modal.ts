import { Component, OnInit, computed, inject, output, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { ApiService } from '../../core/api.service';
import { SettingsStore } from '../../core/stores';
import { ClaudeAuthMode, SettingsDto, UpdateSettingsRequest } from '../../core/models';

/**
 * App-wide settings. Today that is one decision: what the Claude Code CLI signs in with —
 * the subscription login on the API machine (default) or an API key stored in Looper.
 * The key is write-only: the server reports that one exists and its last characters, never the key.
 */
@Component({
  selector: 'app-settings-modal',
  imports: [FormsModule],
  templateUrl: './settings-modal.html',
  styleUrl: './settings-modal.scss',
  host: { '(document:keydown.escape)': 'cancel()' },
})
export class SettingsModal implements OnInit {
  private readonly api = inject(ApiService);
  private readonly store = inject(SettingsStore);

  readonly closed = output<void>();

  protected readonly current = signal<SettingsDto | null>(null);
  protected readonly loadError = signal<string | null>(null);
  protected readonly mode = signal<ClaudeAuthMode>('Subscription');
  /** A new key being typed. Empty means "leave the stored key alone". */
  protected readonly apiKey = signal('');
  /** The user chose to replace the stored key: show the input instead of the stored-key row. */
  protected readonly replacing = signal(false);
  /** The user chose to remove the stored key on save. */
  protected readonly removing = signal(false);
  protected readonly saving = signal(false);
  protected readonly error = signal<string | null>(null);

  protected readonly hasStoredKey = computed(() => (this.current()?.hasApiKey ?? false) && !this.removing());
  protected readonly showKeyInput = computed(() => this.mode() === 'ApiKey' && (!this.hasStoredKey() || this.replacing()));

  /** API-key mode needs a key to use: the stored one (kept) or a new one typed in. */
  protected readonly canSave = computed(() => {
    if (this.saving() || !this.current()) return false;
    if (this.mode() !== 'ApiKey') return true;
    return this.apiKey().trim().length > 0 || (this.hasStoredKey() && !this.replacing()) || (this.hasStoredKey() && this.replacing() && this.apiKey().trim().length === 0);
  });

  ngOnInit(): void {
    this.api.getSettings().subscribe({
      next: settings => {
        this.current.set(settings);
        this.mode.set(settings.claudeAuthMode);
        this.store.apply(settings);
      },
      error: () => this.loadError.set('Settings could not be loaded — is the API running?'),
    });
  }

  protected selectMode(mode: ClaudeAuthMode): void {
    this.mode.set(mode);
    this.error.set(null);
  }

  protected replaceKey(): void {
    this.replacing.set(true);
    this.removing.set(false);
  }

  protected removeKey(): void {
    this.removing.set(true);
    this.replacing.set(false);
    this.apiKey.set('');
  }

  protected keepKey(): void {
    this.removing.set(false);
    this.replacing.set(false);
    this.apiKey.set('');
  }

  protected cancel(): void {
    if (this.saving()) return;
    this.closed.emit();
  }

  protected save(): void {
    if (!this.canSave()) return;
    const typed = this.apiKey().trim();
    const body: UpdateSettingsRequest = {
      claudeAuthMode: this.mode(),
      apiKey: typed.length > 0 ? typed : null,
      clearApiKey: this.removing(),
    };
    this.saving.set(true);
    this.error.set(null);
    this.api.updateSettings(body).subscribe({
      next: saved => {
        this.saving.set(false);
        this.store.apply(saved);
        this.closed.emit();
      },
      error: err => {
        this.saving.set(false);
        const detail = err?.error?.errors
          ? Object.values(err.error.errors as Record<string, string[]>).flat().join('\n')
          : err?.error?.title || err?.error?.detail;
        this.error.set(detail || 'Saving settings failed — is the API running?');
      },
    });
  }
}
