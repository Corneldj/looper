import { Component, OnInit, computed, inject, output, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { ApiService } from '../../core/api.service';
import { apiErrorMessage } from '../../core/format';
import { SettingsStore } from '../../core/stores';
import { ClaudeAuthMode, SettingsDto, UpdateSettingsRequest } from '../../core/models';

/**
 * App-wide settings: what the Claude Code CLI signs in with — the subscription login on the API
 * machine (default) or an API key stored in Looper — and the limits every run starts under.
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
  protected readonly version = signal<string | null>(null);

  /** Mirrors RunLimits.MaxTimeoutMinutes on the API — the longest delay a timer accepts. */
  protected readonly maxTimeoutMinutes = 71582;
  /** Wall-clock cap per run in minutes; 0 means no limit. */
  protected readonly runTimeoutMinutes = signal<number | null>(null);
  /** Budget in USD for agents that set none of their own; empty means no default. */
  protected readonly defaultMaxBudgetUsd = signal<number | null>(null);

  protected readonly hasStoredKey = computed(() => (this.current()?.hasApiKey ?? false) && !this.removing());
  protected readonly showKeyInput = computed(() => this.mode() === 'ApiKey' && (!this.hasStoredKey() || this.replacing()));

  protected readonly limitsValid = computed(() => {
    const minutes = this.runTimeoutMinutes();
    if (minutes == null || !Number.isFinite(minutes) || minutes < 0 || minutes > this.maxTimeoutMinutes) return false;
    const budget = this.defaultMaxBudgetUsd();
    return budget == null || (Number.isFinite(budget) && budget > 0);
  });

  /** API-key mode needs a key to use: the stored one (kept) or a new one typed in. */
  protected readonly canSave = computed(() => {
    if (this.saving() || !this.current() || !this.limitsValid()) return false;
    if (this.mode() !== 'ApiKey') return true;
    return this.apiKey().trim().length > 0 || (this.hasStoredKey() && !this.replacing()) || (this.hasStoredKey() && this.replacing() && this.apiKey().trim().length === 0);
  });

  ngOnInit(): void {
    this.api.getVersion().subscribe({ next: v => this.version.set(v.version), error: () => this.version.set(null) });
    this.api.getSettings().subscribe({
      next: settings => {
        this.current.set(settings);
        this.mode.set(settings.claudeAuthMode);
        this.runTimeoutMinutes.set(settings.runTimeoutMinutes);
        this.defaultMaxBudgetUsd.set(settings.defaultMaxBudgetUsd);
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
    const budget = this.defaultMaxBudgetUsd();
    const body: UpdateSettingsRequest = {
      claudeAuthMode: this.mode(),
      apiKey: typed.length > 0 ? typed : null,
      clearApiKey: this.removing(),
      runTimeoutMinutes: Math.round(this.runTimeoutMinutes() ?? 0),
      defaultMaxBudgetUsd: budget != null && budget > 0 ? budget : null,
      // To the API, null means "leave it alone" — emptying a stored budget has to be said explicitly.
      clearDefaultMaxBudget: budget == null && this.current()?.defaultMaxBudgetUsd != null,
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
        this.error.set(apiErrorMessage(err, 'Saving settings failed — is the API running?'));
      },
    });
  }
}
