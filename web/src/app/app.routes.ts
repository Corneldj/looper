import { Routes } from '@angular/router';

export const routes: Routes = [
  { path: '', pathMatch: 'full', redirectTo: 'workbench' },
  {
    path: 'workbench',
    loadComponent: () => import('./features/workbench/workbench').then(m => m.Workbench),
    title: 'Looper — Workbench',
  },
  {
    path: 'map',
    loadComponent: () => import('./features/architecture-map/architecture-map').then(m => m.ArchitectureMap),
    title: 'Looper — Map',
  },
  {
    path: 'dashboard',
    loadComponent: () => import('./features/dashboard/dashboard').then(m => m.Dashboard),
    title: 'Looper — Dashboard',
  },
  {
    path: 'agents/:id',
    loadComponent: () => import('./features/agent-detail/agent-detail').then(m => m.AgentDetail),
    title: 'Looper — Agent',
  },
  { path: '**', redirectTo: 'workbench' },
];
