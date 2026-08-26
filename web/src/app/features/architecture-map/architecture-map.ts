import { Component, ElementRef, computed, inject, signal, viewChild, AfterViewInit, OnDestroy } from '@angular/core';
import { RouterLink } from '@angular/router';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { catchError, of, switchMap, timer } from 'rxjs';
import { ApiService } from '../../core/api.service';
import { ArchitectureMapDto, AUTONOMY_LEVELS, MapAgentDto, MapResourceDto } from '../../core/models';
import { formatCost, formatInterval, modelShortName } from '../../core/format';

// ---------------------------------------------------------------------------
// Layout: a deterministic three-column flow (no physics). Resources on the
// left grouped by type, agents centered, delivery on the right aligned to its
// agent's row so agent→delivery edges are short and straight. Resources are
// ordered within their group by the average position of the agents they feed
// (a one-pass barycenter), which removes most edge crossings.
// ---------------------------------------------------------------------------

export interface MapNode<T> {
  data: T;
  x: number;
  y: number;
  w: number;
  h: number;
}

export interface MapEdge {
  path: string;
  resourceId: string | null; // null for agent→delivery edges
  agentId: string;
}

export interface MapGroupLabel {
  label: string;
  icon: string;
  y: number;
}

export interface MapLayout {
  width: number;
  height: number;
  resources: MapNode<MapResourceDto>[];
  agents: MapNode<MapAgentDto>[];
  delivery: MapNode<MapAgentDto>[];
  groups: MapGroupLabel[];
  edges: MapEdge[];
  columns: { resources: number; agents: number; delivery: number };
}

const RES_W = 232;
const RES_H = 48;
const AGENT_W = 256;
const AGENT_H = 82;
const DELIV_W = 196;
const DELIV_H = 60;
const NODE_GAP = 12;
const AGENT_GAP = 26;
const GROUP_HEADER = 30;
const GROUP_GAP = 18;
const TOP_PAD = 34;

export function computeMapLayout(map: ArchitectureMapDto, width: number): MapLayout {
  const agentX = Math.max(RES_W + 90, Math.round((width - AGENT_W) / 2));
  const delivX = Math.max(agentX + AGENT_W + 90, width - DELIV_W);

  // Agents stack in name order (the API pre-sorts).
  const agentIndex = new Map(map.agents.map((a, i) => [a.id, i]));
  const agents: MapNode<MapAgentDto>[] = map.agents.map((a, i) => ({
    data: a,
    x: agentX,
    y: TOP_PAD + i * (AGENT_H + AGENT_GAP),
    w: AGENT_W,
    h: AGENT_H,
  }));
  const agentY = new Map(agents.map(n => [n.data.id, n.y]));

  // Resources: group by type label, then barycenter-sort inside each group.
  const groupsInOrder: { key: string; icon: string; items: MapResourceDto[] }[] = [];
  const byGroup = new Map<string, { key: string; icon: string; items: MapResourceDto[] }>();
  for (const r of map.resources) {
    let group = byGroup.get(r.typeLabel);
    if (!group) {
      group = { key: r.typeLabel, icon: r.icon, items: [] };
      byGroup.set(r.typeLabel, group);
      groupsInOrder.push(group);
    }
    group.items.push(r);
  }
  const barycenter = (r: MapResourceDto): number =>
    r.agentIds.length === 0
      ? Number.MAX_SAFE_INTEGER // unused resources sink to the bottom of their group
      : r.agentIds.reduce((sum, id) => sum + (agentIndex.get(id) ?? 0), 0) / r.agentIds.length;

  // Order groups, and items within groups, by where their agents sit — a one-pass
  // barycenter that removes most edge crossings without any physics.
  const groupOrderKey = (group: { items: MapResourceDto[] }): number => {
    const used = group.items.filter(r => r.agentIds.length > 0);
    return used.length === 0
      ? Number.MAX_SAFE_INTEGER
      : used.reduce((sum, r) => sum + barycenter(r), 0) / used.length;
  };
  groupsInOrder.sort((a, b) => groupOrderKey(a) - groupOrderKey(b));

  const resources: MapNode<MapResourceDto>[] = [];
  const groups: MapGroupLabel[] = [];
  let y = TOP_PAD;
  for (const group of groupsInOrder) {
    groups.push({ label: group.key, icon: group.icon, y });
    y += GROUP_HEADER;
    for (const r of [...group.items].sort((a, b) => barycenter(a) - barycenter(b))) {
      resources.push({ data: r, x: 0, y, w: RES_W, h: RES_H });
      y += RES_H + NODE_GAP;
    }
    y += GROUP_GAP;
  }

  // Delivery mirrors the agent rows.
  const delivery: MapNode<MapAgentDto>[] = agents.map(a => ({
    data: a.data,
    x: delivX,
    y: a.y + (AGENT_H - DELIV_H) / 2,
    w: DELIV_W,
    h: DELIV_H,
  }));

  const bezier = (x1: number, y1: number, x2: number, y2: number): string => {
    const mx = (x1 + x2) / 2;
    return `M ${x1} ${y1} C ${mx} ${y1}, ${mx} ${y2}, ${x2} ${y2}`;
  };

  // Slotted endpoints: an agent's incoming edges land spread across its left edge
  // (ordered by source height), and a resource feeding several agents fans out from
  // spread points on its right edge — no more single-point convergence tangles.
  const pairs: { resource: MapNode<MapResourceDto>; agentId: string; ay: number }[] = [];
  for (const r of resources) {
    for (const agentId of r.data.agentIds) {
      const ay = agentY.get(agentId);
      if (ay !== undefined) pairs.push({ resource: r, agentId, ay });
    }
  }

  const slot = (top: number, height: number, index: number, count: number, inset: number): number =>
    count <= 1
      ? top + height / 2
      : top + inset + (index * (height - 2 * inset)) / (count - 1);

  const startYByPair = new Map<{ resource: MapNode<MapResourceDto>; agentId: string }, number>();
  const endY = new Map<typeof pairs[number], number>();
  for (const a of agents) {
    const incoming = pairs.filter(p => p.agentId === a.data.id).sort((p, q) => p.resource.y - q.resource.y);
    incoming.forEach((p, i) => endY.set(p, slot(a.y, AGENT_H, i, incoming.length, 18)));
  }
  for (const r of resources) {
    const outgoing = pairs.filter(p => p.resource === r).sort((p, q) => p.ay - q.ay);
    outgoing.forEach((p, i) => startYByPair.set(p, slot(r.y, RES_H, i, outgoing.length, 12)));
  }

  const edges: MapEdge[] = pairs.map(p => ({
    resourceId: p.resource.data.id,
    agentId: p.agentId,
    path: bezier(
      RES_W,
      startYByPair.get(p) ?? p.resource.y + RES_H / 2,
      agentX,
      endY.get(p) ?? p.ay + AGENT_H / 2,
    ),
  }));
  for (const a of agents) {
    edges.push({
      resourceId: null,
      agentId: a.data.id,
      path: bezier(agentX + AGENT_W, a.y + AGENT_H / 2, delivX, a.y + AGENT_H / 2),
    });
  }

  const height = Math.max(
    y,
    agents.length > 0 ? agents[agents.length - 1].y + AGENT_H : 0,
  ) + 24;

  return {
    width,
    height: Math.max(height, 200),
    resources,
    agents,
    delivery,
    groups,
    edges,
    columns: { resources: 0, agents: agentX, delivery: delivX },
  };
}

