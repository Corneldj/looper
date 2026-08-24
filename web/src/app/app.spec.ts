import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { provideRouter } from '@angular/router';
import { API_BASE } from './core/api.service';
import { App } from './app';

describe('App', () => {
  beforeEach(async () => {
    await TestBed.configureTestingModule({
      imports: [App],
      providers: [provideHttpClient(), provideHttpClientTesting(), provideRouter([])],
    }).compileComponents();
  });

  it('creates the shell', () => {
    const fixture = TestBed.createComponent(App);
    expect(fixture.componentInstance).toBeTruthy();
  });

  it('renders the brand and navigation', () => {
    const fixture = TestBed.createComponent(App);
    fixture.detectChanges();
    const element: HTMLElement = fixture.nativeElement;
    expect(element.querySelector('.brand-name')?.textContent).toContain('Looper');
    const navLinks = [...element.querySelectorAll('.nav a')].map(a => a.textContent?.trim());
    expect(navLinks).toEqual(['Workbench', 'Dashboard']);
  });

  it('shows the setup banner only while Claude Code is missing', () => {
    const http = TestBed.inject(HttpTestingController);
    const fixture = TestBed.createComponent(App);
    fixture.detectChanges();
    const element: HTMLElement = fixture.nativeElement;

    // apiOnline defaults to true (the agents poll fires on an async timer); the probe reports the CLI missing.
    http.expectOne(`${API_BASE}/system/claude-status`)
      .flush({ available: false, version: null, command: 'claude', error: 'No such file or directory' });
    fixture.detectChanges();

    expect(element.querySelector('.claude-banner')?.textContent).toContain('Claude Code isn’t installed');

    // Opening the setup flow from the banner shows the modal with install guidance.
    (element.querySelector('.banner-btn') as HTMLButtonElement).click();
    fixture.detectChanges();
    expect(element.querySelector('app-claude-setup')?.textContent).toContain('Install Claude Code');
  });

  it('hides the banner when Claude Code is available', () => {
    const http = TestBed.inject(HttpTestingController);
    const fixture = TestBed.createComponent(App);
    fixture.detectChanges();

    http.expectOne(`${API_BASE}/system/claude-status`)
      .flush({ available: true, version: '2.1.0 (Claude Code)', command: 'claude', error: null });
    fixture.detectChanges();

    expect((fixture.nativeElement as HTMLElement).querySelector('.claude-banner')).toBeNull();
  });
});
