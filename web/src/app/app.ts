import { Component, inject, signal } from '@angular/core';
import { RouterLink, RouterLinkActive, RouterOutlet } from '@angular/router';
import { AgentsStore, ClaudeStatusStore, SettingsStore, UserActionsStore } from './core/stores';
import { ClaudeSetup } from './shared/claude-setup/claude-setup';
import { SettingsModal } from './shared/settings-modal/settings-modal';
import { WorkflowSwitcher } from './shared/workflow-switcher/workflow-switcher';
import { WorkflowEditor } from './shared/workflow-switcher/workflow-editor';
import { WorkflowDto } from './core/models';

@Component({
  selector: 'app-root',
  imports: [RouterOutlet, RouterLink, RouterLinkActive, ClaudeSetup, SettingsModal, WorkflowSwitcher, WorkflowEditor],
  templateUrl: './app.html',
  styleUrl: './app.scss'
})
export class App {
  protected readonly agentsStore = inject(AgentsStore);
  protected readonly claudeStore = inject(ClaudeStatusStore);
  protected readonly userActionsStore = inject(UserActionsStore);
  protected readonly settingsStore = inject(SettingsStore);
  protected readonly setupOpen = signal(false);
  protected readonly settingsOpen = signal(false);
  /** The workflow editor lives here, outside the topbar, so its fixed backdrop covers the whole page. */
  protected readonly workflowEditor = signal<{ workflow: WorkflowDto | null } | null>(null);
}
