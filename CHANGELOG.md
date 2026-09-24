# Changelog

All notable changes to Looper. Dates are UTC.

## Unreleased

### Models
- **Claude Opus 5.5** (`claude-opus-5-5`) and **Claude Fable 5.1** (`claude-fable-5-1`) are in the model pickers for agents, reviewers and sub-agents. Claude Opus 5 stays the default for new agents. Both need a current Claude Code CLI; run `claude update` if an older install rejects them.
- The Architect, the script assistant and the resource-type generator now run on Claude Opus 5.5 at `--effort high`. Opus 5.5 defaults to medium effort, so the flag keeps their depth where it was on Opus 5. The Architect also offers Opus 5.5 instead of Opus 5 when it picks a model for the agents it creates.
- Dry-run cost estimates price the new models ($4/$20 and $10/$50 per MTok) and use Claude Sonnet 5's current $2/$10 rate instead of $3/$15.

### Resources
- **File** — a resource for one file by exact path, distinct from Folder: the agent gets the file's folder, its path in `$LOOPER_FILE_<NAME>` and a prompt section naming it; small text contents can be inlined into every run's prompt; read-only adds a standing rule; a missing file fails the run before the model starts unless "create if missing". The folder browser gained a file mode for it (`GET /api/filesystem/directories?includeFiles=true`).
- **ElevenLabs voice** — an ElevenLabs account as a tool: the resource stores the API key (masked like every secret); the attached agent gets `generate_speech` and `list_voices` on its run's MCP server. Looper calls ElevenLabs, writes the MP3 and returns the path — the model never sees the key — and records every generation in the run log.
- **Azure DevOps tickets** — tickets to work on, picked from an Azure DevOps board in the editor (the board's open work items, with assignee, team, tag, type and hidden-state filters; `POST /api/boards/tickets`). Every real run reads the selected items fresh into its prompt — description, acceptance criteria, repro steps — and gets a read-only `get_work_item` tool for anything the prompt cut short. A ticket that cannot be read fails the run before the model starts. Optionally clears the selection after a successful run.
- **Jira time tracking** — the Jira issue a run's time goes against, chosen on the resource's workbench card: an issue search right on the canvas (`POST /api/boards/jira-issues`; the pick is saved with `PUT /api/boards/time-trackers/{id}/issue`), while the edit modal holds the connection and booking choices. After each real run that reached the model, Looper books the run's wall-clock time to Tempo on the 5-minute grid, around worklogs already in the timesheet, tagged with the first selected ticket — after every run, after successful runs only, or never.
- **One-off prompt** — extra instructions for the next run only, typed in a box on the resource's workbench card that saves itself as you pause (`PUT /api/boards/prompts/{id}`): the next real run takes them into its prompt and clears them as it starts, and the card shows the empty box within a poll (the run log keeps a copy). A run that never reaches the model puts them back; dry runs leave them in place.
- The workbench map carries each card's live state (`card` on every resource of those two types), and a resource update that leaves out a card-owned value keeps the stored one, so saving the edit modal never undoes the card.
- Resource editors open on the resource as stored now (`GET /api/resources/{id}`), not the list the page loaded — runs rewrite some resources. A refused save shows the API's reason instead of "check that the API is running".

### Run limits
- The time limit per run is a Setting (⚙ Settings → Run limits) and applies to the next run without a restart; `0` means no limit. Before, it was `Looper:RunTimeoutMinutes` only, and `0` timed every run out on its first turn. The shipped fallback is now 240 minutes.
- A default budget per run (USD) for agents that set none of their own, handed to the CLI as `--max-budget-usd`.
- Every run logs the limits it started under (`Run limits: time=…, default budget=…`) and the budget in force on its launch line; a timed-out run names the limit that stopped it.

### Fixed
- A run the CLI ended on an error subtype (turn cap, budget cap, error during execution) was reported as a bare "Run failed (exit code 1)." because the exit code was checked before the subtype; it now names the cause and turn count, and every failed run writes the CLI's result object to its log.
- The agent editor reported "check that the API is running" for every rejected save; it now shows the API's reason (e.g. the max-turns range), and the max-turns field shows its cap.

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
