import { HttpClient, HttpParams } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable } from 'rxjs';
import {
  AgentBreakdownDto,
  ArchitectureMapDto,
  AgentDetailDto,
  AgentSummaryDto,
  ClaudeInstallResultDto,
  ClaudeStatusDto,
  DeliveryMetricsDto,
  PullRequestDto,
  RegisterPrBody,
  UpdatePrBody,
  CostSeriesPointDto,
  DashboardSummaryDto,
  DirectoryListingDto,
  GeneratedResourceTypeDto,
  ModelUsageDto,
  RecentFailureDto,
  ResourceDto,
  ResourceType,
  ResourceTypeDto,
  RunDetailDto,
  RunSummaryDto,
  SaveAgentRequest,
} from './models';

export const API_BASE = 'http://localhost:5210/api';

@Injectable({ providedIn: 'root' })
export class ApiService {
  private readonly http = inject(HttpClient);

  // ---------- Resources ----------

  getResources(type?: ResourceType): Observable<ResourceDto[]> {
    const params = type ? new HttpParams().set('type', type) : undefined;
    return this.http.get<ResourceDto[]>(`${API_BASE}/resources`, { params });
  }

  createResource(body: {
    name: string;
    type: ResourceType;
    customTypeKey?: string | null;
    description: string;
    configJson: string;
  }): Observable<ResourceDto> {
    return this.http.post<ResourceDto>(`${API_BASE}/resources`, body);
  }

  // ---------- Resource types (dynamic modules) ----------

  getResourceTypes(): Observable<ResourceTypeDto[]> {
    return this.http.get<ResourceTypeDto[]>(`${API_BASE}/resource-types`);
  }

  /** Long-running: Claude writes and compiles the module (can take minutes). */
  generateResourceType(description: string): Observable<GeneratedResourceTypeDto> {
    return this.http.post<GeneratedResourceTypeDto>(`${API_BASE}/resource-types/generate`, { description });
  }

  createResourceTypeFromSource(sourceCode: string): Observable<GeneratedResourceTypeDto> {
    return this.http.post<GeneratedResourceTypeDto>(`${API_BASE}/resource-types`, { sourceCode });
  }

  deleteResourceType(typeKey: string): Observable<void> {
    return this.http.delete<void>(`${API_BASE}/resource-types/${typeKey}`);
  }

  // ---------- Architecture map ----------

  getArchitectureMap(): Observable<ArchitectureMapDto> {
    return this.http.get<ArchitectureMapDto>(`${API_BASE}/architecture/map`);
  }

  // ---------- Delivery: PRs, escalations, and the five metrics ----------

  getDeliveryMetrics(days: number): Observable<DeliveryMetricsDto> {
    return this.http.get<DeliveryMetricsDto>(`${API_BASE}/delivery/metrics`, { params: new HttpParams().set('days', days) });
  }

  getPullRequests(days: number, agentId?: string): Observable<PullRequestDto[]> {
    let params = new HttpParams().set('days', days);
    if (agentId) params = params.set('agentId', agentId);
    return this.http.get<PullRequestDto[]>(`${API_BASE}/delivery/prs`, { params });
  }

  registerPullRequest(body: RegisterPrBody): Observable<PullRequestDto> {
    return this.http.post<PullRequestDto>(`${API_BASE}/delivery/prs`, body);
  }

  updatePullRequest(id: string, body: UpdatePrBody): Observable<PullRequestDto> {
    return this.http.put<PullRequestDto>(`${API_BASE}/delivery/prs/${id}`, body);
  }

  deletePullRequest(id: string): Observable<void> {
    return this.http.delete<void>(`${API_BASE}/delivery/prs/${id}`);
  }

  /** On-demand GitHub/survival refresh of one PR. */
  syncPullRequest(id: string): Observable<PullRequestDto> {
    return this.http.post<PullRequestDto>(`${API_BASE}/delivery/prs/${id}/sync`, {});
  }

  escalateRun(runId: string, reason: string): Observable<void> {
    return this.http.post<void>(`${API_BASE}/runs/${runId}/escalate`, { reason });
  }

  // ---------- Claude CLI status ----------

