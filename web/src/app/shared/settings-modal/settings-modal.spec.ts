import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { API_BASE } from '../../core/api.service';
import { SettingsStore } from '../../core/stores';
import { SettingsDto } from '../../core/models';
import { SettingsModal } from './settings-modal';

function settings(overrides: Partial<SettingsDto> = {}): SettingsDto {
  return { claudeAuthMode: 'Subscription', hasApiKey: false, apiKeyHint: null, updatedAtUtc: null, ...overrides };
}

describe('SettingsModal', () => {
  let http: HttpTestingController;

  beforeEach(async () => {
    await TestBed.configureTestingModule({
      imports: [SettingsModal],
      providers: [provideHttpClient(), provideHttpClientTesting()],
    }).compileComponents();
    http = TestBed.inject(HttpTestingController);
  });

  afterEach(() => http.verify());

  /** Opens the modal and answers both settings loads (the store's own and the modal's). */
  function open(current: SettingsDto): { fixture: ComponentFixture<SettingsModal>; closed: number } {
    const fixture = TestBed.createComponent(SettingsModal);
    const state = { fixture, closed: 0 };
    fixture.componentInstance.closed.subscribe(() => state.closed++);
    fixture.detectChanges();
    http.match(`${API_BASE}/settings`).forEach(r => r.flush(current));
    fixture.detectChanges();
    return state;
  }

  function radio(element: HTMLElement, value: string): HTMLInputElement {
    return element.querySelector(`input[type=radio][value=${value}]`) as HTMLInputElement;
  }

  it('starts on the subscription with no key field, and needs a key before API-key mode can be saved', () => {
    const { fixture } = open(settings());
    const element: HTMLElement = fixture.nativeElement;
    expect(radio(element, 'Subscription').checked).toBeTrue();
    expect(element.querySelector('input[type=password]')).toBeNull();

    radio(element, 'ApiKey').dispatchEvent(new Event('change'));
    fixture.detectChanges();
    expect(element.querySelector('input[type=password]')).withContext('key field appears').toBeTruthy();
    expect((element.querySelector('.btn-primary') as HTMLButtonElement).disabled).withContext('no key yet').toBeTrue();

    const input = element.querySelector('input[type=password]') as HTMLInputElement;
    input.value = '  sk-ant-api03-brand-new-key-0000zzzz ';
    input.dispatchEvent(new Event('input'));
    fixture.detectChanges();
    (element.querySelector('.btn-primary') as HTMLButtonElement).click();

    const put = http.expectOne(r => r.method === 'PUT' && r.url === `${API_BASE}/settings`);
    expect(put.request.body).toEqual({ claudeAuthMode: 'ApiKey', apiKey: 'sk-ant-api03-brand-new-key-0000zzzz', clearApiKey: false });
    put.flush(settings({ claudeAuthMode: 'ApiKey', hasApiKey: true, apiKeyHint: '…zzzz' }));
    fixture.detectChanges();

    expect(TestBed.inject(SettingsStore).usingApiKey()).toBeTrue();
  });

  it('keeps a stored key unless replaced, and can remove it when switching back', () => {
    const { fixture } = open(settings({ claudeAuthMode: 'ApiKey', hasApiKey: true, apiKeyHint: '…abcd' }));
    const element: HTMLElement = fixture.nativeElement;
    expect(element.querySelector('.stored-key')?.textContent).toContain('sk-ant-…abcd');
    expect(element.querySelector('input[type=password]')).withContext('stored key shown, not an input').toBeNull();

    // Saving with nothing touched sends no key: the stored one stays.
    (element.querySelector('.btn-primary') as HTMLButtonElement).click();
    const untouched = http.expectOne(r => r.method === 'PUT' && r.url === `${API_BASE}/settings`);
    expect(untouched.request.body).toEqual({ claudeAuthMode: 'ApiKey', apiKey: null, clearApiKey: false });
    untouched.flush(settings({ claudeAuthMode: 'ApiKey', hasApiKey: true, apiKeyHint: '…abcd' }));
    fixture.detectChanges();

    // Switch to the subscription and remove the key.
    radio(element, 'Subscription').dispatchEvent(new Event('change'));
    fixture.detectChanges();
    (element.querySelector('.remove-key') as HTMLButtonElement).click();
    fixture.detectChanges();
    expect(element.querySelector('.stored-key')?.textContent).toContain('removed when you save');
    (element.querySelector('.btn-primary') as HTMLButtonElement).click();

    const cleared = http.expectOne(r => r.method === 'PUT' && r.url === `${API_BASE}/settings`);
    expect(cleared.request.body).toEqual({ claudeAuthMode: 'Subscription', apiKey: null, clearApiKey: true });
    cleared.flush(settings());
    fixture.detectChanges();
    expect(TestBed.inject(SettingsStore).usingApiKey()).toBeFalse();
  });

  it('shows the server’s reason when a save is refused', () => {
    const { fixture, closed } = open(settings({ hasApiKey: true, apiKeyHint: '…abcd' }));
    const element: HTMLElement = fixture.nativeElement;
    radio(element, 'ApiKey').dispatchEvent(new Event('change'));
    fixture.detectChanges();
    (element.querySelector('.btn-primary') as HTMLButtonElement).click();

    http.expectOne(r => r.method === 'PUT' && r.url === `${API_BASE}/settings`)
      .flush({ title: 'Paste an Anthropic API key to use API-key billing.' }, { status: 400, statusText: 'Bad Request' });
    fixture.detectChanges();

    expect(element.querySelector('.save-error')?.textContent).toContain('Paste an Anthropic API key');
    expect(closed).toBe(0);
  });
});
