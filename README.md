# Looper

**Loop engineering for Claude agents.** Define reusable resources (MCP servers, folders, rules, testing gates, credentials), wire them into agents that run on a schedule through the Claude Agent SDK, and watch cost, reliability and output on a live dashboard.

## How it works

- **Resources (left panel)** — capabilities agents can be granted:
  | Type | What it becomes at run time |
  |---|---|
  | MCP Server | `--mcp-config` entry (stdio/http/sse) |
  | Folder / Files | working directory (primary) or `--add-dir` — pick it with the built-in folder browser |
  | Rule | appended to the system prompt |
  | Knowledge (RAG) | knowledge-source instructions in the prompt (+ folder access) |
  | Testing Action | shell command executed after every loop; non-zero exit fails the gate |
  | Sub-agent | `--agents` definition the main agent can delegate to |
  | PAT Token / Azure Connection | environment variables injected into the run (stored masked) |
  | **Custom (AI-generated)** | anything a module contributes: env vars, rules, prompt sections, MCP servers, directories |

- **Dynamic resource types** — in the resource picker, choose **“New type, written by Claude”** and describe a capability (e.g. *“a Postgres connection exposed as PG\* env vars”*). Claude writes a C# module implementing `IResourceTypeModule`, Roslyn compiles it in-process (with one automatic self-repair round on compiler errors), and the DLL is loaded live — the new type appears in the picker immediately, its form is rendered from the module's field specs, and `Password` fields are masked like built-in secrets. Modules are stored (source + DLL) in the `modules/` folder next to the API and reloaded — recompiled from source if the DLL is missing — at startup. Power users can also `POST /api/resource-types` with raw module source.

- **Agents (right panel)** — a prompt that runs every *N* minutes with a chosen model, effort level (`low → max`), turn cap, per-run budget cap (`--max-budget-usd`) and a set of resources. **Dry run** mode simulates iterations with realistic cost numbers without spending tokens — new agents start in dry run.

- **Dashboard** — spend over time, cost by agent/model, success rate, run times, recent failures. Every run records real cost and token usage from the CLI's JSON result.

## Requirements

- .NET SDK 10, Node.js 22
- For **real** (non-dry-run) loops: [Claude Code](https://code.claude.com) installed and authenticated (`claude` on PATH, or set `Looper:ClaudeCommand` in `api/Looper.Api/appsettings.json`). If it's missing, the app shows a setup banner with one-click install (`GET /api/system/claude-status`, `POST /api/system/install-claude` runs the official installer) — after installing, run `claude` once in a terminal to sign in.

## Run it

```bash
./dev.sh            # starts API (http://localhost:5210) + Angular (http://localhost:4200)
```

or manually:

```bash
cd api && dotnet run --project Looper.Api      # API on :5210
cd web && npm start                             # UI on :4200
```

The app starts with an empty workspace — create resources in the left panel, then wire them into your first agent. The SQLite database lives next to the API working directory (`looper.db`); delete it for a clean slate.

## Tests

```bash
cd api && dotnet test                                              # 30 tests: unit + full HTTP integration
cd web && CHROME_BIN=$(which google-chrome) npx ng test --watch=false --browsers=ChromeHeadless
```

Backend tests cover the CQRS decorator pipeline, secret masking (including dynamic-module password fields), the Roslyn module compiler and loader, Claude CLI output parsing, the folder-browse endpoint, the run coordinator (dry-run lifecycle, double-trigger rejection, cancellation), and integration tests that boot the real API on a throwaway SQLite file: resource lifecycle with secret preservation, dynamic type install → use → in-use protection → removal, agent lifecycle with a real (simulated) run, and dashboard aggregation.

## Architecture

- **`api/`** — .NET 10 minimal API. CQRS with the decorator pattern (Scrutor): every command flows through `Logging → Validation → Handler`; queries through `Logging → Handler`. Features are organized as **vertical slices** (`Features/Resources`, `Features/Agents`, `Features/Runs`, `Features/Dashboard`) — one file per use case containing command/query, validator, handler and endpoint. EF Core + SQLite for persistence. A background `AgentSchedulerService` fires due agents; `AgentRunCoordinator` enforces one active run per agent and a global concurrency cap; `ClaudeCliExecutor` drives the Claude Agent SDK headlessly and parses cost/usage from its JSON output.
- **`web/`** — Angular 20 (standalone components, signals, new control flow). Workbench (resources ⟷ agents), agent detail with live logs, dashboard with hand-rolled SVG charts.

## Security notes

- Secrets (PAT tokens, client secrets) are stored in the local SQLite database and **masked in every API response**; they are only decrypted into the environment of a spawned run. Treat `looper.db` accordingly.
- Agents default to full autonomy (`bypassPermissions`) inside their configured working directory — scope their folder resources deliberately.
- The folder browser (`GET /api/filesystem/directories`) enumerates directory **names** on the machine running the API so the UI can offer a real picker. It never reads file contents, and the API binds to localhost only. Don't expose the API on a public interface.
