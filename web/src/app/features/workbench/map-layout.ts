import { ArchitectureMapDto, MapAgentDto, MapResourceDto } from '../../core/models';

// ---------------------------------------------------------------------------
// Layout: a deterministic three-column flow (no physics). Resources on the
// left grouped by type, agents centered, delivery on the right aligned to its
// agent's row so agent→delivery edges are short and straight. Resources are
// ordered within their group by the average position of the agents they feed
// (a one-pass barycenter), which removes most edge crossings.
//
// Pure and exported so the geometry is unit-testable without a DOM.
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
  /** Path midpoint — where the "disconnect" affordance sits. */
  midX: number;
  midY: number;
}

/** One agent waking another: the raiser's topics that the listener's patterns match. */
export interface MapEventEdge {
  fromAgentId: string;
  toAgentId: string;
  topics: string[];
  path: string;
}

/** The dispatcher's rule, mirrored: exact, or prefix when the pattern ends in ".*". */
export function matchesTopic(pattern: string, topic: string): boolean {
  return pattern.endsWith('.*') ? topic.startsWith(pattern.slice(0, -1)) : pattern === topic;
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
  /** Agent→agent event chains, drawn as arcs to the right of the agent column. */
  eventEdges: MapEventEdge[];
  columns: { resources: number; agents: number; delivery: number };
}

// Node dimensions are fixed by design: the canvas is a diagram, not a fluid grid.
export const RES_W = 248;
export const RES_H = 46;
export const AGENT_W = 344;
export const AGENT_H = 154;
export const DELIV_W = 224;
export const DELIV_H = 60;
const NODE_GAP = 12;
const AGENT_GAP = 22;
const GROUP_HEADER = 30;
const GROUP_GAP = 18;
export const TOP_PAD = 34;

/** A horizontal S-curve between two points — every edge on the canvas, drafted or committed. */
export function bezierPath(x1: number, y1: number, x2: number, y2: number): string {
  const mx = (x1 + x2) / 2;
  return `M ${x1} ${y1} C ${mx} ${y1}, ${mx} ${y2}, ${x2} ${y2}`;
}

export function computeMapLayout(map: ArchitectureMapDto, width: number): MapLayout {
  const agentX = Math.max(RES_W + 110, Math.round((width - AGENT_W) / 2));
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

  const startY = new Map<typeof pairs[number], number>();
  const endY = new Map<typeof pairs[number], number>();
  for (const a of agents) {
    const incoming = pairs.filter(p => p.agentId === a.data.id).sort((p, q) => p.resource.y - q.resource.y);
    incoming.forEach((p, i) => endY.set(p, slot(a.y, AGENT_H, i, incoming.length, 22)));
  }
  for (const r of resources) {
    const outgoing = pairs.filter(p => p.resource === r).sort((p, q) => p.ay - q.ay);
    outgoing.forEach((p, i) => startY.set(p, slot(r.y, RES_H, i, outgoing.length, 12)));
  }

  const edge = (resourceId: string | null, agentId: string, x1: number, y1: number, x2: number, y2: number): MapEdge => ({
    resourceId,
    agentId,
    path: bezierPath(x1, y1, x2, y2),
    // The symmetric cubic passes through the straight-line midpoint at t = 0.5.
    midX: (x1 + x2) / 2,
    midY: (y1 + y2) / 2,
  });

  const edges: MapEdge[] = pairs.map(p =>
    edge(
      p.resource.data.id,
      p.agentId,
      RES_W,
      startY.get(p) ?? p.resource.y + RES_H / 2,
      agentX,
      endY.get(p) ?? p.ay + AGENT_H / 2,
    ),
  );
  for (const a of agents) {
    edges.push(edge(null, a.data.id, agentX + AGENT_W, a.y + AGENT_H / 2, delivX, a.y + AGENT_H / 2));
  }

  // Event chains: A raises a topic B listens for → an arc on the right of the agent column,
  // leaving A's lower (or upper) quarter and entering B's upper (or lower) one, each chain
  // bulging a little further out so parallel arcs stay apart.
  const eventEdges: MapEventEdge[] = [];
  const rightX = agentX + AGENT_W;
  const maxBulge = Math.max(40, delivX - rightX - 16);
  for (const from of agents) {
    for (const to of agents) {
      if (from === to) continue;
      const topics = from.data.raises.filter(t => to.data.listens.some(p => matchesTopic(p, t)));
      if (topics.length === 0) continue;
      const down = to.y > from.y;
      const y1 = from.y + AGENT_H * (down ? 0.72 : 0.28);
      const y2 = to.y + AGENT_H * (down ? 0.28 : 0.72);
      const bulge = Math.min(maxBulge, 48 + 22 * (eventEdges.length % 4));
      eventEdges.push({
        fromAgentId: from.data.id,
        toAgentId: to.data.id,
        topics,
        path: `M ${rightX} ${y1} C ${rightX + bulge} ${y1}, ${rightX + bulge} ${y2}, ${rightX} ${y2}`,
      });
    }
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
    eventEdges,
    columns: { resources: 0, agents: agentX, delivery: delivX },
  };
}
