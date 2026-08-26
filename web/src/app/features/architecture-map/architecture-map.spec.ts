import { ComponentFixture, TestBed, discardPeriodicTasks, fakeAsync, tick } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { provideRouter } from '@angular/router';
import { API_BASE } from '../../core/api.service';
import { ArchitectureMapDto, AUTONOMY_LEVELS, MapAgentDto, MapResourceDto } from '../../core/models';
import { ArchitectureMap, computeMapLayout } from './architecture-map';

// Node dimensions mirrored from the layout module (they are intentionally fixed).
const RES_W = 232;
const AGENT_H = 82;
const DELIV_W = 196;
const DELIV_H = 60;

function resource(overrides: Partial<MapResourceDto> = {}): MapResourceDto {
  return {
    id: 'r1',
    name: 'Coding rules',
    type: 'Rule',
    customTypeKey: null,
    icon: '📏',
    typeLabel: 'Rule',
    agentIds: [],
    ...overrides,
  };
}

function agent(overrides: Partial<MapAgentDto> = {}): MapAgentDto {
  return {
    id: 'a1',
    name: 'Docs bot',
    model: 'claude-sonnet-4-5',
    autonomyLevel: 3,
    enabled: true,
    dryRun: false,
    isRunning: false,
    intervalMinutes: 60,
    lastRunStatus: null,
    runsLast24h: 2,
    costLast24hUsd: 1.25,
    openPrs: 0,
    mergedPrs: 1,
    resourceIds: [],
    ...overrides,
  };
}

function mapDto(overrides: Partial<ArchitectureMapDto> = {}): ArchitectureMapDto {
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

    // Agents sit past the resource column and roughly in the middle.
    expect(layout.columns.agents).toBeGreaterThanOrEqual(RES_W + 90);
    // Delivery hugs the right edge without overflowing the canvas.
    expect(layout.columns.delivery + DELIV_W).toBeLessThanOrEqual(WIDTH);
    expect(layout.width).toBe(WIDTH);

    // The canvas is tall enough for both the agent stack and the resource stack.
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

  it('emits one edge per resource→agent link plus one delivery edge per agent', () => {
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

    // 3 resource→agent links + 2 agent→delivery edges.
    expect(layout.edges.length).toBe(5);
    expect(layout.edges.filter(e => e.resourceId === null).length).toBe(2);
  });

  it('barycenter-sorts resources inside a group to follow their agents', () => {
    // r1 feeds B (agent index 1), r2 feeds A (agent index 0): despite arriving
    // first in the DTO, r1 must end up BELOW r2 so the edges do not cross.
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

describe('ArchitectureMap', () => {
  let http: HttpTestingController;

  beforeEach(async () => {
    await TestBed.configureTestingModule({
      imports: [ArchitectureMap],
      providers: [provideHttpClient(), provideHttpClientTesting(), provideRouter([])],
    }).compileComponents();
    http = TestBed.inject(HttpTestingController);
  });

  afterEach(() => {
    // The 5s poll can leave an in-flight map request depending on effect-flush
    // timing under fakeAsync; drain those before asserting nothing unexpected remains.
    http
      .match(r => r.url === `${API_BASE}/architecture/map`)
      .forEach(req => req.flush(mapDto()));
    http.verify();
  });

  function create(): ComponentFixture<ArchitectureMap> {
    const fixture = TestBed.createComponent(ArchitectureMap);
    fixture.detectChanges();
    return fixture;
  }

  /** Advances past timer(0) and answers the initial map request. */
  function flushInitial(fixture: ComponentFixture<ArchitectureMap>, dto: ArchitectureMapDto): void {
    tick();
    http.expectOne(`${API_BASE}/architecture/map`).flush(dto);
    fixture.detectChanges();
  }

  it('renders agent and delivery nodes from the map', fakeAsync(() => {
    const fixture = create();
    flushInitial(
      fixture,
      mapDto({
        agents: [agent({ id: 'a1', name: 'Docs bot', autonomyLevel: 3, mergedPrs: 1, openPrs: 0, resourceIds: ['r1'] })],
        resources: [resource({ id: 'r1', agentIds: ['a1'] })],
      }),
    );

    const element: HTMLElement = fixture.nativeElement;
    const agentNode = element.querySelector('.node.agent') as HTMLElement;
    expect(agentNode.querySelector('.agent-name')?.textContent).toContain('Docs bot');

    // The autonomy level shows as "L3" with the full blurb in the tooltip.
    const autonomy = agentNode.querySelector('.agent-meta span[title]') as HTMLElement;
    expect(autonomy.textContent?.trim()).toBe('L3');
    expect(autonomy.getAttribute('title')).toBe(AUTONOMY_LEVELS.find(l => l.level === 3)!.blurb);

    const delivery = element.querySelector('.node.delivery') as HTMLElement;
    expect(delivery.querySelector('.deliv-main')?.textContent).toContain('1 merged · 0 open');

    discardPeriodicTasks();
  }));

  it('shows the empty state when the map has no agents and no resources', fakeAsync(() => {
    const fixture = create();
    flushInitial(fixture, mapDto());

    const empty = (fixture.nativeElement as HTMLElement).querySelector('.empty-state') as HTMLElement;
    expect(empty.textContent).toContain('Nothing to map yet');

    discardPeriodicTasks();
  }));

  it('shows the offline empty state when the first load fails', fakeAsync(() => {
    const fixture = create();
    tick();
    http
      .expectOne(`${API_BASE}/architecture/map`)
      .flush('down', { status: 500, statusText: 'Server Error' });
    fixture.detectChanges();

    const empty = (fixture.nativeElement as HTMLElement).querySelector('.empty-state') as HTMLElement;
    expect(empty.textContent).toContain('Can’t reach the Looper API');

    discardPeriodicTasks();
  }));
});
