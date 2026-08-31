import { ComponentFixture, TestBed, discardPeriodicTasks, fakeAsync, tick } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { API_BASE } from '../../../core/api.service';
import { DeliveryMetricsDto, PullRequestDto } from '../../../core/models';
import { DeliverySection } from './delivery-section';

function metrics(overrides: Partial<DeliveryMetricsDto> = {}): DeliveryMetricsDto {
  return {
    windowDays: 14,
    totalCostUsd: 0,
    mergedPrs: 0,
    openPrs: 0,
    closedPrs: 0,
    costPerMergedPrUsd: null,
    firstPassRate: null,
    codeSurvivalRate: null,
    survivalCheckedPrs: 0,
    reviewChurnPer100Lines: null,
    escalationRate: null,
    escalatedRuns: 0,
    completedRuns: 0,
    agents: [],
    ...overrides,
  };
}

function pr(overrides: Partial<PullRequestDto> = {}): PullRequestDto {
  return {
    id: 'pr-1',
    agentId: 'a1',
    agentName: 'Docs bot',
    runId: null,
    title: 'Fix flaky retry test',
    url: 'https://github.com/acme/app/pull/12',
    repository: 'acme/app',
    number: 12,
    satisfiesAcs: null,
    status: 'Merged',
    openedAtUtc: '2026-08-20T10:00:00Z',
    mergedAtUtc: '2026-08-21T09:00:00Z',
    closedAtUtc: null,
    additions: 120,
    deletions: 30,
    reviewRounds: 1,
    reviewComments: 2,
    humanCommits: 0,
    firstPass: true,
    repoPath: null,
    mergeCommitSha: 'abc123',
    survivalRate: 0.9,
    survivalCheckedAtUtc: '2026-08-25T00:00:00Z',
    lastSyncedAtUtc: '2026-08-26T00:00:00Z',
    syncError: null,
    ...overrides,
  };
}

