import { ComponentFixture, TestBed, discardPeriodicTasks, fakeAsync, tick } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { API_BASE } from '../../core/api.service';
import { ResourcesStore } from '../../core/stores';
import { GenerationJobDto } from '../../core/models';
import { ResourceTypePicker } from './resource-type-picker';

function job(overrides: Partial<GenerationJobDto> = {}): GenerationJobDto {
  return {
    id: 'job-1',
    description: 'a webhook that posts somewhere useful',
    phase: 'Generating',
    attempt: 1,
    maxAttempts: 4,
    lastErrors: [],
    costUsd: 0,
    result: null,
    error: null,
    startedAtUtc: '2026-09-09T10:00:00Z',
    updatedAtUtc: '2026-09-09T10:00:00Z',
    ...overrides,
  };
}

describe('ResourceTypePicker generation', () => {
  let http: HttpTestingController;

  beforeEach(async () => {
    await TestBed.configureTestingModule({
      imports: [ResourceTypePicker],
      providers: [provideHttpClient(), provideHttpClientTesting()],
    }).compileComponents();
    http = TestBed.inject(HttpTestingController);
  });

  afterEach(() => http.verify());

  /** Opens the AI flow with a description and starts generation; answers the start call with the given job. */
  function start(started: GenerationJobDto): ComponentFixture<ResourceTypePicker> {
    const fixture = TestBed.createComponent(ResourceTypePicker);
    fixture.detectChanges();
    fixture.componentInstance.openAiCreate();
    fixture.componentInstance.aiDescription.set('a webhook that posts somewhere useful');
    fixture.detectChanges();
    fixture.componentInstance.generateType();
    const post = http.expectOne(r => r.method === 'POST' && r.url === `${API_BASE}/resource-types/generate/jobs`);
    expect(post.request.body).toEqual({ description: 'a webhook that posts somewhere useful' });
    post.flush(started);
    fixture.detectChanges();
    return fixture;
  }

  it('shows each repair attempt with its compiler errors and can be cancelled', fakeAsync(() => {
    const fixture = start(job());
    const element: HTMLElement = fixture.nativeElement;
    expect(element.querySelector('.ai-progress')?.textContent).toContain('Claude is writing the module');
    expect(element.querySelector('.cancel-generation')).withContext('a cancel button while busy').toBeTruthy();

    tick(1500);
    http.expectOne(r => r.method === 'GET' && r.url === `${API_BASE}/resource-types/generate/jobs/job-1`)
      .flush(job({ phase: 'Repairing', attempt: 2, lastErrors: ['(3,1): error CS1513: } expected'], costUsd: 0.05 }));
    fixture.detectChanges();
    expect(element.querySelector('.ai-progress')?.textContent).toContain('Attempt 2 of 4 — Claude is fixing 1 compiler error');
    expect(element.querySelector('.ai-errors pre')?.textContent).toContain('CS1513');

    (element.querySelector('.cancel-generation') as HTMLButtonElement).click();
    http.expectOne(r => r.method === 'DELETE' && r.url === `${API_BASE}/resource-types/generate/jobs/job-1`)
      .flush(job({ phase: 'Cancelled', attempt: 2, error: 'Cancelled — nothing was installed.' }));
    fixture.detectChanges();

    expect(fixture.componentInstance.aiBusy()).toBeFalse();
    expect(element.querySelector('.ai-error')?.textContent).toContain('nothing was installed');
    expect(element.querySelector('.cancel-generation')).toBeNull();
    discardPeriodicTasks();
  }));

  it('installs the type when a later attempt compiles', fakeAsync(() => {
    const fixture = start(job());
    const element: HTMLElement = fixture.nativeElement;

    tick(1500);
    http.expectOne(r => r.url === `${API_BASE}/resource-types/generate/jobs/job-1`).flush(job({ phase: 'Compiling', attempt: 2 }));
    tick(2000);
    http.expectOne(r => r.url === `${API_BASE}/resource-types/generate/jobs/job-1`).flush(job({
      phase: 'Installed',
      attempt: 2,
      costUsd: 0.1,
      result: {
        type: { typeKey: 'Webhook', label: 'Webhook', icon: '🔔', blurb: 'Posts somewhere.', builtIn: false, fields: [] },
        sourceCode: 'public sealed class WebhookModule {}',
        costUsd: 0.1,
      },
    }));
    fixture.detectChanges();

    expect(element.querySelector('.ai-success-line')?.textContent).toContain('Webhook');
    expect(TestBed.inject(ResourcesStore).types().some(t => t.typeKey === 'Webhook')).toBeTrue();
    expect(fixture.componentInstance.aiBusy()).toBeFalse();
    discardPeriodicTasks();
  }));
});
