import { Component, inject, signal } from '@angular/core';
import { HttpErrorResponse } from '@angular/common/http';
import { RouterLink } from '@angular/router';
import { ApiService } from '../../../core/api.service';
import { AgentsStore, UserActionsStore } from '../../../core/stores';
import { AgentDetailDto, AgentSummaryDto, AUTONOMY_LEVELS, EFFORT_LEVELS, EffortLevel, RunStatus, UserActionDto } from '../../../core/models';
import { formatCost, formatInterval, modelShortName, relativeTime } from '../../../core/format';
import { AgentEditor } from './agent-editor';
import { ArchitectModal } from '../architect-modal';

@Component({
  selector: 'app-agent-panel',
  imports: [RouterLink, AgentEditor, ArchitectModal],
  templateUrl: './agent-panel.html',
  styleUrl: './agent-panel.scss',
})
export class AgentPanel {
  protected readonly store = inject(AgentsStore);
  protected readonly userActionsStore = inject(UserActionsStore);
  private readonly api = inject(ApiService);

  protected readonly editorOpen = signal(false);
  /** The ✨ Architect (AI workflow builder) modal. */
  protected readonly architectOpen = signal(false);
  protected readonly editingAgent = signal<AgentDetailDto | null>(null);
  protected readonly editLoadingId = signal<string | null>(null);

  // format.ts helpers exposed to the template
  protected readonly formatCost = formatCost;
  protected readonly formatInterval = formatInterval;
  protected readonly modelShortName = modelShortName;
  protected readonly relativeTime = relativeTime;

  protected effortLabel(effort: EffortLevel): string {
    return EFFORT_LEVELS.find(level => level.id === effort)?.label ?? effort;
  }

  /** Open User Action Requests raised by this agent (polled by the store). */
  protected openRequests(agentId: string): UserActionDto[] {
    return this.userActionsStore.forAgent(agentId);
  }

  /** True when a blocking request parks the loop — Run now is pointless until it's resolved. */
  protected hasBlockingRequest(agentId: string): boolean {
    return this.userActionsStore.forAgent(agentId).some(r => r.blocking);
  }

  protected autonomyBlurb(level: number): string {
    return AUTONOMY_LEVELS.find(l => l.level === level)?.blurb ?? '';
  }

  protected statusChipClass(status: RunStatus): string {
    switch (status) {
      case 'Succeeded': return 'chip-green';
      case 'Failed':
      case 'TimedOut': return 'chip-red';
      case 'Cancelled': return 'chip-amber';
      case 'Running': return 'chip-green';
    }
  }

  protected statusLabel(status: RunStatus): string {
    return status === 'TimedOut' ? 'Timed out' : status;
  }

  protected openCreate(): void {
    this.editingAgent.set(null);
    this.editorOpen.set(true);
  }

  protected openEdit(agent: AgentSummaryDto): void {
    if (this.editLoadingId() !== null) return;
    this.editLoadingId.set(agent.id);
    this.api.getAgent(agent.id).subscribe({
      next: detail => {
        this.editLoadingId.set(null);
        this.editingAgent.set(detail);
        this.editorOpen.set(true);
      },
      error: () => {
        this.editLoadingId.set(null);
        this.store.refreshNow();
      },
    });
  }

  protected onEditorClosed(saved: boolean): void {
    this.editorOpen.set(false);
    this.editingAgent.set(null);
    if (saved) this.store.refreshNow();
  }

  protected toggleEnabled(agent: AgentSummaryDto, event: Event): void {
    const enabled = (event.target as HTMLInputElement).checked;
    this.api.setAgentEnabled(agent.id, enabled).subscribe({
      next: () => this.store.refreshNow(),
      error: () => this.store.refreshNow(),
    });
  }

  protected runNow(agent: AgentSummaryDto): void {
    if (agent.isRunning || this.hasBlockingRequest(agent.id)) return;
    this.api.runAgentNow(agent.id).subscribe({
      next: () => this.store.refreshNow(),
      error: (err: HttpErrorResponse) => {
        // 409 = a run is already in flight; the refresh picks up its state.
        if (err.status === 409) this.store.refreshNow();
      },
    });
  }

  protected cancelRun(agent: AgentSummaryDto): void {
    this.api.cancelAgentRun(agent.id).subscribe({
      next: () => this.store.refreshNow(),
      error: () => this.store.refreshNow(),
    });
  }

  protected deleteAgent(agent: AgentSummaryDto): void {
    if (!confirm(`Delete agent “${agent.name}”? Its run history goes with it.`)) return;
    this.api.deleteAgent(agent.id).subscribe({
      next: () => this.store.refreshNow(),
      error: () => this.store.refreshNow(),
    });
  }
}
