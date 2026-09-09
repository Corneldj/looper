import { ComponentFixture, TestBed, discardPeriodicTasks, fakeAsync, tick } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { provideRouter } from '@angular/router';
import { API_BASE } from '../../../core/api.service';
import { MetricSummaryDto, ResourceTypeDto } from '../../../core/models';
import { MetricsSection } from './metrics-section';

function metric(overrides: Partial<MetricSummaryDto> = {}): MetricSummaryDto {
  return {
    resourceId: 'm1',
    name: 'Newsletter sign-ups',
    description: 'People who joined the list.',
    unit: 'sign-ups',
    aggregation: 'Sum',
    direction: 'Higher',
    target: 1000,
    latest: 12,
    latestAtUtc: '2026-09-07T10:00:00Z',
    current: 420,
    previous: 300,
    trendPct: 40,
    countInWindow: 9,
    totalCount: 30,
    series: [
      { date: '2026-09-01', value: 100 },
      { date: '2026-09-03', value: 200 },
      { date: '2026-09-06', value: 120 },
    ],
    agents: ['Campaign loop'],
    ...overrides,
  };
}

const metricType: ResourceTypeDto = {
  typeKey: 'Metric',
  label: 'Metric',
  icon: '📈',
  blurb: 'An outcome you care about.',
  builtIn: true,
  fields: [
    { key: 'unit', label: 'Unit', kind: 'Text', required: false, hint: null, options: null, placeholder: null },
    { key: 'aggregation', label: 'How values combine', kind: 'Select', required: true, hint: null, options: ['latest', 'sum', 'average'], placeholder: null },
    { key: 'direction', label: 'Which way is good', kind: 'Select', required: true, hint: null, options: ['higher', 'lower'], placeholder: null },
  ],
};

