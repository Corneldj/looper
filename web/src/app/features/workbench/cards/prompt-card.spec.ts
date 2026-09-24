import { ComponentFixture, TestBed, fakeAsync, tick } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { API_BASE } from '../../../core/api.service';
import { MapResourceDto, ResourceDto } from '../../../core/models';
import { resource as mapResource } from '../map-layout.spec';
import { PromptCard } from './prompt-card';

function prompt(text: string): MapResourceDto {
  return mapResource({
    id: 'r1', name: 'Next run', type: 'Custom', customTypeKey: 'OneOffPrompt', typeLabel: 'One-off prompt',
    card: { text, issueKey: null, issueSummary: null, connected: true },
  });
}

const savedDto = { id: 'r1', name: 'Next run', type: 'Custom', customTypeKey: 'OneOffPrompt', configJson: '{}' } as ResourceDto;

describe('PromptCard', () => {
  let http: HttpTestingController;
  let saved: ResourceDto[];

  beforeEach(async () => {
    await TestBed.configureTestingModule({
      imports: [PromptCard],
      providers: [provideHttpClient(), provideHttpClientTesting()],
    }).compileComponents();
    http = TestBed.inject(HttpTestingController);
  });

  afterEach(() => http.verify());

  function open(text: string): ComponentFixture<PromptCard> {
    const fixture = TestBed.createComponent(PromptCard);
    fixture.componentRef.setInput('resource', prompt(text));
    saved = [];
    fixture.componentInstance.saved.subscribe(dto => saved.push(dto));
    fixture.detectChanges();
    tick();                                                                     // ngModel writes the box a microtask later
    fixture.detectChanges();
    return fixture;
  }

  const box = (fixture: ComponentFixture<PromptCard>) => (fixture.nativeElement as HTMLElement).querySelector('textarea')!;
  const status = (fixture: ComponentFixture<PromptCard>) =>
    (fixture.nativeElement as HTMLElement).querySelector('.prompt-status')!.textContent!.trim();

  it('shows what waits for the next run and saves the text alone once typing pauses', fakeAsync(() => {
    const fixture = open('Old note.');
    expect(box(fixture).value).toBe('Old note.');
    expect(status(fixture)).toContain('Waiting for the next run');

    fixture.componentInstance.onFocus();
    fixture.componentInstance.onInput('Use the new pricing API.');
    fixture.detectChanges();
    expect(status(fixture)).toBe('Saving…');
    tick(PromptCard.saveDelayMs - 1);
    http.expectNone(`${API_BASE}/boards/prompts/r1`);
    tick(1);

    const request = http.expectOne(`${API_BASE}/boards/prompts/r1`);
    expect(request.request.method).toBe('PUT');
    expect(request.request.body).toEqual({ text: 'Use the new pricing API.' });
    request.flush(savedDto);
    fixture.detectChanges();
    expect(saved).toEqual([savedDto]);
    expect(status(fixture)).toContain('Waiting for the next run');
  }));

  it('follows the server while nobody edits — a run taking the prompt empties the box — but keeps a draft being typed', fakeAsync(() => {
    const fixture = open('Old note.');

    fixture.componentRef.setInput('resource', prompt(''));                    // a run took it
    fixture.detectChanges();
    tick();
    fixture.detectChanges();
    expect(box(fixture).value).toBe('');
    expect(status(fixture)).toBe('Nothing waiting for the next run.');

    fixture.componentInstance.onFocus();
    fixture.componentInstance.onInput('Half-typed');
    fixture.componentRef.setInput('resource', prompt('Something else'));      // a poll mid-edit
    fixture.detectChanges();
    tick();
    fixture.detectChanges();
    expect(box(fixture).value).toBe('Half-typed');

    tick(PromptCard.saveDelayMs);
    http.expectOne(`${API_BASE}/boards/prompts/r1`).flush(savedDto);
  }));

  it('saves at once when the box is left, and says why a save was refused', fakeAsync(() => {
    const fixture = open('');

    fixture.componentInstance.onFocus();
    fixture.componentInstance.onInput('x'.repeat(10));
    fixture.componentInstance.onBlur();
    http.expectOne(`${API_BASE}/boards/prompts/r1`).flush(
      { title: 'Keep the instructions under 8,000 characters — they travel on the run\'s command line.' },
      { status: 400, statusText: 'Bad Request' },
    );
    fixture.detectChanges();

    expect(status(fixture)).toContain('Keep the instructions under 8,000 characters');
    expect(saved).toEqual([]);
    tick(PromptCard.saveDelayMs);
  }));
});