  getClaudeStatus(refresh = false): Observable<ClaudeStatusDto> {
    const params = refresh ? new HttpParams().set('refresh', true) : undefined;
    return this.http.get<ClaudeStatusDto>(`${API_BASE}/system/claude-status`, { params });
  }

  /** Long-running: runs the official Claude Code installer on the API machine. */
  installClaude(): Observable<ClaudeInstallResultDto> {
    return this.http.post<ClaudeInstallResultDto>(`${API_BASE}/system/install-claude`, {});
  }

  // ---------- Folder browsing ----------

  /** Lists sub-directories of a path on the machine running the API. Omit path for the home folder. */
  browseDirectories(path?: string | null): Observable<DirectoryListingDto> {
    const params = path ? new HttpParams().set('path', path) : undefined;
    return this.http.get<DirectoryListingDto>(`${API_BASE}/filesystem/directories`, { params });
  }

  updateResource(id: string, body: { name: string; description: string; configJson: string }): Observable<ResourceDto> {
    return this.http.put<ResourceDto>(`${API_BASE}/resources/${id}`, body);
  }

  deleteResource(id: string): Observable<void> {
    return this.http.delete<void>(`${API_BASE}/resources/${id}`);
  }

  // ---------- Agents ----------

  getAgents(): Observable<AgentSummaryDto[]> {
    return this.http.get<AgentSummaryDto[]>(`${API_BASE}/agents`);
  }

  getAgent(id: string): Observable<AgentDetailDto> {
    return this.http.get<AgentDetailDto>(`${API_BASE}/agents/${id}`);
  }

  createAgent(body: SaveAgentRequest): Observable<AgentDetailDto> {
    return this.http.post<AgentDetailDto>(`${API_BASE}/agents`, body);
  }

  updateAgent(id: string, body: SaveAgentRequest): Observable<AgentDetailDto> {
    return this.http.put<AgentDetailDto>(`${API_BASE}/agents/${id}`, body);
  }

  deleteAgent(id: string): Observable<void> {
    return this.http.delete<void>(`${API_BASE}/agents/${id}`);
  }

  setAgentEnabled(id: string, enabled: boolean): Observable<AgentSummaryDto> {
    return this.http.post<AgentSummaryDto>(`${API_BASE}/agents/${id}/enabled`, { enabled });
  }

  runAgentNow(id: string): Observable<{ runId: string }> {
    return this.http.post<{ runId: string }>(`${API_BASE}/agents/${id}/run`, {});
  }

  cancelAgentRun(id: string): Observable<void> {
    return this.http.post<void>(`${API_BASE}/agents/${id}/cancel`, {});
  }

  // ---------- Runs ----------

  getAgentRuns(agentId: string, take = 50, skip = 0): Observable<RunSummaryDto[]> {
    const params = new HttpParams().set('take', take).set('skip', skip);
    return this.http.get<RunSummaryDto[]>(`${API_BASE}/agents/${agentId}/runs`, { params });
  }

  getRun(id: string): Observable<RunDetailDto> {
    return this.http.get<RunDetailDto>(`${API_BASE}/runs/${id}`);
  }

  // ---------- Dashboard ----------

  getDashboardSummary(days: number): Observable<DashboardSummaryDto> {
    return this.http.get<DashboardSummaryDto>(`${API_BASE}/dashboard/summary`, { params: new HttpParams().set('days', days) });
  }

  getCostSeries(days: number): Observable<CostSeriesPointDto[]> {
    return this.http.get<CostSeriesPointDto[]>(`${API_BASE}/dashboard/cost-series`, { params: new HttpParams().set('days', days) });
  }

  getAgentBreakdown(days: number): Observable<AgentBreakdownDto[]> {
    return this.http.get<AgentBreakdownDto[]>(`${API_BASE}/dashboard/agent-breakdown`, { params: new HttpParams().set('days', days) });
  }

  getModelUsage(days: number): Observable<ModelUsageDto[]> {
    return this.http.get<ModelUsageDto[]>(`${API_BASE}/dashboard/model-usage`, { params: new HttpParams().set('days', days) });
  }

  getRecentFailures(take = 8): Observable<RecentFailureDto[]> {
    return this.http.get<RecentFailureDto[]>(`${API_BASE}/dashboard/recent-failures`, { params: new HttpParams().set('take', take) });
  }
}