describe('MetricsSection', () => {
  let http: HttpTestingController;

  beforeEach(async () => {
    await TestBed.configureTestingModule({
      imports: [MetricsSection],
      providers: [provideHttpClient(), provideHttpClientTesting(), provideRouter([])],
    }).compileComponents();
    http = TestBed.inject(HttpTestingController);
  });

  afterEach(() => {
    http.match(r => r.url === `${API_BASE}/metrics`).filter(r => !r.cancelled).forEach(req => req.flush([]));
    http.match(r => r.url === `${API_BASE}/resource-types`).filter(r => !r.cancelled).forEach(req => req.flush([metricType]));
    http.match(r => r.url.startsWith(`${API_BASE}/agents`)).filter(r => !r.cancelled).forEach(req => req.flush([]));
    http.verify({ ignoreCancelled: true });
  });

  function create(days = 14): ComponentFixture<MetricsSection> {
    const fixture = TestBed.createComponent(MetricsSection);
    fixture.componentRef.setInput('days', days);
    fixture.detectChanges();
    return fixture;
  }

  /** Answers the type catalog and the initial metrics request. */
  function flushInitial(fixture: ComponentFixture<MetricsSection>, list: MetricSummaryDto[]): void {
    http.expectOne(`${API_BASE}/resource-types`).flush([metricType]);
    tick();
    const req = http.expectOne(r => r.url === `${API_BASE}/metrics`);
    expect(req.request.params.get('days')).toBe('14');
    req.flush(list);
    fixture.detectChanges();
  }

  it('renders a card per metric with reading, direction-aware trend, target progress and reporters', fakeAsync(() => {
    const fixture = create();
    flushInitial(fixture, [
      metric(),
      metric({ resourceId: 'm2', name: 'Resolution time', unit: 'h', aggregation: 'Average', direction: 'Lower', current: 6, trendPct: 25, target: null, agents: [] }),
      metric({ resourceId: 'm3', name: 'Churn', unit: '%', aggregation: 'Latest', current: null, latest: null, trendPct: null, target: null, series: [] }),
    ]);
    const element: HTMLElement = fixture.nativeElement;
    const cards = element.querySelectorAll('.metric-card');
    expect(cards.length).toBe(3);

    const signups = cards[0];
    expect(signups.querySelector('.metric-name')?.textContent).toContain('Newsletter sign-ups');
    expect(signups.querySelector('.metric-value')?.textContent?.replace(/\s+/g, '')).toBe('420sign-ups');
    expect(signups.querySelector('.trend')?.classList).toContain('good');       // up, and higher is better
    expect(signups.querySelector('.trend')?.textContent).toContain('▲ 40%');
    expect(signups.querySelector('.target-text')?.textContent).toContain('42% of 1,000 sign-ups');
    expect(signups.querySelector('.spark polyline')).toBeTruthy();
    expect(signups.querySelector('.metric-foot')?.textContent).toContain('reported by Campaign loop');

    const resolution = cards[1];
    expect(resolution.querySelector('.trend')?.classList).toContain('bad');     // up, but lower is better
    expect(resolution.querySelector('.metric-foot')?.textContent).toContain('not attached to an agent yet');
    expect(resolution.querySelector('.target')).toBeNull();

    const churn = cards[2];
    expect(churn.querySelector('.metric-value')?.textContent?.trim()).toBe('—');
    expect(churn.querySelector('.metric-trend')?.textContent).toContain('no readings yet');
    expect(churn.querySelector('.spark')).toBeNull();

    discardPeriodicTasks();
  }));

  it('shows the empty state with a create action when no metric exists', fakeAsync(() => {
    const fixture = create();
    flushInitial(fixture, []);
    const element: HTMLElement = fixture.nativeElement;
    expect(element.querySelector('.metrics-empty')?.textContent).toContain('No metrics yet');

    // "+ New metric" opens the generic resource form for the Metric type.
    (element.querySelector('.metrics-head .btn') as HTMLButtonElement).click();
    fixture.detectChanges();
    expect(element.querySelector('app-resource-editor')).toBeTruthy();
    expect(element.querySelector('app-resource-editor')?.textContent).toContain('New Metric');

    discardPeriodicTasks();
  }));

  it('opens a metric into its values and records a manual entry', fakeAsync(() => {
    const fixture = create();
    flushInitial(fixture, [metric()]);
    const element: HTMLElement = fixture.nativeElement;

    (element.querySelector('.metric-card') as HTMLButtonElement).click();
    fixture.detectChanges();
    http.expectOne(r => r.url === `${API_BASE}/metrics/m1/values`).flush([
      { id: 'v1', resourceId: 'm1', agentId: 'a1', agentName: 'Campaign loop', runId: null, value: 12, note: 'launch day', source: 'Agent', recordedAtUtc: '2026-09-07T10:00:00Z' },
    ]);
    fixture.detectChanges();
    const modal = element.querySelector('app-metric-detail-modal') as HTMLElement;
    expect(modal.querySelector('h2')?.textContent).toContain('Newsletter sign-ups');
    expect(modal.querySelectorAll('tbody tr').length).toBe(1);
    expect(modal.querySelector('tbody tr')?.textContent).toContain('launch day');
    expect(modal.querySelector('tbody .chip')?.textContent?.trim()).toBe('Agent');

    const valueInput = modal.querySelector('.value-input') as HTMLInputElement;
    valueInput.value = '7';
    valueInput.dispatchEvent(new Event('input'));
    fixture.detectChanges();
    (modal.querySelector('.add-row .btn') as HTMLButtonElement).click();

    const post = http.expectOne(r => r.method === 'POST' && r.url === `${API_BASE}/metrics/values`);
    expect(post.request.body).toEqual(jasmine.objectContaining({ metric: 'm1', value: 7, source: 'Manual' }));
    post.flush({ id: 'v2', resourceId: 'm1', agentId: null, agentName: null, runId: null, value: 7, note: null, source: 'Manual', recordedAtUtc: '2026-09-07T11:00:00Z' });
    // The cards refresh after a change.
    http.expectOne(r => r.url === `${API_BASE}/metrics`).flush([metric({ current: 427 })]);
    fixture.detectChanges();
    expect(modal.querySelectorAll('tbody tr').length).toBe(2);
    expect(element.querySelector('.metric-value')?.textContent?.replace(/\s+/g, '')).toBe('427sign-ups');

    discardPeriodicTasks();
  }));
});
