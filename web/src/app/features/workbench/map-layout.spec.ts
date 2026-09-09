import { ArchitectureMapDto, MapAgentDto, MapResourceDto } from '../../core/models';
import { AGENT_H, AGENT_W, DELIV_H, DELIV_W, RES_W, computeMapLayout, matchesTopic } from './map-layout';

export function resource(overrides: Partial<MapResourceDto> = {}): MapResourceDto {
  return {
    id: 'r1',
    name: 'Coding rules',
    type: 'Rule',
    customTypeKey: null,
    icon: '📏',
    typeLabel: 'Rule',
    description: '',
    agentIds: [],
    ...overrides,
  };
}

export function agent(overrides: Partial<MapAgentDto> = {}): MapAgentDto {
  return {
    id: 'a1',
    name: 'Docs bot',
    description: '',
    model: 'claude-sonnet-5',
    effort: 'High',
    autonomyLevel: 3,
    enabled: true,
    dryRun: false,
    isRunning: false,
    intervalMinutes: 60,
    triggerMode: 'Scheduled',
    triggerTopics: null,
    lastRunAtUtc: null,
    nextRunAtUtc: null,
    lastRunStatus: null,
    runsLast24h: 2,
    costLast24hUsd: 1.25,
    openPrs: 0,
    mergedPrs: 1,
    resourceIds: [],
    metrics: [],
    raises: [],
    listens: [],
    ...overrides,
  };
}

export function mapDto(overrides: Partial<ArchitectureMapDto> = {}): ArchitectureMapDto {
  return { resources: [], agents: [], ...overrides };
}

/** Final (x2 y2) endpoint of a bezier path string — the last number is y2. */
function pathEndY(path: string): number {
  const parts = path.trim().split(/[\s,]+/);
  return parseFloat(parts[parts.length - 1]);
}

