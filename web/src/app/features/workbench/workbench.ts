import { AfterViewInit, Component, DestroyRef, ElementRef, Injector, OnDestroy, OnInit, computed, inject, signal, viewChild } from '@angular/core';
import { HttpErrorResponse } from '@angular/common/http';
import { RouterLink } from '@angular/router';
import { takeUntilDestroyed, toObservable } from '@angular/core/rxjs-interop';
import { Subject, catchError, merge, of, skip, switchMap, timer } from 'rxjs';
import { ApiService } from '../../core/api.service';
import { AgentsStore, ResourcesStore, UserActionsStore, WorkflowsStore } from '../../core/stores';
import {
  AgentDetailDto,
  ArchitectureMapDto,
  AUTONOMY_LEVELS,
  EFFORT_LEVELS,
  EffortLevel,
  MapAgentDto,
  MapResourceDto,
  ResourceDto,
  ResourceType,
  ResourceTypeDto,
  RunStatus,
  UserActionDto,
} from '../../core/models';
import { formatCost, formatInterval, formatMetric, modelShortName, relativeTime } from '../../core/format';
import { MapEdge, MapEventEdge, MapLayout, MapNode, bezierPath, computeMapLayout } from './map-layout';
import { ResourceEditor } from './resource-editor/resource-editor';
import { ResourceTypePicker } from './resource-type-picker';
import { WorkspacesModal } from './resource-editor/workspaces-modal';
import { ScriptRunModal } from './resource-editor/script-run-modal';
import { AgentEditor } from './agent-editor/agent-editor';
import { ArchitectModal } from './architect-modal';

type Hover = { kind: 'resource' | 'agent'; id: string } | null;

/** A connection being dragged from a resource's port towards an agent. */
interface DragState {
  resourceId: string;
  resourceName: string;
  x1: number;
  y1: number;
  x2: number;
  y2: number;
  /** Agent under the cursor, if any. */
  over: string | null;
  /** False when the agent under the cursor already has this resource. */
  allowed: boolean;
}

/**
 * The workbench IS the architecture map: resources feed agents, agents produce delivery,
 * and everything is editable in place. Drag a resource's port onto an agent to wire it in,
 * hover an edge and click ✕ to cut it, act on agents (run, pause, edit) right on their node.
 * Identity is carried by icons, labels and column grouping — edges stay one neutral hue,
 * with state (running / hover focus / drafting) as the only color change.
 */
@Component({
  selector: 'app-workbench',
  imports: [RouterLink, ResourceEditor, ResourceTypePicker, WorkspacesModal, ScriptRunModal, AgentEditor, ArchitectModal],
  templateUrl: './workbench.html',
  styleUrl: './workbench.scss',
  host: {
    '(document:pointermove)': 'onPointerMove($event)',
    '(document:pointerup)': 'onPointerUp($event)',
    '(document:pointercancel)': 'cancelDrag()',
    '(document:keydown.escape)': 'onEscape()',
  },
})
export class Workbench implements OnInit, AfterViewInit, OnDestroy {
  private readonly api = inject(ApiService);
  protected readonly resourcesStore = inject(ResourcesStore);
  protected readonly agentsStore = inject(AgentsStore);
  protected readonly userActionsStore = inject(UserActionsStore);
  protected readonly workflowsStore = inject(WorkflowsStore);

  private readonly injector = inject(Injector);
  private readonly destroyRef = inject(DestroyRef);
  private readonly page = viewChild<ElementRef<HTMLDivElement>>('page');
  private readonly canvas = viewChild<ElementRef<HTMLDivElement>>('canvas');
  private resizeObserver: ResizeObserver | null = null;
  private readonly refresh$ = new Subject<void>();
  private edgeLeaveTimer: ReturnType<typeof setTimeout> | null = null;

  protected readonly map = signal<ArchitectureMapDto | null>(null);
  protected readonly loaded = signal(false);
  protected readonly offline = signal(false);
  protected readonly error = signal<string | null>(null);
  protected readonly hover = signal<Hover>(null);
  protected readonly edgeHover = signal<MapEdge | null>(null);
  protected readonly drag = signal<DragState | null>(null);
  private readonly width = signal(1200);

