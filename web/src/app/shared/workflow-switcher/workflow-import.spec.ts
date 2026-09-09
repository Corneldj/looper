import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { API_BASE } from '../../core/api.service';
import { WorkflowsStore } from '../../core/stores';
import { WorkflowDto, WorkflowPackageSummaryDto } from '../../core/models';
import { WorkflowImport } from './workflow-import';

function summary(overrides: Partial<WorkflowPackageSummaryDto> = {}): WorkflowPackageSummaryDto {
  return {
    name: 'Growth',
    description: 'Newsletter loop',
    exportedFrom: 'Looper 1.0.0',
    exportedAtUtc: '2026-09-09T10:00:00Z',
    resources: 2,
    agents: 1,
    resourceTypes: [{ typeKey: 'SlackHook', displayName: 'Slack Hook', icon: '💬', hasDll: true }],
    redactedSecrets: [{ resourceRef: 'r1', resourceName: 'GitHub token', field: 'token' }],
    ...overrides,
  };
}

function workflow(overrides: Partial<WorkflowDto> = {}): WorkflowDto {
  return {
    id: 'wf-new', name: 'Growth', description: '', isDefault: false, agentCount: 1, resourceCount: 2,
    createdAtUtc: '2026-09-09T00:00:00Z', updatedAtUtc: '2026-09-09T00:00:00Z', ...overrides,
  };
}

describe('WorkflowImport', () => {
  let http: HttpTestingController;

  beforeEach(async () => {
    localStorage.removeItem('looper.workflow');
    await TestBed.configureTestingModule({
      imports: [WorkflowImport],
      providers: [provideHttpClient(), provideHttpClientTesting()],
    }).compileComponents();
    http = TestBed.inject(HttpTestingController);
  });

  afterEach(() => {
    http.verify();
    localStorage.removeItem('looper.workflow');
  });

  function create(): { fixture: ComponentFixture<WorkflowImport>; closed: number } {
    const fixture = TestBed.createComponent(WorkflowImport);
    const state = { fixture, closed: 0 };
    fixture.componentInstance.closed.subscribe(() => state.closed++);
    fixture.detectChanges();
    return state;
  }

  /** Chooses a file and answers the API's read-back with the given summary. */
  async function choose(fixture: ComponentFixture<WorkflowImport>, name: string, reply: WorkflowPackageSummaryDto | { status: number; title: string }): Promise<void> {
    const file = new File(['PK\u0003\u0004fake'], name, { type: 'application/vnd.looper.workflow+zip' });
    fixture.componentInstance.choose(file);
    fixture.detectChanges();
    const inspect = http.expectOne(r => r.method === 'POST' && r.url === `${API_BASE}/workflows/import/inspect`);
    expect(inspect.request.body instanceof FormData).withContext('the file goes up as multipart').toBeTrue();
    expect(((inspect.request.body as FormData).get('file') as File).name).toBe(name);
    if ('status' in reply) inspect.flush({ title: reply.title }, { status: reply.status, statusText: 'Bad Request' });
    else inspect.flush(reply);
    fixture.detectChanges();
    await fixture.whenStable(); // ngModel writes the prefilled name on a resolved promise
    fixture.detectChanges();
  }

  it('previews the file through the API, imports it as a new workflow, and shows what still needs a human', async () => {
    const store = TestBed.inject(WorkflowsStore);
    store.workflows.set([workflow({ id: 'default', name: 'Default', isDefault: true })]);
    const state = create();
    const element: HTMLElement = state.fixture.nativeElement;
    expect((element.querySelector('.import-btn') as HTMLButtonElement).disabled).withContext('nothing chosen yet').toBeTrue();

    await choose(state.fixture, 'growth.workflow', summary());
    expect(element.querySelector('.preview-grid')?.textContent).toContain('💬 Slack Hook');
    expect(element.querySelector('.preview-grid')?.textContent).toContain('GitHub token → token');
    expect((element.querySelector('.preview input') as HTMLInputElement).value).toBe('Growth');

    const nameInput = element.querySelector('.preview input') as HTMLInputElement;
    nameInput.value = 'Growth (imported)';
    nameInput.dispatchEvent(new Event('input'));
    state.fixture.detectChanges();
    (element.querySelector('.import-btn') as HTMLButtonElement).click();

    const post = http.expectOne(r => r.method === 'POST' && r.url === `${API_BASE}/workflows/import/file`);
    const form = post.request.body as FormData;
    expect(form.get('name')).toBe('Growth (imported)');
    expect((form.get('file') as File).name).toBe('growth.workflow');
    post.flush({
      workflow: workflow({ name: 'Growth (imported)' }),
      resourceTypesInstalled: ['SlackHook'],
      resourceTypesReused: [],
      resourcesCreated: 2,
      agentsCreated: 1,
      warnings: ["Resource 'GitHub token' needs its 'token' filled in — secrets are not exported.", '1 agent is paused; review and switch on the ones you want running.'],
    });
    state.fixture.detectChanges();

    expect(element.querySelector('.done-line')?.textContent).toContain('2 resources, 1 agent, 1 resource type installed (SlackHook).');
    expect(element.querySelectorAll('.warnings li').length).toBe(2);
    expect(store.workflows().some(w => w.name === 'Growth (imported)')).toBeTrue();

    (element.querySelector('.open-btn') as HTMLButtonElement).click();
    expect(store.selectedId()).toBe('wf-new');
    expect(state.closed).toBe(1);
  });

  it('refuses the wrong extension locally and shows the API reason for a bad package', async () => {
    const state = create();
    const element: HTMLElement = state.fixture.nativeElement;

    state.fixture.componentInstance.choose(new File(['{}'], 'notes.json'));
    state.fixture.detectChanges();
    expect(element.querySelector('.save-error')?.textContent).toContain('Choose a .workflow file');

    await choose(state.fixture, 'tampered.workflow', { status: 400, title: "The package is corrupt or was modified: 'workflow.json' does not match its checksum." });
    expect(element.querySelector('.save-error')?.textContent).toContain('does not match its checksum');
    expect((element.querySelector('.import-btn') as HTMLButtonElement).disabled).toBeTrue();
  });

  it("shows the server's reason when the import is refused", async () => {
    const state = create();
    const element: HTMLElement = state.fixture.nativeElement;
    await choose(state.fixture, 'growth.workflow', summary());
    (element.querySelector('.import-btn') as HTMLButtonElement).click();

    http.expectOne(r => r.method === 'POST' && r.url === `${API_BASE}/workflows/import/file`)
      .flush({ title: "A resource type 'SlackHook' already exists here with different code." }, { status: 400, statusText: 'Bad Request' });
    state.fixture.detectChanges();

    expect(element.querySelector('.save-error')?.textContent).toContain('already exists here with different code');
    expect(state.closed).toBe(0);
  });
});
