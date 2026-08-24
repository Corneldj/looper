import { Component, inject, signal } from '@angular/core';
import { RouterLink, RouterLinkActive, RouterOutlet } from '@angular/router';
import { AgentsStore, ClaudeStatusStore } from './core/stores';
import { ClaudeSetup } from './shared/claude-setup/claude-setup';

@Component({
  selector: 'app-root',
  imports: [RouterOutlet, RouterLink, RouterLinkActive, ClaudeSetup],
  templateUrl: './app.html',
  styleUrl: './app.scss'
})
export class App {
  protected readonly agentsStore = inject(AgentsStore);
  protected readonly claudeStore = inject(ClaudeStatusStore);
  protected readonly setupOpen = signal(false);
}
