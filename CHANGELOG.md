# Changelog

All notable changes to Looper. Dates are UTC.

## 1.0.0 — 2026-09-09

The first release. Looper runs Claude agents in loops for any profession: define resources, wire them into agents that start on a schedule or on events, and watch cost, reliability and the outcomes you care about.

### Loops and resources
- Agents with model, effort, turn and budget caps, autonomy levels, dry-run rehearsal, and a scheduled *or* event-driven trigger.
- Resources: folders, rules and rule sets, checks, scripts, metrics, specifications, reviewers, sub-agents, MCP servers, workspace pools, "Ask the user", credentials, and four memory graph types — plus resource types written by Claude and compiled live.
- **Scripts** run deterministically before an iteration (output becomes context) or after it (a gate). Write them in-app or have Claude write them.
- **Checks** run a shell command or one of the workflow's scripts; exit code decides.
- **Metrics** are outcomes you define; they appear on the dashboard the moment they exist.
- **Event Raisers** raise named events when a run ends; an agent's "on events" trigger is its subscription.

### Resources are tools
- Every real run gets its own MCP server. Attached resources become typed tools — `ask_user`, `record_metric`, `claim_workspace` and friends — instead of curl recipes in the prompt.

### Workflows
- A workflow is one workbench: its own resources, agents and metrics. Switch, create, rename and delete them from the topbar; the dashboard filters by workflow.
- Export any workflow as a `.workflow` file and import it on another Looper — resources, agents, wiring and the dynamic resource types they need (source and DLL), with checksums, secrets stripped, and an all-or-nothing import.

### The harness fails closed
- A run succeeds only when the model finished **and** every gate passed. Turn and budget caps, empty results, unusable resources, empty checks, blank rubrics, unparseable verdicts and failed scripts are all honest failures with a reason.
- Reviewer gates run in a fresh context and loop fix instructions back to the worker.
- Answers to "Ask the user" requests are recorded as standing rules or memory, never carried as hidden context.

### Settings
- Choose whether the Claude Code CLI uses the machine's Claude subscription login or an API key stored in Looper; the choice is applied to every launch and shown in every run log.

### Workbench and dashboard
- One live map: resources flow into agents, agents into outcomes; drag to wire, hover to cut, edit in place.
- Spend over time, cost by agent and model, success rate, run times, recent failures, your metrics, and (for coding loops) delivery metrics that measure work that stuck.
