<!--
  Copyright (c) 2026 Wiseair S.r.l.
  Task 11 deliverable — review before applying further description edits.
-->

# Tool description audit (v2.1.0)

Audit pass over all 22 Vitally MCP tool descriptions. For each tool, three questions:

1. Does the description tell the LLM **when to use this tool** vs. siblings?
2. Are required vs optional params obvious?
3. Are there silent gotchas (filters that don't work, stale caches, always-null fields)?

Items already applied in this PR are marked **APPLIED**. Items left as
recommendations for a follow-up PR are marked **RECOMMEND**.

---

## search_tools
- **Verdict**: fine. Single param, clear purpose.
- **Recommend**: none.

## search_users / get_user
- **Verdict**: fine. The `at-least-one-of` requirement on `search_users` lives in the runtime error rather than the schema; LLMs handle this via trial-and-error. Acceptable.
- **Recommend**: none.

## search_accounts (APPLIED)
- **Was**: said "returns full account payload" but didn't mention the `traits` projection or the conflict resolution.
- **Now**: mentions `traits`, the slim shape via `includeTraits=false`, and the conflict rule (`traits` wins). Distinction from `find_account_by_name` left intentional — `find_account_by_name` is deprecated.

## find_account_by_name (APPLIED)
- **Was**: described as the name-lookup tool. Overlapped 80% with `search_accounts`.
- **Now**: marked DEPRECATED in the description; runtime forwards to `search_accounts` and tags the response with `_deprecation`. Pre-existing callers unaffected.

## get_account (APPLIED)
- **Was**: full payload by id/externalId.
- **Now**: also notes that workspace-level `_warnings` (e.g. "healthScore not configured") may be attached on first call.

## list_accounts (APPLIED)
- **Was**: cursor-paginated proxy of `/resources/accounts`.
- **Now**: explains the cache-mode switch when `sortBy`/`filterTraits`/`traits` is used. Documents the field set valid for `sortBy`. Notes `from` is ignored in cache-mode.

## update_account (APPLIED)
- **Was**: said "Use to set traits like ARR" — actively encouraged a data-integrity bug.
- **Now**: explains guarded traits and the `force` requirement.

## aggregate_accounts (NEW)
- Description states the metric set, the role of `metricField`, and the `groupBy=null` aggregation case.

## get_account_health
- **Verdict**: fine. Could mention that this is a separate endpoint from the `healthScore` field on the account, but the LLM rarely confuses them.
- **Recommend**: none.

## get_account_conversations / list_conversations (APPLIED)
- **Was**: just said "paginated list".
- **Now**: documents the `includeAccount=false` default and that `accountId` is preserved.

## get_account_tasks (APPLIED)
- **Was**: said the upstream may ignore `status` — actively misleading.
- **Now**: status filter is enforced client-side. Documents: (a) the predicate for each value, (b) the 5-page scan cap, (c) `pagesScanned` / `truncated` return fields, (d) `includeAccount=false` default, (e) `descriptionFormat='plain'` default.

## list_tasks (APPLIED)
- **Was**: noted the API doesn't support server-side filters.
- **Now**: same plus `includeAccount` and `descriptionFormat` defaults.

## get_account_notes / list_notes / get_note_by_id (APPLIED)
- **Was**: terse "get notes" / "get note metadata".
- **Now**: documents the plain-text default for body fields and the `includeAccount=false` default on list endpoints.

## create_account_note
- **Verdict**: fine. Two required params, single side effect.
- **Recommend**: none.

## list_projects / get_project / list_organizations
- **Verdict**: fine. Project/org data isn't heavily used by the team yet; punt on optimisation until usage warrants it.
- **Recommend**: none.

## refresh_accounts
- **Verdict**: fine. Single-purpose, clear.
- **Recommend**: consider wiring this into the `_warnings` flow so a manual refresh re-runs the health/NPS sample. Low priority — the current "once per server lifetime" semantics are documented.

---

## Cross-cutting recommendations (not yet applied)

1. **Cursor pagination semantics** — every `list_*` tool says "Cursor token from the previous response's `next` field". The LLM occasionally tries to construct a cursor from scratch. Consider a single sentence: _"Treat `from` as opaque; only ever pass back a value the server returned to you."_
2. **Cache freshness** — `search_accounts`, `find_account_by_name`, `aggregate_accounts`, and cache-mode `list_accounts` all read from a cache that's lazily filled (max 100 accounts). For workspaces with > 100 accounts this is a silent under-count. Recommend either: (a) bumping the cache size to 1000 on first load, or (b) adding a `cacheStale` boolean on each response when `next` was non-null at fill time. Option (b) is the bigger lift but the more honest signal.
3. **`accountId` semantics** — most account-aware tools accept either a Vitally UUID or an externalId. Worth saying so once in a top-level `tools/list` description, since the LLM occasionally infers from the param name that only UUIDs are accepted.

These are intentionally left for a follow-up PR — the spec said review the report first.
