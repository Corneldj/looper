// Mirrors the Looper.Api DTO contract. Enums are serialized as strings by the API.

export type ResourceType =
  | 'McpServer'
  | 'FileLocation'
  | 'Rag'
  | 'TestingAction'
  | 'Rule'
  | 'RuleSet'
  | 'WorkspacePool'
  | 'UserAction'
  | 'SubAgent'
  | 'Reviewer'
  | 'AzureConnection'
  | 'PatToken'
  | 'Custom';

export type EffortLevel = 'Low' | 'Medium' | 'High' | 'XHigh' | 'Max';

export type RunStatus = 'Running' | 'Succeeded' | 'Failed' | 'Cancelled' | 'TimedOut';

export type RunTrigger = 'Scheduled' | 'Manual';

/** Sentinel the API returns in place of stored secrets; sending it back preserves the stored value. */
export const SECRET_SENTINEL = '__SECRET_UNCHANGED__';

export interface ResourceDto {
  id: string;
  name: string;
  type: ResourceType;
  /** For type 'Custom': the TypeKey of the dynamic resource type that owns this resource. */
  customTypeKey: string | null;
  description: string;
  configJson: string;
  agentCount: number;
  createdAtUtc: string;
  updatedAtUtc: string;
}

// ---------- Dynamic resource types ----------

export type ResourceFieldKind = 'Text' | 'Multiline' | 'Number' | 'Boolean' | 'Password' | 'Select' | 'Path';

export interface ResourceFieldDto {
  key: string;
  label: string;
  kind: ResourceFieldKind;
  required: boolean;
  hint: string | null;
  options: string[] | null;
  placeholder: string | null;
}

/** A resource type: built-ins have bespoke forms (fields = null); dynamic ones describe theirs. */
export interface ResourceTypeDto {
  typeKey: string;
  label: string;
  icon: string;
  blurb: string;
  builtIn: boolean;
  fields: ResourceFieldDto[] | null;
}

export interface GeneratedResourceTypeDto {
  type: ResourceTypeDto;
  sourceCode: string;
  costUsd: number;
}

// ---------- Claude CLI status ----------

export interface ClaudeStatusDto {
  available: boolean;
  version: string | null;
  command: string;
  error: string | null;
}

export interface ClaudeInstallResultDto {
  success: boolean;
  output: string;
  status: ClaudeStatusDto;
}

// ---------- Folder browsing ----------

export interface DirectoryEntryDto {
  name: string;
  path: string;
  isHidden: boolean;
}

export interface QuickLinkDto {
  label: string;
  path: string;
}

export interface DirectoryListingDto {
  path: string;
  parentPath: string | null;
  exists: boolean;
  error: string | null;
  directories: DirectoryEntryDto[];
  quickLinks: QuickLinkDto[];
}

export interface AgentSummaryDto {
  id: string;
  name: string;
  description: string;
  model: string;
  effort: EffortLevel;
  intervalMinutes: number;
  enabled: boolean;
  dryRun: boolean;
  /** 1 proposes · 2 sandboxed+approval · 3 autonomous+review · 4 autonomous+sampled audits. */
  autonomyLevel: number;
  isRunning: boolean;
  resourceCount: number;
  lastRunAtUtc: string | null;
  nextRunAtUtc: string | null;
  lastRunStatus: RunStatus | null;
  runsLast24h: number;
  costLast24hUsd: number;
}

export interface AgentDetailDto extends AgentSummaryDto {
  prompt: string;
  maxTurns: number;
  maxBudgetUsd: number | null;
  workingDirectory: string | null;
  allowedTools: string | null;
  bypassPermissions: boolean;
  resourceIds: string[];
  createdAtUtc: string;
}

export interface SaveAgentRequest {
  name: string;
  description: string;
  prompt: string;
  model: string;
  effort: EffortLevel;
  intervalMinutes: number;
  maxTurns: number;
  maxBudgetUsd: number | null;
  workingDirectory: string | null;
  allowedTools: string | null;
  bypassPermissions: boolean;
  dryRun: boolean;
  autonomyLevel: number;
  resourceIds: string[];
}

export interface RunSummaryDto {
  id: string;
  agentId: string;
  startedAtUtc: string;
  completedAtUtc: string | null;
  status: RunStatus;
  trigger: RunTrigger;
  escalated: boolean;
  /** The run raised a User Action Request — the loop is waiting on the human. Not a failure. */
  actionRequested: boolean;
  costUsd: number;
  inputTokens: number;
  outputTokens: number;
  cacheReadTokens: number;
  numTurns: number;
  durationMs: number;
  testsPassed: boolean | null;
  /** Verdict of the independent review gate; null when no Reviewer resource is attached. */
  reviewPassed: boolean | null;
  /** Fix-and-re-review cycles the run needed; 0 with a pass = first-pass acceptance. */
  reviewRounds: number;
  errorMessage: string | null;
}

