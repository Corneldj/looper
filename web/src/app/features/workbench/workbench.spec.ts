import { ComponentFixture, TestBed, discardPeriodicTasks, fakeAsync, tick } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { provideRouter } from '@angular/router';
import { API_BASE } from '../../core/api.service';
import { ArchitectureMapDto, AUTONOMY_LEVELS } from '../../core/models';
import { agent, mapDto, resource } from './map-layout.spec';
import { Workbench } from './workbench';

describe('Workbench', () => {
  let http: HttpTestingController;

  beforeEach(async () => {
    await TestBed.configureTestingModule({
      imports: [Workbench],
      providers: [provideHttpClient(), provideHttpClientTesting(), provideRouter([])],
    }).compileComponents();
    http = TestBed.inject(HttpTestingController);
  });

  afterEach(() => {
    drain();
    http.verify({ ignoreCancelled: true });
  });

  /**
   * Answers every background poll the workbench and its stores start, so verify() sees only
   * what a test forgot. Polls the component teardown already cancelled are left alone.
   */
  function drain(): void {
    const live = (predicate: (url: string, method: string) => boolean) =>
      http.match(r => predicate(r.url, r.method)).filter(req => !req.cancelled);
    live((url) => url === `${API_BASE}/architecture/map`).forEach(req => req.flush(mapDto()));
    live((url, method) => url.startsWith(`${API_BASE}/agents`) && method === 'GET').forEach(req => req.flush([]));
    live((url) => url.startsWith(`${API_BASE}/user-actions`)).forEach(req => req.flush([]));
    live((url) => url === `${API_BASE}/resources`).forEach(req => req.flush([]));
    live((url) => url === `${API_BASE}/resource-types`).forEach(req => req.flush([]));
    live((url) => url === `${API_BASE}/graphs`).forEach(req => req.flush([]));
  }

  function create(): ComponentFixture<Workbench> {
    const fixture = TestBed.createComponent(Workbench);
    fixture.detectChanges();
    return fixture;
  }

  /** Advances past timer(0) and answers the initial map request. */
  function flushInitial(fixture: ComponentFixture<Workbench>, dto: ArchitectureMapDto): void {
    tick();
    http.expectOne(r => r.url === `${API_BASE}/architecture/map`).flush(dto);
    fixture.detectChanges();
  }

  const mapRequest = (r: { url: string }) => r.url === `${API_BASE}/architecture/map`;

  function pointer(type: string, x: number, y: number): PointerEvent {
    return new PointerEvent(type, { clientX: x, clientY: y, button: 0, bubbles: true, cancelable: true });
  }

  it('renders resource, agent and delivery nodes with the workbench controls', fakeAsync(() => {
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

    // Workbench functionality lives on the node: schedule switch, Run now, edit, delete.
    expect(agentNode.querySelector('.switch input')).toBeTruthy();
    expect(agentNode.querySelector('.agent-actions .btn')?.textContent).toContain('Run now');
    expect(agentNode.querySelector('[title="Edit agent"]')).toBeTruthy();

    const resourceNode = element.querySelector('.node.resource') as HTMLElement;
    expect(resourceNode.querySelector('.res-name')?.textContent).toContain('Coding rules');
    expect(resourceNode.querySelector('.port')).withContext('the extension point').toBeTruthy();
    expect(resourceNode.querySelector('[title="Edit"]')).toBeTruthy();

    const delivery = element.querySelector('.node.delivery') as HTMLElement;
    expect(delivery.querySelector('.deliv-main')?.textContent).toContain('1 merged · 0 open');

    // One resource→agent edge (with its hover twin) and one agent→delivery edge.
    expect(element.querySelectorAll('svg path.edge').length).toBe(2);
    expect(element.querySelectorAll('svg path.edge-hit').length).toBe(1);

    discardPeriodicTasks();
  }));

  it('shows the empty state with create actions when the workspace is blank', fakeAsync(() => {
    const fixture = create();
    flushInitial(fixture, mapDto());

    const empty = (fixture.nativeElement as HTMLElement).querySelector('.empty-state') as HTMLElement;
    expect(empty.textContent).toContain('Nothing in');
    expect(empty.querySelectorAll('.empty-actions .btn').length).toBe(3);

    discardPeriodicTasks();
  }));

  it('shows the offline state when the first load fails', fakeAsync(() => {
    const fixture = create();
    tick();
    http.expectOne(mapRequest).flush('down', { status: 500, statusText: 'Server Error' });
    fixture.detectChanges();

    const empty = (fixture.nativeElement as HTMLElement).querySelector('.empty-state') as HTMLElement;
    expect(empty.textContent).toContain('Can’t reach the Looper API');

    discardPeriodicTasks();
  }));

  it('drags a resource port onto an agent to connect them, then redraws', fakeAsync(() => {
    const fixture = create();
    flushInitial(
      fixture,
      mapDto({
        agents: [agent({ id: 'a1', resourceIds: [] })],
        resources: [resource({ id: 'r1', agentIds: [] })],
      }),
    );
    const element: HTMLElement = fixture.nativeElement;
    const port = element.querySelector('.node.resource .port') as HTMLElement;
    const agentNode = element.querySelector('[data-agent-id="a1"]') as HTMLElement;

    // Grab the port: the canvas enters wiring mode and a draft line starts at the port.
    port.dispatchEvent(pointer('pointerdown', 10, 10));
    fixture.detectChanges();
    expect(element.querySelector('.map-canvas')?.classList).toContain('dragging');
    expect(element.querySelector('path.edge.draft')?.classList).not.toContain('allowed');

    // Move over the agent: the draft line snaps to it and the agent lights up as the target.
    spyOn(document, 'elementFromPoint').and.returnValue(agentNode);
    document.dispatchEvent(pointer('pointermove', 400, 80));
    fixture.detectChanges();
    expect(element.querySelector('path.edge.draft')?.classList).toContain('allowed');
    expect(agentNode.classList).toContain('drop-target');
    expect(element.querySelector('.drag-hint')?.textContent).toContain('Release to connect');

    // Release: one attach call, then an immediate map refresh showing the new edge.
    document.dispatchEvent(pointer('pointerup', 400, 80));
    http.expectOne(r => r.method === 'POST' && r.url === `${API_BASE}/agents/a1/resources/r1`).flush({});
    http.expectOne(mapRequest).flush(
      mapDto({
        agents: [agent({ id: 'a1', resourceIds: ['r1'] })],
        resources: [resource({ id: 'r1', agentIds: ['a1'] })],
      }),
    );
    fixture.detectChanges();
    expect(element.querySelector('.map-canvas')?.classList).not.toContain('dragging');
    expect(element.querySelectorAll('svg path.edge-hit').length).toBe(1);

    discardPeriodicTasks();
  }));

  it('refuses to drop a resource onto an agent that already has it', fakeAsync(() => {
    const fixture = create();
    flushInitial(
      fixture,
      mapDto({
        agents: [agent({ id: 'a1', resourceIds: ['r1'] })],
        resources: [resource({ id: 'r1', agentIds: ['a1'] })],
      }),
    );
    const element: HTMLElement = fixture.nativeElement;
    const agentNode = element.querySelector('[data-agent-id="a1"]') as HTMLElement;

    (element.querySelector('.node.resource .port') as HTMLElement).dispatchEvent(pointer('pointerdown', 10, 10));
    spyOn(document, 'elementFromPoint').and.returnValue(agentNode);
    document.dispatchEvent(pointer('pointermove', 400, 80));
    fixture.detectChanges();
    expect(agentNode.classList).toContain('drop-blocked');
    expect(element.querySelector('.drag-hint')?.textContent).toContain('Already connected');

    document.dispatchEvent(pointer('pointerup', 400, 80));
    fixture.detectChanges();
    http.expectNone(r => r.method === 'POST' && r.url.includes('/resources/'));

    discardPeriodicTasks();
  }));

  it('cuts an edge from its hover ✕', fakeAsync(() => {
    const fixture = create();
    flushInitial(
      fixture,
      mapDto({
        agents: [agent({ id: 'a1', resourceIds: ['r1'] })],
        resources: [resource({ id: 'r1', agentIds: ['a1'] })],
      }),
    );
    const element: HTMLElement = fixture.nativeElement;

    (element.querySelector('path.edge-hit') as SVGPathElement).dispatchEvent(new MouseEvent('mouseenter'));
    fixture.detectChanges();
    const cut = element.querySelector('.edge-cut') as HTMLButtonElement;
    expect(cut).withContext('the ✕ appears on hover').toBeTruthy();
    expect(element.querySelector('path.edge')?.classList).toContain('cut');

    cut.click();
    http.expectOne(r => r.method === 'DELETE' && r.url === `${API_BASE}/agents/a1/resources/r1`).flush({});
    http.expectOne(mapRequest).flush(
      mapDto({ agents: [agent({ id: 'a1' })], resources: [resource({ id: 'r1' })] }),
    );
    fixture.detectChanges();
    expect(element.querySelector('.edge-cut')).toBeNull();
    expect(element.querySelectorAll('svg path.edge-hit').length).toBe(0);

    discardPeriodicTasks();
  }));

  it('draws event chains between agents with raise/listen chips', fakeAsync(() => {
    const fixture = create();
    flushInitial(
      fixture,
      mapDto({
        agents: [
          agent({ id: 'a1', name: 'Campaign', raises: ['agent.campaign.succeeded', 'newsletter.sent'] }),
          agent({ id: 'a2', name: 'Follow-up', listens: ['newsletter.*'] }),
        ],
      }),
    );
    const element: HTMLElement = fixture.nativeElement;
    const chain = element.querySelector('path.edge.event') as SVGPathElement;
    expect(chain).withContext('one event edge').toBeTruthy();
    expect(chain.querySelector('title')?.textContent).toBe('newsletter.sent');
    expect(element.querySelector('[data-agent-id="a1"] .chip[title^="Raises"]')?.getAttribute('title')).toBe('Raises: newsletter.sent');
    expect(element.querySelector('[data-agent-id="a2"] .chip[title^="Listens"]')?.getAttribute('title')).toBe('Listens for: newsletter.*');

    discardPeriodicTasks();
  }));

  it('runs an agent from its node', fakeAsync(() => {
    const fixture = create();
    flushInitial(fixture, mapDto({ agents: [agent({ id: 'a1' })] }));
    const element: HTMLElement = fixture.nativeElement;

    (element.querySelector('.agent-actions .btn') as HTMLButtonElement).click();
    http.expectOne(r => r.method === 'POST' && r.url === `${API_BASE}/agents/a1/run`).flush({ runId: 'x' });
    http.expectOne(mapRequest).flush(mapDto({ agents: [agent({ id: 'a1', isRunning: true })] }));
    fixture.detectChanges();
    expect(element.querySelector('.node.agent .status-dot')?.classList).toContain('running');
    expect(element.querySelector('.agent-actions .btn-danger')?.textContent).toContain('Cancel');

    discardPeriodicTasks();
  }));
});
