# SP4 — Read-only safeguard

**Date:** 2026-06-22
**Status:** Design (approved) — ready for implementation plan.
**Parent:** [Vitally MCP feedback backlog decomposition](./2026-06-10-vitally-mcp-feedback-decomposition-design.md)
**Feedback item:** P1.5 (safety) — CS reported goals going missing, suspected accidental MCP deletions; flagged *urgent given the rollout plans*.

## 1. Purpose

Give a Vitally MCP deployment a **guaranteed, deployment-level write-lock** that does not depend
on the (not-yet-rolled-out) per-user Entra-group RBAC. A read-only deployment must make
create/update/delete **impossible** and not even advertise those tools — so a CS deployment can't
accidentally mutate or delete customer data.

This is the in-repo slice of SP4. The finer-grained per-user RBAC rollout (assigning Entra
Reader/Editor/Admin groups, the Auth0 post-login Action, enabling `LiveGroupCheck`) is ops work
outside this repo and is captured as a runbook, not code.

## 2. Design — two layers

The server-side RBAC backstop already exists (`ToolAuthorizer`, enforced in
`VitallyService.SendAsync`, mapping HTTP verb → permission tier). SP4 adds a blunt, always-on
read-only switch on top.

### Layer 1 — Enforcement (the guarantee)
- New `Authorization:ReadOnly` bool on `ToolAuthorizationOptions` (default `false`).
- In `ToolAuthorizer.EnsureAuthorizedAsync`, **before** the existing `!Enabled || NoAuth`
  early-return: if `ReadOnly` is true and the HTTP verb is mutating (anything other than `GET` —
  POST/PUT/PATCH/DELETE, and any unknown verb), throw `UnauthorizedAccessException` with a clear
  message: *"This server is deployed in read-only mode; create, update and delete operations are
  disabled."*
- Checking it before the `Enabled`/`NoAuth` gate is deliberate: the lock must hold even when
  per-user RBAC is disabled (`Authorization:Enabled=false`) or in local `NoAuth` dev — it is a
  deployment-level guarantee independent of token claims.
- Denials flow through the existing `AuditLogger.LogDenied` path (`SendAsync` already catches
  `UnauthorizedAccessException`), so read-only blocks are audited with no extra work.
- Defence-in-depth: even if a client invokes a hidden tool by name (Layer 2), Layer 1 refuses it.

### Layer 2 — Hide from `tools/list` (the UX)
- When `ReadOnly` is true, register an `AddListToolsFilter` (via `WithRequestFilters` in
  `Program.cs`) that post-filters the `tools/list` result to keep **only** tools whose annotation
  is read-only (`Annotations?.ReadOnlyHint == true`), dropping every `Create_*`/`Update_*`/
  `Delete_*`. So CS clients never see the destructive tools.
- The hidden set (non-read tools) exactly equals the Layer-1 denied set (non-GET), so the two
  layers stay consistent. (Repo convention: read/list/get tools set `ReadOnly = true`; creates set
  `ReadOnly = false`; updates/deletes set `Destructive = true`. Keeping `ReadOnlyHint == true`
  keeps exactly the reads.)
- The filter resolves `IOptions<ToolAuthorizationOptions>` from `context.Services`; when `ReadOnly`
  is false it is a pass-through (or not registered at all).
- Confirmed feasible in the pinned SDK: `WithRequestFilters` / `AddListToolsFilter` exist in
  `ModelContextProtocol(.AspNetCore) 1.3.0` (verified against the installed assemblies).

## 3. Components

- `ToolAuthorizationOptions.ReadOnly` (bool, default false). No new validation needed; it is
  independent of `Enabled`.
- `ToolAuthorizer.EnsureAuthorizedAsync` — the pre-gate read-only deny.
- `ReadOnlyToolFilter` — a small pure helper, e.g.
  `IEnumerable<Tool> FilterTools(IEnumerable<Tool> tools, bool readOnly)` returning only
  read-only-hinted tools when `readOnly` is true, all tools otherwise — so the filtering logic is
  unit-testable without a running server.
- `Program.cs` — wire the `AddListToolsFilter` that calls `ReadOnlyToolFilter` using the bound
  `ToolAuthorizationOptions`.

## 4. Non-goals (YAGNI)

- The per-user Entra-group RBAC rollout itself (runbook only — out of repo).
- Per-request / per-caller tool sets (read-only is deployment-wide, not per-user).
- A separate "read-only" permission tier — `ReadOnly` is a boolean kill switch, not a new tier.

## 5. Testing

- `ToolAuthorizerTests`: with `ReadOnly=true`, `EnsureAuthorizedAsync` throws
  `UnauthorizedAccessException` for POST/PUT/PATCH/DELETE and **allows** GET; it throws even when
  `Enabled=false` and when `NoAuth=true`; with `ReadOnly=false` (default) behaviour is unchanged.
- `ReadOnlyToolFilterTests`: returns only `ReadOnlyHint==true` tools when on; returns all when off;
  handles tools with null annotations safely.
- One integration check via the existing ASP.NET test-server harness: `tools/list` with
  `Authorization:ReadOnly=true` returns no tool whose name starts `Create_`/`Update_`/`Delete_`
  (and still returns the read tools).

## 6. Docs

- `CLAUDE.md`: document `Authorization:ReadOnly` and the two-layer behaviour (deny + hide) in the
  authorization/config section.
- `docs/runbooks/`: a new runbook covering (a) deploying read-only
  (`Authorization__ReadOnly=true`), and (b) the out-of-repo per-user RBAC rollout checklist —
  assign Entra Reader/Editor/Admin group object ids, deploy the Auth0 post-login Action mapping
  groups → `vitally:*` permissions, enable `Authorization:LiveGroupCheck`, and confirm enforcement
  on the deployed revision.

## 7. Acceptance criteria

- With `Authorization:ReadOnly=true`: every create/update/delete tool call is denied with the
  read-only message (even with `Enabled=false`/`NoAuth=true`); reads succeed.
- With `Authorization:ReadOnly=true`: `tools/list` advertises only read tools — no
  `Create_*`/`Update_*`/`Delete_*`.
- With `Authorization:ReadOnly=false` (default): behaviour and the tool list are unchanged.
- Read-only denials are audited. Full suite green; behaviour confirmed locally.