export interface RunLogEntryDto {
  timestampUtc: string;
  level: 'info' | 'warn' | 'error';
  message: string;
}

export interface TestingActionResultDto {
  name: string;
  command: string;
  exitCode: number;
  passed: boolean;
  durationMs: number;
  output: string;
}

export interface ReviewRoundDto {
  round: number;
  reviewer: string;
  verdict: 'pass' | 'fail' | 'inconclusive';
  summary: string;
  fixInstructions: string | null;
  costUsd: number;
}

export interface RunDetailDto extends RunSummaryDto {
  agentName: string;
  escalationReason: string | null;
  cacheCreationTokens: number;
  resultText: string | null;
  testResults: TestingActionResultDto[] | null;
  reviews: ReviewRoundDto[] | null;
  logs: RunLogEntryDto[];
}

// ---------- Dashboard ----------

export interface DashboardSummaryDto {
  totalCostUsd: number;
  totalRuns: number;
  successRate: number;          // 0..1, of completed runs in period
  activeAgents: number;         // enabled agents
  totalAgents: number;
  avgDurationMs: number;
  totalInputTokens: number;
  totalOutputTokens: number;
  costTrendPct: number | null;  // vs previous period; null when no baseline
  runsTrendPct: number | null;
}

export interface CostSeriesPointDto {
  date: string;                 // yyyy-MM-dd
  costUsd: number;
  runs: number;
  failures: number;
}

export interface AgentBreakdownDto {
  agentId: string;
  name: string;
  model: string;
  enabled: boolean;
  costUsd: number;
  runs: number;
  successRate: number;
  avgDurationMs: number;
  avgCostPerRunUsd: number;
}

export interface ModelUsageDto {
  model: string;
  costUsd: number;
  runs: number;
  inputTokens: number;
  outputTokens: number;
}

export interface RecentFailureDto {
  runId: string;
  agentId: string;
  agentName: string;
  startedAtUtc: string;
  status: RunStatus;
  errorMessage: string | null;
}

// ---------- UI catalogs ----------

export const RESOURCE_TYPES: { type: ResourceType; label: string; icon: string; blurb: string }[] = [
  { type: 'McpServer', label: 'MCP Server', icon: '⚡', blurb: 'Tools exposed to the agent over the Model Context Protocol.' },
  { type: 'FileLocation', label: 'Folder / Files', icon: '📁', blurb: 'A directory the agent can read and edit. The primary one becomes its working directory.' },
  { type: 'Rag', label: 'Knowledge (RAG)', icon: '📚', blurb: 'A knowledge source the agent is told to consult.' },
  { type: 'TestingAction', label: 'Testing Action', icon: '🧪', blurb: 'A command that runs after every loop and gates the result.' },
  { type: 'Rule', label: 'Rule', icon: '📏', blurb: 'Standing instructions appended to the agent’s system prompt.' },
  { type: 'RuleSet', label: 'Rule Set', icon: '📋', blurb: 'A managed collection of rules — add, toggle and remove without the clutter.' },
  { type: 'WorkspacePool', label: 'Dynamic Workspaces', icon: '🗂️', blurb: 'Agents claim a dedicated workspace per unit of work — provisioned on demand, context passed in, cleaned up on retention.' },
  { type: 'UserAction', label: 'User Action Requests', icon: '🙋', blurb: 'The agent can ask you to do or decide something — the loop parks until you respond, without failing.' },
  { type: 'SubAgent', label: 'Sub-agent', icon: '🤖', blurb: 'A helper agent the main agent can delegate to.' },
  { type: 'Reviewer', label: 'Reviewer', icon: '🧐', blurb: 'An independent agent that reviews the work after every loop — pass, or fail with fix instructions.' },
  { type: 'AzureConnection', label: 'Azure Connection', icon: '☁️', blurb: 'Azure identity exposed as environment variables.' },
  { type: 'PatToken', label: 'PAT Token', icon: '🔑', blurb: 'A personal access token injected as an environment variable.' },
];

export const MODELS: { id: string; label: string; note: string }[] = [
  { id: 'claude-opus-5', label: 'Claude Opus 5', note: 'Default — strongest general model' },
  { id: 'claude-fable-5', label: 'Claude Fable 5', note: 'Most capable, premium pricing' },
  { id: 'claude-opus-4-8', label: 'Claude Opus 4.8', note: 'Previous Opus generation' },
  { id: 'claude-sonnet-5', label: 'Claude Sonnet 5', note: 'Fast and economical' },
  { id: 'claude-haiku-4-5', label: 'Claude Haiku 4.5', note: 'Cheapest, simple tasks' },
];

export const EFFORT_LEVELS: { id: EffortLevel; label: string }[] = [
  { id: 'Low', label: 'Low' },
  { id: 'Medium', label: 'Medium' },
  { id: 'High', label: 'High' },
  { id: 'XHigh', label: 'X-High' },
  { id: 'Max', label: 'Max' },
];