  // ---------- modals ----------
  protected readonly pickerOpen = signal(false);
  protected readonly editorType = signal<ResourceType | null>(null);
  protected readonly editorTypeDef = signal<ResourceTypeDto | null>(null);
  protected readonly editingResource = signal<ResourceDto | null>(null);
  /**
   * One key per open editor, so the component is re-created whenever a different resource (or a
   * different new type) is edited — the form is initialised once, from inputs, and never reused.
   */
  protected readonly editorKeys = computed(() => {
    const type = this.editorType();
    if (!type) return [];
    return [this.editingResource()?.id ?? `new:${type}:${this.editorTypeDef()?.typeKey ?? ''}`];
  });
  protected readonly workspacesFor = signal<ResourceDto | null>(null);
  protected readonly runScriptFor = signal<ResourceDto | null>(null);
  protected readonly agentEditorOpen = signal(false);
  protected readonly editingAgent = signal<AgentDetailDto | null>(null);
  protected readonly editLoadingId = signal<string | null>(null);
  protected readonly architectOpen = signal(false);

  protected readonly formatCost = formatCost;
  protected readonly formatInterval = formatInterval;
  protected readonly modelShortName = modelShortName;
  protected readonly relativeTime = relativeTime;

  protected readonly layout = computed<MapLayout | null>(() => {
    const data = this.map();
    return data ? computeMapLayout(data, this.width()) : null;
  });

  protected readonly isEmpty = computed(() => {
    const data = this.map();
    return this.loaded() && data !== null && data.agents.length === 0 && data.resources.length === 0;
  });

  /** The connection being drafted, snapped to the agent's left edge when over a valid target. */
  protected readonly draftPath = computed<string | null>(() => {
    const d = this.drag();
    if (!d) return null;
    const target = d.over ? this.layout()?.agents.find(a => a.data.id === d.over) : undefined;
    return target
      ? bezierPath(d.x1, d.y1, target.x, target.y + target.h / 2)
      : bezierPath(d.x1, d.y1, d.x2, d.y2);
  });

  constructor() {
    // Poll every 5s, and re-fetch immediately after anything the user changes here —
    // always for the workflow selected in the topbar.
    merge(timer(0, 5_000), this.refresh$)
      .pipe(
        switchMap(() => this.api.getArchitectureMap(this.workflowsStore.selectedId()).pipe(catchError(() => of(null)))),
        takeUntilDestroyed(),
      )
      .subscribe(data => {
        if (data === null) {
          this.offline.set(!this.map());
          this.loaded.set(true);
          return;
        }
        this.map.set(data);
        this.offline.set(false);
        this.loaded.set(true);
      });
  }

  ngOnInit(): void {
    // Full resource DTOs (for the editors) and the type catalog (for the picker / dynamic forms).
    this.resourcesStore.load();
    this.resourcesStore.loadTypes();
    // Switching workflow in the topbar swaps the whole canvas.
    toObservable(this.workflowsStore.selectedId, { injector: this.injector })
      .pipe(skip(1), takeUntilDestroyed(this.destroyRef))
      .subscribe(() => {
        this.map.set(null);
        this.loaded.set(false);
        this.hover.set(null);
        this.resourcesStore.load();
        this.refreshMap();
      });
  }

  ngAfterViewInit(): void {
    const element = this.page()?.nativeElement;
    if (!element) return;
    this.resizeObserver = new ResizeObserver(entries => {
      const w = entries[0]?.contentRect.width;
      if (w) this.width.set(Math.max(760, Math.round(w)));
    });
    this.resizeObserver.observe(element);
  }

  ngOnDestroy(): void {
    this.resizeObserver?.disconnect();
    if (this.edgeLeaveTimer) clearTimeout(this.edgeLeaveTimer);
  }

  protected refreshMap(): void {
    this.refresh$.next();
  }

  protected onEscape(): void {
    if (this.drag()) this.cancelDrag();
  }

  // =========================================================================
  // Hover-connected highlighting
  // =========================================================================

  protected setHover(kind: 'resource' | 'agent', id: string): void {
    this.hover.set({ kind, id });
  }

  protected clearHover(): void {
    this.hover.set(null);
  }

  protected edgeClass(edge: MapEdge): string {
    const running = this.agentById(edge.agentId)?.isRunning ? ' running' : '';
    const cut = this.edgeHover();
    if (cut) {
      return 'edge' + (cut.resourceId === edge.resourceId && cut.agentId === edge.agentId ? ' focus cut' : ' faded') + running;
    }
    const h = this.hover();
    if (!h || this.drag()) return 'edge' + running;
    const connected = h.kind === 'agent' ? edge.agentId === h.id : edge.resourceId === h.id;
    return 'edge ' + (connected ? 'focus' : 'faded') + running;
  }

  /** Event chains light up with either endpoint agent; they fade like everything else otherwise. */
  protected eventEdgeClass(edge: MapEventEdge): string {
    if (this.drag()) return '';
    if (this.edgeHover()) return 'faded';
    const h = this.hover();
    if (!h) return '';
    return h.kind === 'agent' && (edge.fromAgentId === h.id || edge.toAgentId === h.id) ? 'focus' : 'faded';
  }

