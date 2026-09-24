import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { API_BASE } from '../../../core/api.service';
import { ResourcesStore } from '../../../core/stores';
import { ResourceDto, ResourceFieldDto, ResourceTypeDto } from '../../../core/models';
import { ResourceEditor } from './resource-editor';

function resource(overrides: Partial<ResourceDto>): ResourceDto {
  return {
    id: 'r', workflowId: 'default', name: 'x', type: 'Custom', customTypeKey: 'Script', description: '', configJson: '{}',
    agentCount: 0, createdAtUtc: '2026-09-09T00:00:00Z', updatedAtUtc: '2026-09-09T00:00:00Z', ...overrides,
  } as ResourceDto;
}

describe('ResourceEditor — Check', () => {
  let http: HttpTestingController;

  beforeEach(async () => {
    await TestBed.configureTestingModule({
      imports: [ResourceEditor],
      providers: [provideHttpClient(), provideHttpClientTesting()],
    }).compileComponents();
    http = TestBed.inject(HttpTestingController);
    TestBed.inject(ResourcesStore).resources.set([
      resource({ id: 's1', name: 'Verify report' }),
      resource({ id: 's2', name: 'Check links' }),
      resource({ id: 'c1', name: 'Old check', type: 'TestingAction', customTypeKey: null, configJson: '{"scriptResourceId":"gone"}' }),
    ]);
  });

  afterEach(() => {
    // The agents store polls on creation; that traffic is not what these specs are about.
    http.match(r => r.url === `${API_BASE}/agents`).forEach(r => r.flush([]));
    http.verify({ ignoreCancelled: true });
  });

  function open(existing: ResourceDto | null = null): ComponentFixture<ResourceEditor> {
    const fixture = TestBed.createComponent(ResourceEditor);
    fixture.componentRef.setInput('type', 'TestingAction');
    fixture.componentRef.setInput('resource', existing);
    fixture.componentRef.setInput('workflowId', 'default');
    fixture.detectChanges();
    return fixture;
  }

  it('can run one of the workflow’s scripts instead of a command, and saves only that choice', () => {
    const fixture = open();
    const element: HTMLElement = fixture.nativeElement;
    expect(element.querySelector('input.mono')).withContext('command field by default').toBeTruthy();
    expect(element.querySelector('select.check-script')).toBeNull();

    (element.querySelector('input[type=radio][value=script]') as HTMLInputElement).dispatchEvent(new Event('change'));
    fixture.detectChanges();
    const select = element.querySelector('select.check-script') as HTMLSelectElement;
    expect([...select.options].map(o => o.textContent?.trim())).toEqual(['Choose a script…', 'Check links', 'Verify report']);

    fixture.componentInstance.name.set('Report check');
    fixture.componentInstance.testScriptId.set('s1');
    fixture.componentInstance.testCommand.set('should not be saved');
    fixture.detectChanges();
    expect(fixture.componentInstance.canSave()).toBeTrue();

    fixture.componentInstance.save();
    const post = http.expectOne(r => r.method === 'POST' && r.url === `${API_BASE}/resources`);
    expect(post.request.body.type).toBe('TestingAction');
    expect(JSON.parse(post.request.body.configJson)).toEqual({ scriptResourceId: 's1' });
    post.flush(resource({ id: 'new', name: 'Report check', type: 'TestingAction', customTypeKey: null, configJson: '{"scriptResourceId":"s1"}' }));
  });

  it('flags a check whose script no longer exists and refuses to save it as is', () => {
    const fixture = open(resource({ id: 'c1', name: 'Old check', type: 'TestingAction', customTypeKey: null, configJson: '{"scriptResourceId":"gone"}' }));
    const element: HTMLElement = fixture.nativeElement;

    expect(fixture.componentInstance.testMode()).toBe('script');
    expect(element.querySelector('.hint-error')?.textContent).toContain('no longer exists');
    expect(fixture.componentInstance.canSave()).toBeFalse();

    fixture.componentInstance.testScriptId.set('s2');
    fixture.detectChanges();
    expect(fixture.componentInstance.canSave()).toBeTrue();
  });
});

