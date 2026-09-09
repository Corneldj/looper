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

export type RunTrigger = 'Scheduled' | 'Manual' | 'Event';

export type TriggerMode = 'Scheduled' | 'Event';

/** Sentinel the API returns in place of stored secrets; sending it back preserves the stored value. */
export const SECRET_SENTINEL = '__SECRET_UNCHANGED__';

// ---------- Settings: how the Claude Code CLI authenticates ----------

/** Subscription = the Claude Code login on the API machine (default); ApiKey = a stored Anthropic API key. */
export type ClaudeAuthMode = 'Subscription' | 'ApiKey';

export interface SettingsDto {
  claudeAuthMode: ClaudeAuthMode;
  /** Whether a key is stored. The key itself never leaves the server. */
  hasApiKey: boolean;
  /** The last characters of the stored key, e.g. "…a1b2". */
  apiKeyHint: string | null;
  updatedAtUtc: string | null;
}

export interface UpdateSettingsRequest {
  claudeAuthMode: ClaudeAuthMode;
  /** A new key replaces the stored one; omit (or send the secret sentinel) to keep it. */
  apiKey?: string | null;
  /** Removes the stored key. Refused while the mode still needs one. */
  clearApiKey?: boolean;
}

// ---------- Workflows: one workbench each ----------

export interface WorkflowDto {
  id: string;
  name: string;
  description: string;
  isDefault: boolean;
  agentCount: number;
  resourceCount: number;
  createdAtUtc: string;
  updatedAtUtc: string;
}

export interface SaveWorkflowRequest {
  name: string;
  description: string;
}

