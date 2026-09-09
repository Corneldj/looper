import { HttpClient, HttpParams } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable } from 'rxjs';
import {
  AgentBreakdownDto,
  AgentDetailDto,
  AgentSummaryDto,
  ArchitectResultDto,
  ArchitectureMapDto,
  ClaudeInstallResultDto,
  ClaudeStatusDto,
  CostSeriesPointDto,
  DashboardSummaryDto,
  DeliveryMetricsDto,
  DirectoryListingDto,
  EventTopicDto,
  GeneratedResourceTypeDto,
  GenerationJobDto,
  GraphStatusDto,
  MetricSummaryDto,
  MetricValueDto,
  ModelUsageDto,
  PullRequestDto,
  RecentFailureDto,
  RecordAs,
  RecordMetricBody,
  RegisterPrBody,
  ResourceDto,
  ResourceType,
  ResourceTypeDto,
  RunDetailDto,
  RunSummaryDto,
  SaveAgentRequest,
  SaveWorkflowRequest,
  ScriptAssistRequest,
  ScriptAssistResultDto,
  ScriptRunRequest,
  ScriptRunResultDto,
  SettingsDto,
  UpdatePrBody,
  UpdateSettingsRequest,
  UserActionDto,
  VersionDto,
  WorkflowDto,
  WorkflowImportResultDto,
  WorkflowPackageSummaryDto,
  WorkspaceClaimDto,
  WorkspaceDto,
} from './models';

export const API_BASE = 'http://localhost:5210/api';

@Injectable({ providedIn: 'root' })
export class ApiService {
  private readonly http = inject(HttpClient);

  // ---------- Resources ----------

  getResources(type?: ResourceType, workflowId?: string | null): Observable<ResourceDto[]> {
    let params = new HttpParams();
    if (type) params = params.set('type', type);
    if (workflowId) params = params.set('workflowId', workflowId);
    return this.http.get<ResourceDto[]>(`${API_BASE}/resources`, { params });
  }

  createResource(body: {
    name: string;
    type: ResourceType;
    customTypeKey?: string | null;
    description: string;
    configJson: string;
    workflowId?: string | null;
  }): Observable<ResourceDto> {
    return this.http.post<ResourceDto>(`${API_BASE}/resources`, body);
  }

  // ---------- Settings ----------

  getSettings(): Observable<SettingsDto> {
    return this.http.get<SettingsDto>(`${API_BASE}/settings`);
  }

  updateSettings(body: UpdateSettingsRequest): Observable<SettingsDto> {
    return this.http.put<SettingsDto>(`${API_BASE}/settings`, body);
  }

  // ---------- Workflows ----------

  getWorkflows(): Observable<WorkflowDto[]> {
    return this.http.get<WorkflowDto[]>(`${API_BASE}/workflows`);
  }

  createWorkflow(body: SaveWorkflowRequest): Observable<WorkflowDto> {
    return this.http.post<WorkflowDto>(`${API_BASE}/workflows`, body);
  }

  updateWorkflow(id: string, body: SaveWorkflowRequest): Observable<WorkflowDto> {
    return this.http.put<WorkflowDto>(`${API_BASE}/workflows/${id}`, body);
  }

  /** Removes the workflow and everything in it — agents, runs, resources, metric values. */
  deleteWorkflow(id: string): Observable<void> {
    return this.http.delete<void>(`${API_BASE}/workflows/${id}`);
  }

  /** The workflow as a .workflow file: resources (secrets stripped), agents, wiring and the dynamic types they need, with their DLLs. */
  exportWorkflowFile(id: string): Observable<Blob> {
    return this.http.get(`${API_BASE}/workflows/${id}/export`, { responseType: 'blob' });
  }

  /** Reads a .workflow file back without creating anything — the import preview. */
  inspectWorkflowFile(file: File): Observable<WorkflowPackageSummaryDto> {
    const form = new FormData();
    form.append('file', file, file.name);
    return this.http.post<WorkflowPackageSummaryDto>(`${API_BASE}/workflows/import/inspect`, form);
  }

  /** Creates a new workflow from a .workflow file, installing missing resource types. All or nothing. */
  importWorkflowFile(file: File, name?: string | null): Observable<WorkflowImportResultDto> {
    const form = new FormData();
    form.append('file', file, file.name);
    if (name) form.append('name', name);
    return this.http.post<WorkflowImportResultDto>(`${API_BASE}/workflows/import/file`, form);
  }

  // ---------- Graph memory infrastructure ----------

