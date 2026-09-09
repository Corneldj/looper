import { TestBed, discardPeriodicTasks, fakeAsync, tick } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { API_BASE } from './api.service';
import { UserActionDto } from './models';
import { UserActionsStore } from './stores';

function request(overrides: Partial<UserActionDto> = {}): UserActionDto {
  return {
    id: 'ua-1',
    agentId: 'a1',
    agentName: 'Docs bot',
    runId: 'run-1',
    title: 'Approve the schema change',
    details: 'The migration drops a column — confirm before I continue.',
    status: 'Open',
    blocking: true,
    response: null,
    createdAtUtc: '2026-08-26T09:00:00Z',
    resolvedAtUtc: null,
    resolutionNote: null,
    canRecordToMemory: false,
    ...overrides,
  };
}

describe('UserActionsStore', () => {
  let http: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [provideHttpClient(), provideHttpClientTesting()],
    });
    http = TestBed.inject(HttpTestingController);
  });

  afterEach(() => {
    // The 10s poll can leave an in-flight request depending on timer-flush timing
    // under fakeAsync; drain those before asserting nothing unexpected remains.
    http.match(r => r.url === `${API_BASE}/user-actions`).forEach(req => req.flush([]));
    http.verify();
  });

  /** Instantiates the store inside fakeAsync and answers the initial poll tick. */
  function create(list: UserActionDto[]): UserActionsStore {
    const store = TestBed.inject(UserActionsStore);
    tick();
    http.expectOne(r => r.url === `${API_BASE}/user-actions`).flush(list);
    return store;
  }

  it('loads the open requests on the first poll tick', fakeAsync(() => {
    const store = TestBed.inject(UserActionsStore);
    expect(store.loaded()).toBe(false);
    tick();

    const req = http.expectOne(r => r.url === `${API_BASE}/user-actions`);
    expect(req.request.method).toBe('GET');
    req.flush([request(), request({ id: 'ua-2', agentId: 'a2', blocking: false })]);

    expect(store.loaded()).toBe(true);
    expect(store.open().length).toBe(2);
    expect(store.openCount()).toBe(2);

    discardPeriodicTasks();
  }));

  it('polls again every 10 seconds', fakeAsync(() => {
    const store = create([request()]);
    expect(store.openCount()).toBe(1);

    tick(10_000);
    http.expectOne(r => r.url === `${API_BASE}/user-actions`).flush([]);
    expect(store.openCount()).toBe(0);

    discardPeriodicTasks();
  }));

  it('filters requests per agent with forAgent', fakeAsync(() => {
    const store = create([
      request(),
      request({ id: 'ua-2', agentId: 'a2', title: 'Pick a branch name' }),
      request({ id: 'ua-3', agentId: 'a1', blocking: false }),
    ]);

    expect(store.forAgent('a1').map(r => r.id)).toEqual(['ua-1', 'ua-3']);
    expect(store.forAgent('a2').map(r => r.id)).toEqual(['ua-2']);
    expect(store.forAgent('missing')).toEqual([]);

    discardPeriodicTasks();
  }));

  it('keeps the last loaded list when a poll fails', fakeAsync(() => {
    const store = create([request()]);

    tick(10_000);
    http
      .expectOne(r => r.url === `${API_BASE}/user-actions`)
      .flush('down', { status: 500, statusText: 'Server Error' });

    expect(store.openCount()).toBe(1);
    expect(store.open()[0].id).toBe('ua-1');

    discardPeriodicTasks();
  }));

  it('refreshNow fetches outside the poll cadence', fakeAsync(() => {
    const store = create([request()]);

    store.refreshNow();
    http.expectOne(r => r.url === `${API_BASE}/user-actions`).flush([]);
    expect(store.openCount()).toBe(0);

    discardPeriodicTasks();
  }));
});
