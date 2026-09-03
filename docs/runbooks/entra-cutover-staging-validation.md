# Staging validation — the Entra cutover (#108)

`https://vitally-staging.fiscaltec.com` runs Entra-direct. This is the part of the acceptance suite
that needs a real sign-in, and therefore a person.

Written for the #108 cutover, but it is the standing checklist for **any** identity-provider change:
staging is the pre-production target for exactly this, so re-run it whenever the authority, the app
registration or the entitlement wiring moves.

**Nothing here touches production.** Staging is a separate Container App with its own configuration,
and none of the steps below reach production whatever state it is in.

⚠️ **Staging reads the production `vitally-shared` Vitally key.** There is one Vitally tenant and its
API keys are global, so its write and delete tools mutate **real customer data**. `Authorization:ReadOnly`
is deliberately not set, because step 4 needs the write tools visible. Pick a harmless target if you
exercise one at all — reading the tool list is enough for every check below.

## Already verified, so you can skip it

Verified on the branch head before merge, and re-runnable at any time:

| | |
|---|---|
| `bash .github/scripts/verify-oauth-metadata.sh https://vitally-staging.fiscaltec.com` | 11/11, with `jwks_uri` and `userinfo_endpoint` on `login.microsoftonline.com` / `graph.microsoft.com` |
| The app boots at all | proves the OIDC discovery document was fetched and its `issuer` matched `OAuth:Authority` |
| `/oauth/authorize` → upstream | Entra's v2 endpoint; `resource` **absent**; `scope` = the client's scopes + `https://vitally.fiscaltec.com/mcp.access`; our fixed callback |
| Following that to Entra | HTTP 200 sign-in page, no `AADSTS` — every parameter accepted |
| `POST /mcp` unauthenticated / bad token | exactly 401, with `resource_metadata` and `error="invalid_token"` respectively |
| `resource` we do not publish / do publish | 400 `invalid_target` / 302 |
| `POST /oauth/register` | returns `c3812e7d-a413-4169-b57e-803326611ba3` |

## What needs you

### 1. Complete a real sign-in

```bash
claude mcp add --transport http vitally-staging https://vitally-staging.fiscaltec.com/mcp
```

or `npx @modelcontextprotocol/inspector` pointed at the same URL — the harness #90 established.

**Expect:** a Microsoft sign-in (not Auth0), no consent screen, and the flow completing. A consent
prompt would mean the admin-consent grant or `api.preAuthorizedApplications` has drifted.

### 2. Decode the access token

Paste it into <https://jwt.ms>. Check:

| Claim | Expect |
|---|---|
| `iss` | `https://login.microsoftonline.com/75bd6050-92a8-4bde-a406-50000b310c86/v2.0` |
| `aud` | `c3812e7d-a413-4169-b57e-803326611ba3` — the **appId GUID**, because `requestedAccessTokenVersion = 2`. `https://vitally.fiscaltec.com` is also accepted (a v1 token) but is not what should arrive |
| `oid` | present, a GUID — this is the *only* thing entitlement is resolved from |
| `scp` | `mcp.access` — the short name, not the URI-qualified form |

An `aud` of anything else, or a missing `oid`, is a stop.

### 3. `tools/list` as a **department-nested** user — the one that matters most

Every tier except `sg-vitally-admins` is granted by *nesting*: a department group inside an
`sg-vitally-*` group. The #102 spike produced three separate wrong conclusions by reasoning about
nesting instead of testing it, so **a directly-assigned admin account passing proves very little.**

Sign in as someone whose tier comes only via a department group and confirm they see a non-empty tool
list of the right shape:

| Tier | Sees |
|---|---|
| reader | `List_*` / `Get_*` only — no `Create_`, `Update_`, `Delete_` |
| editor | the above plus `Create_` / `Update_`, no `Delete_` |
| admin | all 93 |

An **empty** list means Graph resolved the caller into none of the three groups — the fail-closed
working, but the wrong answer. Check `transitiveMembers` for that user before assuming code.

### 4. A reader is denied a write tool, and it is audited

With a reader account, the write tools should not appear in `tools/list` at all (discovery
filtering). To see the enforcement rather than the filtering, the denial is recorded by
`AuditLogger.LogToolCallDenied` — in Application Insights, look for the tool name, the caller's
subject id and the required permission. **No email, no tool arguments.** If either appears, that is a
defect worth raising on its own.

### 5. Sanity-check the logs

Nothing at `Warning` from `VitallyMcp.ToolAuthorizer` or `VitallyMcp.GraphGroupPermissionResolver`
during a normal sign-in. A warning naming a subject id and a staleness in seconds means Graph is
failing and the stale copy is being served — correct behaviour, but worth knowing about before it is
mistaken for something this change caused.

## If something fails

Staging rolls back by reverting its `OAuth__*` variables and its Container App secret to the previous
provider's — see *The Auth0 → Entra cutover (#108) and its rollback* in `CLAUDE.md`. Production is
untouched throughout and needs nothing.

## After it passes

If production has not yet had the same five variables applied, that is the next step: the values are
in `CLAUDE.md`, and the Container App secret is copied from the Key Vault secret
`entra-mcp-client-secret` through the two-switch network window in
`docs/runbooks/entra-app-registration.md`. The code ships ahead of the configuration and is inert
until `OAuth__UpstreamResourceScope` is set, so the flip is the whole change and reverting it is the
whole rollback.
