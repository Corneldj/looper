import { ComponentFixture, TestBed, fakeAsync, tick } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { API_BASE } from '../../../core/api.service';
import { MapResourceDto, ResourceDto } from '../../../core/models';
import { resource as mapResource } from '../map-layout.spec';
import { JiraIssueCard } from './jira-issue-card';

function tracker(issueKey: string, issueSummary = '', connected = true): MapResourceDto {
  return mapResource({
    id: 'r1', name: 'Costing time', type: 'Custom', customTypeKey: 'JiraTimeTracking', typeLabel: 'Jira time tracking',
    card: { text: null, issueKey, issueSummary, connected },
  });
}

const savedDto = { id: 'r1', name: 'Costing time', type: 'Custom', customTypeKey: 'JiraTimeTracking', configJson: '{}' } as ResourceDto;

describe('JiraIssueCard', () => {
  let http: HttpTestingController;
  let saved: ResourceDto[];

  beforeEach(async () => {
    await TestBed.configureTestingModule({
      imports: [JiraIssueCard],
      providers: [provideHttpClient(), provideHttpClientTesting()],
    }).compileComponents();
    http = TestBed.inject(HttpTestingController);
  });

  afterEach(() => http.verify());

  function open(resource: MapResourceDto): ComponentFixture<JiraIssueCard> {
    const fixture = TestBed.createComponent(JiraIssueCard);
    fixture.componentRef.setInput('resource', resource);
    saved = [];
    fixture.componentInstance.saved.subscribe(dto => saved.push(dto));
    fixture.detectChanges();
    tick();
    fixture.detectChanges();
    return fixture;
  }

  const element = (fixture: ComponentFixture<JiraIssueCard>) => fixture.nativeElement as HTMLElement;

  function search(fixture: ComponentFixture<JiraIssueCard>, text: string, answer: { key: string; summary: string }[]): void {
    fixture.componentInstance.onQuery(text);
    tick(JiraIssueCard.debounceMs);
    const request = http.expectOne(`${API_BASE}/boards/jira-issues`);
    expect(request.request.body).toEqual({ resourceId: 'r1', configJson: null, query: text });   // the stored resource, no form
    request.flush(answer);
    fixture.detectChanges();
  }

  it('shows the issue, searches through the stored resource, and a pick writes the issue alone', fakeAsync(() => {
    const fixture = open(tracker('COST-1', 'Old sprint'));
    expect(element(fixture).querySelector('.issue-picked')?.textContent).toContain('COST-1');

    search(fixture, 'costing', [{ key: 'COST-120', summary: 'Inventory costing' }, { key: 'COST-121', summary: 'Maintenance' }]);
    const results = [...element(fixture).querySelectorAll('.issue-result')] as HTMLButtonElement[];
    expect(results.map(r => r.querySelector('.issue-key')!.textContent!.trim())).toEqual(['COST-120', 'COST-121']);

    results[0].click();
    fixture.detectChanges();
    const write = http.expectOne(`${API_BASE}/boards/time-trackers/r1/issue`);
    expect(write.request.method).toBe('PUT');
    expect(write.request.body).toEqual({ issueKey: 'COST-120', issueSummary: 'Inventory costing' });
    expect(element(fixture).querySelector('.issue-picked')?.textContent).withContext('shown before the poll confirms it').toContain('COST-120');
    expect(element(fixture).querySelectorAll('.issue-result').length).toBe(0);
    write.flush(savedDto);
    expect(saved).toEqual([savedDto]);

    // The poll confirms it; the card speaks for the server again.
    fixture.componentRef.setInput('resource', tracker('COST-120', 'Inventory costing'));
    fixture.detectChanges();
    expect(element(fixture).querySelector('.issue-picked')?.textContent).toContain('Inventory costing');
  }));

  it('clears the issue with ✕, and Enter takes the top result', fakeAsync(() => {
    const fixture = open(tracker('COST-1', 'Old sprint'));

    (element(fixture).querySelector('.issue-clear') as HTMLButtonElement).click();
    const clear = http.expectOne(`${API_BASE}/boards/time-trackers/r1/issue`);
    expect(clear.request.body).toEqual({ issueKey: '', issueSummary: '' });
    clear.flush(savedDto);
    fixture.componentRef.setInput('resource', tracker(''));
    fixture.detectChanges();
    tick();
    fixture.detectChanges();
    expect(element(fixture).querySelector('.issue-none')?.textContent).toContain('No issue yet');

    search(fixture, 'cost', [{ key: 'COST-120', summary: 'Inventory costing' }]);
    fixture.componentInstance.onKey(new KeyboardEvent('keydown', { key: 'Enter' }));
    http.expectOne(`${API_BASE}/boards/time-trackers/r1/issue`).flush(savedDto);
  }));

  it('cannot search before the resource has its Jira address and token', fakeAsync(() => {
    const fixture = open(tracker('', '', false));
    const input = element(fixture).querySelector('.issue-input') as HTMLInputElement;
    expect(input.disabled).toBeTrue();
    expect(input.placeholder).toContain('Add Jira address + token');

    fixture.componentInstance.onQuery('costing');
    tick(1000);
    http.expectNone(`${API_BASE}/boards/jira-issues`);
  }));
});