  /** Topics raised by attached Event Raisers — the built-in completion topics stay implicit. */
  protected raisedTopics(agent: MapAgentDto): string[] {
    return agent.raises.filter(t => !t.startsWith('agent.'));
  }

  protected nodeClass(kind: 'resource' | 'agent' | 'delivery', id: string): string {
    if (this.drag()) return ''; // no fading while wiring — every agent is a candidate
    const cut = this.edgeHover();
    if (cut) {
      if (kind === 'resource') return id === cut.resourceId ? 'focus' : 'faded';
      return id === cut.agentId ? 'focus' : 'faded';
    }
    const h = this.hover();
    if (!h) return '';
    if (h.kind === 'resource') {
      if (kind === 'resource') return id === h.id ? 'focus' : 'faded';
      const resource = this.map()?.resources.find(r => r.id === h.id);
      return resource?.agentIds.includes(id) ? 'focus' : 'faded';
    }
    if (kind === 'resource') {
      const agent = this.agentById(h.id);
      return agent?.resourceIds.includes(id) ? 'focus' : 'faded';
    }
    return id === h.id ? 'focus' : 'faded';
  }

  // =========================================================================
  // Drag-to-connect: resource port → agent
  // =========================================================================

  protected onPortDown(event: PointerEvent, node: MapNode<MapResourceDto>): void {
    if (event.button !== 0) return;
    event.preventDefault();
    event.stopPropagation();
    this.hover.set(null);
    this.edgeHover.set(null);
    const x1 = node.x + node.w;
    const y1 = node.y + node.h / 2;
    this.drag.set({
      resourceId: node.data.id,
      resourceName: node.data.name,
      x1,
      y1,
      x2: x1,
      y2: y1,
      over: null,
      allowed: false,
    });
  }

  protected onPointerMove(event: PointerEvent): void {
    const d = this.drag();
    if (!d) return;
    const point = this.canvasPoint(event);
    const over = this.agentUnderPointer(event);
    const allowed = over !== null && !(this.agentById(over)?.resourceIds.includes(d.resourceId) ?? false);
    this.drag.set({ ...d, x2: point.x, y2: point.y, over, allowed });
  }

  protected onPointerUp(event: PointerEvent): void {
    const d = this.drag();
    if (!d) return;
    const over = this.agentUnderPointer(event) ?? d.over;
    this.drag.set(null);
    if (over && !(this.agentById(over)?.resourceIds.includes(d.resourceId) ?? false)) {
      this.attach(d.resourceId, over);
    }
  }

  protected cancelDrag(): void {
    this.drag.set(null);
  }

  /** Drop-state class for an agent node while a connection is being drafted. */
  protected agentDropClass(agentId: string): string {
    const d = this.drag();
    if (!d) return '';
    if (d.over !== agentId) return 'droppable';
    return d.allowed ? 'drop-target' : 'drop-blocked';
  }

  private attach(resourceId: string, agentId: string): void {
    this.api.attachResource(agentId, resourceId).subscribe({
      next: () => {
        this.error.set(null);
        this.refreshMap();
        this.agentsStore.refreshNow();
      },
      error: () => this.error.set('Connecting the resource failed — is the API running?'),
    });
  }

  private canvasPoint(event: PointerEvent): { x: number; y: number } {
    const rect = this.canvas()?.nativeElement.getBoundingClientRect();
    return rect ? { x: event.clientX - rect.left, y: event.clientY - rect.top } : { x: event.clientX, y: event.clientY };
  }

  private agentUnderPointer(event: PointerEvent): string | null {
    const hit = document.elementFromPoint(event.clientX, event.clientY);
    return hit?.closest?.('[data-agent-id]')?.getAttribute('data-agent-id') ?? null;
  }

  // =========================================================================
  // Edges: hover to reveal ✕, click to cut
  // =========================================================================

  protected onEdgeEnter(edge: MapEdge): void {
    if (this.drag() || !edge.resourceId) return;
    if (this.edgeLeaveTimer) {
      clearTimeout(this.edgeLeaveTimer);
      this.edgeLeaveTimer = null;
    }
    this.edgeHover.set(edge);
  }

  protected onEdgeLeave(): void {
    // A short grace period so the cursor can travel from the line onto the ✕ button.
    if (this.edgeLeaveTimer) clearTimeout(this.edgeLeaveTimer);
    this.edgeLeaveTimer = setTimeout(() => {
      this.edgeHover.set(null);
      this.edgeLeaveTimer = null;
    }, 140);
  }