describe('ResourceEditor — board types', () => {
  let http: HttpTestingController;

  const field = (key: string, kind: ResourceFieldDto['kind'], required = false, options: string[] | null = null): ResourceFieldDto =>
    ({ key, label: key, kind, required, hint: null, options, placeholder: null });

  const ticketsType: ResourceTypeDto = {
    typeKey: 'AzureDevOpsTickets', label: 'Azure DevOps tickets', icon: '🎫', blurb: '', builtIn: true,
    fields: [
      field('organization', 'Text', true), field('project', 'Text', true), field('pat', 'Password', true),
      field('assignee', 'Select', false, ['Assigned to me', 'Assigned to me or unassigned', 'Anyone']),
      field('ticketIds', 'Text'), field('clearAfterSuccess', 'Boolean'),
    ],
  };

  const jiraType: ResourceTypeDto = {
    typeKey: 'JiraTimeTracking', label: 'Jira time tracking', icon: '⏱️', blurb: '', builtIn: true,
    fields: [
      field('baseUrl', 'Text', true), field('pat', 'Password', true), field('issueKey', 'Text'), field('issueSummary', 'Text'),
      field('booking', 'Select', false, ['After every run', 'After successful runs', 'Never']),
    ],
  };

  const promptType: ResourceTypeDto = {
    typeKey: 'OneOffPrompt', label: 'One-off prompt', icon: '📝', blurb: '', builtIn: true,
    fields: [field('text', 'Multiline')],
  };

  beforeEach(async () => {
    await TestBed.configureTestingModule({
      imports: [ResourceEditor],
      providers: [provideHttpClient(), provideHttpClientTesting()],
    }).compileComponents();
    http = TestBed.inject(HttpTestingController);
  });

  afterEach(() => {
    http.match(r => r.url === `${API_BASE}/agents`).forEach(r => r.flush([]));
    http.verify({ ignoreCancelled: true });
  });

  function open(typeDef: ResourceTypeDto): ComponentFixture<ResourceEditor> {
    const fixture = TestBed.createComponent(ResourceEditor);
    fixture.componentRef.setInput('type', 'Custom');
    fixture.componentRef.setInput('typeDef', typeDef);
    fixture.componentRef.setInput('resource', null);
    fixture.componentRef.setInput('workflowId', 'default');
    fixture.detectChanges();
    return fixture;
  }

  it('edits Azure DevOps tickets on the board picker and saves the picked tickets', () => {
    const fixture = open(ticketsType);
    const element: HTMLElement = fixture.nativeElement;
    expect(element.querySelector('app-ticket-picker')).toBeTruthy();
    expect(element.querySelector('app-jira-settings')).toBeNull();

    const editor = fixture.componentInstance;
    editor.name.set('Sprint board');
    editor.customValues.set({ organization: 'contoso', project: 'Fabrikam', pat: 'token', assignee: '', ticketIds: '12, 13', clearAfterSuccess: true });
    fixture.detectChanges();
    expect(editor.canSave()).toBeTrue();

    editor.save();
    const post = http.expectOne(r => r.method === 'POST' && r.url === `${API_BASE}/resources`);
    expect(post.request.body.customTypeKey).toBe('AzureDevOpsTickets');
    expect(JSON.parse(post.request.body.configJson)).toEqual({
      organization: 'contoso', project: 'Fabrikam', pat: 'token', ticketIds: '12, 13', clearAfterSuccess: true,
    });
    post.flush(resource({ id: 'new', name: 'Sprint board', customTypeKey: 'AzureDevOpsTickets' }));
  });

  it('edits Jira time tracking without the issue — the card owns it — and never sends it back', () => {
    const fixture = open(jiraType);
    const element: HTMLElement = fixture.nativeElement;
    expect(element.querySelector('app-jira-settings')).toBeTruthy();
    expect(element.textContent).toContain('pick the issue on the resource\'s card');

    const editor = fixture.componentInstance;
    editor.name.set('Costing time');
    editor.customValues.set({ baseUrl: 'https://jira.test', pat: 'token', issueKey: 'COST-1', issueSummary: 'Stale copy', booking: 'Never' });
    fixture.detectChanges();
    expect(editor.canSave()).withContext('no issue is needed to save').toBeTrue();

    editor.save();
    const post = http.expectOne(r => r.method === 'POST' && r.url === `${API_BASE}/resources`);
    expect(JSON.parse(post.request.body.configJson)).toEqual({ baseUrl: 'https://jira.test', pat: 'token', booking: 'Never' });
    post.flush(resource({ id: 'new', name: 'Costing time', customTypeKey: 'JiraTimeTracking' }));
  });

  it('keeps the one-off prompt’s box off the modal and out of what it saves, and a refused save says why', () => {
    const fixture = open(promptType);
    const element: HTMLElement = fixture.nativeElement;
    expect(element.querySelector('textarea')).withContext('the box lives on the card').toBeNull();
    expect(element.querySelector('.card-note')?.textContent).toContain('box on this resource\'s card');

    const editor = fixture.componentInstance;
    editor.name.set('Next run');
    editor.customValues.set({ text: 'A copy from when the modal opened' });
    editor.save();
    const post = http.expectOne(r => r.method === 'POST' && r.url === `${API_BASE}/resources`);
    expect(JSON.parse(post.request.body.configJson)).toEqual({});
    post.flush({ title: 'Resource names must be unique in a workflow.' }, { status: 400, statusText: 'Bad Request' });
    fixture.detectChanges();

    expect(element.querySelector('.save-error')?.textContent).toContain('Resource names must be unique');
  });
});
