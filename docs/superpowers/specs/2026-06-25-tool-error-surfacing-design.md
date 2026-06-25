# Tool-error surfacing

**Date:** 2026-06-25
**Status:** Design (approved) — ready for implementation plan.
**Context:** Cross-cutting follow-up surfaced during SP4. Not part of the original SP1–SP5 decomposition.

## 1. Purpose

Restore a documented design goal that the MCP SDK currently defeats. `VitallyService.SendAsync`
deliberately throws `HttpRequestException` carrying the Vitally failure body (truncated to 1 KB) so
"MCP clients see the actual failure reason" (CLAUDE.md). But the SDK only surfaces an exception's
message to the client when it is an `McpException`; every other exception becomes a generic
*"An error occurred invoking 'X'."* So the LLM never sees the Vitally reason, the read-only/RBAC
denial reason, or our validation messages — observed repeatedly (SP1 `/search` diagnosis, get-by-id
not-found, SP4 read-only denial).

## 2. Design

A single **CallTool request filter** plus a small pure helper. No change to `VitallyService` or
`ToolAuthorizer` (keeping MCP-protocol concerns out of those layers — we do **not** switch them to
throw `McpException`).

### `ToolErrorResult` (new, namespace `VitallyMcp`)
- `static bool IsSurfaceable(Exception ex)` — true for `HttpRequestException`,
  `UnauthorizedAccessException`, `ArgumentException`; false otherwise.
- `static CallToolResult Build(Exception ex)` — returns
  `new CallToolResult { IsError = true, Content = [ new TextContentBlock { Text = ex.Message } ] }`.
- (`CallToolResult` / `TextContentBlock` are `ModelContextProtocol.Protocol` types.)

### The filter (`Program.cs`)
Registered via `AddCallToolFilter` (confirmed present in the installed `ModelContextProtocol` 1.3.0):

```
filters.AddCallToolFilter(next => async (context, ct) =>
{
    try { return await next(context, ct); }
    catch (Exception ex) when (ToolErrorResult.IsSurfaceable(ex)) { return ToolErrorResult.Build(ex); }
});
```

The exception **filter** (`when`) means only the surfaceable types are caught; `McpException`,
`OperationCanceledException`, and any genuinely-unexpected exception propagate untouched to the SDK,
which preserves its protocol-error / cancellation handling and the generic message for the
unexpected case (so we never leak internal exception detail we didn't choose to expose).

### `Program.cs` restructure
There is currently a `WithRequestFilters` block added **only when** `readOnlyMode` (the SP4
`tools/list` filter). The CallTool filter must register **always**. So make `WithRequestFilters`
unconditional: always add the CallTool filter inside it, and keep the read-only `AddListToolsFilter`
gated on `readOnlyMode` within the same block.

## 3. Rationale for the surfaceable set

- `HttpRequestException` — the Vitally API failure body (`SendAsync` includes + 1 KB-truncates it);
  the whole point of the SendAsync design.
- `UnauthorizedAccessException` — the read-only / RBAC denial reason (our own controlled strings).
- `ArgumentException` — our actionable client-side validation (e.g. instance-search criteria rules,
  bad ISO date).
- Everything else stays generic: avoids leaking unexpected internal exception messages.

**PII note:** surfacing `HttpRequestException.Message` exposes the Vitally 4xx body to the calling
LLM. It is the authenticated user's own data, already truncated to 1 KB, and is typically a
validation string — this is the documented intent. The audit log retains full server-side detail
regardless; bodies are still never written to the audit log.

## 4. Non-goals (YAGNI)

- Changing `SendAsync` / `ToolAuthorizer` to throw `McpException` (rejected — couples those layers to
  the MCP SDK and complicates the audit catch).
- Surfacing stack traces (only `ex.Message`).
- Surfacing arbitrary/unexpected exception types.

## 5. Testing

- `ToolErrorResultTests`: `IsSurfaceable` true for each of the three types and false for a
  representative other (e.g. `InvalidOperationException`); `Build` returns `IsError == true` with a
  single text block equal to `ex.Message`.
- Extend `ReadOnlyToolsListTests` (or add a sibling) with an end-to-end check: in read-only mode a
  `Create_organization` tool call now returns a result whose text contains *"read-only mode"*
  (proving the CallTool filter surfaces the denial through the real host) rather than the generic
  message.
- Live check: a tool call that triggers a surfaceable exception returns the real message.

## 6. Docs

- `CLAUDE.md`: update the `SendAsync` error-handling note to state that a CallTool request filter
  (`ToolErrorResult` + `AddCallToolFilter`) is what actually delivers the Vitally failure reason to
  the client for `HttpRequestException`/`UnauthorizedAccessException`/`ArgumentException`, while
  other exceptions remain the SDK's generic error.

## 7. Acceptance criteria

- A Vitally 4xx surfaces its (truncated) body text to the caller, not the generic message.
- A write in read-only mode surfaces the "read-only mode" message.
- An invalid argument surfaces its validation message.
- An unexpected exception still yields the SDK's generic error (no internal detail leaked).
- Full suite green; behaviour confirmed live.
