import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { provideRouter } from '@angular/router';
import { AgentsStore } from '../../../core/stores';
import { AgentSummaryDto } from '../../../core/models';
import { AgentPanel } from './agent-panel';

const fakeAgent: AgentSummaryDto = {
  id: 'agent-1',
  name: 'Nightly refactorer',
  description: 'Keeps the codebase tidy while everyone sleeps.',
  model: 'claude-opus-5',
  effort: 'High',
  intervalMinutes: 60,
  triggerMode: 'Scheduled',
  triggerTopics: null,
  enabled: true,
  dryRun: false,
  autonomyLevel: 3,
  isRunning: false,
  resourceCount: 2,
  lastRunAtUtc: null,
  nextRunAtUtc: null,
  lastRunStatus: null,
  runsLast24h: 0,
  costLast24hUsd: 0,
};

describe('AgentPanel', () => {
  beforeEach(async () => {
    await TestBed.configureTestingModule({
      imports: [AgentPanel],
      providers: [provideHttpClient(), provideHttpClientTesting(), provideRouter([])],
    }).compileComponents();
  });

  it('renders the agent card with its autonomy chip', () => {
    // The store's /agents poll is timer-based; seed it directly instead of flushing HTTP.
    const store = TestBed.inject(AgentsStore);
    store.agents.set([fakeAgent]);
    store.loaded.set(true);

    const fixture = TestBed.createComponent(AgentPanel);
    fixture.detectChanges();
    const element: HTMLElement = fixture.nativeElement;

    expect(element.querySelector('.agent-card h3')?.textContent).toContain('Nightly refactorer');

    const chip = [...element.querySelectorAll('.agent-meta .chip')]
      .find(c => c.textContent?.trim() === 'L3');
    expect(chip).withContext('autonomy chip in the meta line').toBeTruthy();
    expect(chip?.getAttribute('title')).toContain('review after the fact');
  });
});