describe('DeliverySection', () => {
  let http: HttpTestingController;

  beforeEach(async () => {
    await TestBed.configureTestingModule({
      imports: [DeliverySection],
      providers: [provideHttpClient(), provideHttpClientTesting()],
    }).compileComponents();
    http = TestBed.inject(HttpTestingController);
  });

  afterEach(() => {
    // The 60s poll can leave an in-flight metrics+PRs pair depending on effect-flush
    // timing under fakeAsync; drain those before asserting nothing unexpected remains.
    http
      .match(r => r.url.startsWith(`${API_BASE}/delivery/`))
      .forEach(req => req.flush(req.request.url.includes('/metrics') ? metrics() : []));
    http.verify();
  });

  function create(days = 14): ComponentFixture<DeliverySection> {
    const fixture = TestBed.createComponent(DeliverySection);
    fixture.componentRef.setInput('days', days);
    fixture.detectChanges();
    return fixture;
  }

  /** Advances past timer(0) and answers the initial metrics + PR-list pair. */
  function flushInitial(
    fixture: ComponentFixture<DeliverySection>,
    m: DeliveryMetricsDto,
    list: PullRequestDto[],
  ): void {
    tick();
    http.expectOne(r => r.url === `${API_BASE}/delivery/metrics`).flush(m);
    http.expectOne(r => r.url === `${API_BASE}/delivery/prs`).flush(list);
    fixture.detectChanges();
  }

  it('requests delivery metrics and PRs for the given period', fakeAsync(() => {
    create(30);
    tick();

    const metricsReq = http.expectOne(r => r.url === `${API_BASE}/delivery/metrics`);
    expect(metricsReq.request.method).toBe('GET');
    expect(metricsReq.request.params.get('days')).toBe('30');
    metricsReq.flush(metrics());

    const prsReq = http.expectOne(r => r.url === `${API_BASE}/delivery/prs`);
    expect(prsReq.request.params.get('days')).toBe('30');
    prsReq.flush([]);

    discardPeriodicTasks();
  }));

  it('renders the five delivery tiles with their values', fakeAsync(() => {
    const fixture = create();
    flushInitial(
      fixture,
      metrics({
        totalCostUsd: 50,
        mergedPrs: 4,
        openPrs: 1,
        closedPrs: 1,
        costPerMergedPrUsd: 12.5,
        firstPassRate: 0.75,
        codeSurvivalRate: 0.92,
        survivalCheckedPrs: 3,
        reviewChurnPer100Lines: 4.2,
        escalationRate: 0.05,
        escalatedRuns: 2,
        completedRuns: 40,
      }),
      [pr()],
    );

    const tiles = [...(fixture.nativeElement as HTMLElement).querySelectorAll('.kpi')];
    expect(tiles.length).toBe(5);
    expect(tiles[0].textContent).toContain('$12.50');
    expect(tiles[1].textContent).toContain('75%');
    expect(tiles[2].textContent).toContain('92%');
    expect(tiles[2].textContent).toContain('3 PRs checked');
    expect(tiles[3].textContent).toContain('4.2');
    expect(tiles[3].textContent).toContain('/ 100 lines');
    expect(tiles[4].textContent).toContain('5%');
    expect(tiles[4].textContent).toContain('2/40 runs');

    discardPeriodicTasks();
  }));

  it('shows the honest null-state copy when the metrics are null', fakeAsync(() => {
    const fixture = create();
    flushInitial(fixture, metrics(), []);

    const element: HTMLElement = fixture.nativeElement;
    const tiles = [...element.querySelectorAll('.kpi')];
    expect(tiles[0].textContent).toContain('no real runs yet');
    expect(tiles[1].textContent).toContain('no merged PRs yet');
    expect(tiles[2].textContent).toContain('no matured PRs yet');
    expect(tiles[3].textContent).toContain('no reviewed PRs yet');
    expect(tiles[4].textContent).toContain('no completed real runs yet');

    // No PRs either: the table gives way to the delivery-protocol empty state.
    expect(element.querySelector('.prs .empty-state')?.textContent).toContain('No PRs tracked yet');

    discardPeriodicTasks();
  }));

  it('flags spend that has not converted into a merge yet', fakeAsync(() => {
    const fixture = create();
    flushInitial(fixture, metrics({ totalCostUsd: 18 }), []);

    const tile = (fixture.nativeElement as HTMLElement).querySelector('.kpi .kpi-null.amber');
    expect(tile?.textContent).toContain('$18.00 spent, nothing merged yet');

    discardPeriodicTasks();
  }));

  it('renders PR rows with status, link, size and first-pass', fakeAsync(() => {
    const fixture = create();
    flushInitial(fixture, metrics({ mergedPrs: 1 }), [pr()]);

    const row = (fixture.nativeElement as HTMLElement).querySelector('.prs tbody tr') as HTMLElement;
    expect(row.querySelector('.chip-green')?.textContent?.trim()).toBe('Merged');
    const link = row.querySelector('a.pr-title') as HTMLAnchorElement;
    expect(link.href).toBe('https://github.com/acme/app/pull/12');
    expect(link.textContent).toContain('Fix flaky retry test');
    expect(row.querySelector('.repo')?.textContent).toContain('acme/app');
    expect(row.querySelector('.size')?.textContent).toContain('+120');
    expect(row.querySelector('.size')?.textContent).toContain('−30');
    expect(row.querySelector('.fp.good')?.textContent?.trim()).toBe('✓');

    discardPeriodicTasks();
  }));

  it('shows autonomy recommendations for each agent', fakeAsync(() => {
    const fixture = create();
    flushInitial(
      fixture,
      metrics({
        agents: [
          {
            agentId: 'a1', name: 'Docs bot', autonomyLevel: 2, mergedPrs: 5, costUsd: 12,
            costPerMergedPrUsd: 2.4, firstPassRate: 0.9, escalationRate: 0.02,
            completedRuns: 30, recommendation: 'promote',
          },
          {
            agentId: 'a2', name: 'Refactorer', autonomyLevel: 3, mergedPrs: 0, costUsd: 3,
            costPerMergedPrUsd: null, firstPassRate: null, escalationRate: null,
            completedRuns: 1, recommendation: null,
          },
        ],
      }),
      [],
    );

    const panel = (fixture.nativeElement as HTMLElement).querySelector('.autonomy') as HTMLElement;
    expect(panel.textContent).toContain('▲ promote');
    expect(panel.textContent).toContain('gathering evidence');
    expect(panel.querySelector('.chip-accent')?.textContent?.trim()).toBe('L2');

    discardPeriodicTasks();
  }));

  it('replaces a row with the server copy after a sync', fakeAsync(() => {
    const fixture = create();
    flushInitial(fixture, metrics({ openPrs: 1 }), [pr({ status: 'Open', firstPass: null, mergedAtUtc: null })]);
    const element: HTMLElement = fixture.nativeElement;

    (element.querySelector('.actions .icon-btn[title="Sync now"]') as HTMLButtonElement).click();
    http.expectOne(`${API_BASE}/delivery/prs/pr-1/sync`).flush(pr({ status: 'Merged', firstPass: true }));
    // Tiles keep step with the table: a one-off metrics refresh follows the sync.
    http.expectOne(r => r.url === `${API_BASE}/delivery/metrics`).flush(metrics({ mergedPrs: 1 }));
    fixture.detectChanges();

    expect(element.querySelector('.prs tbody .chip-green')?.textContent?.trim()).toBe('Merged');

    discardPeriodicTasks();
  }));

  it('keeps the last loaded data when a refresh fails', fakeAsync(() => {
    const fixture = create();
    flushInitial(fixture, metrics({ costPerMergedPrUsd: 12.5 }), [pr()]);

    // Next poll: both requests fail — data stays up, a stale note appears.
    tick(60_000);
    http.expectOne(r => r.url === `${API_BASE}/delivery/metrics`).flush('down', { status: 500, statusText: 'Server Error' });
    http.expectOne(r => r.url === `${API_BASE}/delivery/prs`).flush('down', { status: 500, statusText: 'Server Error' });
    fixture.detectChanges();

    const element: HTMLElement = fixture.nativeElement;
    expect(element.querySelector('.stale-note')?.textContent).toContain('Couldn’t reach the Looper API');
    expect(element.querySelectorAll('.kpi')[0].textContent).toContain('$12.50');
    expect(element.querySelector('.prs tbody tr')).toBeTruthy();

    discardPeriodicTasks();
  }));
});
