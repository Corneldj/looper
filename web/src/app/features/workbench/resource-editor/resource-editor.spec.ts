import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { API_BASE } from '../../../core/api.service';
import { ResourcesStore } from '../../../core/stores';
import { ResourceDto } from '../../../core/models';
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
