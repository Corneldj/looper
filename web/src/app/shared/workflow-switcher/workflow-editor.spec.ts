import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { API_BASE } from '../../core/api.service';
import { WorkflowsStore } from '../../core/stores';
import { WorkflowDto } from '../../core/models';
import { WorkflowEditor } from './workflow-editor';

function workflow(overrides: Partial<WorkflowDto> = {}): WorkflowDto {
  return {
    id: 'default', name: 'Default', description: '', isDefault: true, agentCount: 0, resourceCount: 0,
    createdAtUtc: '2026-09-09T00:00:00Z', updatedAtUtc: '2026-09-09T00:00:00Z', ...overrides,
  };
}

describe('WorkflowEditor', () => {
  let http: HttpTestingController;

  beforeEach(async () => {
    localStorage.removeItem('looper.workflow');
    await TestBed.configureTestingModule({
      imports: [WorkflowEditor],
      providers: [provideHttpClient(), provideHttpClientTesting()],
    }).compileComponents();
    http = TestBed.inject(HttpTestingController);
  });

  afterEach(() => {
    http.verify();
    localStorage.removeItem('looper.workflow');
  });

  function create(existing: WorkflowDto | null): { fixture: ComponentFixture<WorkflowEditor>; closed: number } {
    const fixture = TestBed.createComponent(WorkflowEditor);
    fixture.componentRef.setInput('workflow', existing);
    const state = { fixture, closed: 0 };
    fixture.componentInstance.closed.subscribe(() => state.closed++);
    fixture.detectChanges();
    return state;
  }

  it('creates a workflow and switches the store to it', () => {
    const store = TestBed.inject(WorkflowsStore);
    store.workflows.set([workflow()]);
    store.select('default');
    const state = create(null);
    const element: HTMLElement = state.fixture.nativeElement;
    expect(element.querySelector('h2')?.textContent).toContain('New workflow');
    expect(element.querySelector('.btn-danger')).withContext('no delete while creating').toBeNull();

    const name = element.querySelector('input') as HTMLInputElement;
    name.value = 'Support';
    name.dispatchEvent(new Event('input'));
    state.fixture.detectChanges();
    (element.querySelector('.btn-primary') as HTMLButtonElement).click();

    const post = http.expectOne(r => r.method === 'POST' && r.url === `${API_BASE}/workflows`);
    expect(post.request.body).toEqual({ name: 'Support', description: '' });
    post.flush(workflow({ id: 'sup', name: 'Support', isDefault: false }));

    expect(store.selectedId()).toBe('sup');
    expect(store.workflows().map(w => w.name)).toEqual(['Default', 'Support']);
    expect(state.closed).toBe(1);
  });

  it('refuses to delete the default workflow but deletes another one and falls back to the default', () => {
    const store = TestBed.inject(WorkflowsStore);
    store.workflows.set([workflow(), workflow({ id: 'mkt', name: 'Marketing', isDefault: false })]);
    store.select('mkt');

    const onDefault = create(workflow());
    expect((onDefault.fixture.nativeElement as HTMLElement).querySelector('.btn-danger')?.hasAttribute('disabled')).toBeTrue();

    spyOn(window, 'confirm').and.returnValue(true);
    const state = create(workflow({ id: 'mkt', name: 'Marketing', isDefault: false }));
    ((state.fixture.nativeElement as HTMLElement).querySelector('.btn-danger') as HTMLButtonElement).click();
    http.expectOne(r => r.method === 'DELETE' && r.url === `${API_BASE}/workflows/mkt`).flush(null);

    expect(store.workflows().map(w => w.id)).toEqual(['default']);
    expect(store.selectedId()).toBe('default');
    expect(state.closed).toBe(1);
  });
});