export interface ResourceDto {
  id: string;
  /** The workflow (workbench) this resource lives in. */
  workflowId: string;
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

export type GenerationPhase = 'Generating' | 'Compiling' | 'Repairing' | 'Installing' | 'Installed' | 'Failed' | 'Cancelled';

/** A resource-type generation in flight: Claude writes, Roslyn compiles, errors go back to Claude — up to a cap, until cancelled. */
export interface GenerationJobDto {
  id: string;
  description: string;
  phase: GenerationPhase;
  attempt: number;
  maxAttempts: number;
  lastErrors: string[];
  costUsd: number;
  result: GeneratedResourceTypeDto | null;
  error: string | null;
  startedAtUtc: string;
  updatedAtUtc: string;
}

// ---------- Scripts (runnable Python/Bash resources) ----------

export type ScriptLanguage = 'python' | 'bash';

/** before = Looper runs it pre-iteration (stdout → prompt) · after = post-iteration gate. Always by the harness, never by the model. */
export type ScriptTrigger = 'before' | 'after';

export const SCRIPT_TRIGGERS: { id: ScriptTrigger; label: string; blurb: string }[] = [
  { id: 'before', label: 'Before every iteration', blurb: 'Looper runs it first and hands the output to the model as context.' },
  { id: 'after', label: 'After every iteration (gate)', blurb: 'Looper runs it after the model; a non-zero exit fails the run.' },
];

export interface ScriptRunRequest {
  resourceId?: string | null;
  language?: ScriptLanguage | null;
  code?: string | null;
  args?: string | null;
  workingDirectory?: string | null;
  timeoutSeconds?: number | null;
}

export interface ScriptRunResultDto {
  command: string;
  exitCode: number;
  passed: boolean;
  durationMs: number;
  output: string;
}

export interface ScriptAssistRequest {
  name: string;
  description: string | null;
  language: ScriptLanguage;
  code: string;
  instruction: string;
  allowRun: boolean;
}

export interface ScriptAssistResultDto {
  code: string;
  summary: string;
  costUsd: number;
}

// ---------- Metrics: user-defined outcomes ----------

export type MetricAggregation = 'Latest' | 'Sum' | 'Average';
export type MetricDirection = 'Higher' | 'Lower';
export type MetricSource = 'Agent' | 'Script' | 'Manual' | 'Api';

export interface MetricPointDto {
  /** yyyy-MM-dd (UTC) */
  date: string;
  value: number;
}

/** A Metric resource as the dashboard shows it: definition, current reading, trend, sparkline, reporters. */
export interface MetricSummaryDto {
  resourceId: string;
  name: string;
  description: string;
  unit: string;
  aggregation: MetricAggregation;
  direction: MetricDirection;
  target: number | null;
  latest: number | null;
  latestAtUtc: string | null;
  /** The window's reading per the aggregation; null with no data. */
  current: number | null;
  previous: number | null;
  trendPct: number | null;
  countInWindow: number;
  totalCount: number;
  series: MetricPointDto[];
  agents: string[];
}

export interface MetricValueDto {
  id: string;
  resourceId: string;
  agentId: string | null;
  agentName: string | null;
  runId: string | null;
  value: number;
  note: string | null;
  source: MetricSource;
  recordedAtUtc: string;
}

export interface RecordMetricBody {
  /** Metric id, name or slug. */
  metric: string;
  value: number;
  note?: string | null;
  runId?: string | null;
  agentId?: string | null;
  source?: MetricSource | null;
}

export const METRIC_AGGREGATIONS: { id: MetricAggregation; label: string; blurb: string }[] = [
  { id: 'Latest', label: 'gauge', blurb: 'the newest reading is the current value' },
  { id: 'Sum', label: 'total', blurb: 'reports add up over the period' },
  { id: 'Average', label: 'average', blurb: 'the mean of the reports in the period' },
];

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
  /** The workflow (workbench) this agent lives in. */
  workflowId: string;
  name: string;
  description: string;
  model: string;
  effort: EffortLevel;
  intervalMinutes: number;
  /** How the loop starts — on its schedule or on events. One or the other, never both. */
  triggerMode: TriggerMode;
  /** For event mode: newline/comma-separated topic patterns (exact or trailing '.*'). */
  triggerTopics: string | null;
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
  triggerMode: TriggerMode;
  triggerTopics: string | null;
  maxTurns: number;
  maxBudgetUsd: number | null;
  workingDirectory: string | null;
  allowedTools: string | null;
  bypassPermissions: boolean;
  dryRun: boolean;
  autonomyLevel: number;
  resourceIds: string[];
  /** Workflow for a new agent; ignored on update — agents don't move. */
  workflowId?: string | null;
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

// ---------- Graph memory infrastructure ----------

export interface GraphHealthDto {
  checkedUtc: string;
  nodes: number;
  factsCurrent: number;
  factsTotal: number;
  episodes: number;
  inboxPending: number;
  competingCount: number;
  staleCount: number;
  problemCount: number;
  usageEvents: number;
}

/** A graph resource seen as shared infrastructure: role wiring + the janitor's last health measurement. */
export interface GraphStatusDto {
  resourceId: string;
  name: string;
  typeKey: string;
  path: string;
  curator: string | null;
  curationTopic: string;
  autoLog: boolean;
  preambleK: number;
  inboxThreshold: number;
  health: GraphHealthDto | null;
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
  { type: 'FileLocation', label: 'Folder / Files', icon: '📁', blurb: 'A folder the agent can read and edit — documents, data, a codebase. The primary one becomes its working directory.' },
  { type: 'Rag', label: 'Knowledge (RAG)', icon: '📚', blurb: 'A knowledge source the agent is told to consult.' },
  { type: 'TestingAction', label: 'Check', icon: '🧪', blurb: 'A command that runs after every iteration and must exit 0 for the work to count — a test suite, a validator, a link checker, anything scriptable.' },
  { type: 'Rule', label: 'Rule', icon: '📏', blurb: 'Standing instructions appended to the agent’s system prompt.' },
  { type: 'RuleSet', label: 'Rule Set', icon: '📋', blurb: 'A managed collection of rules — add, toggle and remove without the clutter.' },
  { type: 'WorkspacePool', label: 'Dynamic Workspaces', icon: '🗂️', blurb: 'Agents claim a dedicated workspace per unit of work — provisioned on demand, context passed in, cleaned up on retention.' },
  { type: 'UserAction', label: 'Ask the user', icon: '🙋', blurb: 'A tool, not a question: lets the agent raise a User Action Request whenever it needs something only you can do or decide. Attach one; the agent decides what to ask.' },
  { type: 'SubAgent', label: 'Sub-agent', icon: '🤖', blurb: 'A helper agent the main agent can delegate to.' },
  { type: 'Reviewer', label: 'Reviewer', icon: '🧐', blurb: 'An independent agent that reviews the work after every loop — pass, or fail with fix instructions.' },
  { type: 'AzureConnection', label: 'Azure Connection', icon: '☁️', blurb: 'Azure identity exposed as environment variables.' },
  { type: 'PatToken', label: 'API key / secret', icon: '🔑', blurb: 'A secret injected as an environment variable — an API key, a token, a password.' },
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
  /** Acceptance criteria this PR satisfies ("AC-1, AC-3"), verified against the agent's Specification. */
  satisfiesAcs: string | null;
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
  /** AC citations, e.g. "AC-1,AC-3" — required when the agent has an enforced Specification attached. */
  satisfies?: string | null;
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
  { level: 2, label: 'L2 · Sandboxed', blurb: 'Executes in a sandbox; a human approves the result before it lands.' },
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
  description: string;
  agentIds: string[];
}

/** An agent as the workbench canvas draws it: identity, trigger, schedule state, 24h activity, wiring. */
/** A metric attached to an agent with its 30-day reading (total / average / gauge) — the canvas outcome line. */
export interface MapMetricDto {
  resourceId: string;
  name: string;
  unit: string;
  current: number | null;
}

export interface MapAgentDto {
  id: string;
  name: string;
  description: string;
  model: string;
  effort: EffortLevel;
  autonomyLevel: number;
  enabled: boolean;
  dryRun: boolean;
  isRunning: boolean;
  intervalMinutes: number;
  triggerMode: TriggerMode;
  triggerTopics: string | null;
  lastRunAtUtc: string | null;
  nextRunAtUtc: string | null;
  lastRunStatus: RunStatus | null;
  runsLast24h: number;
  costLast24hUsd: number;
  openPrs: number;
  mergedPrs: number;
  resourceIds: string[];
  metrics: MapMetricDto[];
  /** Topics this agent raises: its completion topics plus attached Event Raisers. */
  raises: string[];
  /** Patterns that wake this agent: its own topics (Event mode) plus attached Event Listeners. */
  listens: string[];
}

// ---------- Events ----------

export type EventTopicKind = 'completion' | 'raiser' | 'listener' | 'curation' | 'seen';

/** One entry of the event catalog: a topic (or listen pattern) and where it comes from. */
export interface EventTopicDto {
  topic: string;
  kind: EventTopicKind;
  source: string;
  isPattern: boolean;
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
  /** Where the answer was recorded when resolved. */
  resolutionNote: string | null;
  /** The agent has a memory graph attached, so an answer may be recorded there instead of as a rule. */
  canRecordToMemory: boolean;
}

/** Where a resolved request's answer goes — a deterministic edit, never hidden context. */
export type RecordAs = 'rule' | 'memory' | 'none';
