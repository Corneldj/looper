import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { API_BASE } from '../../../core/api.service';
import { SECRET_SENTINEL, TicketSummaryDto } from '../../../core/models';
import { TicketPicker, parseTicketIds } from './ticket-picker';

function ticket(id: number, title: string, overrides: Partial<TicketSummaryDto> = {}): TicketSummaryDto {
  return {
    id, title, type: 'Bug', state: 'Active', assignedTo: 'Ann', tags: [], changedAtUtc: null, listed: true,
    url: `https://dev.azure.com/contoso/Fabrikam/_workitems/edit/${id}`, ...overrides,
  };
}

describe('parseTicketIds', () => {
  it('reads ids as leniently as the API does, each once, in order', () => {
    expect(parseTicketIds('12345, #12346 12347;12345')).toEqual([12345, 12346, 12347]);
    expect(parseTicketIds('abc, 0, -4, 12')).toEqual([12]);
    expect(parseTicketIds('')).toEqual([]);
  });
});

describe('TicketPicker', () => {
  let http: HttpTestingController;
  let emitted: Record<string, unknown>[];

  beforeEach(async () => {
    await TestBed.configureTestingModule({
      imports: [TicketPicker],
      providers: [provideHttpClient(), provideHttpClientTesting()],
    }).compileComponents();
    http = TestBed.inject(HttpTestingController);
  });

  afterEach(() => http.verify());

  /** Mounts the picker the way the editor does: every emit comes straight back in as the new values. */
  function open(values: Record<string, unknown>, resourceId: string | null = null): ComponentFixture<TicketPicker> {
    const fixture = TestBed.createComponent(TicketPicker);
    fixture.componentRef.setInput('values', values);
    fixture.componentRef.setInput('resourceId', resourceId);
    emitted = [];
    fixture.componentInstance.valuesChange.subscribe(next => {
      emitted.push(next);
      fixture.componentRef.setInput('values', next);
    });
    fixture.detectChanges();
    return fixture;
  }

  const connected = { organization: 'contoso', project: 'Fabrikam', pat: 'token', ticketIds: '' };

  function retrieveButton(fixture: ComponentFixture<TicketPicker>): HTMLButtonElement {
    return [...(fixture.nativeElement as HTMLElement).querySelectorAll('button')].find(b =>
      /Retrieve tickets|Refresh/.test(b.textContent ?? ''))!;
  }

  function retrieve(fixture: ComponentFixture<TicketPicker>, tickets: TicketSummaryDto[]): void {
    retrieveButton(fixture).click();
    http.expectOne(`${API_BASE}/boards/tickets`).flush(tickets);
    fixture.detectChanges();
  }

  function titles(fixture: ComponentFixture<TicketPicker>): string[] {
    return [...(fixture.nativeElement as HTMLElement).querySelectorAll('.ticket-title')].map(t => t.textContent!.replace(/\s+/g, ' ').trim());
  }

  function checkbox(fixture: ComponentFixture<TicketPicker>, id: number): HTMLInputElement {
    const row = [...(fixture.nativeElement as HTMLElement).querySelectorAll('.ticket')].find(r =>
      r.querySelector('.ticket-title')!.textContent!.includes(`#${id}`))!;
    return row.querySelector('input[type=checkbox]') as HTMLInputElement;
  }

  it('retrieves the board with the form as it stands, and the saved resource so its stored token is used', () => {
    const fixture = open({ ...connected, pat: SECRET_SENTINEL, tag: 'costing' }, 'r1');

    retrieveButton(fixture).click();
    const request = http.expectOne(`${API_BASE}/boards/tickets`);
    expect(request.request.method).toBe('POST');
    expect(request.request.body.resourceId).toBe('r1');
    expect(JSON.parse(request.request.body.configJson)).toEqual(jasmine.objectContaining({ tag: 'costing', pat: SECRET_SENTINEL }));
    request.flush([ticket(1, 'Fix the costing'), ticket(2, 'Round the totals')]);
    fixture.detectChanges();

    expect(titles(fixture)).toEqual(['#1 Fix the costing', '#2 Round the totals']);
    expect((fixture.nativeElement as HTMLElement).textContent).toContain('2 open tickets');
    expect(emitted).withContext('retrieving never edits the resource').toEqual([]);
  });

  it('ticks tickets into the selection in pick order, and a ticked ticket never hides behind the search', () => {
    const fixture = open(connected);
    retrieve(fixture, [ticket(1, 'Fix the costing'), ticket(2, 'Round the totals'), ticket(3, 'Tidy the report')]);

    checkbox(fixture, 2).click();
    fixture.detectChanges();
    checkbox(fixture, 1).click();
    fixture.detectChanges();
    expect(emitted.at(-1)!['ticketIds']).toBe('2, 1');

    fixture.componentInstance.search.set('report');
    fixture.detectChanges();
    expect(titles(fixture)).toEqual(['#1 Fix the costing', '#2 Round the totals', '#3 Tidy the report']);
    expect((fixture.nativeElement as HTMLElement).textContent).toContain('3 of 3 shown · 2 selected');

    fixture.componentInstance.search.set('nothing like it');
    fixture.detectChanges();
    expect(titles(fixture)).toEqual(['#1 Fix the costing', '#2 Round the totals']);

    checkbox(fixture, 2).click();
    fixture.detectChanges();
    expect(emitted.at(-1)!['ticketIds']).toBe('1');
  });

  it('marks a selected ticket the board no longer lists, and names one it could not find at all', () => {
    const fixture = open({ ...connected, ticketIds: '7, 9' });
    retrieve(fixture, [ticket(7, 'Already closed', { listed: false, state: 'Closed' }), ticket(1, 'Fix the costing')]);

    const element = fixture.nativeElement as HTMLElement;
    const closed = [...element.querySelectorAll('.ticket')].find(r => r.textContent!.includes('#7'))!;
    expect(closed.querySelector('.chip')?.textContent).toContain('not on the board');
    expect(checkbox(fixture, 7).checked).toBeTrue();
    expect(element.querySelector('.hint.warn')?.textContent).toContain('Selected but not found: 9');
  });

  it('shows the API’s reason when the board cannot be retrieved', () => {
    const fixture = open(connected);

    retrieveButton(fixture).click();
    http.expectOne(`${API_BASE}/boards/tickets`).flush(
      { title: 'Azure DevOps rejected the token (HTTP 401): it is likely expired, revoked, or missing the Work Items (Read) scope.' },
      { status: 400, statusText: 'Bad Request' },
    );
    fixture.detectChanges();

    expect((fixture.nativeElement as HTMLElement).querySelector('.board-error')?.textContent).toContain('rejected the token');
  });

  it('cannot retrieve before the connection is filled in', () => {
    const fixture = open({ organization: 'contoso' });

    expect(retrieveButton(fixture).disabled).toBeTrue();
    expect((fixture.nativeElement as HTMLElement).textContent).toContain('Fill in the organization, project and token');
  });
});
