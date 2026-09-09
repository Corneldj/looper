import { Routes } from '@angular/router';

export const routes: Routes = [
  { path: '', pathMatch: 'full', redirectTo: 'workbench' },
  {
    path: 'workbench',
    loadComponent: () => import('./features/workbench/workbench').then(m => m.Workbench),
    title: 'Looper — Workbench',
  },
  // The map and the workbench are one page now; old links keep working.
  { path: 'map', redirectTo: 'workbench' },
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