type Hover = { kind: 'resource' | 'agent'; id: string } | null;

/**
 * The architecture at a glance: resources feed agents, agents produce delivery.
 * Identity is carried by icons, labels and column grouping — edges stay one
 * neutral hue, with state (running / hover focus) as the only color change.
 */
@Component({
  selector: 'app-architecture-map',
  imports: [RouterLink],
  templateUrl: './architecture-map.html',
  styleUrl: './architecture-map.scss',
})
export class ArchitectureMap implements AfterViewInit, OnDestroy {
  private readonly api = inject(ApiService);
  private readonly canvas = viewChild<ElementRef<HTMLDivElement>>('canvas');
  private resizeObserver: ResizeObserver | null = null;

  protected readonly map = signal<ArchitectureMapDto | null>(null);
  protected readonly loaded = signal(false);
  protected readonly offline = signal(false);
  protected readonly hover = signal<Hover>(null);
  private readonly width = signal(1200);

  protected readonly formatCost = formatCost;
  protected readonly formatInterval = formatInterval;
  protected readonly modelShortName = modelShortName;

  protected readonly layout = computed<MapLayout | null>(() => {
    const data = this.map();
    return data ? computeMapLayout(data, this.width()) : null;
  });

  protected readonly isEmpty = computed(() => {
    const data = this.map();
    return this.loaded() && data !== null && data.agents.length === 0 && data.resources.length === 0;
  });

  constructor() {
    timer(0, 5_000)
      .pipe(
        switchMap(() => this.api.getArchitectureMap().pipe(catchError(() => of(null)))),
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

  ngAfterViewInit(): void {
    const element = this.canvas()?.nativeElement;
    if (!element) return;
    this.width.set(Math.max(700, element.clientWidth));
    this.resizeObserver = new ResizeObserver(entries => {
      const w = entries[0]?.contentRect.width;
      if (w) this.width.set(Math.max(700, Math.round(w)));
    });
    this.resizeObserver.observe(element);
  }

  ngOnDestroy(): void {
    this.resizeObserver?.disconnect();
  }

  // ---------- hover-connected highlighting ----------

  protected setHover(kind: 'resource' | 'agent', id: string): void {
    this.hover.set({ kind, id });
  }

  protected clearHover(): void {
    this.hover.set(null);
  }

  protected edgeClass(edge: MapEdge): string {
    const running = this.agentById(edge.agentId)?.isRunning ? ' running' : '';
    const h = this.hover();
    if (!h) return 'edge' + running;
    const connected =
      h.kind === 'agent'
        ? edge.agentId === h.id
        : edge.resourceId === h.id;
    return 'edge ' + (connected ? 'focus' : 'faded') + running;
  }

  protected nodeClass(kind: 'resource' | 'agent' | 'delivery', id: string): string {
    const h = this.hover();
    if (!h) return '';
    if (h.kind === 'resource') {
      if (kind === 'resource') return id === h.id ? 'focus' : 'faded';
      const resource = this.map()?.resources.find(r => r.id === h.id);
      return resource?.agentIds.includes(id) ? 'focus' : 'faded';
    }
    // hovering an agent
    if (kind === 'resource') {
      const agent = this.agentById(h.id);
      return agent?.resourceIds.includes(id) ? 'focus' : 'faded';
    }
    return id === h.id ? 'focus' : 'faded';
  }

  protected agentStatus(agent: MapAgentDto): { cls: string; label: string } {
    if (agent.isRunning) return { cls: 'running', label: 'Running' };
    if (agent.enabled) return { cls: 'scheduled', label: 'Scheduled' };
    return { cls: 'paused', label: 'Paused' };
  }

  protected autonomyBlurb(level: number): string {
    return AUTONOMY_LEVELS.find(l => l.level === level)?.blurb ?? '';
  }

  private agentById(id: string): MapAgentDto | undefined {
    return this.map()?.agents.find(a => a.id === id);
  }
}