  protected detachEdge(edge: MapEdge): void {
    if (!edge.resourceId) return;
    this.edgeHover.set(null);
    this.api.detachResource(edge.agentId, edge.resourceId).subscribe({
      next: () => {
        this.error.set(null);
        this.refreshMap();
        this.agentsStore.refreshNow();
      },
      error: () => this.error.set('Disconnecting the resource failed — is the API running?'),
    });
  }

  // =========================================================================
  // Resources
  // =========================================================================

  protected isScript(resource: MapResourceDto): boolean {
    return resource.type === 'Custom' && resource.customTypeKey === 'Script';
  }

  protected isPool(resource: MapResourceDto): boolean {
    return resource.type === 'WorkspacePool';
  }

  protected graphFor(resourceId: string) {
    return this.resourcesStore.graphs().find(g => g.resourceId === resourceId);
  }

  protected newResource(): void {
    this.error.set(null);
    this.pickerOpen.set(true);
  }

  protected onTypePicked(entry: ResourceTypeDto): void {
    this.pickerOpen.set(false);
    this.editingResource.set(null);
    // Field specs present → the generic dynamic form (AI-generated and shipped module types alike);
    // absent → one of the classic types with a bespoke form. builtIn only governs deletability.
    if (entry.fields) {
      this.editorTypeDef.set(entry);
      this.editorType.set('Custom');
    } else {
      this.editorTypeDef.set(null);
      this.editorType.set(entry.typeKey as ResourceType);
    }
  }

  /** The full DTO (config included) behind a map node — the editors need more than the map carries. */
  private fullResource(resource: MapResourceDto): ResourceDto | null {
    return this.resourcesStore.resources().find(r => r.id === resource.id) ?? null;
  }

  protected editResource(resource: MapResourceDto): void {
    const dto = this.fullResource(resource);
    if (!dto) {
      this.error.set(`“${resource.name}” hasn’t finished loading — try again in a moment.`);
      this.resourcesStore.load();
      return;
    }
    if (dto.type === 'Custom') {
      const def = this.resourcesStore.types().find(t => t.typeKey === dto.customTypeKey);
      if (!def) {
        this.error.set(`The resource type '${dto.customTypeKey}' is no longer installed.`);
        return;
      }
      this.editorTypeDef.set(def);
    } else {
      this.editorTypeDef.set(null);
    }
    this.error.set(null);
    this.editingResource.set(dto);
    this.editorType.set(dto.type);
  }

  protected onResourceEditorClosed(saved: ResourceDto | null): void {
    if (saved) {
      this.resourcesStore.upsert(saved);
      this.resourcesStore.loadGraphs();
      this.refreshMap();
    }
    this.editingResource.set(null);
    this.editorType.set(null);
    this.editorTypeDef.set(null);
  }

  protected deleteResource(resource: MapResourceDto): void {
    const inUse =
      resource.agentIds.length > 0
        ? ` It is used by ${resource.agentIds.length} agent${resource.agentIds.length === 1 ? '' : 's'}.`
        : '';
    if (!confirm(`Delete “${resource.name}”?${inUse}`)) return;
    this.api.deleteResource(resource.id).subscribe({
      next: () => {
        this.resourcesStore.remove(resource.id);
        this.error.set(null);
        this.refreshMap();
        this.agentsStore.refreshNow();
      },
      error: () => this.error.set(`Couldn’t delete “${resource.name}” — is the API running?`),
    });
  }

  protected openWorkspaces(resource: MapResourceDto): void {
    const dto = this.fullResource(resource);
    if (dto) this.workspacesFor.set(dto);
  }

  protected runScript(resource: MapResourceDto): void {
    const dto = this.fullResource(resource);
    if (dto) this.runScriptFor.set(dto);
  }

  // =========================================================================
  // Agents
  // =========================================================================

  protected agentStatus(agent: MapAgentDto): { cls: string; label: string } {
    if (agent.isRunning) return { cls: 'running', label: 'Running' };
    if (agent.enabled) return { cls: 'scheduled', label: agent.triggerMode === 'Event' ? 'Listening for events' : 'Scheduled' };
    return { cls: 'paused', label: 'Paused' };
  }

  protected autonomyBlurb(level: number): string {
    return AUTONOMY_LEVELS.find(l => l.level === level)?.blurb ?? '';
  }

  protected effortLabel(effort: EffortLevel): string {
    return EFFORT_LEVELS.find(level => level.id === effort)?.label ?? effort;
  }

