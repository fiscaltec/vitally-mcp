# SP2 — Conversations: surface `source` & `status` by default

**Date:** 2026-06-22
**Status:** Design (approved) — ready for implementation plan.
**Parent:** [Vitally MCP feedback backlog decomposition](./2026-06-10-vitally-mcp-feedback-decomposition-design.md)
**Feedback item:** P1.2 — conversations list can't distinguish genuine support tickets from calendar invites/emails.

## 1. Purpose

Let a caller tell real support tickets apart from calendar/email conversations **from the list
response alone**, without an expensive per-record fetch — by including the conversation `source`
(e.g. `outlook`, `intercom`) and `status` in the default field set.

## 2. Verified finding (live, against real Vitally EU)

A direct `GET /resources/conversations` returns each conversation with:

```
id, createdAt, updatedAt, externalId, subject, status, rating, source, traits
```

(envelope: `{results, next, atEnd}`). So `source`, `status` and `subject` **are** present on the
list payload — no per-record fetch needed. Sample rows were all `source=outlook, status=(empty)`,
i.e. exactly the calendar/email items the feedback wants to filter out. This was verified live
because the docs list example omitted these fields and the `/search` bug taught us not to trust
the docs on response shapes.

## 3. The change

Add `source` and `status` to the `conversations` entry in `VitallyService.ResourceDefaultFields`:

- **From:** `id, externalId, subject, authorId, accountId, organizationId`
- **To:** `id, externalId, subject, status, source, authorId, accountId, organizationId`

Because all conversation read tools resolve defaults from the same `conversations` key, this
surfaces `source`/`status` on `List_conversations`, `List_conversations_by_account`,
`List_conversations_by_organization` **and** `Get_conversation` with no new parameters. Fields are
included only when present (existing `TryGetProperty` behaviour), so the change is safe.

## 4. Non-goals

- **`rating`** — also in the payload, but not requested by the feedback (YAGNI). Callers can still
  request it explicitly via `fields`.
- **No new parameters**, and **no server-side filtering by `source`** — Vitally's list endpoint
  offers no `source` filter, and SP3 owns any client-side page-and-filter work.

## 5. Testing

- Update `TestHelpers.GetSampleConversationJson()` to include `source` and `status` (mirroring the
  **real** list shape — applying the `/search` lesson; the current sample omits them).
- Add a test asserting `List_conversations` with no `fields` returns `source` and `status`.
- Existing conversation tests must still pass.

## 6. Docs

- `CLAUDE.md`: update the **Conversations** row in the default-fields table to
  `id, externalId, subject, status, source, authorId, accountId, organizationId`.

## 7. Acceptance criteria

- `List_conversations` (no `fields`) returns `source` and `status` for conversations that have
  them.
- `Get_conversation` likewise surfaces `source`/`status` by default.
- Test suite green; the conversation sample reflects the real list shape.
