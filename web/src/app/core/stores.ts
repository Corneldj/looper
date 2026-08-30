import { Injectable, computed, inject, signal } from '@angular/core';
import { catchError, of, timer, switchMap } from 'rxjs';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { ApiService } from './api.service';
import { AgentSummaryDto, ClaudeStatusDto, GraphStatusDto, RESOURCE_TYPES, ResourceDto, ResourceTypeDto, UserActionDto } from './models';

/** Holds the resource list shared by the workbench panels and agent editor. */
@Injectable({ providedIn: 'root' })
export class ResourcesStore {
  private readonly api = inject(ApiService);

  readonly resources = signal<ResourceDto[]>([]);
  /** Graph resources as infrastructure: curator wiring + last measured health, by resource id. */
  readonly graphs = signal<GraphStatusDto[]>([]);
  readonly loaded = signal(false);
  /** True when the last load failed — the panel shows a retry state instead of "no resources". */
  readonly failed = signal(false);

  load(): void {
    this.loadGraphs();
    this.api.getResources().subscribe({
      next: resources => {
        this.resources.set(resources);
        this.loaded.set(true);
        this.failed.set(false);
      },
      error: () => {
        this.loaded.set(true);
        this.failed.set(true);
      },
    });
  }

  loadGraphs(): void {
    this.api.getGraphs().subscribe({
      next: graphs => this.graphs.set(graphs),
      error: () => { /* health is decoration — the panel works without it */ },
    });
  }

  upsert(resource: ResourceDto): void {
    this.resources.update(list => {
      const index = list.findIndex(r => r.id === resource.id);
      if (index < 0) return [...list, resource];
      const next = [...list];
      next[index] = resource;
      return next;
    });
  }

  remove(id: string): void {
    this.resources.update(list => list.filter(r => r.id !== id));
  }

  // ---------- Resource-type catalog (built-ins + dynamic modules) ----------

  /** Falls back to the static built-in catalog until the API answers. */
  readonly types = signal<ResourceTypeDto[]>(
    RESOURCE_TYPES.map(t => ({
      typeKey: t.type, label: t.label, icon: t.icon, blurb: t.blurb, builtIn: true, fields: null,
    })),
  );
  readonly typesLoaded = signal(false);

  loadTypes(): void {
    this.api.getResourceTypes().subscribe({
      next: types => {
        this.types.set(types);
        this.typesLoaded.set(true);
      },
      error: () => this.typesLoaded.set(true),
    });
  }

  addType(type: ResourceTypeDto): void {
    this.types.update(list => [...list.filter(t => t.typeKey !== type.typeKey), type]);
  }

  removeType(typeKey: string): void {
    this.types.update(list => list.filter(t => t.typeKey !== typeKey));
  }

  /** Icon/label metadata for a resource, resolving dynamic types through the catalog. */
  typeMeta(resource: ResourceDto): { icon: string; label: string } {
    const key = resource.type === 'Custom' ? resource.customTypeKey : resource.type;
    const entry = this.types().find(t => t.typeKey === key);
    return entry ?? { icon: '🧩', label: resource.customTypeKey ?? resource.type };
  }
}

/**
 * Polls the agent list so run state, costs and schedules stay live everywhere
 * (workbench cards, navbar activity chip, detail header).
 */
@Injectable({ providedIn: 'root' })
export class AgentsStore {
  private readonly api = inject(ApiService);

  readonly agents = signal<AgentSummaryDto[]>([]);
  readonly loaded = signal(false);
  readonly apiOnline = signal(true);

  readonly runningCount = computed(() => this.agents().filter(a => a.isRunning).length);
  readonly enabledCount = computed(() => this.agents().filter(a => a.enabled).length);

  constructor() {
    timer(0, 5_000)
      .pipe(
        switchMap(() => this.api.getAgents().pipe(catchError(() => of(null)))),
        takeUntilDestroyed(),
      )
      .subscribe(agents => {
        if (agents === null) {
          this.apiOnline.set(false);
          return;
        }
        this.apiOnline.set(true);
        this.agents.set(agents);
        this.loaded.set(true);
      });
  }

  refreshNow(): void {
    this.api.getAgents().subscribe({
      next: agents => {
        this.agents.set(agents);
        this.loaded.set(true);
        this.apiOnline.set(true);
      },
      error: () => this.apiOnline.set(false),
    });
  }
}

/**
 * Tracks whether the Claude Code CLI is available on the API machine. Real agent runs
 * need it; the shell shows a setup banner while it's missing.
 */
@Injectable({ providedIn: 'root' })
export class ClaudeStatusStore {
  private readonly api = inject(ApiService);

  readonly status = signal<ClaudeStatusDto | null>(null);
  readonly loaded = signal(false);
  readonly checking = signal(false);
  readonly installing = signal(false);
  readonly installOutput = signal<string | null>(null);
  readonly installError = signal<string | null>(null);

  readonly missing = computed(() => this.loaded() && this.status()?.available === false);

  constructor() {
    this.check();
  }

  check(refresh = false): void {
    this.checking.set(true);
    this.api.getClaudeStatus(refresh).subscribe({
      next: status => {
        this.status.set(status);
        this.loaded.set(true);
        this.checking.set(false);
      },
      // API down: stay quiet — the offline banner already covers that state.
      error: () => this.checking.set(false),
    });
  }

  install(): void {
    if (this.installing()) return;
    this.installing.set(true);
    this.installOutput.set(null);
    this.installError.set(null);
    this.api.installClaude().subscribe({
      next: result => {
        this.installing.set(false);
        this.installOutput.set(result.output);
        this.status.set(result.status);
        this.loaded.set(true);
        if (!result.success) {
          this.installError.set('The installer finished but Claude Code still isn’t runnable — see the output below.');
        }
      },
      error: () => {
        this.installing.set(false);
        this.installError.set('The installer couldn’t be started — is the API running?');
      },
    });
  }
}

/**
 * Polls open User Action Requests so "waiting on you" surfaces everywhere:
 * the topbar, the agent cards, and the agent detail panel.
 */
@Injectable({ providedIn: 'root' })
export class UserActionsStore {
  private readonly api = inject(ApiService);

  readonly open = signal<UserActionDto[]>([]);
  readonly loaded = signal(false);

  readonly openCount = computed(() => this.open().length);

  constructor() {
    timer(0, 10_000)
      .pipe(
        switchMap(() => this.api.getUserActions().pipe(catchError(() => of(null)))),
        takeUntilDestroyed(),
      )
      .subscribe(list => {
        if (list === null) return;
        this.open.set(list);
        this.loaded.set(true);
      });
  }

  refreshNow(): void {
    this.api.getUserActions().subscribe({
      next: list => {
        this.open.set(list);
        this.loaded.set(true);
      },
      error: () => {},
    });
  }

  forAgent(agentId: string): UserActionDto[] {
    return this.open().filter(r => r.agentId === agentId);
  }
}
