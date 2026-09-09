import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { API_BASE } from '../../core/api.service';
import { WorkflowsStore } from '../../core/stores';
import { WorkflowDto } from '../../core/models';
import { FileDownloads } from '../../core/file-downloads';
import { WorkflowSwitcher } from './workflow-switcher';

function workflow(overrides: Partial<WorkflowDto> = {}): WorkflowDto {
  return {
    id: 'default',
    name: 'Default',
    description: '',
    isDefault: true,
    agentCount: 2,
    resourceCount: 3,
    createdAtUtc: '2026-09-09T00:00:00Z',
    updatedAtUtc: '2026-09-09T00:00:00Z',
    ...overrides,
  };
}

describe('WorkflowSwitcher', () => {
  let http: HttpTestingController;

  beforeEach(async () => {
    localStorage.removeItem('looper.workflow');
    await TestBed.configureTestingModule({
      imports: [WorkflowSwitcher],
      providers: [provideHttpClient(), provideHttpClientTesting()],
    }).compileComponents();
    http = TestBed.inject(HttpTestingController);
  });

  afterEach(() => {
    http.verify();
    localStorage.removeItem('looper.workflow');
  });

  function create(list: WorkflowDto[]): ComponentFixture<WorkflowSwitcher> {
    const fixture = TestBed.createComponent(WorkflowSwitcher);
    fixture.detectChanges();
    http.expectOne(`${API_BASE}/workflows`).flush(list);
    fixture.detectChanges();
    return fixture;
  }

  it('lists the workflows, selects the default and remembers a switch', () => {
    const fixture = create([workflow(), workflow({ id: 'mkt', name: 'Marketing', isDefault: false })]);
    const store = TestBed.inject(WorkflowsStore);
    const select = (fixture.nativeElement as HTMLElement).querySelector('select') as HTMLSelectElement;

    expect([...select.options].map(o => o.textContent?.trim())).toEqual(['Default', 'Marketing']);
    expect(store.selectedId()).toBe('default');

    select.value = 'mkt';
    select.dispatchEvent(new Event('change'));
    fixture.detectChanges();
    expect(store.selectedId()).toBe('mkt');
    expect(localStorage.getItem('looper.workflow')).toBe('mkt');
    expect(store.selected()?.name).toBe('Marketing');
  });

  it('restores a remembered workflow and falls back to the default when it is gone', () => {
    localStorage.setItem('looper.workflow', 'gone');
    create([workflow()]);
    expect(TestBed.inject(WorkflowsStore).selectedId()).toBe('default');
  });

  it('asks the shell for the editor instead of rendering it inside the filtered topbar', () => {
    const fixture = create([workflow()]);
    const element: HTMLElement = fixture.nativeElement;
    const requests: (WorkflowDto | 'new')[] = [];
    fixture.componentInstance.newRequested.subscribe(() => requests.push('new'));
    fixture.componentInstance.editRequested.subscribe(w => requests.push(w));

    (element.querySelector('[title="New workflow"]') as HTMLButtonElement).click();
    (element.querySelector('[title="Rename or delete this workflow"]') as HTMLButtonElement).click();

    expect(requests).toEqual(['new', jasmine.objectContaining({ id: 'default' })]);
    expect(element.querySelector('app-workflow-editor')).toBeNull();
  });

  it('exports the selected workflow as a .workflow file from the topbar', () => {
    const saved = spyOn(TestBed.inject(FileDownloads), 'saveBlob');
    const fixture = create([workflow(), workflow({ id: 'mkt', name: 'Marketing loops', isDefault: false })]);
    TestBed.inject(WorkflowsStore).select('mkt');
    fixture.detectChanges();

    (fixture.nativeElement.querySelector('.export-btn') as HTMLButtonElement).click();
    const get = http.expectOne(r => r.method === 'GET' && r.url === `${API_BASE}/workflows/mkt/export`);
    get.flush(new Blob(['PK']));
    fixture.detectChanges();

    expect(saved).toHaveBeenCalledWith('marketing-loops.workflow', jasmine.any(Blob));
  });
});