  getGraphs(): Observable<GraphStatusDto[]> {
    return this.http.get<GraphStatusDto[]>(`${API_BASE}/graphs`);
  }

  // ---------- Resource types (dynamic modules) ----------

  getResourceTypes(): Observable<ResourceTypeDto[]> {
    return this.http.get<ResourceTypeDto[]>(`${API_BASE}/resource-types`);
  }

  /** Long-running: Claude writes and compiles the module (can take minutes). Prefer the job endpoints from the UI. */
  generateResourceType(description: string): Observable<GeneratedResourceTypeDto> {
    return this.http.post<GeneratedResourceTypeDto>(`${API_BASE}/resource-types/generate`, { description });
  }

  /** Starts generation as a job so the UI can show attempts and cancel it. */
  startResourceTypeGeneration(description: string): Observable<GenerationJobDto> {
    return this.http.post<GenerationJobDto>(`${API_BASE}/resource-types/generate/jobs`, { description });
  }

  getResourceTypeGeneration(id: string): Observable<GenerationJobDto> {
    return this.http.get<GenerationJobDto>(`${API_BASE}/resource-types/generate/jobs/${id}`);
  }

  /** Kills the running Claude process; nothing is installed. */
  cancelResourceTypeGeneration(id: string): Observable<GenerationJobDto> {
    return this.http.delete<GenerationJobDto>(`${API_BASE}/resource-types/generate/jobs/${id}`);
  }

  createResourceTypeFromSource(sourceCode: string): Observable<GeneratedResourceTypeDto> {
    return this.http.post<GeneratedResourceTypeDto>(`${API_BASE}/resource-types`, { sourceCode });
  }

  deleteResourceType(typeKey: string): Observable<void> {
    return this.http.delete<void>(`${API_BASE}/resource-types/${typeKey}`);
  }

  // ---------- Architecture map ----------

  getArchitectureMap(workflowId?: string | null): Observable<ArchitectureMapDto> {
    const params = workflowId ? new HttpParams().set('workflowId', workflowId) : undefined;
    return this.http.get<ArchitectureMapDto>(`${API_BASE}/architecture/map`, { params });
  }

  // ---------- Delivery: PRs, escalations, and the five metrics ----------

  getDeliveryMetrics(days: number, workflowId?: string | null): Observable<DeliveryMetricsDto> {
    return this.http.get<DeliveryMetricsDto>(`${API_BASE}/delivery/metrics`, { params: this.window(days, workflowId) });
  }

  getPullRequests(days: number, agentId?: string, workflowId?: string | null): Observable<PullRequestDto[]> {
    let params = this.window(days, workflowId);
    if (agentId) params = params.set('agentId', agentId);
    return this.http.get<PullRequestDto[]>(`${API_BASE}/delivery/prs`, { params });
  }