describe('computeMapLayout', () => {
  const WIDTH = 1200;

  it('places the three columns: agents centered-ish, delivery right-aligned, height covering the tallest column', () => {
    const layout = computeMapLayout(
      mapDto({
        agents: [agent({ id: 'a1' }), agent({ id: 'a2', name: 'Refactorer' })],
        resources: [resource({ id: 'r1', agentIds: ['a1'] })],
      }),
      WIDTH,
    );

    expect(layout.columns.agents).toBeGreaterThanOrEqual(RES_W + 110);
    expect(layout.columns.delivery + DELIV_W).toBeLessThanOrEqual(WIDTH);
    expect(layout.width).toBe(WIDTH);

    const lastAgent = layout.agents[layout.agents.length - 1];
    const lastResource = layout.resources[layout.resources.length - 1];
    expect(layout.height).toBeGreaterThanOrEqual(lastAgent.y + lastAgent.h);
    expect(layout.height).toBeGreaterThanOrEqual(lastResource.y + lastResource.h);
  });

  it('aligns each delivery row to its agent row', () => {
    const layout = computeMapLayout(
      mapDto({ agents: [agent({ id: 'a1' }), agent({ id: 'a2', name: 'Refactorer' })] }),
      WIDTH,
    );

    expect(layout.delivery.length).toBe(layout.agents.length);
    layout.delivery.forEach((d, i) => {
      expect(d.data.id).toBe(layout.agents[i].data.id);
      expect(d.y).toBe(layout.agents[i].y + (AGENT_H - DELIV_H) / 2);
    });
  });

  it('emits one edge per resource→agent link plus one delivery edge per agent, each with a midpoint', () => {
    const layout = computeMapLayout(
      mapDto({
        agents: [agent({ id: 'a1' }), agent({ id: 'a2', name: 'Refactorer' })],
        resources: [
          resource({ id: 'r1', agentIds: ['a1', 'a2'] }),
          resource({ id: 'r2', agentIds: ['a1'] }),
        ],
      }),
      WIDTH,
    );

    expect(layout.edges.length).toBe(5);
    expect(layout.edges.filter(e => e.resourceId === null).length).toBe(2);

    // The ✕ affordance sits halfway between the columns, inside the canvas.
    const wired = layout.edges.find(e => e.resourceId === 'r2')!;
    expect(wired.midX).toBe((RES_W + layout.columns.agents) / 2);
    expect(wired.midY).toBeGreaterThan(0);
  });

  it('barycenter-sorts resources inside a group to follow their agents', () => {
    const layout = computeMapLayout(
      mapDto({
        agents: [agent({ id: 'A' }), agent({ id: 'B', name: 'Refactorer' })],
        resources: [
          resource({ id: 'r1', agentIds: ['B'] }),
          resource({ id: 'r2', agentIds: ['A'] }),
        ],
      }),
      WIDTH,
    );

    const r1 = layout.resources.find(n => n.data.id === 'r1')!;
    const r2 = layout.resources.find(n => n.data.id === 'r2')!;
    expect(r2.y).toBeLessThan(r1.y);
  });

  it('fans three incoming edges across distinct slots on the agent left edge', () => {
    const layout = computeMapLayout(
      mapDto({
        agents: [agent({ id: 'a1' })],
        resources: [
          resource({ id: 'r1', agentIds: ['a1'] }),
          resource({ id: 'r2', agentIds: ['a1'] }),
          resource({ id: 'r3', agentIds: ['a1'] }),
        ],
      }),
      WIDTH,
    );

    const incoming = layout.edges.filter(e => e.resourceId !== null && e.agentId === 'a1');
    expect(incoming.length).toBe(3);

    const agentNode = layout.agents[0];
    const endYs = incoming.map(e => pathEndY(e.path));
    expect(new Set(endYs).size).toBe(3);
    for (const y of endYs) {
      expect(y).toBeGreaterThanOrEqual(agentNode.y);
      expect(y).toBeLessThanOrEqual(agentNode.y + agentNode.h);
    }
  });

  it('draws an event chain from a raiser to every listener whose pattern matches, on the right of the agents', () => {
    const layout = computeMapLayout(
      mapDto({
        agents: [
          agent({ id: 'campaign', name: 'Campaign', raises: ['agent.campaign.succeeded', 'newsletter.sent'] }),
          agent({ id: 'follow', name: 'Follow-up', listens: ['newsletter.*'] }),
          agent({ id: 'audit', name: 'Audit', listens: ['agent.campaign.succeeded', 'newsletter.sent'] }),
          agent({ id: 'deaf', name: 'Deaf', listens: ['docs.updated'] }),
        ],
      }),
      WIDTH,
    );

    expect(layout.eventEdges.map(e => `${e.fromAgentId}>${e.toAgentId}`)).toEqual(['campaign>follow', 'campaign>audit']);
    expect(layout.eventEdges[1].topics).toEqual(['agent.campaign.succeeded', 'newsletter.sent']);
    // The arc leaves and re-enters the agent column's right edge — never through the resource edges.
    const rightX = layout.columns.agents + AGENT_W;
    for (const edge of layout.eventEdges) {
      expect(edge.path.startsWith(`M ${rightX} `)).toBeTrue();
      expect(edge.path.trim().split(' ').at(-2)).toBe(`${rightX}`);
    }
  });

  it('matches topics exactly or by trailing-wildcard prefix, like the dispatcher', () => {
    expect(matchesTopic('newsletter.sent', 'newsletter.sent')).toBeTrue();
    expect(matchesTopic('newsletter.sent', 'newsletter.sent.eu')).toBeFalse();
    expect(matchesTopic('agent.docs.*', 'agent.docs.succeeded')).toBeTrue();
    expect(matchesTopic('agent.docs.*', 'agent.docs-two.succeeded')).toBeFalse();
  });

  it('sinks an unused resource to the bottom of its group', () => {
    const layout = computeMapLayout(
      mapDto({
        agents: [agent({ id: 'a1' })],
        resources: [
          resource({ id: 'unused', agentIds: [] }),
          resource({ id: 'used', agentIds: ['a1'] }),
        ],
      }),
      WIDTH,
    );

    const used = layout.resources.find(n => n.data.id === 'used')!;
    const unused = layout.resources.find(n => n.data.id === 'unused')!;
    expect(unused.y).toBeGreaterThan(used.y);
  });
});