export function resourceTypeMeta(type: ResourceType) {
  return RESOURCE_TYPES.find(t => t.type === type) ?? RESOURCE_TYPES[0];
}

// ---------- Delivery: the metrics that measure value that stuck ----------

export type PrStatus = 'Open' | 'Merged' | 'Closed';

export interface PullRequestDto {
  id: string;
  agentId: string;
  agentName: string;
  runId: string | null;
  title: string;
  url: string | null;
  repository: string;
  number: number | null;
  status: PrStatus;
  openedAtUtc: string;
  mergedAtUtc: string | null;
  closedAtUtc: string | null;
  additions: number;
  deletions: number;
  reviewRounds: number;
  reviewComments: number;
  humanCommits: number;
  /** Merged with zero change-request rounds and zero human commits; null until merged. */
  firstPass: boolean | null;
  repoPath: string | null;
  mergeCommitSha: string | null;
  survivalRate: number | null;
  survivalCheckedAtUtc: string | null;
  lastSyncedAtUtc: string | null;
  syncError: string | null;
}

export interface RegisterPrBody {
  runId?: string | null;
  agentId?: string | null;
  url?: string | null;
  title?: string | null;
  repoPath?: string | null;
  repository?: string | null;
}

export interface UpdatePrBody {
  title: string;
  status: PrStatus;
  additions: number;
  deletions: number;
  reviewRounds: number;
  reviewComments: number;
  humanCommits: number;
  repoPath: string | null;
  mergeCommitSha: string | null;
}

export interface AgentDeliveryRowDto {
  agentId: string;
  name: string;
  autonomyLevel: number;
  mergedPrs: number;
  costUsd: number;
  costPerMergedPrUsd: number | null;
  firstPassRate: number | null;
  escalationRate: number | null;
  completedRuns: number;
  recommendation: 'promote' | 'demote' | 'hold' | null;
}

export interface DeliveryMetricsDto {
  windowDays: number;
  totalCostUsd: number;
  mergedPrs: number;
  openPrs: number;
  closedPrs: number;
  costPerMergedPrUsd: number | null;
  firstPassRate: number | null;
  codeSurvivalRate: number | null;
  survivalCheckedPrs: number;
  reviewChurnPer100Lines: number | null;
  escalationRate: number | null;
  escalatedRuns: number;
  completedRuns: number;
  agents: AgentDeliveryRowDto[];
}

export const AUTONOMY_LEVELS: { level: number; label: string; blurb: string }[] = [
  { level: 1, label: 'L1 · Proposes', blurb: 'Suggests changes; a human executes them.' },
  { level: 2, label: 'L2 · Sandboxed', blurb: 'Executes in a sandbox; a human approves the diff.' },
  { level: 3, label: 'L3 · Autonomous', blurb: 'Executes autonomously; humans review after the fact.' },
  { level: 4, label: 'L4 · Audited', blurb: 'Fully autonomous with sampled audits.' },
];

// ---------- Architecture map ----------

export interface MapResourceDto {
  id: string;
  name: string;
  type: ResourceType;
  customTypeKey: string | null;
  icon: string;
  typeLabel: string;
  agentIds: string[];
}

export interface MapAgentDto {
  id: string;
  name: string;
  model: string;
  autonomyLevel: number;
  enabled: boolean;
  dryRun: boolean;
  isRunning: boolean;
  intervalMinutes: number;
  lastRunStatus: RunStatus | null;
  runsLast24h: number;
  costLast24hUsd: number;
  openPrs: number;
  mergedPrs: number;
  resourceIds: string[];
}

export interface ArchitectureMapDto {
  resources: MapResourceDto[];
  agents: MapAgentDto[];
}

// ---------- Dynamic workspaces ----------

export type WorkspaceStatus = 'Active' | 'Done' | 'Cleaned';

export interface WorkspaceDto {
  id: string;
  resourceId: string;
  poolName: string;
  agentId: string | null;
  agentName: string | null;
  unit: string;
  path: string;
  status: WorkspaceStatus;
  contextBrief: string;
  createdAtUtc: string;
  lastUsedAtUtc: string;
  doneAtUtc: string | null;
  cleanedAtUtc: string | null;
}

export interface WorkspaceClaimDto {
  id: string;
  unit: string;
  path: string;
  created: boolean;
  brief: string;
}

// ---------- Architect (AI workflow builder) ----------

export interface CreatedItemDto {
  id: string;
  name: string;
  detail: string;
}

export interface ArchitectResultDto {
  success: boolean;
  report: string;
  costUsd: number;
  createdResources: CreatedItemDto[];
  createdAgents: CreatedItemDto[];
  error: string | null;
}

// ---------- User action requests ----------

export type UserActionStatus = 'Open' | 'Resolved';

export interface UserActionDto {
  id: string;
  agentId: string;
  agentName: string;
  runId: string | null;
  title: string;
  details: string;
  status: UserActionStatus;
  blocking: boolean;
  response: string | null;
  createdAtUtc: string;
  resolvedAtUtc: string | null;
}