  /** days + optional workflow filter — the shape every dashboard query takes. */
  private window(days: number, workflowId?: string | null): HttpParams {
    let params = new HttpParams().set('days', days);
    if (workflowId) params = params.set('workflowId', workflowId);
    return params;
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

  // ---------- Events ----------

  /** Every topic the workspace knows: agent completions, raisers, listeners, curation, and what crossed the bus. */
  getEventTopics(): Observable<EventTopicDto[]> {
    return this.http.get<EventTopicDto[]>(`${API_BASE}/events/topics`);
  }

  // ---------- Metrics: user-defined outcomes ----------

  getMetrics(days: number, workflowId?: string | null): Observable<MetricSummaryDto[]> {
    return this.http.get<MetricSummaryDto[]>(`${API_BASE}/metrics`, { params: this.window(days, workflowId) });
  }

  getMetricValues(resourceId: string, take = 200): Observable<MetricValueDto[]> {
    return this.http.get<MetricValueDto[]>(`${API_BASE}/metrics/${resourceId}/values`, { params: new HttpParams().set('take', take) });
  }

  recordMetricValue(body: RecordMetricBody): Observable<MetricValueDto> {
    return this.http.post<MetricValueDto>(`${API_BASE}/metrics/values`, body);
  }

  deleteMetricValue(id: string): Observable<void> {
    return this.http.delete<void>(`${API_BASE}/metrics/values/${id}`);
  }

  // ---------- Architect (AI workflow builder) ----------

  /** Long-running: the architect composes resources and agents through the API (minutes). */
  buildWorkflow(description: string, workflowId?: string | null): Observable<ArchitectResultDto> {
    return this.http.post<ArchitectResultDto>(`${API_BASE}/architect/build`, { description, workflowId: workflowId ?? null });
  }

  // ---------- User action requests ----------

  getUserActions(agentId?: string, includeResolved = false): Observable<UserActionDto[]> {
    let params = new HttpParams();
    if (agentId) params = params.set('agentId', agentId);
    if (includeResolved) params = params.set('includeResolved', true);
    return this.http.get<UserActionDto[]>(`${API_BASE}/user-actions`, { params });
  }

  /** Complete a request. The answer is recorded as a rule, into the memory graph inbox, or nowhere — never injected into a prompt. */
  resolveUserAction(id: string, response?: string, recordAs: RecordAs = 'rule'): Observable<UserActionDto> {
    return this.http.post<UserActionDto>(`${API_BASE}/user-actions/${id}/resolve`, { response: response ?? null, recordAs });
  }

  // ---------- Dynamic workspaces ----------

  getWorkspaces(resourceId?: string): Observable<WorkspaceDto[]> {
    const params = resourceId ? new HttpParams().set('resourceId', resourceId) : undefined;
    return this.http.get<WorkspaceDto[]>(`${API_BASE}/workspaces`, { params });
  }

  claimWorkspace(body: { resourceId: string; unit: string; context?: string | null }): Observable<WorkspaceClaimDto> {
    return this.http.post<WorkspaceClaimDto>(`${API_BASE}/workspaces`, body);
  }

  completeWorkspace(id: string, summary?: string): Observable<void> {
    return this.http.post<void>(`${API_BASE}/workspaces/${id}/done`, { summary: summary ?? null });
  }

  /** Removes the workspace directory immediately; the record remains as history. */
  cleanWorkspace(id: string): Observable<void> {
    return this.http.delete<void>(`${API_BASE}/workspaces/${id}`);
  }

  // ---------- Scripts ----------

  /** Runs a saved script (by resourceId) or unsaved code from the editor; blocks until it exits or times out. */
  runScript(body: ScriptRunRequest): Observable<ScriptRunResultDto> {
    return this.http.post<ScriptRunResultDto>(`${API_BASE}/scripts/run`, body);
  }

  /** Long-running: Claude edits the script in a scratch workspace and the result comes back as a proposal. */
  assistScript(body: ScriptAssistRequest): Observable<ScriptAssistResultDto> {
    return this.http.post<ScriptAssistResultDto>(`${API_BASE}/scripts/assist`, body);
  }

  // ---------- About ----------

  getVersion(): Observable<VersionDto> {
    return this.http.get<VersionDto>(`${API_BASE}/system/version`);
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

  /** Wires one resource into an agent (idempotent) — the canvas drag-to-connect gesture. */
  attachResource(agentId: string, resourceId: string): Observable<AgentDetailDto> {
    return this.http.post<AgentDetailDto>(`${API_BASE}/agents/${agentId}/resources/${resourceId}`, {});
  }

  /** Cuts one resource→agent edge (idempotent). */
  detachResource(agentId: string, resourceId: string): Observable<AgentDetailDto> {
    return this.http.delete<AgentDetailDto>(`${API_BASE}/agents/${agentId}/resources/${resourceId}`);
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

  getDashboardSummary(days: number, workflowId?: string | null): Observable<DashboardSummaryDto> {
    return this.http.get<DashboardSummaryDto>(`${API_BASE}/dashboard/summary`, { params: this.window(days, workflowId) });
  }

  getCostSeries(days: number, workflowId?: string | null): Observable<CostSeriesPointDto[]> {
    return this.http.get<CostSeriesPointDto[]>(`${API_BASE}/dashboard/cost-series`, { params: this.window(days, workflowId) });
  }

  getAgentBreakdown(days: number, workflowId?: string | null): Observable<AgentBreakdownDto[]> {
    return this.http.get<AgentBreakdownDto[]>(`${API_BASE}/dashboard/agent-breakdown`, { params: this.window(days, workflowId) });
  }

  getModelUsage(days: number, workflowId?: string | null): Observable<ModelUsageDto[]> {
    return this.http.get<ModelUsageDto[]>(`${API_BASE}/dashboard/model-usage`, { params: this.window(days, workflowId) });
  }

  getRecentFailures(take = 8, workflowId?: string | null): Observable<RecentFailureDto[]> {
    let params = new HttpParams().set('take', take);
    if (workflowId) params = params.set('workflowId', workflowId);
    return this.http.get<RecentFailureDto[]>(`${API_BASE}/dashboard/recent-failures`, { params });
  }
}
