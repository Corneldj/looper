import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { API_BASE } from '../../core/api.service';
import { ArchitectResultDto } from '../../core/models';
import { ArchitectModal } from './architect-modal';

function architectResult(overrides: Partial<ArchitectResultDto> = {}): ArchitectResultDto {
  return {
    success: true,
    report: 'Reused the repo folder; created a unit-test gate, a reviewer and the nightly agent.',
    costUsd: 1.2345,
    createdResources: [
      { id: 'r1', name: 'Unit tests gate', detail: 'Testing Action · npm test' },
      { id: 'r2', name: 'Docs reviewer', detail: 'Reviewer · claude-sonnet-5' },
    ],
    createdAgents: [
      { id: 'a1', name: 'Docs sync agent', detail: 'claude-opus-5 · every 24h · dry-run, paused' },
    ],
    error: null,
    ...overrides,
  };
}

describe('ArchitectModal', () => {
  let http: HttpTestingController;

  beforeEach(async () => {
    await TestBed.configureTestingModule({
      imports: [ArchitectModal],
      providers: [provideHttpClient(), provideHttpClientTesting()],
    }).compileComponents();
    http = TestBed.inject(HttpTestingController);
  });

  // AgentsStore polls GET /agents on a timer — drain whatever is still open, then verify.
  afterEach(() => {
    http.match(() => true).forEach(request => request.flush([]));
    http.verify();
  });

  function create(): ComponentFixture<ArchitectModal> {
    const fixture = TestBed.createComponent(ArchitectModal);
    fixture.detectChanges();
    return fixture;
  }

  function buildButton(fixture: ComponentFixture<ArchitectModal>): HTMLButtonElement {
    return fixture.nativeElement.querySelector('button.btn-primary') as HTMLButtonElement;
  }

  it('disables the build button until the description is long enough', () => {
    const fixture = create();
    expect(buildButton(fixture).disabled).toBeTrue();

    fixture.componentInstance.description.set('too short');
    fixture.detectChanges();
    expect(buildButton(fixture).disabled).toBeTrue();

    fixture.componentInstance.description.set('A nightly docs-sync loop gated by the unit tests.');
    fixture.detectChanges();
    expect(buildButton(fixture).disabled).toBeFalse();
  });

  it('renders the created items on success and refreshes the workbench stores', () => {
    const fixture = create();
    fixture.componentInstance.description.set('A nightly loop that keeps the docs in sync with the repo.');
    fixture.detectChanges();

    buildButton(fixture).click();

    const build = http.expectOne(`${API_BASE}/architect/build`);
    expect(build.request.method).toBe('POST');
    expect(build.request.body).toEqual({ description: 'A nightly loop that keeps the docs in sync with the repo.' });
    build.flush(architectResult());

    // Success refreshes the workbench behind the modal before the result is shown.
    http.expectOne(`${API_BASE}/agents`).flush([]);
    http.expectOne(`${API_BASE}/resources`).flush([]);
    http.expectOne(`${API_BASE}/resource-types`).flush([]);

    fixture.detectChanges();
    const text = (fixture.nativeElement as HTMLElement).textContent ?? '';
    expect(text).toContain('Created resources');
    expect(text).toContain('Unit tests gate');
    expect(text).toContain('Created agents');
    expect(text).toContain('Docs sync agent');
    expect(text).toContain('Build cost: $1.2345');
    expect(text).toContain('Review everything, fill in any placeholder credentials');
  });

  it('shows the error strip when the architect fails', () => {
    const fixture = create();
    fixture.componentInstance.description.set('A build that is doomed to fail, for testing.');
    fixture.detectChanges();

    buildButton(fixture).click();
    http.expectOne(`${API_BASE}/architect/build`).flush(
      architectResult({
        success: false,
        report: '',
        costUsd: 0,
        createdResources: [],
        createdAgents: [],
        error: 'The architect ran out of budget before wiring the agents.',
      }),
    );
    fixture.detectChanges();

    const strip = fixture.nativeElement.querySelector('.error-strip') as HTMLElement | null;
    expect(strip?.textContent).toContain('ran out of budget');
    // A failed build does not refresh the stores.
    expect(http.match(`${API_BASE}/resources`).length).toBe(0);
    expect(http.match(`${API_BASE}/resource-types`).length).toBe(0);
  });
});