  protected statusChipClass(status: RunStatus): string {
    switch (status) {
      case 'Succeeded':
      case 'Running':
        return 'chip-green';
      case 'Failed':
      case 'TimedOut':
        return 'chip-red';
      case 'Cancelled':
        return 'chip-amber';
    }
  }

  protected statusLabel(status: RunStatus): string {
    return status === 'TimedOut' ? 'Timed out' : status;
  }

  /** Open User Action Requests raised by this agent (polled by the store). */
  protected openRequests(agentId: string): UserActionDto[] {
    return this.userActionsStore.forAgent(agentId);
  }

  /** True when a blocking request parks the loop — Run now is pointless until it's resolved. */
  protected hasBlockingRequest(agentId: string): boolean {
    return this.userActionsStore.forAgent(agentId).some(r => r.blocking);
  }

  protected newAgent(): void {
    this.editingAgent.set(null);
    this.agentEditorOpen.set(true);
  }

  protected editAgent(agent: MapAgentDto): void {
    if (this.editLoadingId() !== null) return;
    this.editLoadingId.set(agent.id);
    this.api.getAgent(agent.id).subscribe({
      next: detail => {
        this.editLoadingId.set(null);
        this.editingAgent.set(detail);
        this.agentEditorOpen.set(true);
      },
      error: () => {
        this.editLoadingId.set(null);
        this.error.set(`Couldn’t load “${agent.name}” — is the API running?`);
      },
    });
  }

  protected onAgentEditorClosed(saved: boolean): void {
    this.agentEditorOpen.set(false);
    this.editingAgent.set(null);
    if (saved) {
      this.refreshMap();
      this.agentsStore.refreshNow();
    }
  }

  protected onArchitectClosed(): void {
    this.architectOpen.set(false);
    this.resourcesStore.load();
    this.refreshMap();
  }

  protected toggleEnabled(agent: MapAgentDto, event: Event): void {
    const enabled = (event.target as HTMLInputElement).checked;
    this.api.setAgentEnabled(agent.id, enabled).subscribe({
      next: () => this.afterAgentChange(),
      error: () => this.afterAgentChange(),
    });
  }

  protected runNow(agent: MapAgentDto): void {
    if (agent.isRunning || this.hasBlockingRequest(agent.id)) return;
    this.api.runAgentNow(agent.id).subscribe({
      next: () => this.afterAgentChange(),
      error: (err: HttpErrorResponse) => {
        // 409 = a run is already in flight; the refresh picks up its state.
        if (err.status === 409) this.afterAgentChange();
        else this.error.set(err.error?.detail || err.error?.title || `Couldn’t start “${agent.name}”.`);
      },
    });
  }

  protected cancelRun(agent: MapAgentDto): void {
    this.api.cancelAgentRun(agent.id).subscribe({
      next: () => this.afterAgentChange(),
      error: () => this.afterAgentChange(),
    });
  }

  protected deleteAgent(agent: MapAgentDto): void {
    if (!confirm(`Delete agent “${agent.name}”? Its run history goes with it.`)) return;
    this.api.deleteAgent(agent.id).subscribe({
      next: () => this.afterAgentChange(),
      error: () => this.error.set(`Couldn’t delete “${agent.name}” — is the API running?`),
    });
  }

  /**
   * The outcome node shows what the agent moves: its attached metrics' newest readings first,
   * pull requests as one more outcome. Two lines fit; the tooltip carries the rest.
   */
  protected outcomeLines(agent: MapAgentDto): { label: string; value: string }[] {
    const lines = agent.metrics.map(m => ({ label: m.name, value: m.current === null ? '—' : formatMetric(m.current, m.unit) }));
    if (agent.mergedPrs > 0 || agent.openPrs > 0) lines.push({ label: 'PRs', value: `${agent.mergedPrs} merged · ${agent.openPrs} open` });
    return lines.slice(0, 2);
  }

  protected outcomeTitle(agent: MapAgentDto): string {
    const parts = agent.metrics.map(m => `${m.name}: ${m.current === null ? 'no reading yet' : formatMetric(m.current, m.unit) + ' (30d)'}`);
    parts.push(`PRs · 30d: ${agent.mergedPrs} merged, ${agent.openPrs} open`);
    parts.push(`${formatCost(agent.costLast24hUsd)} spent / 24h`);
    return parts.join('\n');
  }

  private afterAgentChange(): void {
    this.refreshMap();
    this.agentsStore.refreshNow();
  }

  private agentById(id: string): MapAgentDto | undefined {
    return this.map()?.agents.find(a => a.id === id);
  }
}
