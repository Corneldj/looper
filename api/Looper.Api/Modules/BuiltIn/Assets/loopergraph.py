#!/usr/bin/env python3
# loopergraph v2
"""Looper graph toolkit — the read/write path for Looper's graph resources.

Seeded into every graph storage folder by Looper. Self-contained, stdlib-only.
Adapted from the context-engineering course reference code (ce/graph.py,
ce/vectorgraph.py, ce/execgraph.py): typed edges from a controlled ontology,
bi-temporal validity (invalidate, never delete), vector entry with graph
expansion, and a validated execution graph.

v2 makes a graph safe to run as shared infrastructure across parallel loops:
mutations take an advisory file lock; consumers contribute through an
append-only inbox (`remember`) that a curator merges (`inbox`, `inbox-merge`,
`inbox-reject`); recall logs usage telemetry; `health` reports what needs
curation and `decay` mechanically down-weights facts nobody uses.

Run from the graph folder (or pass --dir). `python3 loopergraph.py help` lists
commands; the README.md next to this file carries the protocol.
"""

from __future__ import annotations

import argparse
import contextlib
import json
import math
import os
import re
import sys
import tempfile
import zlib
from datetime import datetime, timezone

try:
    import fcntl  # POSIX advisory locking; absent on Windows
except ImportError:
    fcntl = None

FOREVER = "9999-12-31T00:00:00Z"
_TOKEN = re.compile(r"[a-z0-9]+")


