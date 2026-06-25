# Read-only deployments & per-user RBAC rollout

## Deploy read-only (immediate safety net)

Set `Authorization__ReadOnly=true` on the Container App revision. Effect:
- All create/update/delete tool calls are denied (`ToolAuthorizer`, before the RBAC/NoAuth gate),
  audited via `LogDenied`.
- `tools/list` advertises only read tools (no `Create_*`/`Update_*`/`Delete_*`).
- Independent of `Authorization:Enabled` and of any Entra-group/Auth0 setup — a guaranteed lock.

Use this for CS-facing deployments until per-user RBAC (below) is rolled out and verified.

## Per-user RBAC rollout (finer-grained; out of the application repo)

The server-side RBAC backstop already exists (`ToolAuthorizer` maps HTTP verb → `vitally:read` /
`vitally:write` / `vitally:delete`). To grant tiers per user via Entra group membership:

1. **Entra:** create/confirm three security groups (Reader, Editor, Admin); collect their object ids.
2. **App config:** set `Authorization__ReaderGroupId` / `EditorGroupId` / `AdminGroupId` to those ids;
   set `Authorization__LiveGroupCheck=true` (resolves live membership via Microsoft Graph, so
   revocations take effect within the cache window). Requires the managed identity to hold Graph
   `GroupMember.Read.All`.
3. **Auth0 (alternative/auxiliary):** a post-login Action mapping Entra group membership to the
   `vitally:*` permissions, written to the namespaced `Authorization:CustomPermissionsClaim`.
4. **Verify on the live revision:** with a reader token, a write returns the RBAC denial; with an
   editor token, writes succeed but deletes are denied; with admin, all tiers succeed. Confirm
   denials appear in the audit log (`LogDenied`, by `sub`).
5. Once verified, `Authorization__ReadOnly` can be removed from editor/admin deployments while
   read-only stays the default for view-only consumers.

## Data-classification gate

Wider rollout remains gated on the pending data-classification review (customer data exposure).
Keep deployments read-only by default until that clears.
