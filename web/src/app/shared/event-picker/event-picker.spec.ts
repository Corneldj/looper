import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { API_BASE } from '../../core/api.service';
import { EventTopicDto } from '../../core/models';
import { EventPicker } from './event-picker';

const catalog: EventTopicDto[] = [
  { topic: 'agent.docs-gardener.succeeded', kind: 'completion', source: 'Docs gardener succeeds', isPattern: false },
  { topic: 'newsletter.sent', kind: 'raiser', source: 'Newsletter sent · raised by Campaign loop', isPattern: false },
  { topic: 'newsletter.*', kind: 'listener', source: 'On newsletter listens', isPattern: true },
];

describe('EventPicker', () => {
  let http: HttpTestingController;

  beforeEach(async () => {
    await TestBed.configureTestingModule({
      imports: [EventPicker],
      providers: [provideHttpClient(), provideHttpClientTesting()],
    }).compileComponents();
    http = TestBed.inject(HttpTestingController);
  });

  afterEach(() => http.verify());

  function create(inputs: { value?: string; multiple?: boolean; allowPatterns?: boolean }): { fixture: ComponentFixture<EventPicker>; emitted: string[] } {
    const fixture = TestBed.createComponent(EventPicker);
    fixture.componentRef.setInput('value', inputs.value ?? '');
    fixture.componentRef.setInput('multiple', inputs.multiple ?? false);
    fixture.componentRef.setInput('allowPatterns', inputs.allowPatterns ?? false);
    const emitted: string[] = [];
    fixture.componentInstance.valueChange.subscribe(v => emitted.push(v));
    fixture.detectChanges();
    http.expectOne(`${API_BASE}/events/topics`).flush(catalog);
    fixture.detectChanges();
    return { fixture, emitted };
  }

  it('lists known events grouped by origin and hides patterns where only topics are allowed', () => {
    const { fixture } = create({});
    const element: HTMLElement = fixture.nativeElement;
    const labels = [...element.querySelectorAll('.group-label')].map(l => l.textContent?.trim());
    expect(labels).toEqual(['Agent finishes', 'Raised by a resource']);
    expect([...element.querySelectorAll('.option')].map(o => o.textContent?.trim())).not.toContain('newsletter.*');
  });

  it('picks one topic in single mode and toggles many in multiple mode', () => {
    const single = create({});
    (single.fixture.nativeElement as HTMLElement).querySelectorAll<HTMLButtonElement>('.option')[1].click();
    expect(single.emitted).toEqual(['newsletter.sent']);

    const multi = create({ value: 'newsletter.sent', multiple: true, allowPatterns: true });
    const element: HTMLElement = multi.fixture.nativeElement;
    expect(element.querySelector('.picked')?.textContent).toContain('newsletter.sent');
    const options = element.querySelectorAll<HTMLButtonElement>('.option');
    options[0].click(); // agent.docs-gardener.succeeded
    expect(multi.emitted.at(-1)).toBe('newsletter.sent\nagent.docs-gardener.succeeded');
    expect([...options].map(o => o.textContent?.trim())).toContain('newsletter.*');
  });

  it('creates a new topic with the API rule and refuses a malformed one', () => {
    const { fixture, emitted } = create({ allowPatterns: true });
    const element: HTMLElement = fixture.nativeElement;
    const input = element.querySelector('.custom input') as HTMLInputElement;
    const add = element.querySelector('.custom .btn') as HTMLButtonElement;

    input.value = 'Bad Topic';
    input.dispatchEvent(new Event('input'));
    fixture.detectChanges();
    expect(add.disabled).toBeTrue();
    expect(element.querySelector('.hint.invalid')).toBeTruthy();

    input.value = 'campaign.launched';
    input.dispatchEvent(new Event('input'));
    fixture.detectChanges();
    expect(add.disabled).toBeFalse();
    expect(add.textContent?.trim()).toBe('Create');
    add.click();
    expect(emitted).toEqual(['campaign.launched']);
  });
});