def now_iso() -> str:
    return datetime.now(timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ")


def parse_when(value: str | None) -> str:
    """Accept ISO dates/datetimes; None means now."""
    if not value or value == "now":
        return now_iso()
    v = value.strip()
    if re.fullmatch(r"\d{4}-\d{2}", v):
        v += "-01"
    if re.fullmatch(r"\d{4}-\d{2}-\d{2}", v):
        v += "T00:00:00Z"
    if not v.endswith("Z") and "+" not in v:
        v += "Z"
    return v


def slug(text: str) -> str:
    s = re.sub(r"[^a-z0-9]+", "-", text.strip().lower()).strip("-")
    return s or "node"


def fail(msg: str) -> "NoReturn":  # noqa: F821
    print(f"error: {msg}")
    sys.exit(1)


# ---------------------------------------------------------------------------
# Store: JSONL files in the working folder, atomically rewritten on mutation.
# ---------------------------------------------------------------------------

class Store:
    def __init__(self, root: str):
        self.root = root
        self.ontology_path = os.path.join(root, "ontology.json")
        self.nodes_path = os.path.join(root, "nodes.jsonl")
        self.edges_path = os.path.join(root, "edges.jsonl")
        self.episodes_path = os.path.join(root, "episodes.jsonl")
        self.exec_path = os.path.join(root, "execution.json")
        self.inbox_path = os.path.join(root, "inbox")
        self.archive_path = os.path.join(root, "inbox", "archived")
        self.usage_path = os.path.join(root, "usage.jsonl")
        self.lock_path = os.path.join(root, ".graph.lock")

    @contextlib.contextmanager
    def lock(self):
        """Advisory exclusive lock so parallel loops sharing this graph never interleave a
        read-modify-write. Held only for the duration of one command's mutation."""
        if fcntl is None:
            yield
            return
        with open(self.lock_path, "w") as handle:
            fcntl.flock(handle, fcntl.LOCK_EX)
            try:
                yield
            finally:
                fcntl.flock(handle, fcntl.LOCK_UN)

    # -- io helpers --------------------------------------------------------

    @staticmethod
    def _read_jsonl(path: str) -> list[dict]:
        if not os.path.exists(path):
            return []
        rows = []
        with open(path, encoding="utf-8") as f:
            for i, line in enumerate(f, 1):
                line = line.strip()
                if not line:
                    continue
                try:
                    rows.append(json.loads(line))
                except json.JSONDecodeError:
                    print(f"warning: {os.path.basename(path)}:{i} is not valid JSON; skipped", file=sys.stderr)
        return rows

    @staticmethod
    def _write_atomic(path: str, content: str) -> None:
        fd, tmp = tempfile.mkstemp(dir=os.path.dirname(path) or ".", suffix=".tmp")
        try:
            with os.fdopen(fd, "w", encoding="utf-8") as f:
                f.write(content)
            os.replace(tmp, path)
        except BaseException:
            try:
                os.unlink(tmp)
            except OSError:
                pass
            raise

    def _write_jsonl(self, path: str, rows: list[dict]) -> None:
        self._write_atomic(path, "".join(json.dumps(r, ensure_ascii=False) + "\n" for r in rows))

    @staticmethod
    def _append_jsonl(path: str, row: dict) -> None:
        with open(path, "a", encoding="utf-8") as f:
            f.write(json.dumps(row, ensure_ascii=False) + "\n")

    # -- ontology: the controlled edge vocabulary ---------------------------

    def ontology(self) -> dict[str, str]:
        if not os.path.exists(self.ontology_path):
            return {}
        with open(self.ontology_path, encoding="utf-8") as f:
            data = json.load(f)
        return dict(data.get("edge_types", {}))

    def require_edge_type(self, etype: str) -> None:
        types = self.ontology()
        if not types:
            fail("no ontology.json here — is this a Looper graph folder? (run from the storage folder or pass --dir)")
        if etype not in types:
            known = ", ".join(sorted(types))
            fail(
                f"edge type '{etype}' is not in the ontology. Declared types: {known}. "
                "Reuse one of these; only extend ontology.json for a genuinely new relationship "
                "(keep the vocabulary small — synonyms make edges unqueryable)."
            )

    # -- nodes and edges ----------------------------------------------------

    def nodes(self) -> dict[str, dict]:
        return {n["id"]: n for n in self._read_jsonl(self.nodes_path)}

    def edges(self) -> list[dict]:
        return self._read_jsonl(self.edges_path)

    def ensure_node(self, node_id: str, ntype: str = "entity", props: dict | None = None) -> str:
        node_id = slug(node_id)
        existing = self.nodes()
        if node_id not in existing:
            self._append_jsonl(self.nodes_path, {
                "id": node_id, "type": ntype, "props": props or {}, "created": now_iso(),
            })
        return node_id

    def add_edge(self, etype: str, src: str, dst: str, *, confidence: float,
                 source: str | None, t_valid: str) -> dict:
        self.require_edge_type(etype)
        src, dst = self.ensure_node(src), self.ensure_node(dst)
        rows = self.edges()
        edge = {
            "id": f"e{len(rows) + 1:05d}",
            "etype": etype, "src": src, "dst": dst,
            "confidence": round(confidence, 3),
            "source": source,
            "t_valid": t_valid, "t_invalid": FOREVER,
            "t_created": now_iso(), "t_expired": FOREVER,
        }
        self._append_jsonl(self.edges_path, edge)
        return edge

    @staticmethod
    def true_at(edge: dict, when: str) -> bool:
        return edge["t_valid"] <= when < edge["t_invalid"] and edge["t_expired"] == FOREVER

    def current_edges(self, when: str) -> list[dict]:
        return [e for e in self.edges() if self.true_at(e, when)]

    def close_edges(self, etype: str, src: str, dst: str | None, *, t_invalid: str) -> list[dict]:
        """Invalidate matching currently-true edges. Never deletes — history survives."""
        self.require_edge_type(etype)
        src = slug(src)
        rows = self.edges()
        closed = []
        for e in rows:
            if e["etype"] == etype and e["src"] == src and self.true_at(e, now_iso()):
                if dst is not None and e["dst"] != slug(dst):
                    continue
                e["t_invalid"] = t_invalid
                closed.append(e)
        if closed:
            self._write_jsonl(self.edges_path, rows)
        return closed

    # -- episodes: raw immutable events (memory graphs) ----------------------

    def add_episode(self, text: str) -> dict:
        rows = self._read_jsonl(self.episodes_path)
        episode = {"id": f"ep{len(rows) + 1:05d}", "time": now_iso(), "text": text.strip()}
        self._append_jsonl(self.episodes_path, episode)
        return episode

    # -- inbox: one file per contribution, so parallel writers never collide ---

    def inbox_add(self, text: str, kind: str, agent: str | None, run: str | None) -> dict:
        os.makedirs(self.inbox_path, exist_ok=True)
        stamp = datetime.now(timezone.utc).strftime("%Y%m%dT%H%M%S")
        item_id = f"in-{stamp}-{os.urandom(3).hex()}"
        item = {"id": item_id, "kind": kind, "text": text.strip(),
                "agent": agent, "run": run, "created": now_iso()}
        self._write_atomic(os.path.join(self.inbox_path, f"{item_id}.json"),
                           json.dumps(item, ensure_ascii=False, indent=2) + "\n")
        return item

    def inbox_pending(self) -> list[dict]:
        if not os.path.isdir(self.inbox_path):
            return []
        items = []
        for name in sorted(os.listdir(self.inbox_path)):
            if not name.endswith(".json"):
                continue
            try:
                with open(os.path.join(self.inbox_path, name), encoding="utf-8") as f:
                    items.append(json.load(f))
            except (json.JSONDecodeError, OSError):
                print(f"warning: inbox/{name} is unreadable; skipped", file=sys.stderr)
        return items

    def inbox_archive(self, item_id: str, verdict: str, reason: str | None = None) -> dict:
        source = os.path.join(self.inbox_path, f"{item_id}.json")
        if not os.path.exists(source):
            fail(f"no pending inbox item '{item_id}' — run `inbox` to list them")
        with open(source, encoding="utf-8") as f:
            item = json.load(f)
        item["verdict"] = verdict
        item["resolved"] = now_iso()
        if reason:
            item["reason"] = reason
        os.makedirs(self.archive_path, exist_ok=True)
        self._write_atomic(os.path.join(self.archive_path, f"{item_id}.json"),
                           json.dumps(item, ensure_ascii=False, indent=2) + "\n")
        os.unlink(source)
        return item

    # -- usage telemetry: recall/neighbors hits, appended best-effort ---------

    def log_usage(self, command: str, query: str, edge_ids: list[str]) -> None:
        try:
            self._append_jsonl(self.usage_path, {
                "time": now_iso(), "cmd": command, "query": query[:200], "hits": edge_ids[:20],
            })
        except OSError:
            pass  # telemetry must never break a read

    def last_used(self) -> dict[str, str]:
        used: dict[str, str] = {}
        for row in self._read_jsonl(self.usage_path):
            for edge_id in row.get("hits", []):
                if row.get("time", "") > used.get(edge_id, ""):
                    used[edge_id] = row["time"]
        return used


def edge_text(e: dict) -> str:
    return f"{e['src']} {e['etype'].replace('_', ' ')} {e['dst']}"


def edge_line(e: dict) -> str:
    window = "" if e["t_invalid"] == FOREVER else f"  [until {e['t_invalid'][:10]}]"
    src_note = f"  (from {e['source']})" if e.get("source") else ""
    return f"{e['id']}  {e['src']} --{e['etype']}--> {e['dst']}  conf {e['confidence']}{window}{src_note}"


# ---------------------------------------------------------------------------
# Vector entry: deterministic bag-of-tokens embedding (lexical, offline).
# Recomputed on demand, so summaries can never go stale.
# ---------------------------------------------------------------------------

def hash_embed(text: str, dims: int = 256) -> list[float]:
    counts = [0.0] * dims
    for token in _TOKEN.findall(text.lower()):
        counts[zlib.crc32(token.encode()) % dims] += 1.0
    norm = math.sqrt(sum(c * c for c in counts))
    return counts if norm == 0 else [c / norm for c in counts]


def cosine(a: list[float], b: list[float]) -> float:
    return sum(x * y for x, y in zip(a, b))


def node_summary(store: Store, node_id: str, nodes: dict[str, dict], when: str) -> str:
    node = nodes.get(node_id, {})
    parts = [node_id.replace("-", " "), node.get("type", ""), *map(str, node.get("props", {}).values())]
    for e in store.current_edges(when):
        if e["src"] == node_id or e["dst"] == node_id:
            parts.append(edge_text(e))
    return " ".join(parts)


def recall(store: Store, query: str, when: str, k: int, max_hops: int = 2) -> list[tuple[float, dict, str, int]]:
    """ENTRY -> EXPAND -> FILTER -> RANK. Returns (score, edge, entry_node, hops)."""
    nodes = store.nodes()
    if not nodes:
        return []
    qv = hash_embed(query)
    entry_scores = sorted(
        ((cosine(qv, hash_embed(node_summary(store, nid, nodes, when))), nid) for nid in nodes),
        reverse=True,
    )[:4]

    valid = store.current_edges(when)
    by_node: dict[str, list[dict]] = {}
    for e in valid:
        by_node.setdefault(e["src"], []).append(e)
        by_node.setdefault(e["dst"], []).append(e)

    results: dict[str, tuple[float, dict, str, int]] = {}
    for entry_sim, entry_node in entry_scores:
        if entry_sim <= 0:
            continue
        frontier, seen = {entry_node}, {entry_node}
        for hop in range(max_hops + 1):
            next_frontier = set()
            for nid in frontier:
                for e in by_node.get(nid, []):
                    sim = cosine(qv, hash_embed(edge_text(e)))
                    score = 0.5 * sim + 0.25 * entry_sim + 0.15 / (1 + hop) + 0.10 * e["confidence"]
                    if e["id"] not in results or results[e["id"]][0] < score:
                        results[e["id"]] = (score, e, entry_node, hop)
                    for other in (e["src"], e["dst"]):
                        if other not in seen:
                            seen.add(other)
                            next_frontier.add(other)
            frontier = next_frontier
    return sorted(results.values(), key=lambda r: -r[0])[:k]


# ---------------------------------------------------------------------------
# Execution graph: tasks as nodes, dependencies as edges, file as checkpoint.
# ---------------------------------------------------------------------------

EXEC_STATUSES = ("pending", "in_progress", "done", "blocked")


def exec_load(store: Store) -> dict:
    if not os.path.exists(store.exec_path):
        return {"nodes": []}
    with open(store.exec_path, encoding="utf-8") as f:
        return json.load(f)


def exec_save(store: Store, data: dict) -> None:
    store._write_atomic(store.exec_path, json.dumps(data, indent=2, ensure_ascii=False) + "\n")


def exec_find(data: dict, node_id: str) -> dict:
    for n in data["nodes"]:
        if n["id"] == node_id:
            return n
    fail(f"no execution node '{node_id}' — run exec-status to list nodes")


def exec_validate(data: dict) -> list[str]:
    problems = []
    ids = {n["id"] for n in data["nodes"]}
    for n in data["nodes"]:
        for dep in n.get("depends_on", []):
            if dep not in ids:
                problems.append(f"{n['id']} depends on unknown node '{dep}'")
        if n.get("status") not in EXEC_STATUSES:
            problems.append(f"{n['id']} has invalid status '{n.get('status')}'")

    # Cycle detection: a cyclic dependency deadlocks the loop forever.
    colors: dict[str, int] = {}

    def dfs(nid: str, path: list[str]) -> None:
        colors[nid] = 1
        for dep in exec_find(data, nid).get("depends_on", []) if nid in ids else []:
            if colors.get(dep) == 1:
                problems.append(f"dependency cycle: {' -> '.join(path + [dep])}")
            elif colors.get(dep, 0) == 0 and dep in ids:
                dfs(dep, path + [dep])
        colors[nid] = 2

    for n in data["nodes"]:
        if colors.get(n["id"], 0) == 0:
            dfs(n["id"], [n["id"]])
    return problems


def exec_unblocked(data: dict) -> list[dict]:
    done = {n["id"] for n in data["nodes"] if n["status"] == "done"}
    return [
        n for n in data["nodes"]
        if n["status"] == "pending" and all(dep in done for dep in n.get("depends_on", []))
    ]


# ---------------------------------------------------------------------------
# Health and mechanical maintenance — the deterministic side of curation.
# ---------------------------------------------------------------------------

def compute_health(store: Store, stale_days: int = 30) -> dict:
    """Everything a curator (or the Looper janitor) needs to decide whether this
    graph needs attention. Pure report — never mutates."""
    now = now_iso()
    nodes = store.nodes()
    edges = store.edges()
    current = [e for e in edges if store.true_at(e, now)]
    episodes = store._read_jsonl(store.episodes_path)
    pending = store.inbox_pending()
    used = store.last_used()
    usage_rows = store._read_jsonl(store.usage_path)

    # Competing facts: several currently-true objects for one (subject, edge type).
    # Sometimes legitimate (a service `uses` many libraries) — the curator judges.
    by_key: dict[tuple[str, str], list[str]] = {}
    for e in current:
        by_key.setdefault((e["src"], e["etype"]), []).append(e["dst"])
    competing = [
        {"src": src, "etype": etype, "dsts": sorted(set(dsts))}
        for (src, etype), dsts in sorted(by_key.items())
        if len(set(dsts)) > 1
    ]

    horizon = datetime.now(timezone.utc).timestamp() - stale_days * 86400
    horizon_iso = datetime.fromtimestamp(horizon, timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ")
    stale = [e["id"] for e in current
             if used.get(e["id"], e["t_created"]) < horizon_iso]

    problems: list[str] = []
    ontology = store.ontology()
    for e in current:
        for endpoint in (e["src"], e["dst"]):
            if endpoint not in nodes:
                problems.append(f"edge {e['id']} references unknown node '{endpoint}'")
        if ontology and e["etype"] not in ontology:
            problems.append(f"edge {e['id']} uses edge type '{e['etype']}' missing from ontology.json")
    if os.path.exists(store.exec_path):
        try:
            problems.extend(exec_validate(exec_load(store)))
        except (json.JSONDecodeError, OSError) as ex:
            problems.append(f"execution.json unreadable: {ex}")

    oldest_hours = 0.0
    if pending:
        oldest = min(p.get("created", now) for p in pending)
        try:
            delta = datetime.now(timezone.utc) - datetime.strptime(oldest, "%Y-%m-%dT%H:%M:%SZ").replace(tzinfo=timezone.utc)
            oldest_hours = round(delta.total_seconds() / 3600, 1)
        except ValueError:
            pass

    return {
        "tool_version": 2,
        "checked": now,
        "nodes": len(nodes),
        "facts_current": len(current),
        "facts_total": len(edges),
        "episodes": len(episodes),
        "inbox_pending": len(pending),
        "inbox_oldest_hours": oldest_hours,
        "competing_count": len(competing),
        "competing": competing[:10],
        "stale_count": len(stale),
        "stale_days": stale_days,
        "usage_events": len(usage_rows),
        "problem_count": len(problems),
        "problems": problems[:20],
    }


def decay_confidence(store: Store, days: int, factor: float, floor: float) -> int:
    """Down-weight currently-true facts nothing has recalled in `days`. Mechanical,
    deterministic, floor-bounded — decayed facts stay retrievable, just ranked lower."""
    now = now_iso()
    used = store.last_used()
    horizon = datetime.now(timezone.utc).timestamp() - days * 86400
    horizon_iso = datetime.fromtimestamp(horizon, timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ")
    rows = store.edges()
    decayed = 0
    for e in rows:
        if not store.true_at(e, now):
            continue
        if used.get(e["id"], e["t_created"]) >= horizon_iso:
            continue
        lowered = max(floor, round(e["confidence"] * factor, 3))
        if lowered < e["confidence"]:
            e["confidence"] = lowered
            decayed += 1
    if decayed:
        store._write_jsonl(store.edges_path, rows)
    return decayed


# ---------------------------------------------------------------------------
# Commands
# ---------------------------------------------------------------------------

def main() -> None:
    parser = argparse.ArgumentParser(prog="loopergraph", description=__doc__)
    parser.add_argument("--dir", default=".", help="graph folder (default: current directory)")
    sub = parser.add_subparsers(dest="cmd")

    p = sub.add_parser("add-node", help="declare a typed node")
    p.add_argument("id"); p.add_argument("--type", default="entity"); p.add_argument("--props", default="{}")

    p = sub.add_parser("add", help="add a typed fact (edge) valid from now (or --from)")
    p.add_argument("etype"); p.add_argument("src"); p.add_argument("dst")
    p.add_argument("--confidence", type=float, default=0.9)
    p.add_argument("--source", help="provenance, e.g. an episode id")
    p.add_argument("--from", dest="t_valid", help="when it became true (default now)")

    p = sub.add_parser("supersede", help="close the current fact and record its replacement")
    p.add_argument("etype"); p.add_argument("src"); p.add_argument("new_dst")
    p.add_argument("--confidence", type=float, default=0.9)
    p.add_argument("--source")

    p = sub.add_parser("invalidate", help="mark a fact no longer true (never deletes)")
    p.add_argument("etype"); p.add_argument("src"); p.add_argument("--dst")
    p.add_argument("--at", dest="when", help="when it stopped being true (default now)")

    p = sub.add_parser("neighbors", help="facts touching a node")
    p.add_argument("id"); p.add_argument("--type", dest="etype"); p.add_argument("--at")

    p = sub.add_parser("path", help="how two nodes connect, with compounded confidence")
    p.add_argument("src"); p.add_argument("dst"); p.add_argument("--max-hops", type=int, default=3)
    p.add_argument("--at")

    p = sub.add_parser("missing", help="nodes of a type lacking an edge type (absence query)")
    p.add_argument("node_type"); p.add_argument("etype")

    p = sub.add_parser("history", help="all facts about a node, including superseded ones")
    p.add_argument("id")

    p = sub.add_parser("episode", help="append a raw immutable episode (memory graphs)")
    p.add_argument("text")

    p = sub.add_parser("recall", help="fuzzy query: vector entry, graph expansion, validity filter")
    p.add_argument("query"); p.add_argument("--at"); p.add_argument("-k", type=int, default=8)

    p = sub.add_parser("remember", help="drop a contribution in the inbox for the curator to merge")
    p.add_argument("text")
    p.add_argument("--kind", default="note", choices=["note", "lesson", "episode", "proposal"])
    p.add_argument("--agent", help="who contributed it"); p.add_argument("--run", help="originating run id")

    p = sub.add_parser("inbox", help="pending contributions awaiting curation")
    p.add_argument("--json", action="store_true")

    p = sub.add_parser("inbox-merge", help="archive an inbox item as merged (extract its facts FIRST)")
    p.add_argument("id")

    p = sub.add_parser("inbox-reject", help="archive an inbox item without merging")
    p.add_argument("id"); p.add_argument("--reason", default="")

    p = sub.add_parser("health", help="curation report: inbox, competing facts, stale facts, problems")
    p.add_argument("--json", action="store_true")
    p.add_argument("--stale-days", type=int, default=30)

    p = sub.add_parser("decay", help="down-weight facts unused for N days (mechanical, floor-bounded)")
    p.add_argument("--days", type=int, default=30)
    p.add_argument("--factor", type=float, default=0.9)
    p.add_argument("--floor", type=float, default=0.25)

    sub.add_parser("status", help="store overview")

    p = sub.add_parser("exec-add", help="add a work item")
    p.add_argument("id"); p.add_argument("title"); p.add_argument("--after", default="", help="comma-separated dependency ids")

    sub.add_parser("exec-next", help="unblocked pending work items")
    for name in ("exec-start", "exec-done", "exec-block"):
        p = sub.add_parser(name)
        p.add_argument("id"); p.add_argument("--note", default="")
    sub.add_parser("exec-validate", help="static checks: unknown deps, cycles, bad statuses")
    sub.add_parser("exec-status", help="all work items with status")

    sub.add_parser("help")
    args = parser.parse_args()
    if args.cmd in (None, "help"):
        parser.print_help()
        return

    store = Store(os.path.abspath(args.dir))

    # Shared-infrastructure guard: any command that rewrites store files runs
    # under the advisory lock so parallel loops can't interleave.
    mutating = {"add-node", "add", "supersede", "invalidate", "episode", "decay",
                "exec-add", "exec-start", "exec-done", "exec-block"}
    lock = store.lock() if args.cmd in mutating else contextlib.nullcontext()
    with lock:
        dispatch(args, store)


def dispatch(args: argparse.Namespace, store: Store) -> None:
    if args.cmd == "add-node":
        try:
            props = json.loads(args.props)
        except json.JSONDecodeError:
            fail("--props must be a JSON object")
        node_id = store.ensure_node(args.id, args.type, props)
        print(f"node {node_id} ({args.type})")

    elif args.cmd == "add":
        edge = store.add_edge(args.etype, args.src, args.dst, confidence=args.confidence,
                              source=args.source, t_valid=parse_when(args.t_valid))
        print(f"added {edge_line(edge)}")

    elif args.cmd == "supersede":
        when = now_iso()
        closed = store.close_edges(args.etype, args.src, None, t_invalid=when)
        edge = store.add_edge(args.etype, args.src, args.new_dst, confidence=args.confidence,
                              source=args.source, t_valid=when)
        for c in closed:
            print(f"closed {edge_line(c)}")
        print(f"added  {edge_line(edge)}")

    elif args.cmd == "invalidate":
        closed = store.close_edges(args.etype, args.src, args.dst, t_invalid=parse_when(args.when))
        if not closed:
            fail("no currently-true fact matches — check `history` for what exists")
        for c in closed:
            print(f"closed {edge_line(c)}")

    elif args.cmd == "neighbors":
        when = parse_when(args.at)
        node_id = slug(args.id)
        hits = [e for e in store.current_edges(when)
                if node_id in (e["src"], e["dst"]) and (not args.etype or e["etype"] == args.etype)]
        if not hits:
            print(f"no facts about {node_id}" + (f" at {when[:10]}" if args.at else ""))
        for e in hits:
            print(edge_line(e))

    elif args.cmd == "path":
        when = parse_when(args.at)
        src, dst = slug(args.src), slug(args.dst)
        valid = store.current_edges(when)
        adjacency: dict[str, list[tuple[dict, str]]] = {}
        for e in valid:
            adjacency.setdefault(e["src"], []).append((e, e["dst"]))
            adjacency.setdefault(e["dst"], []).append((e, e["src"]))
        found: list[list[dict]] = []

        def walk(nid: str, target: str, sofar: list[dict], seen: set[str]) -> None:
            if len(sofar) > args.max_hops or len(found) >= 5:
                return
            for e, other in adjacency.get(nid, []):
                if other in seen:
                    continue
                if other == target:
                    found.append(sofar + [e])
                else:
                    walk(other, target, sofar + [e], seen | {other})

        walk(src, dst, [], {src})
        if not found:
            print(f"no path from {src} to {dst} within {args.max_hops} hops")
        for p_edges in sorted(found, key=len):
            conf = 1.0
            for e in p_edges:
                conf *= e["confidence"]
            steps = " ; ".join(edge_text(e) for e in p_edges)
            print(f"[{len(p_edges)} hops, confidence {conf:.2f}] {steps}")
            if conf < 0.6:
                print("  ^ low confidence — verify before relying on this path")

    elif args.cmd == "missing":
        nodes = store.nodes()
        have = {e["src"] for e in store.current_edges(now_iso()) if e["etype"] == args.etype}
        lacking = [nid for nid, n in nodes.items() if n.get("type") == args.node_type and nid not in have]
        if not lacking:
            print(f"every {args.node_type} node has {args.etype}")
        for nid in lacking:
            print(nid)

    elif args.cmd == "history":
        node_id = slug(args.id)
        hits = [e for e in store.edges() if node_id in (e["src"], e["dst"])]
        if not hits:
            print(f"no facts ever recorded about {node_id}")
        for e in hits:
            state = "current" if store.true_at(e, now_iso()) else f"ended {e['t_invalid'][:10]}"
            print(f"{edge_line(e)}  [{e['t_valid'][:10]} → {state}]")

    elif args.cmd == "episode":
        episode = store.add_episode(args.text)
        print(f"{episode['id']} recorded. Now extract durable facts from it: "
              f"loopergraph add <etype> <src> <dst> --source {episode['id']}")

    elif args.cmd == "recall":
        when = parse_when(args.at)
        results = recall(store, args.query, when, args.k)
        if not results:
            print("nothing recalled — the graph may be empty, or nothing matches; try `status`")
        for score, e, entry, hops in results:
            print(f"[{score:.2f}] {edge_text(e)}  (via {entry}, hop {hops}, conf {e['confidence']}, {e['id']})")
        if results:
            store.log_usage("recall", args.query, [r[1]["id"] for r in results])

    elif args.cmd == "remember":
        item = store.inbox_add(args.text, args.kind, args.agent, args.run)
        print(f"{item['id']} queued for curation ({item['kind']}). The graph's curator merges the inbox; "
              "canonical facts stay consistent because only it writes them.")

    elif args.cmd == "inbox":
        pending = store.inbox_pending()
        if args.json:
            print(json.dumps(pending, ensure_ascii=False, indent=2))
        elif not pending:
            print("inbox is empty — nothing awaiting curation")
        else:
            for item in pending:
                who = f" from {item['agent']}" if item.get("agent") else ""
                print(f"{item['id']}  [{item['kind']}]{who}  {item['created']}\n  {item['text'][:160]}")

    elif args.cmd == "inbox-merge":
        item = store.inbox_archive(args.id, "merged")
        print(f"{item['id']} archived as merged. If you haven't extracted its durable facts yet, do it now "
              "(`add`/`episode` with --source noting this item).")

    elif args.cmd == "inbox-reject":
        item = store.inbox_archive(args.id, "rejected", args.reason or None)
        print(f"{item['id']} archived as rejected" + (f": {args.reason}" if args.reason else "."))

    elif args.cmd == "health":
        report = compute_health(store, args.stale_days)
        if args.json:
            print(json.dumps(report, ensure_ascii=False, indent=2))
        else:
            print(f"nodes {report['nodes']}  facts {report['facts_current']}/{report['facts_total']}  "
                  f"episodes {report['episodes']}  usage events {report['usage_events']}")
            print(f"inbox: {report['inbox_pending']} pending"
                  + (f" (oldest {report['inbox_oldest_hours']}h)" if report['inbox_pending'] else ""))
            print(f"competing facts: {report['competing_count']}   stale (> {report['stale_days']}d unused): {report['stale_count']}")
            for c in report["competing"]:
                print(f"  ? {c['src']} --{c['etype']}--> {', '.join(c['dsts'])}")
            for p_msg in report["problems"]:
                print(f"  problem: {p_msg}")
            if not report["problems"]:
                print("no structural problems")

    elif args.cmd == "decay":
        count = decay_confidence(store, args.days, args.factor, args.floor)
        print(f"decayed {count} fact(s) unused for {args.days}+ days (factor {args.factor}, floor {args.floor})")

    elif args.cmd == "status":
        nodes, edges = store.nodes(), store.edges()
        current = [e for e in edges if store.true_at(e, now_iso())]
        episodes = store._read_jsonl(store.episodes_path)
        print(f"nodes: {len(nodes)}   facts: {len(current)} current / {len(edges)} total   episodes: {len(episodes)}")
        types = store.ontology()
        if types:
            print("edge types: " + ", ".join(sorted(types)))
        if os.path.exists(store.exec_path):
            data = exec_load(store)
            by_status: dict[str, int] = {}
            for n in data["nodes"]:
                by_status[n["status"]] = by_status.get(n["status"], 0) + 1
            print("execution: " + (", ".join(f"{v} {k}" for k, v in sorted(by_status.items())) or "empty"))

    elif args.cmd == "exec-add":
        data = exec_load(store)
        node_id = slug(args.id)
        if any(n["id"] == node_id for n in data["nodes"]):
            fail(f"execution node '{node_id}' already exists")
        deps = [slug(d) for d in args.after.split(",") if d.strip()]
        data["nodes"].append({"id": node_id, "title": args.title, "status": "pending",
                              "depends_on": deps, "notes": [], "updated": now_iso()})
        problems = exec_validate(data)
        if problems:
            fail("rejected — " + "; ".join(problems))
        exec_save(store, data)
        print(f"added {node_id}" + (f" (after {', '.join(deps)})" if deps else ""))

    elif args.cmd == "exec-next":
        data = exec_load(store)
        candidates = exec_unblocked(data)
        in_progress = [n for n in data["nodes"] if n["status"] == "in_progress"]
        for n in in_progress:
            print(f"in progress: {n['id']} — {n['title']} (finish or block this first)")
        if not candidates and not in_progress:
            pending = [n for n in data["nodes"] if n["status"] == "pending"]
            print("all done" if not pending else
                  f"nothing unblocked; {len(pending)} pending are waiting on dependencies (exec-validate to check for cycles)")
        for n in candidates:
            print(f"ready: {n['id']} — {n['title']}")

    elif args.cmd in ("exec-start", "exec-done", "exec-block"):
        data = exec_load(store)
        node = exec_find(data, slug(args.id))
        if args.cmd == "exec-start":
            missing_deps = [d for d in node.get("depends_on", [])
                            if exec_find(data, d)["status"] != "done"]
            if missing_deps:
                fail(f"dependencies not complete: {', '.join(missing_deps)}")
            node["status"] = "in_progress"
        elif args.cmd == "exec-done":
            node["status"] = "done"
        else:
            node["status"] = "blocked"
        if args.note:
            node.setdefault("notes", []).append({"time": now_iso(), "note": args.note})
        node["updated"] = now_iso()
        exec_save(store, data)
        print(f"{node['id']} -> {node['status']}")

    elif args.cmd == "exec-validate":
        problems = exec_validate(exec_load(store))
        if not problems:
            print("execution graph is valid")
        for p_msg in problems:
            print(f"problem: {p_msg}")
        if problems:
            sys.exit(1)

    elif args.cmd == "exec-status":
        data = exec_load(store)
        if not data["nodes"]:
            print("execution graph is empty — exec-add the first work items")
        for n in data["nodes"]:
            deps = f"  after: {', '.join(n['depends_on'])}" if n.get("depends_on") else ""
            note = f"  ({n['notes'][-1]['note']})" if n.get("notes") else ""
            print(f"[{n['status']:<11}] {n['id']} — {n['title']}{deps}{note}")


if __name__ == "__main__":
    main()
