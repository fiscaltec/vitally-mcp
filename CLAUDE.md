# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project Overview

This is a Model Context Protocol (MCP) server implementation in C# that provides full CRUD access to the Vitally customer success platform. The server is a **remote HTTP MCP server** secured with Auth0 (which federates to Microsoft Entra for FISCAL identity); users connect to it by URL rather than installing a binary.

**Key characteristics:**
- Full CRUD API access to Vitally resources (accounts, organisations, users, conversations, notes, projects, tasks, admins, NPS responses, project templates, project categories, messages, custom objects, meetings — including participants and transcripts — custom traits, custom surveys)
- Permission management via `ReadOnly` and `Destructive` flags on every tool, for MCP clients to enforce per-category permissions
- **Streamable HTTP transport** (MCP 2026-07-28) on the `ModelContextProtocol.AspNetCore` package, stateless mode
- **Auth0 OAuth 2.1 protection** via JwtBearer on `/mcp`; publishes RFC 9728 protected-resource metadata at `/.well-known/oauth-protected-resource`. An in-process OAuth proxy fronts the upstream Auth0 tenant when `OAuth:SharedClientId` is set — it implements an RFC 7591 DCR shim so every MCP client converges on one pre-registered first-party app (skipping the per-session consent screen and accepting any RFC 8252 loopback port). Non-loopback `redirect_uri` values must be in `OAuth:AllowedClientRedirectUris`. **Auth0 tenant must have "Resource Parameter Compatibility Profile" enabled** (Settings → Advanced) so the `resource` parameter from MCP clients is consumed locally and not forwarded to upstream IdPs (avoids AADSTS9010010 with the Entra federation hop). Still required: the proxy *validates* `resource` (#105) but deliberately keeps forwarding it, because on Auth0 it is the only thing binding the token audience — see the `resource` section under Architecture.
- **On-demand Vitally API key fetch**: the server fetches the `vitally-shared` secret from Azure Key Vault via its user-assigned managed identity (with a short in-memory cache) and uses it to call Vitally on behalf of all authenticated users. Future per-user keys can be added by reintroducing claim-based secret resolution.
- .NET 10 ASP.NET Core, framework-dependent — runs in any .NET 10 container
- Built on the official `ModelContextProtocol` C# SDK 2.2.0 + `ModelContextProtocol.AspNetCore` 2.2.0
- Multi-region support: EU (default, `rest.vitally-eu.io`) and US (`{subdomain}.rest.vitally.io`)
- Rate-limit-aware HTTP pipeline: auto-retries on `429 Too Many Requests` and logs a warning when `X-RateLimit-Remaining` drops below threshold

## Common Development Commands

### Build, test, run

```powershell
# Restore + build + run the test suite
dotnet test VitallyMcp.sln -c Debug

# Build only (Debug)
dotnet build VitallyMcp.sln

# Run a single test class
dotnet test VitallyMcp.sln -c Debug --filter-class "*MeetingsToolsTests"

# Start the server in dev mode (no Auth0, no Key Vault)
$env:OAuth__NoAuth = "true"
$env:Vitally__Region = "EU"
$env:Vitally__DevelopmentApiKey = "sk_live_your_key"
$env:ASPNETCORE_URLS = "http://localhost:5099"
dotnet run --project VitallyMcp/VitallyMcp.csproj
```

### Smoke-testing the server

With the server running locally (or against the deployed URL with a real JWT in the `Authorization` header):

```powershell
# OAuth metadata document — clients use this to discover the auth server
Invoke-RestMethod http://localhost:5099/.well-known/oauth-protected-resource

# MCP initialise. This deliberately requests 2025-06-18: the `initialize` handshake exists only in
# revisions up to 2025-11-25, because 2026-07-28 removed it in favour of per-request `_meta` and
# headers. So this is a legacy-path smoke test, and the server replies with a revision it supports.
# Do NOT substitute 2026-07-28 here — that revision has no `initialize` method and the call errors.
$body = @{ jsonrpc='2.0'; id=1; method='initialize'; params=@{ protocolVersion='2025-06-18'; capabilities=@{}; clientInfo=@{ name='smoke'; version='0.0.1' } } } | ConvertTo-Json -Depth 10 -Compress
Invoke-RestMethod -Method Post -Uri http://localhost:5099/mcp -ContentType 'application/json' -Headers @{ Accept='application/json, text/event-stream' } -Body $body

# tools/list on the legacy path (no MCP-Protocol-Version header — the server accepts it)
$body = @{ jsonrpc='2.0'; id=2; method='tools/list' } | ConvertTo-Json -Compress
Invoke-RestMethod -Method Post -Uri http://localhost:5099/mcp -ContentType 'application/json' -Headers @{ Accept='application/json, text/event-stream' } -Body $body
```

**Exercising the 2026-07-28 path itself.** The revision above replaced the `initialize` handshake with
per-request metadata, and the server enforces the full contract — so a bare request is *not* enough.
All of the following are required together, and the server rejects each omission with a distinct
error (verified against the running container):

1. the `MCP-Protocol-Version: 2026-07-28` header — omit it and you get
   `-32020 "The MCP-Protocol-Version header is required when the request body declares a per-request metadata protocol version."`
2. `_meta/io.modelcontextprotocol/protocolVersion` in `params` — omit it and you get
   `-32602 "Requests using protocol version '2026-07-28' must include '_meta/io.modelcontextprotocol/protocolVersion'."`
3. `_meta/io.modelcontextprotocol/clientCapabilities` as a JSON object — omit it and you get a
   matching `-32602`.

`Mcp-Method` must also equal the body's `method`; a mismatch is rejected with
`"Header mismatch: Mcp-Method header value 'x' does not match body value 'y'."`

```powershell
$meta = @{
  'io.modelcontextprotocol/protocolVersion'   = '2026-07-28'
  'io.modelcontextprotocol/clientCapabilities' = @{}
  'io.modelcontextprotocol/clientInfo'         = @{ name = 'smoke'; version = '0.0.1' }
}
$body = @{ jsonrpc='2.0'; id=3; method='tools/list'; params=@{ _meta=$meta } } | ConvertTo-Json -Depth 10 -Compress
Invoke-RestMethod -Method Post -Uri http://localhost:5099/mcp -ContentType 'application/json' `
  -Headers @{ Accept='application/json, text/event-stream'; 'MCP-Protocol-Version'='2026-07-28'; 'Mcp-Method'='tools/list' } `
  -Body $body
```

A successful response carries `ttlMs: 300000` and `cacheScope: "private"` alongside `tools`.

## Installing for End Users

FISCAL employees point their MCP client at `https://vitally.fiscaltec.com/mcp`. The client handles the Auth0 OAuth flow automatically on first use via the protected-resource metadata document (Auth0 federates to Entra for the actual sign-in).

| Client | How to connect |
|---|---|
| Claude Desktop | Settings → Connectors → Add custom connector → paste the URL |
| Claude Code | `claude mcp add --transport http vitally https://vitally.fiscaltec.com/mcp` |
| VS Code / Cursor / other | Add an MCP server entry pointing at the URL; client handles OAuth |

To update: nothing for end users. The server is the source of truth; new deploys ship automatically.

## GitHub issues (issue-driven work)

Track non-trivial work as GitHub issues via the `gh` CLI. Trivial one-off changes don't need an
issue — use judgement. This mirrors the flow used in `searledan/rosetechnologies.co.uk`,
`searledan/dansearle.co.uk` and `searledan/spendy`; the labels below were created here to match.

**Why it matters here:** each issue is normally picked up in a *fresh* session, often in a worktree or
via a subagent. The issue body and this file are the only context that new session gets, so an issue
that assumes prior conversation is an issue that cannot be worked.

**Labels** — one of each per issue:

- **type** (categorises the issue — *distinct* from the Conventional-Commits type in the PR title):
  `feature`, `bug`, `tech-debt`, `security`, `ux`, `content`, `ops` (infra/deployment/config),
  `documentation`
- **priority:** `priority: high` / `priority: medium` / `priority: low`
- **status** (progresses `ready` → `in-progress` → `complete`; `blocked` is a side-state):
  `status: ready` (defined enough to start) / `status: in-progress` / `status: blocked` (waiting on a
  dependency) / `status: complete` (work merged)

**Lifecycle** — one `status:` label at a time:

1. Pick a `status: ready` issue respecting priority, or create one with type + priority +
   `status: ready`.
2. Flip to in-progress:
   `gh issue edit <n> --remove-label "status: ready" --add-label "status: in-progress"`.
3. Branch `<cc-type>/<short-description>`, where `<cc-type>` is the **Conventional-Commits** type used
   in the PR title — not the issue's type label. They map loosely: `feature` → `feat/…`,
   `documentation`/`content` → `docs/…`, `tech-debt` → `chore/…` or `refactor/…`, `bug` → `fix/…`,
   `ops` → `ci/…` or `chore/…`.
4. Reference `#<n>` in commits where relevant.
5. Open the PR with **`Closes #<n>`**. `.github/workflows/pr-title.yml` enforces the
   Conventional-Commits prefix on the PR *title*, and `main` takes squash merges — so the PR title
   becomes the commit subject on `main`. On squash-merge the issue auto-closes; flip it to
   `status: complete` then, so a finished issue ends up *closed + `status: complete`*, distinguishable
   from one closed as won't-fix or duplicate.
6. If work stalls, swap the status for `status: blocked` and comment what's blocking.

**What actually gates a merge** (the `Secure branches` ruleset, verified 2026-08-18):
`required_approving_review_count` is **0** — no approving review is needed. What is required is all
review threads resolved, the branch up to date with `main` (`strict_required_status_checks_policy`),
and these checks green: `Analyze (csharp)`, `Validate PR title`, `Build and test (ubuntu-latest,
net10.0)`, `nuget-vuln`, `image-cve`. Read the ruleset rather than inferring from
`mergeStateStatus`, which reports `BLOCKED` for unresolved threads and pending checks too.

**That ruleset is not the whole gate.** It says nothing about Copilot, and reading "0 approvals
required" as "nothing else to wait for" is exactly what merged #117 with three unreviewed commits —
see the next section before merging anything.

### Copilot review & merge gate

Copilot reviews every PR automatically, **asynchronously**, and its reviews are always `COMMENTED` —
never `APPROVED`. So it can never satisfy an approval-count rule, and the ruleset above is blind to
it. Two consequences, both of which have cost real time in this repo and its siblings:

- **`mergeStateStatus: CLEAN` does NOT mean Copilot has finished.** It reflects the ruleset only.
  `searledan/rosetechnologies.co.uk` records its own version of this: PR #24 merged 15 seconds before
  Copilot's second review posted two valid comments. Here, **#117 merged with Copilot having reviewed
  only the first pushed commit** — the two fixes Copilot itself asked for went in unseen, while
  `CLEAN` held the whole time because the threads had been resolved.
- **A push does not reliably start a re-review.** The automatic request fires when the PR is
  *opened*. After pushing review fixes you must **re-request explicitly**, or you will wait for a
  review that is never coming and read the silence as approval.

**The merge sequence — a loop, not a one-shot:**

1. Open the PR; let the checks and the first Copilot pass run.
2. Each round:
   1. **Wait for Copilot to finish reviewing the _current head_.** Done only when it is **not** in
      requested reviewers **AND** its latest review's `commit_id` **equals the head SHA**. A stale
      review of an earlier commit does not count, and timestamps cannot be lined up against the head
      commit — compare the SHA.
      ```bash
      n=<PR>; head=$(gh pr view "$n" --json headRefOid --jq .headRefOid)
      gh pr view "$n" --json reviewRequests \
        --jq '[.reviewRequests[].login] | index("copilot-pull-request-reviewer") != null'   # false = not pending
      gh api "repos/fiscaltec/vitally-mcp/pulls/$n/reviews" \
        --jq '[.[] | select(.user.login == "copilot-pull-request-reviewer[bot]")] | sort_by(.submitted_at) | last | .commit_id'
      ```

      ⚠️ **Take the head SHA from `git rev-parse HEAD`, not from `gh pr view --json headRefOid`,
      in the seconds after a push.** The GraphQL field lags: on #122 it still reported the previous
      commit right after a push, so the comparison matched Copilot's *old* review and the gate read
      as passing. Two consequences, and the second is the expensive one: re-requesting in that
      window gets a review of the previous commit (that happened on #122 too — a review arrived four
      minutes after the request, on the superseded SHA), so **wait until the API reports the new head
      before re-requesting**, then compare against `git rev-parse`.

      ⚠️ **"Not pending" alone is meaningless.** Copilot dequeues itself the moment it accepts a
      request, so `reviewRequests` is empty within seconds of asking — long before it has reviewed
      anything. Both conditions, always.
   2. A clean pass says *"reviewed N of N files … generated no new comments"* and adds no threads.
   3. Work every open thread: fix and reply, or reply with the reasoning — then **resolve** it.
   4. **If you pushed code in (3), re-request and go back to (1):**
      ```bash
      gh api repos/fiscaltec/vitally-mcp/pulls/$n/requested_reviewers \
        -f 'reviewers[]=copilot-pull-request-reviewer[bot]'
      ```
      **`gh pr edit --add-reviewer` silently no-ops on the Copilot bot** — use the REST endpoint.
      `requested_reviewers` reading empty seconds later is **not** failure (see the warning above);
      check the timeline (`.event == "review_requested"`) if in doubt. Reply-only rounds need no
      re-request.
3. Only then merge (squash), re-checking all three immediately beforehand: required checks green and
   branch current, Copilot's latest review on the current head, zero unresolved threads.

**Mind the two spellings of the bot's login — both are correct, don't "align" them.** The suffix
tracks *which API answered*, not which field you read. **REST** (`gh api …/pulls/N/reviews`,
`…/requested_reviewers`) uses `copilot-pull-request-reviewer[bot]`; **GraphQL** — and
`gh pr view --json`, which is GraphQL underneath — reports `copilot-pull-request-reviewer` without it.

⚠️ **Never `--auto` merge a human PR.** It fires the instant CI passes and beats a pending review.
`--auto` is for Dependabot only, which carries no human review.

**This is enforced mechanically, not just documented.** `.claude/hooks/pre-merge-copilot-gate.sh` is a
`PreToolUse` hook (registered in `.claude/settings.json` under the `Bash` matcher with
`if: "Bash(gh pr merge*)"`) that **denies** `gh pr merge` unless Copilot is not a requested reviewer,
its latest review's `commit_id` equals the head, and unresolved threads are zero. It exempts
Dependabot and **fails closed** — including if `jq`/`sed`/`awk` are missing or broken, so a damaged
toolchain cannot wave a merge through. Ported from `searledan/dansearle.co.uk`; it needs no
adaptation, resolving owner/repo at runtime.

Caveats worth knowing before trusting it:

- It guards **only Claude Code's own tool calls** — a merge from the GitHub UI is unaffected.
- A newly added hook needs `/hooks` opened once (or a restart) to activate.
- **Run `gh pr merge` as a standalone command — no pipes, no `;`, no `&&`.** The hook resolves the PR
  by counting non-flag positional tokens after the subcommand, so a chained form turns every
  following word into a candidate and the gate denies as ambiguous.
- The `if` filter matches the command text, so *any* Bash call containing `gh pr merge` is gated —
  including a test harness. Testing the hook means invoking it from a script file rather than inline.
  Over-matching is the safe direction; don't loosen it.

Verified on porting (2026-08-28) by running the hook against real PRs: it allows #118, whose Copilot
review was on the merged head, and **denies #117** — *"Copilot's latest review (6808805…) is not on
the current head (58afb2b…)"* — which is the mistake that prompted the port.

**Forms** — `.github/ISSUE_TEMPLATE/` provides seven, one per type label except `ux`: `bug.yml`,
`feature.yml`, `tech-debt.yml`, `security.yml`, `ops.yml`, `documentation.yml`, `content.yml`.
Filenames match the label they preset, and the shared four match the sibling repos. Each form
presets its type label plus `status: ready`, and its title with the matching Conventional-Commits
prefix; add a `priority:` label after creating. Blank issues stay enabled for quick notes and for
`ux`. Bug and Feature carry Vitally-specific fields (region, MCP client, server URL, a failure
timestamp for correlating with Application Insights) — keep those if you edit the forms.

`documentation` and `content` both map to `docs/…` branches but cover different audiences:
**Documentation** is prose for humans (`CLAUDE.md`, `README.md`, `docs/`), whereas **Content** is the
copy an *LLM* reads — tool `[Description]` and `Title` values and `VitallyServerInstructions.Text`.
Content wording changes model behaviour (which tool gets picked, how it's called), so they are
defects rather than cosmetics; the form asks for the observed effect to keep that distinction sharp.

**Writing issues** — aim for "detailed enough to implement without further context":

- **Title** — conveys the scope at a glance without reading the body.
- **Lead paragraph** — what and why, plus how it was discovered if that matters.
- **`## Problem`** (or `## Description` + `## Current state / problem`) — evidence, not assertion.
  Quote real error output, cite file paths and line numbers, and say what was *verified* versus
  *assumed*.
- **`## Proposed fix` / `## Proposed solution`** — concrete numbered steps. Record rejected
  alternatives and why, so the next session doesn't re-litigate them.
- **`## Files to create/modify`** — explicit paths.
- **`## CLAUDE.md updates needed`** — which sections of this file the change invalidates. Easy to
  forget and the most common source of drift.
- **`## Dependencies`** — related issues as `#N` with the relationship ("blocked on #92", "related to
  #90"); if it depends on unresolved work, use `status: blocked`.
- Prefer tables for structured data. Flag anything designed-but-unvalidated as such, explicitly.

## Architecture

### Hosting and transport (Program.cs)

The server uses ASP.NET Core 10 with `WebApplication.CreateBuilder` + `Microsoft.NET.Sdk.Web`. Key wiring:

- Binds `VitallyServerOptions` from the `Vitally:` configuration section, calls `Validate()` at startup to fail-fast on bad config.
- Binds `OAuthOptions` from `OAuth:` (provider-agnostic — works with Auth0, Entra direct, Keycloak, etc.).
- Conditionally registers `SecretClient` (Azure Key Vault) as singleton when `Vitally:KeyVaultUri` is set; uses `DefaultAzureCredential` so it works with managed identity in production and `az login` locally.
- `IMemoryCache` registered for the API key cache and OAuth proxy state cache.
- Authorisation policy plumbing: `VitallyPermissionRequirement.cs` carries one `vitally:*` permission,
  and `VitallyPermissionHandler.cs` evaluates it by delegating to `ToolAuthorizer`, so tool discovery
  filtering and the `VitallyService.SendAsync` backstop share one resolution path.
- `ToolsListCacheOptions.cs` binds the `ToolsListCache:` section for the `tools/list` cache hints.
- `ProtectedResourceMetadataBuilder.cs` builds the RFC 9728 document served from both well-known paths.
- `UpstreamOidcMetadata.cs` resolves the *upstream* provider's `authorization_endpoint`, `token_endpoint`, `jwks_uri` and `userinfo_endpoint` from its OIDC discovery document, cached in `IMemoryCache` (12 h). Registered singleton with its own named `HttpClient`. `StartupGuards.EnsureUpstreamOidcEndpointsAsync` resolves it once after `builder.Build()` and **refuses to start** if the document is unreachable or missing any of the four — see the discovery section below.
- `VitallyApiKeyProvider` registered scoped.
- `VitallyRateLimitHandler` registered transient and attached to the `HttpClient` used by `VitallyService`.
- `JwtBearer` authentication added unless `OAuth:NoAuth=true`.
- `ForwardedHeadersOptions` configured to honour `X-Forwarded-Proto` / `X-Forwarded-Host` / `X-Forwarded-For` from the Container Apps ingress (trust model: network isolation, not header authentication — see comments in `Program.cs`).
- MCP server registered via `AddMcpServer().WithHttpTransport(o => o.Stateless = true).WithToolsFromAssembly()`.
- Publishes server-level usage guidance in the MCP `initialize` response via `McpServerOptions.ServerInstructions` (text in `VitallyServerInstructions.Text`): steers clients toward organisation-level data, the traits-vs-custom-objects distinction, the name/date-range filters, and the read-only/permission model.
- OAuth proxy endpoints (only active when `OAuth:SharedClientId` is set):
  - `GET /.well-known/oauth-protected-resource` — RFC 9728 metadata, serialised with `McpJsonUtilities.DefaultOptions`. That is load-bearing, not incidental: the ASP.NET Core defaults write every unset optional as an explicit `null`, and RFC 9728 §3.2 requires an unused parameter to be *omitted* — strict clients reject the whole document over the difference.
  - `GET /.well-known/oauth-authorization-server` — RFC 8414 metadata declaring **our own origin** as `issuer` (see the façade section below), pointing `authorization_endpoint` / `token_endpoint` / `registration_endpoint` at our own proxy endpoints, and advertising `authorization_response_iss_parameter_supported: true`. `userinfo_endpoint` and `jwks_uri` still point upstream — **read from the provider's discovery document**, not concatenated onto `Authority`.
  - `GET /oauth/authorize` — validates the client `redirect_uri` against `OAuth:AllowedClientRedirectUris` (plus implicit loopback exemption), validates any RFC 8707 `resource` against the identifier we publish (rejecting a mismatch with `invalid_target`; see the `resource` section below), stashes it keyed by `state`, and proxies to the **discovered** upstream `authorization_endpoint` with our own fixed callback.
  - `GET /oauth/callback` — reverses the stash, **replaces any upstream `iss` with our own origin**, and redirects to the original client URI.
  - `POST /oauth/token` — proxies code/refresh exchanges to the **discovered** upstream `token_endpoint`, server-side-injecting `SharedClientSecret`. Applies the same `resource` validation to the form body.
  - `POST /oauth/register` — RFC 7591 DCR shim returning `SharedClientId` plus filtered `redirect_uris`.
- `MapMcp("/mcp").RequireAuthorization()` — auth requirement is dropped when `NoAuth=true`.
- The 401 challenge on `/mcp` carries a single `WWW-Authenticate` value pointing at the protected-resource metadata document (`resource_metadata="{PublicBaseUrl}/.well-known/oauth-protected-resource/mcp"`), adding `error="invalid_token"` when a token was presented and failed validation. The metadata document is served from both `/.well-known/oauth-protected-resource` and the `/mcp`-suffixed path (`ProtectedResourceMetadataBuilder.MetadataPath`). The status stays exactly 401 — `.github/workflows/deploy.yml` smoke-tests that and rolls back if it changes, so don't alter it. That smoke **also** pins the RFC 8414/9728 documents (`.github/scripts/verify-oauth-metadata.sh`): the `issuer`, its byte-for-byte equality with `authorization_servers`, the advertised `iss` flag, the façade endpoints naming our origin, `jwks_uri`/`userinfo_endpoint` being absolute https without a fragment, and no null-serialised optionals. Those are deploy-gating contracts now, not only test-suite ones.


### The OAuth proxy is a complete authorisation-server façade

From a client's point of view this server *is* the authorisation server: `/oauth/authorize`,
`/oauth/token` and `/oauth/register` are all ours. So the RFC 8414 document declares **our own
origin** as `issuer` (`PublicBaseUrl`, falling back to the request origin) — the same string
`ProtectedResourceMetadataBuilder` publishes as `authorization_servers`. Auth0 still issues the
tokens and `jwks_uri` / `userinfo_endpoint` still point there; the façade covers identity and
discovery, not issuance.

It previously declared **Auth0's** issuer while being served from our origin, which violates RFC 8414
§3.3 (an anti-mix-up control: a metadata document can only ever speak for itself) and made strict
clients — including **MCP Inspector** — abort before reaching DCR. Fixed 2026-08-21 (#90).

**Three pieces that must stay together.** Changing any one alone breaks the flow:

| Piece | Why |
|---|---|
| `issuer` = our origin | RFC 8414 §3.3. Must equal `authorization_servers` in the RFC 9728 document byte for byte — the check is simple string equality, tolerating only a trailing slash *on the expected value*. |
| `/oauth/callback` strips any upstream `iss` and appends our own | RFC 9207. **Mandatory, not defensive.** Clients compare a *present* `iss` against the metadata `issuer` even when support is not advertised — so an Auth0 `iss` forwarded through would now be a hard failure, and appending ours alongside would leave two values. |
| `authorization_response_iss_parameter_supported: true` | Honest only because of the row above. Advertising it and then omitting `iss` reads as a stripped-parameter attack and the client aborts. |

**Verified 2026-08-21** against `@modelcontextprotocol/client` 2.0.0 — the package MCP Inspector 2.3.0
actually depends on — driving a local container: the RFC 9728 document parses, `discoverAuthorizationServerMetadata`
passes §3.3, **DCR is reached and returns the shared `client_id`** (it was never called before), and
`validateAuthorizationResponseIssuer` accepts the `iss` we emit. A control assertion confirms the same
function still rejects Auth0's issuer, so the passes are not a skipped check.

Two things were verified rather than assumed, both of which the design had flagged as open:

- **No client validates the access token's `iss` against the metadata `issuer`.** The SDK's client
  auth module never decodes a JWT or fetches JWKS — access tokens are opaque to it — so `jwks_uri`
  pointing at Auth0 while we claim the issuer is safe. Our own `JwtBearer` validates against Auth0's
  `Authority` internally and is unaffected either way.
- **The published SDK 1.x does not enforce §3.3 at all**; the enforcement ships in the 2.x packages.
  So this was latent for Claude Desktop / Claude Code and immediately fatal for anything on 2.x.

**Validated against the live Auth0 tenant on 2026-08-22**, which the local run could not cover. MCP
Inspector 2.3.0 completed the whole flow against the server behind an HTTPS tunnel with
`PublicBaseUrl` set: metadata 200, DCR `201`, real Auth0→Entra sign-in, `/oauth/callback` 302 carrying
`iss=<our origin>` which the client *accepted*, `/oauth/token` 200, and authenticated `POST /mcp` 200.
That is the acceptance test in #90, and it is past the point where the flow previously aborted.

**If you touch this,** re-run that validation rather than reasoning about it — `npx @modelcontextprotocol/inspector`
against a local container is now a working end-to-end client, which is the practical payoff.

**Two traps when validating against a throwaway Auth0 audience** (both cost time on 2026-08-22):

- **`tools/list` will be empty, and that is expected.** The post-login Action `Vitally MCP claims`
  opens with `if (event.resource_server?.identifier !== 'https://vitally.fiscaltec.com/') return;`,
  so no `permissions` claim is minted for any other audience and every tool is filtered out. It looks
  exactly like an authorisation failure at the moment you are least able to dismiss it. Set
  `Authorization:Enabled=false` to confirm the rest of the path, or accept the empty list as evidence
  the RBAC discovery filter fails closed.
- **Auth0 API identifiers are immutable and must equal the server origin** (the client throws if the
  RFC 9728 `resource` does not match), so an ephemeral tunnel URL orphans one API per run. This is
  what the staging environment now exists to avoid — validate identity-provider changes against
  `https://vitally-staging.fiscaltec.com`, whose hostname is stable and whose Resource Server already
  matches it. Reach for a tunnel only for something staging genuinely cannot cover, and budget for the
  cleanup if you do.

### The RFC 8707 `resource` parameter is validated here — and still relayed upstream

`OAuthOptions.IsResourceIndicatorAllowed(value)` is the single check, reading
`PublishedResourceIdentifier` (`OAuth:Resource`, falling back to `OAuth:Audience`) — the same
property `ProtectedResourceMetadataBuilder` publishes, so the value validated against is by
construction the value clients were told to send. `/oauth/authorize` (query) and `/oauth/token`
(form body) both apply it and reject a mismatch with `invalid_target`; every value is checked when
the parameter repeats, and a present-but-empty one is a mismatch rather than an absence. An absent
`resource` is untouched — RFC 8707 is optional for clients.

**Why it is a real control and not paperwork.** `resource` is what binds the audience of the token
that comes back: `Program.cs` sends **no `audience` parameter anywhere**, and the Auth0 tenant's
*Resource Parameter Compatibility Profile* consumes `resource` locally to that end. Until #105 it
was relayed unvalidated, so a client could ask to be bound to an audience this server never
published and the proxy would pass the request on.

**Comparison rules** — component-wise per RFC 3986 §6.2.2, not one string compare. Scheme and host
case-insensitive; port, path and query exact; a fragment rejected outright (RFC 8707 §2); a single
trailing slash tolerated on either side. That last one is load-bearing: Entra refuses to register an
`identifierUris` value ending in a slash, while Claude Code normalises a bare-host resource *to* the
trailing-slash form, so the two forms have to name one resource. Nothing else is normalised. With
neither `Resource` nor `Audience` configured — only possible under `NoAuth` — there is nothing to
compare against and every value is accepted.

**A malformed identifier fails at boot**, not at the first sign-in. `OAuth:Resource` (or `Audience`
standing in for it) must be an absolute **http(s)** URI with no fragment or `OAuthOptions.Validate()`
throws — because it is no longer only *published*: an unparseable value would refuse every request carrying
`resource`, an authentication outage discovered at sign-in time. An Entra-style client-ID GUID is a
perfectly good `aud` but not a resource identifier, and lands here whenever `Resource` is left unset;
set `Resource` to the server origin in that case.

The **scheme** is checked, not merely absoluteness, and that is not fussiness:
`Uri.TryCreate("/mcp", UriKind.Absolute)` *succeeds* on Unix — as `file:///mcp` — and fails on
Windows. An absoluteness-only check therefore passes locally and is inert on the Linux containers
this runs on; CI on `ubuntu-latest` caught exactly that in #122. `http` is allowed alongside `https`
so loopback development still works.

**It is still forwarded, deliberately.** Dropping or substituting it is the Auth0-breaking half of
#105 and moved to the cutover (#108): with Auth0 live, removing the parameter removes the audience
binding and `aud` stops matching `OAuth:Audience` for **every** user. So the tenant still needs the
compatibility profile enabled, and terminating `resource` at the façade — where Entra's exact-match
rule against a non-slashed `identifierUris` starts to matter — happens only once `OAuth:Authority`
points at Entra.

### Upstream endpoints come from OIDC discovery, not from `Authority`

The façade above owns `/oauth/authorize`, `/oauth/token` and `/oauth/register`; those keep naming our
own origin and are unaffected by anything here. This section is about the *other* half — the four
**upstream** URLs the proxy needs, which used to be string-concatenated onto `OAuth:Authority` in
Auth0's path shapes and are now read from `{Authority}/.well-known/openid-configuration`
(`UpstreamOidcMetadata.cs`, #104).

| Endpoint | Read by | Was |
|---|---|---|
| `authorization_endpoint` | `/oauth/authorize` redirect target | `{authority}/authorize` |
| `token_endpoint` | `/oauth/token` forward target | `{authority}/oauth/token` |
| `jwks_uri` | republished in our RFC 8414 document | `{authority}/.well-known/jwks.json` |
| `userinfo_endpoint` | republished in our RFC 8414 document | `{authority}/userinfo` |

**Choosing a different `Authority` cannot fix the old shapes**, which is the obvious first instinct
and a dead end. Entra's issuer is `https://login.microsoftonline.com/{tid}/v2.0` while its endpoints
hang off `https://login.microsoftonline.com/{tid}/oauth2/v2.0/` — no single prefix yields both — and
its `userinfo_endpoint` is on `graph.microsoft.com` entirely. The discovery *path* is the one
concatenation that is safe, because OIDC Discovery §4 standardises it.

The bottom two rows are why this is a correctness fix rather than tidying: they are published to every
MCP client as fact, so a wrong value is advertised, not merely used.

**The document must speak for `Authority`.** `issuer` in the fetched document is checked against
`OAuth:Authority` before any endpoint is read (OIDC Discovery §4.3) — the same anti-mix-up control as
the RFC 8414 §3.3 rule the façade section describes, and for the same reason: a metadata document can
only ever speak for its own issuer. It is load-bearing rather than decorative, because the discovery
client follows redirects; without the check a redirect could hand us another provider's endpoints,
which we would cache and then republish to clients as *this* provider's. Trailing slashes are
normalised on both sides and nothing else is — Auth0 issuers carry one and Entra's do not, so that
much drift is tolerated and no more.

**Fail-fast.** `StartupGuards.EnsureUpstreamOidcEndpointsAsync` resolves the document once after
`builder.Build()` (15 s cap) and throws if it is unreachable or missing any of the four, so the
container refuses to start rather than serve unverified endpoints. It is a no-op when
`OAuth:SharedClientId` is unset — no proxy, nothing reads the document, no reason to depend on the
provider at boot. `proxyEnabled` is taken from the **resolved** `IOptions<OAuthOptions>`, not from
`oauthSection[...]` alongside `noAuth`: that composition-time read happens before
`WebApplicationFactory` injects test configuration, so reading it raw would silently skip the guard
in every integration test.

After startup the endpoints are cached for 12 h; a *failed refresh* falls back to the last resolved
copy rather than failing the request, since startup already proved those values good. The fail-fast
that matters is the one before the server accepts traffic.

Two details of that fallback are easy to get wrong and are pinned by tests:

- The stale copy is **re-cached** for `FailedRefreshRetryInterval` (1 min) before being returned.
  Without that, a prolonged provider outage would put a fresh discovery attempt — and a wait of up to
  the 10 s client timeout — in front of *every* proxy request once the TTL lapsed, turning a fallback
  meant to absorb the outage into an amplifier of it.
- The fallback is gated on **the caller's cancellation token**, not on the exception type. An
  `HttpClient` timeout surfaces as `TaskCanceledException` — an `OperationCanceledException` — so
  filtering that type out would have excluded a slow provider, which is precisely the case the
  fallback exists for. Only a genuinely cancelled caller skips it, because there is then no one left
  to serve.

`UpstreamOidcMetadata` funnels every failure mode — transport errors included — into
`InvalidOperationException`, so `StartupGuards` catches two specific types rather than a catch-all.

### Configuration (VitallyServerOptions.cs + OAuthOptions.cs)

`VitallyServerOptions` (singleton, bound from `Vitally:` section):
- `Region` — `EU` (default) or `US`. Validated at startup.
- `Subdomain` — required only when `Region=US`.
- `KeyVaultUri` — Azure Key Vault URI. When unset, the server requires `DevelopmentApiKey` instead (local dev only).
- `DefaultSecretRef` — Key Vault secret name to fetch (default `vitally-shared`).
- `SecretCacheDuration` — TTL for the in-memory API key cache (default 5 min).
- `DevelopmentApiKey` — local-only fallback used when `KeyVaultUri` is unset.
- `BaseUrl` — computed: EU → `https://rest.vitally-eu.io`; US → `https://{Subdomain}.rest.vitally.io`.
- `MaxAutoPageFetches` — hard cap on page fetches per server-side filtered call (default 10; 100 items/page). Bounds fan-out against Vitally's 1000 req/min budget.

`OAuthOptions` (singleton, bound from `OAuth:` section):
- `Authority` — the provider's OIDC **issuer** identifier, e.g. `https://fiscal-it.uk.auth0.com/` (Auth0) or `https://login.microsoftonline.com/{tenant-id}/v2.0` (Entra). It is *not* a prefix the endpoint URLs are built from: `{Authority}/.well-known/openid-configuration` is fetched and the endpoints come from that document. The trailing slash is whatever the provider's own issuer carries — Auth0's has one, Entra's does not.
- `Audience` — the Auth0 Resource Server / API identifier, e.g. `https://vitally.fiscaltec.com/` — **with the trailing slash**, because Auth0 identifiers are exact-match and production's carries one (verified against the live tenant, 2026-08-22). Validated against the JWT `aud` claim. **Under Entra it becomes the App ID URI `https://vitally.fiscaltec.com` — with NO slash**, because Entra refuses to register a slash-suffixed `identifierUris` value; see the divergence warning below.
- `Resource` — canonical resource identifier published in `/.well-known/oauth-protected-resource` (falls back to `Audience` if empty). Set explicitly when MCP clients validate metadata `resource` against the server URL/origin (RFC 8707 + RFC 9728 compliance) — the published client rejects the whole document on a mismatch. **On Auth0 it happens to equal `Audience`; under Entra it must not** — see the divergence warning below.  Second role: `PublishedResourceIdentifier` (this value, falling back to `Audience`) is what an incoming RFC 8707 `resource` parameter is validated *against* on `/oauth/authorize` and `/oauth/token` — so it is now a control on what audience a caller may ask to be bound to, not only a value published. `PublicBaseUrl` is the odd one out: it is an origin, so no trailing slash (and `Validate()` trims one anyway).

> ⚠️ **`Audience` and `Resource` are equal today by coincidence, and must diverge at the Entra
> cutover.** Auth0's Resource Server identifier carries a trailing slash and so does the value
> clients normalise to, so both are `https://vitally.fiscaltec.com/` and they look like one setting.
> Under Entra they are two:
>
> | | Auth0 (today) | Entra (#108) |
> |---|---|---|
> | `OAuth:Audience` — validated against JWT `aud` | `https://vitally.fiscaltec.com/` | `https://vitally.fiscaltec.com` — **no slash**, Entra refuses to register one on `identifierUris` |
> | `OAuth:Resource` — published in RFC 9728, and validated against | `https://vitally.fiscaltec.com/` | `https://vitally.fiscaltec.com/` — **unchanged**, Claude Code normalises to it |
>
> Reconciling them "for consistency" breaks token validation in one direction and the metadata
> document in the other. On staging they diverge by *host* as well, because one app registration
> serves both origins (#107), so a staging token's `aud` is production's App ID URI.
> `OAuthOptions.IsResourceIndicatorAllowed` tolerating exactly one trailing slash is what lets the
> two forms name one resource — see `docs/runbooks/entra-app-registration.md`.
- `SharedClientId` — pre-registered Auth0 native-app client_id that every MCP client converges on via the DCR shim. When set, the OAuth proxy endpoints become active.
- `SharedClientSecret` — confidential-client secret for `SharedClientId`, injected server-side at `/oauth/token`.
- `AllowedClientRedirectUris` — non-loopback `redirect_uri` allowlist for the OAuth proxy. Loopback URIs (`localhost`, `127.0.0.1`, `[::1]`) on any port are always allowed per RFC 8252 §7.3; this list covers hosted MCP clients like `https://claude.ai/api/mcp/auth_callback`. `OAuthOptions.IsRedirectUriAllowed(uri)` is the single check; `/oauth/authorize` and `/oauth/register` both use it. **This is the only thing standing between the proxy and an open redirector with authorisation-code theft — never bypass it.**
- `PublicBaseUrl` — canonical public origin (e.g. `https://vitally.fiscaltec.com`). When set, `/.well-known/*` metadata and the OAuth proxy callback are built from this instead of the request `Host`, defending against Host-header injection into the metadata documents. Empty in local dev (falls back to request scheme+host so loopback works). Validated as absolute https.
- `NoAuth` — local-only dev flag that bypasses JWT validation entirely.

`ToolAuthorizationOptions` (singleton, bound from `Authorization:` section):
- `Enabled` (default `true`), `ReadPermission` (`vitally:read`), `WritePermission` (`vitally:write`), `DeletePermission` (`vitally:delete`), `CustomPermissionsClaim` (default `https://vitally.fiscaltec.com/permissions`).
- `ReadOnly` (default `false`) — deployment-level read-only kill switch. When true, **every** mutating tool call (create/update/delete) is denied in `ToolAuthorizer` (checked before the `Enabled`/`NoAuth` gate, so it holds even with RBAC off), and the destructive tools are hidden from `tools/list` via an `AddListToolsFilter`. A blunt safety net for read-only deployments that doesn't depend on the per-user Entra-group RBAC. Denials are audited via `LogDenied`.
- `LiveGroupCheck` (default `false`), `LiveGroupCacheSeconds` (default `60`), `LiveGroupStaleSeconds` (default `3600`; `0` disables stale serving), `ReaderGroupId`/`EditorGroupId`/`AdminGroupId` (Entra group object ids).
- Permissions are read from the JWT `permissions` claim (Auth0 RBAC), the namespaced `CustomPermissionsClaim` (for the Entra-group→Action→custom-claim assignment model), or space-delimited `scope`. The Entra-group-driven model is the chosen assignment approach: a post-login Auth0 Action maps Entra group membership to the `vitally:*` permissions and writes them to the custom claim.
- **Live group check (preferred for prompt propagation):** when `LiveGroupCheck=true`, `ToolAuthorizer` resolves permissions from the caller's *current* Entra group membership via `GraphGroupPermissionResolver` (Microsoft Graph — it lists each configured group's `transitiveMembers` filtered to the caller's object id, using the managed identity, cached `LiveGroupCacheSeconds` per user) instead of the token claim — so grants and **revocations** take effect within the cache window regardless of token/refresh age (the post-login Action does **not** re-run on refresh grants, so the claim alone is frozen at login). **`transitiveMembers` expands nested groups**, so a user who gets a tier via a department group nested inside an `sg-vitally-*` group is authorised, not only users assigned to the `sg-vitally-*` group directly. The object id is taken from the `oid` claim or the trailing GUID of `sub`. Requires the managed identity to hold Graph `GroupMember.Read.All`.
- **A Graph failure degrades in two steps, not one** (#106). The effective order is **fresh Graph → stale Graph → token claim → deny**: `GraphGroupPermissionResolver` keeps each successful lookup with the time it was resolved and, when a Graph call fails, serves that caller's last known-good set for up to `LiveGroupStaleSeconds` (default 1 h) before giving up and returning null. Only then does `ToolAuthorizer` fall through to the token claim. Serving stale logs **one** warning carrying the subject id and how stale the result is in seconds — never the email, never two lines per call.
  - **Why it exists:** once Auth0 is retired the claim tier is permanently empty, so a Graph outage would deny every user while the code still *read* as having a working degraded path. Bounded staleness is the trade — a revoked user could retain access for up to the window, but only while Graph is unavailable, which is strictly tighter than the 8-hour frozen claim the design tolerated before the live check existed.
  - **The two windows are separate on purpose, and must stay separate.** `LiveGroupCacheSeconds` governs answering *without asking Graph*; `LiveGroupStaleSeconds` is consulted *only after a Graph call has failed*. Collapsing them — e.g. by simply lengthening the cache TTL to an hour — would stop revocations propagating, which is the whole reason the live check exists. `ReHitsGraph_OnceTheFreshTtlLapses_DespiteRetainingAStaleCopy` is the regression guard.
  - Age is measured against an injected `TimeProvider`, not by cache expiry: the warning needs the age itself, and `IMemoryCache` expiry cannot be wound forward in a test. The cache entry is keyed per user, which is what stops one caller's retained tier being served to another during an outage (`DoesNotServeOneUsersStaleResult_ToAnother`).
  - **The claim tier is still live and still load-bearing.** #106 deliberately added the stale cache *beneath* it rather than replacing it: while Auth0 issues tokens the claim genuinely authorises, so removing it now would swap a working fallback for a denial. Removing it — and making the fail-closed explicit — belongs to the cutover (#108), where staging can prove the degraded path before it is the only path.
- Server-side RBAC backstop. `ToolAuthorizer.EnsureAuthorizedAsync(method, ct)` is awaited from **`VitallyService.SendAsync`** — the single point every Vitally call funnels through — so all 93 tools are covered without per-tool annotation. The HTTP verb maps to the tier: GET → read, POST/PUT/PATCH → write, DELETE → delete (unknown verbs fall back to the strictest). Permissions are read from the JWT `permissions` claim (Auth0 RBAC), the namespaced `CustomPermissionsClaim`, or space-delimited `scope` (same three sources as above). Bypassed when `Enabled=false` or `OAuth:NoAuth=true`. **The `ReadOnly`/`Destructive` tool attributes are advisory client hints; this is the actual enforcement — when adding a new call path, route it through `VitallyService.SendAsync` so it stays covered, and never call the Vitally API around it.**
- **Per-caller discovery filtering.** All 93 tools carry `[Authorize(Policy = "vitally:read|write|delete")]` (56 read / 25 write / 12 delete). `mcpBuilder.AddAuthorizationFilters()` makes the SDK evaluate that attribute on each tool, so `tools/list` shows only the tools the caller may actually invoke and an unauthorised call is rejected before the handler runs. **It and `AddAuthorizationBuilder()` are registered unconditionally — never guarded on `OAuth:NoAuth`.** Once any tool carries `[Authorize]`, the SDK *fails closed*: it throws ("Authorization filter was not invoked for tools/call operation, but authorization metadata was found on the tool") so a guarded registration yields a dev server that can neither list nor call any tool. Dev mode stays unfiltered instead via `VitallyPermissionHandler`, which succeeds when `ToolAuthorizer.IsAuthorizationBypassedAsync()` reports RBAC disabled or `NoAuth`. `VitallyPermissionHandler` resolves those policies through `ToolAuthorizer.HasEffectivePermissionAsync`, so discovery and the `VitallyService.SendAsync` backstop cannot drift apart. This is **discovery filtering** — the security boundary remains `SendAsync`. Distinct from the deployment-wide `Authorization:ReadOnly` switch, which hides destructive tools from everyone.
- A denial refused at this SDK authorisation checkpoint is audited separately: see `LogToolCallDenied` under `AuditOptions` below — `SendAsync`'s own `LogDenied` never fires for a tier mismatch, because the SDK rejects the call before `SendAsync` runs.

`AuditOptions` (singleton, bound from `Audit:` section):
- `Enabled` (default `true`), `IncludeReads` (default `false`).
- `AuditLogger` is invoked from **`VitallyService.SendAsync`** (same choke point): `LogAction` after each upstream response and `LogDenied` on an RBAC denial. Records the authenticated user's stable subject id (`sub` claim, falling back to NameIdentifier, else `anonymous`), HTTP verb, resource path (query string stripped) and status code via structured logging — so the named properties become queryable dimensions in Application Insights / Log Analytics. **Log the `sub` (opaque, attributable Entra object id), never the email — keep personal data out of telemetry. Never log request/response bodies here either — they can contain customer PII (traits, transcripts).** This is the attribution mechanism while a single shared Vitally key is in use (per-user keys via the `secret_ref` claim remain a future option).
- A separate call, `AuditLogger.LogToolCallDenied`, is invoked from `VitallyPermissionHandler` for a denial at the SDK's per-tool `[Authorize]` checkpoint (see the discovery-filtering note above) — it records the caller's subject id, the tool name and the required permission, never the email, request bodies or tool arguments. This exists because that checkpoint rejects out-of-tier calls before `VitallyService.SendAsync` runs, so `LogDenied` there would never see a tier mismatch.

### API key resolution (VitallyApiKeyProvider.cs)

Scoped. Resolution order on each call to `GetApiKeyAsync()`:

1. If no `SecretClient` is registered (i.e. `KeyVaultUri` not set) and `DevelopmentApiKey` is set, return it. If neither is set, throw.
2. Check `IMemoryCache` for `"vitally-api-key::{DefaultSecretRef}"`. Return if hit.
3. Call `SecretClient.GetSecretAsync(DefaultSecretRef)` (uses the Container App's user-assigned managed identity), cache the value for `SecretCacheDuration`, return.

This means: rotating the Vitally key is a `Set-AzKeyVaultSecret` away (cache expires on its own). Per-user keys can be re-introduced later by extending the provider to read the `https://vitally.fiscaltec.com/secret_ref` claim (set by the Auth0 Action) and selecting a different secret name per user — no other architecture changes needed.

### HTTP Service (VitallyService.cs)

Scoped via `AddHttpClient<VitallyService>()`. Per-request auth: the constructor takes the per-request `VitallyApiKeyProvider`, and the private `SendAsync(method, url, content?)` helper builds each `HttpRequestMessage`, fetches the API key from the provider, sets the `Authorization: Basic` header on the message, and dispatches via `_httpClient.SendAsync`. The shared `HttpClient` is *not* mutated — there's no `DefaultRequestHeaders.Authorization`, so multi-user safety is preserved.

On non-2xx responses `SendAsync` reads the response body, disposes the response, and throws `HttpRequestException` with `StatusCode` set and a message that includes a truncated copy of the response body. This deliberately replaces `EnsureSuccessStatusCode()` because Vitally returns the actual failure reason (e.g. `{"message":"externalId is required"}`) in the body, and surfacing it gives the LLM something concrete to act on. The MCP SDK only forwards an exception's own message to the client when it is an `McpException`, so a CallTool request filter (`ToolErrorResult` + `AddCallToolFilter` in `Program.cs`) is what actually delivers this body — and the read-only/RBAC denial and `ArgumentException` validation messages — to the client; other (unexpected) exceptions still yield the SDK's generic error.

Standard methods (apply field/trait filtering and the `{results, next}` envelope):
- `GetResourcesAsync` — list with pagination, sorting, filtering
- `GetResourceByIdAsync`
- `CreateResourceAsync` / `UpdateResourceAsync` / `DeleteResourceAsync`

Raw pass-through methods (no field filtering — for endpoints whose response shape is not the standard `{results, next}` envelope, e.g. surveys returning `{data, next}`, customFields returning a bare array, or for sub-resource paths like meeting participants):
- `GetRawAsync(path, queryParams)` — GET with URL-encoded query string
- `PostRawAsync(path, jsonBody)`
- `DeleteRawAsync(path)`

### Rate-Limit Handler (VitallyRateLimitHandler.cs)

Vitally's documented limit is **1000 requests / minute (sliding window)**. The handler is a `DelegatingHandler` registered via `AddHttpMessageHandler<VitallyRateLimitHandler>()` in `Program.cs`, so all HTTP calls made by `VitallyService` go through it transparently.

Behaviour:
- **On HTTP 429 Too Many Requests:** waits and retries up to `MaxRetries` (default 3). Wait time is taken from `Retry-After` (preferred), then `X-RateLimit-Reset` (Unix seconds), falling back to `FallbackRetryDelay` (default 5s). The wait is capped at `MaxRetryDelay` (default 60s).
- **On any non-429 response:** if `X-RateLimit-Remaining` is below `LowRemainingThreshold` (default 50), logs a warning via `ILogger` so callers can throttle themselves.
- **When retries are exhausted:** the 429 response is returned to the caller, which propagates as `HttpRequestException` via `EnsureSuccessStatusCode`.

All thresholds are public mutable properties, so they can be tweaked in tests or future configuration without touching the handler internals.

**Vitally API Parameters:**
- Pagination uses `from` parameter (not `cursor`) - pass the `next` value from previous response
- Sorting via `sortBy` parameter: `"createdAt"` or `"updatedAt"` (default: updatedAt)
- Resource-specific filters (e.g., `status` for accounts: active, churned, activeOrChurned)

**Client-Side Filtering:**
- The Vitally API does NOT support field or trait selection natively
- `VitallyService` implements client-side JSON filtering after receiving full API response
- Uses `System.Text.Json.JsonDocument` to parse and filter fields and traits
- Only includes fields that actually exist on the resource (via `TryGetProperty`)
- Preserves pagination metadata (`next` field) in filtered responses
- **Trait filtering:** When traits parameter is specified, filters the traits object to include only requested trait keys
- **Default behaviour:** Traits are excluded by default to reduce response size - use traits parameter to include specific traits
- Reduces response size before returning to LLM

**Resource-Specific Default Fields:**

When no fields are specified, each resource type returns an optimised field set:

| Resource | Default Fields |
|----------|----------------|
| **Accounts** | id, name, createdAt, updatedAt, externalId, organizationId, healthScore, mrr, accountOwnerId, lastSeenTimestamp |
| **Organizations** | id, name, createdAt, updatedAt, externalId, healthScore, mrr, lastSeenTimestamp |
| **Users** | id, name, createdAt, updatedAt, externalId, email, accountId, organizationId, lastSeenTimestamp |
| **Conversations** | id, externalId, subject, status, source, authorId, accountId, organizationId |
| **Notes** | id, createdAt, updatedAt, externalId, subject, noteDate, authorId, accountId, organizationId, categoryId, archivedAt |
| **Tasks** | id, name, createdAt, updatedAt, externalId, dueDate, completedAt, assignedToId, accountId, organizationId, archivedAt |
| **Projects** | id, name, createdAt, updatedAt, accountId, organizationId, archivedAt |
| **Admins** | id, name, email |
| **NPS Responses** | id, externalId, userId, score, feedback, respondedAt |
| **Project Templates** | id, name, createdAt, updatedAt, projectCategoryId, description |
| **Project Categories** | id, name, createdAt, updatedAt |
| **Messages** | id, type, externalId, timestamp, message, from, to |
| **Custom Objects** | id, name, createdAt, updatedAt |
| **Note Categories** | id, name, createdAt, updatedAt |
| **Task Categories** | id, name, createdAt, updatedAt |
| **Meetings** | id, title, externalId, startDateTime, endDateTime, location, source, accountIds, organizationIds, participants, createdAt, updatedAt |
| **Meeting Transcripts** | id, meetingId, createdAt, updatedAt |
| **Custom Object Instances** | id, name, externalId, createdAt, updatedAt, organizationId, customerId, archivedAt |
| **Admins / Admins Search** | id, name, email |

These defaults balance usefulness (business context, relationships, key metrics) with response size (excluding large fields like traits objects, rich text content, transcript bodies, and meeting summaries).

**Resources NOT using field filtering** (raw pass-through):
- **Custom Traits** (`customFields` endpoint) — returns a bare array of trait definitions; client-side filtering does not apply.
- **Custom Surveys** (`surveys/:id/responses`, `surveyResponses/:id`, `surveyQuestions/:id`) — uses a `{data}` envelope rather than `{results, next}`.
- **Meeting sub-resources** (`meetings/:id/participants`, `meetings/:id/transcript`) — body is returned as-is from the API.

**Trait Filtering:**

Resources supporting traits: **Accounts, Organizations, Users, Tasks, Notes, Projects, Project Templates**

Traits are excluded by default to minimise response size. To include specific traits:
1. Add `"traits"` to the `fields` parameter
2. Specify desired trait names in the `traits` parameter (comma-separated)

Example: To get account name and payment method trait:
- `fields="id,name,traits"`
- `traits="paymentMethod"`

This will return only the `paymentMethod` trait, filtering out all other traits from the response.

### Tool Structure (Tools/*.cs)

Each resource type has a dedicated tool class:
- Decorated with `[McpServerToolType]` for discovery
- Static methods decorated with `[McpServerTool]` and `[Description]`
- Pattern: `List{Resource}` and `Get{Resource}` methods
- Dependency injection: `VitallyService` injected as method parameter
- All parameters use `[Description]` attributes for MCP tool schema generation

**Example tool pattern:**
```csharp
[McpServerToolType]
public static class AccountsTools
{
    [McpServerTool, Description("List Vitally accounts...")]
    public static async Task<string> ListAccounts(
        VitallyService vitallyService,
        [Description("Maximum number...")] int limit = 20,
        [Description("Pagination cursor from previous response (use the 'next' value)")] string? from = null,
        [Description("Comma-separated fields... Client-side filtering.")] string? fields = null,
        [Description("Sort by field: 'createdAt' or 'updatedAt'")] string? sortBy = null,
        [Description("Filter by account status: 'active', 'churned', 'activeOrChurned'")] string? status = null,
        [Description("Comma-separated trait names... Client-side filtering.")] string? traits = null)
    {
        var additionalParams = new Dictionary<string, string>();
        if (!string.IsNullOrEmpty(status))
            additionalParams["status"] = status;

        return await vitallyService.GetResourcesAsync("accounts", limit, from, fields, sortBy, additionalParams, traits);
    }
}
```

**Note:** The `status` parameter is specific to `AccountsTools`, and `archived` is specific to `MeetingsTools`. The `traits` parameter is available for resources that support traits (Accounts, Organizations, Users, Tasks, Notes, Projects, Meetings, Project Templates). Other resource types have the standard parameters (limit, from, fields, sortBy).

**Raw pass-through tools:** `CustomTraitsTools`, `SurveysTools`, and the participant/transcript methods on `MeetingsTools` call `GetRawAsync` / `PostRawAsync` / `DeleteRawAsync` directly. They do not accept a `fields` parameter because Vitally returns these endpoints with a non-standard JSON envelope (`{data}` for surveys, bare arrays for `customFields`).

**Custom object instances:** `List_custom_object_instances` accepts an optional single scope
criterion — `organizationId`, `customerId`, `externalId`, or `customFieldId`+`customFieldValue`
— which routes to Vitally's `customObjects/:id/instances/search` endpoint (exactly one criterion;
paging params are ignored when scoped). `Get_custom_object_instance` reads one instance by id via
the same search endpoint (Vitally has no direct single-instance GET). The legacy free-text
`Search_custom_object_instances` tool has been removed in favour of these typed paths.

**Organisation summary (SP5b):** `Get_organization_summary(organizationId)` is a read-only composite
that collapses the common "everything about this customer" shape into one call. It makes 4 upstream
calls — org get-by-id (with a curated set of rollup traits), one `customObjects` list to resolve the
goals/product-feedback object names to ids, and two organisation-scoped instance searches — and
returns `{ organization, goals, productFeedback }`. `goals`/`productFeedback` are each
`{results:[...]}` or `{error:...}` (a single sub-failure never sinks the summary; a bad org id
surfaces an error). The tenant-specific policy (the curated trait CSV and the default object names
`customerGoals` / `productFeedback`) lives as constants in `Tools/SummaryTools.cs`;
`VitallyService.GetOrganizationSummaryAsync` is generic and takes them as parameters. Object ids are
resolved by name at runtime (never hardcoded); the trait set and object names are overridable per call.

**Server-side page-and-filter (SP3):** Vitally's list endpoints can't filter by name or date, so a
bounded auto-pager (`VitallyService.GetByNameContainsAsync` / `GetByCreatedRangeAsync`, capped by
`Vitally:MaxAutoPageFetches`, default 10 pages × 100) pages and filters client-side. The tools that
use it — `List_organizations` (`nameContains`) and the activity lists (`createdAfter`/`createdBefore`
on conversations incl. by-account/by-organization, notes, tasks, meetings) — return a
`{results, truncated, pagesFetched}` envelope; `truncated: true` means the page cap was hit before
exhaustion (narrow the query). `List_custom_traits` takes a `nameContains` that filters the single
trait-catalogue array client-side (no paging) — it does **not** introduce field/trait projection
(the `customFields` endpoint remains the raw pass-through described above); `nameContains` only drops
array elements whose `label`/`path` don't match. Unfiltered calls keep the plain `{results, next}` path.

## Adding New Resource Types

To add support for a new Vitally resource:

1. Create `Tools/{ResourceName}Tools.cs` following the pattern in `AccountsTools.cs`
2. Implement `List{ResourceName}` and `Get{ResourceName}` methods
3. Use `VitallyService` with appropriate resource type string
4. **If the endpoint returns the standard `{results, next}` envelope:** add an entry to `ResourceDefaultFields` in `VitallyService.cs` with the optimised default field set
5. **If the endpoint returns a non-standard envelope** (e.g. `{data}`) or is a sub-resource path (e.g. `meetings/:id/participants`): use `GetRawAsync` / `PostRawAsync` / `DeleteRawAsync` — these bypass client-side filtering and return the body unchanged
6. **For sub-paths under an existing resource** (e.g. `admins/search`): add an explicit entry to `ResourceDefaultFields` for the full path — the lookup is exact-match, not prefix-match
7. Tools are automatically discovered via assembly scanning — no manual registration needed
8. Add a matching `Tools/{ResourceName}ToolsTests.cs` under `VitallyMcp.Tests/Tools/`

## Important Notes

- **UK English**: Use UK spelling (organisations, authorisation, etc.) in all code comments and documentation
- **Permission management**: Tools use `ReadOnly = true` flag for GET/LIST operations and `Destructive = true` flag for CREATE/UPDATE/DELETE operations. This allows MCP clients to bulk enable/disable operations by permission level.
- **Write operations**: All resources support full CRUD operations (where applicable). JSON body parameters accept complete request bodies for create/update operations.
- **Configuration**: Never hardcode credentials. Production deployments use Key Vault via managed identity; local dev uses `Vitally:DevelopmentApiKey` (env var `Vitally__DevelopmentApiKey`).
- **Error handling**: `VitallyService.SendAsync` throws `HttpRequestException` with the Vitally response body included in the message on non-2xx responses. A CallTool request filter (`ToolErrorResult` + `AddCallToolFilter`, `Program.cs`) surfaces the messages of `HttpRequestException`, `UnauthorizedAccessException` (read-only / RBAC denial) and `ArgumentException` (validation) to the client as the tool-call error text, so the LLM sees the actual failure reason rather than the SDK's generic "An error occurred invoking 'X'."; other exceptions keep the generic message.
- **Client-side filtering**: Field and trait selection is done client-side (Vitally API doesn't support it natively).
- **Trait filtering**: Traits are excluded by default — use the `traits` parameter to include specific trait keys (requires `"traits"` in the `fields` parameter).
- **Resource-specific defaults**: Each resource type has optimised default fields (see table above).
- **Field existence**: Only includes fields that actually exist on the resource — no null/undefined placeholders.
- **Pagination**: Use the `from` parameter (not `cursor`) — this matches the Vitally API spec.
- **JSON responses**: Tools return filtered JSON strings to reduce LLM context usage.
- **MCP SDK**: Using `ModelContextProtocol` 2.2.0 plus `ModelContextProtocol.AspNetCore` 2.2.0 for HTTP hosting. Check `VitallyMcp.csproj` rather than trusting this line — Dependabot bumps the SDK and prose drifts.
- **`tools/list` cache hints**: bound from `ToolsListCache:` (`Enabled`, `TimeToLive` default 5 min, `Scope` default `Private`) and serialised as `ttlMs` / `cacheScope` per MCP 2026-07-28. Scope must stay `Private` while per-caller filtering is active — a public cache would leak one tier's tool catalogue to another.
- **Tool annotations**: every tool sets `ReadOnly`, `Destructive`, `Idempotent` and `OpenWorld`. `ToolAnnotationCoverageTests` enforces this by reflection — all four must be explicitly set, and `ReadOnly == true` must imply `Destructive == false` (and vice versa) — so a new tool cannot ship unannotated.

## Testing

The `VitallyMcp.Tests` project contains the automated test suite (xUnit + FluentAssertions + Moq).

**The test runner is Microsoft.Testing.Platform (MTP), not VSTest.** `xunit.v3` 4.0.0 dropped VSTest
support outright — under the .NET 10 SDK its targets fail the build with *"Testing with VSTest target
is no longer supported by Microsoft.Testing.Platform"* rather than falling back. Three things follow,
and each one bites silently if forgotten:

1. **`global.json` selects the runner** (`test.runner = "Microsoft.Testing.Platform"`). Without it
   `dotnet test` still picks VSTest and every run fails at that MSBuild error. It pins no SDK version
   — deliberately, so `actions/setup-dotnet` stays in charge of that.
2. **The test project is an `Exe`.** xunit.v3 self-hosts its runner, so `Microsoft.NET.Test.Sdk` and
   `xunit.runner.visualstudio` are gone from `VitallyMcp.Tests.csproj`; omitting `<OutputType>Exe</OutputType>`
   fails the build with *"xUnit.net v3 test projects must be executable"*.
3. **VSTest-only CLI options are rejected, not ignored** — `--nologo`, `--verbosity`, `--logger`,
   `--collect` and `--filter "FullyQualifiedName~X"` all error out or silently run zero tests. On the
   .NET 10 SDK the MTP replacements are first-class `dotnet test` flags needing **no `--` separator**:
   `--report-trx` / `--report-trx-filename` (via `Microsoft.Testing.Extensions.TrxReport`), `--coverage`
   / `--coverage-output-format cobertura` (via `Microsoft.Testing.Extensions.CodeCoverage`), and
   `--filter-class` / `--filter-method`. MTP's coverage report is named `<guid>.cobertura.xml`, so
   `.github/workflows/ci.yml` globs `TestResults/**/*.cobertura.xml` rather than a fixed filename.

```powershell
# Run the full suite
dotnet test VitallyMcp.sln -c Debug

# Run a single test class
dotnet test VitallyMcp.sln -c Debug --filter-class "*MeetingsToolsTests"
```

**Coverage:**
- `VitallyApiKeyProviderTests` — dev-fallback resolution (no SecretClient → returns `DevelopmentApiKey`; missing both → throws)
- `VitallyServiceTests` — JSON field/trait filtering, pagination, resource-specific defaults, plus all six service methods (`GetResourcesAsync`, `GetResourceByIdAsync`, `CreateResourceAsync`, `UpdateResourceAsync`, `DeleteResourceAsync`, `GetRawAsync`, `PostRawAsync`, `DeleteRawAsync`) including HTTP-verb / URL / auth-header verification via Moq protected verification
- `VitallyRateLimitHandlerTests` — 429 retry behaviour, header parsing, low-remaining warnings
- `UpstreamOidcMetadataTests` / `UpstreamOidcStartupFailFastTests` — the OIDC-discovery resolver (all four endpoints, cache reuse, last-known-good on a failed refresh, rejection of an incomplete or malformed document) and the startup fail-fast wired into `Program.cs`
- `Tools/*ToolsTests` — one test class per `Tools/*Tools.cs`, covering every public `[McpServerTool]` method (list/get/create/update/delete plus sub-resources)

**When adding a new tool method:** add a matching test in the appropriate `*ToolsTests.cs` file. Use `TestHelpers.BuildVitallyService(httpClient)` — it builds a `VitallyService` with a stub `VitallyApiKeyProvider` that returns a fixed test API key (no Key Vault required).

**Manual testing considerations** (require live Vitally credentials and a real Auth0-issued token in production):
- Test pagination by using low limit values (e.g., `limit=5`) and verify `from` parameter works with `next` cursor
- Test client-side field filtering by specifying various field combinations
- Test trait filtering by combining `fields="traits"` with `traits="trait1,trait2"`
- For accounts, test the status filter with: `active`, `churned`, `activeOrChurned`
- For meetings, test the `archived` filter
- For local dev without Auth0, set `OAuth__NoAuth=true` and `Vitally__DevelopmentApiKey=<your key>`
- Verify error handling with invalid IDs / missing config

## Deployment

The deployment shape is **Azure Container Apps + Azure Key Vault + Auth0** (with Auth0 federating to Microsoft Entra for FISCAL employee sign-in), and the container image hosted in Azure Container Registry. `.github/workflows/deploy.yml` builds the image, imports it into the private ACR (via GitHub OIDC, no long-lived credentials) and rolls a Container App to the new revision.

**It deploys to one of two targets, and the target is a GitHub *environment* name:** `production`
(the default, and what the nightly release train ships to) or `staging` — see the staging section
below. Everything that differs between targets is an environment-scoped GitHub variable
(`CONTAINER_APP`, `PUBLIC_ORIGIN`), so the workflow contains no per-target literals and the smoke
test, metadata verification and rollback are shared rather than duplicated per target and left to
drift. Shared values (`ACR_NAME` / `RESOURCE_GROUP` / `IMAGE_NAME`) stay repo-level, as do the secrets
`AZURE_CLIENT_ID` / `AZURE_TENANT_ID` / `AZURE_SUBSCRIPTION_ID`. A first step fails the run *before*
anything is built if any of those is missing, or if `PUBLIC_ORIGIN` is not a slash-free absolute https
origin — unset, every smoke assertion below would be made against a relative path, fail, and roll back
a deploy that was in fact fine.

Each target needs its **own federated credential** on the managed identity, subject
`repo:fiscaltec/vitally-mcp:environment:<target>`, plus `Contributor` on that target's Container App.
The subject is an exact string match, so a target with no credential fails at `azure/login` rather
than deploying somewhere unintended.

| Component | Resource | Notes |
|---|---|---|
| Hosting (production) | Azure Container Apps `vitally-prod-ca-uksouth` (consumption plan) | `minReplicas: 1` (one warm replica); HTTPS-native ingress; managed cert on `vitally.fiscaltec.com` |
| Hosting (staging) | Azure Container Apps `vitally-staging-ca-uksouth`, **same** RG and CAE | Scale-to-zero (`minReplicas: 0`); managed cert on `vitally-staging.fiscaltec.com`; the pre-production target for identity changes — see below |
| Secrets | Azure Key Vault | `vitally-shared` is the default secret name; managed identity has `Key Vault Secrets User` |
| Identity | User-assigned managed identity | `AcrPull` on the registry + `Key Vault Secrets User` on the vault |
| Image registry | Azure Container Registry (Premium SKU) | `vitally-mcp:sha-<short-sha>` tag per build; untagged purged after 7 days; ACR Task weekly purge keeps last 5 tags / 30 days |
| Logs | Log Analytics (attached to the CAE) | + Application Insights for traces |
| Auth | Auth0 tenant `fiscal-it.uk.auth0.com` | Two Resource Servers, one per origin: `https://vitally.fiscaltec.com/` and `https://vitally-staging.fiscaltec.com/` (trailing slash — identifiers are exact-match and immutable). **One** shared client (`Vitally MCP`) carrying both origins' `/oauth/callback`, so both targets use the same `SharedClientId` / `SharedClientSecret`. Post-login Action sets the `secret_ref` claim; tenant has **Resource Parameter Compatibility Profile** enabled to stop `resource=` forwarding to the Entra federation — and still needs it, since the proxy validates but does not terminate `resource` until the cutover |
| Identity (Entra, provisioned but inert) | App registration `Vitally MCP` `c3812e7d-a413-4169-b57e-803326611ba3` | Provisioned by #107 on 2026-09-02 and **not yet used by anything** — the cutover (#108) is what points `OAuth:Authority` at it. Both OAuth client and API resource in one registration; App ID URI `https://vitally.fiscaltec.com` (no slash), exposes `mcp.access`, both origins' `/oauth/callback`, `appRoleAssignmentRequired` with the same seven department groups assigned **directly** (nesting does not grant sign-in). Secret `entra-mcp-client-secret` in the vault, 12-month lifetime — a rotation commitment Auth0 did not carry. See `docs/runbooks/entra-app-registration.md` |
| CI/CD | GitHub Actions → OIDC federation → Azure | Reusable `deploy.yml` (build → GHCR → `az acr import` → roll, with smoke + rollback — the smoke covers `/health`, the exact-401 challenge **and** the OAuth metadata documents); nightly `release.yml` cuts a semver tag + GitHub Release, then deploys it — freeze by disabling the workflow, see the deploy-freeze note below; OIDC, no long-lived secrets in GitHub |
| IaC | Terraform (`infra/terraform/`) | Infrastructure-as-code is in this repo at `infra/terraform/` (adopted via import blocks; see `infra/terraform/README.md`). The `deploy.yml` workflow consumes whatever that provisions. |
| IaC — Entra | `infra/terraform/entra.tf` + the `azuread` provider | Added by #107. Same adopt-by-import convention, but it is the **first non-`azurerm` provider here**, so `terraform init` must be re-run before any plan. The client secret and the admin-consent grant are deliberately *not* modelled — state would hold the secret value, and the vault is private-endpoint only so Terraform cannot write it from outside the VNet regardless |

### Staging (`vitally-staging-ca-uksouth`) — an on-demand pre-production target

**Staging is created when it is needed and torn down when the work is done.** It is not a standing
environment, and `az containerapp list` showing only `vitally-prod-ca-uksouth` is a normal state, not
a fault — that is exactly what #112 was raised for. Decided 2026-08-28, after evaluating and rejecting
a separate dev environment (see the topology note at the end of this section).

**Deploy to it:**

```powershell
gh workflow run deploy.yml -f target=staging -f ref=<branch-tag-or-sha>
```

`https://vitally-staging.fiscaltec.com` — a second Container App in the *same* resource group and the
*same* Container Apps Environment as production, not a second environment. That is what makes it
cheap: the CAE is VNet-injected, so a new app inside it reaches Key Vault and ACR over the existing
private endpoints with no additional networking, and it reuses the same user-assigned managed
identity, so `AcrPull`, `Key Vault Secrets User` and the Graph `GroupMember.Read.All` grant already
cover it.

**Why it exists.** Authentication has the largest blast radius in this system, so the Entra migration
(#102 / #108) is validated here before production. The alternatives were rejected: a local server
behind an ephemeral HTTPS tunnel orphans one identity-provider app registration per run — identifier
URIs are immutable and must equal the server origin — which cost two sessions during #90; and
validating straight against production is the failure mode a staging-first design exists to prevent.
**The hostname being stable is the load-bearing property, not a convenience:** it is what lets one app
registration be reused across a multi-run validation.

**What it shares with production, deliberately:** the CAE, the managed identity, the ACR, the Key
Vault *and its `vitally-shared` secret*, the `sg-vitally-*` tier group ids, and the single Auth0
client. Sharing is the point — a staging environment that differs in more than the thing under test
cannot tell you whether a failure is the change or the environment.

**What diverges:** `OAuth__Audience` / `OAuth__Resource` (`https://vitally-staging.fiscaltec.com/`,
its own Auth0 Resource Server), `OAuth__PublicBaseUrl`, `minReplicas: 0`, and — at #108 —
`OAuth__Authority`, which staging flips to Entra first while production stays on Auth0.

**Two divergences that will cost you time if you meet them cold:**

- **A staging token carries no `permissions` claim.** The post-login Action's guard is
  `if (event.resource_server?.identifier !== 'https://vitally.fiscaltec.com/') return;`, so it mints
  nothing for the staging audience. That is harmless today because `Authorization:LiveGroupCheck` is
  `true` on both targets and entitlement comes from Graph `transitiveMembers` — but it does mean
  staging has **no claim-based fallback tier**: a Graph failure denies on staging where production
  would still degrade to the claim. Do **not** "fix" this by widening the Action's guard. The Action
  and its claim disappear at cutover, so the work would be thrown away, and the divergence is in the
  safe direction.
- **There is one Vitally tenant and its API keys are global.** Staging reads the *production*
  `vitally-shared` secret, so its write and delete tools mutate real customer data — there is no
  sandbox to point it at. `Authorization:ReadOnly=true` is deliberately **not** set, because #108's
  acceptance test needs the write tools visible to prove a reader is denied one. So staging is
  read-only by convention, not by configuration.

**The custom domain is bound out of band**, as production's is. `fiscaltec.com` is on Cloudflare, so
DNS is not in `infra/terraform/`: the zone needs an **un-proxied** (DNS-only) `CNAME` from
`vitally-staging` to the app's default FQDN, plus an `asuid.vitally-staging` `TXT` carrying the CAE's
`customDomainVerificationId`. Proxying the CNAME breaks managed-certificate issuance. Then
`az containerapp hostname add`, followed by
`az containerapp hostname bind --validation-method CNAME`.

#### What must survive a teardown

Tearing down the **Container App** is the whole teardown. Everything below is persistent scaffolding
that makes the next spin-up cheap, and deleting any of it is what turns a recreate back into the
multi-session exercise #112 was raised to end:

| Keep | Why |
|---|---|
| The Cloudflare `CNAME` + `asuid.vitally-staging` `TXT` | Costs nothing while the app is gone. The `CNAME` target is deterministic (`<app-name>.<CAE default domain>`), so it keeps pointing at the right place after a recreate under the same name |
| The Auth0 Resource Server `https://vitally-staging.fiscaltec.com/` | **Identifiers are immutable.** Deleting and recreating it per run is precisely the orphaning cost the stable hostname exists to avoid |
| The staging `/oauth/callback` on the shared Auth0 client | Same reason, and it is inert while no app answers there |
| The Entra staging redirect URI (#107) | Same reason |
| The `staging` GitHub environment + its `CONTAINER_APP` / `PUBLIC_ORIGIN` variables | The workflow reads them; recreating them by hand invites a typo into the origin, which the preflight check would catch but only after a wasted run |
| The federated credential and role assignments | See the identity note below |
| `containerapps-staging.tf` | The recreate recipe. Keep it in step with the live app rather than deleting it when the app goes |

The managed TLS certificate and the hostname binding go with the app and are re-created by the two
`az containerapp hostname` commands above — that plus the app itself is the entire spin-up, because
the CAE, identity, ACR and Key Vault are all shared and never leave.

#### One managed identity serves both targets — an accepted risk, not an open defect

`vitally-prod-id-uksouth` serves both targets and holds `Contributor` on the production Container App,
so a federated credential for `environment:staging` mints a token that **can roll production**. The
per-app role assignments give no protection, because one identity holds both — do not read them as a
boundary.

**Accepted deliberately on 2026-08-28**, on this basis: neither the `production` nor the `staging`
GitHub environment has protection rules or a deployment branch policy (verified, not assumed), and
`deploy.yml` is `workflow_dispatch`-able against production directly. So anyone who can trigger a
staging deploy can already trigger a production one, and the shared identity grants no privilege they
did not already hold. Don't "fix" this on sight — it was priced and taken.

**What would change the answer:** the moment `production` gains a protection rule that `staging` does
not — required reviewers, or a deployment branch policy — the shared identity becomes a way around
that gate, and it stops being an accepted risk. Revisit it then, and also if staging starts routinely
deploying unreviewed refs. The remedy is a `vitally-staging-id-uksouth` with its own `AcrPull`,
`Key Vault Secrets User`, Graph `GroupMember.Read.All` and an app-scoped `Contributor`; the Graph
grant needs admin consent, which is the only real friction.

#### Why there is no separate dev environment

Evaluated on 2026-08-28 and deliberately not done. A `global` / `prod` / `dev` split is the right end
state and the `global` tier already exists implicitly — the Premium ACR with CMK, the CMK vault, the
Auth0 tenant and DNS are all genuinely shared but carry `prod` names. The cheap version, if it is ever
picked up, is **not** full duplication: the VNet is `10.80.0.0/23` with only `10.80.0.0-.127`
allocated, so a second `/27` app subnet and its own CAE drop in with no re-addressing, one NAT gateway
serves multiple subnets in the same VNet, and both CAEs resolve the same private endpoints through the
shared DNS zone links. That closes the one gap the shared model cannot: **CAE-level and platform
changes cannot be rehearsed before production sees them.**

What no topology fixes: there is one Vitally tenant and its API keys are global, so any staging or dev
environment reads real customer data.

### The deploy smoke covers the OAuth metadata, not just liveness

`.github/scripts/verify-oauth-metadata.sh <origin>` is run by `deploy.yml` after the revision is live
and before the rollback step, so a failure reverts the revision.

It exists because `/health` == 200 and unauthenticated `/mcp` == 401 — the original smoke — both stay
green while the metadata documents are wrong. A wrong `issuer`, a broken
`issuer` ↔ `authorization_servers` pairing, a bad `jwks_uri`, an `/oauth/authorize` pointed at the
wrong upstream, a dropped `iss` flag, or a null-serialised optional each break every MCP client at its
next re-authentication *and deploy green*. The rollback therefore used to read as assurance it did not
provide (#110).

**Run it by hand against any origin** — that is the point of it being a script rather than inline
YAML, and it is also how the same assertions cover staging with no second copy to keep in step:

```bash
bash .github/scripts/verify-oauth-metadata.sh https://vitally.fiscaltec.com
bash .github/scripts/verify-oauth-metadata.sh https://vitally-staging.fiscaltec.com
bash .github/scripts/verify-oauth-metadata.sh http://localhost:5099   # a local run, for testing it
```

**The whole assertion set retries, not just the fetches.** Container Apps reports a new revision
`Provisioned`/`Healthy` *before* ingress finishes shifting traffic, so a check that runs immediately
can be answered by the **old** revision — a valid 200, a valid document, the previous configuration.
Asserting once turned that race into a rollback of a healthy deploy on 2026-08-28 (revision 21 came up
Healthy and was reverted 23 seconds later). `/health` and the 401 cannot catch the race in either
direction, because both pass on whichever revision answers; this is the first check able to tell them
apart, so it is the one that has to wait for the swap. Failures are collected and emitted as
annotations only after the final attempt.

Three things about it that are deliberate and shouldn't be "tidied":

- **No normalisation anywhere.** Trailing slashes and case are compared literally, because that is
  what clients do — a check that tolerated a difference would pass configurations clients reject.
- **It is invoked through `bash`**, not by its executable bit. The repo is authored on Windows with
  `core.filemode=false`, so an edit can silently drop the mode; relying on it would surface as
  "Permission denied" during a deploy rather than in review.

Verified when written by running it both ways round: it **passes** against a local server built from
`main`, and **fails with 6 problems** against the then-current production revision (which predated
#100) — including `jwks_uri: null` in the RFC 9728 document, the exact defect that makes the published
`@modelcontextprotocol/client` reject the whole document.

### Freezing deploys

**`gh workflow disable release.yml`** (re-enable with `gh workflow enable release.yml`). That is the
whole mechanism. It stops tagging, releasing and deploying together, which is the coherent unit: a
freeze must not be able to leave a GitHub Release behind for something that never shipped.

An **attended** deploy stays available throughout via `deploy.yml`'s own `workflow_dispatch`, which is
a separate workflow and unaffected by the freeze:

```powershell
gh workflow run deploy.yml --ref <tag-or-sha> -f ref=<tag-or-sha> -f image_tag=<tag>
```

That is the intended route during a freeze, and #108 needs it — the Entra cutover is gated on its
predecessors being *deployed*, not merely merged, so a freeze that blocked every deploy would deadlock
it.

**Nothing is lost by pausing.** The commits stay on `main`; whenever the train next runs it cuts one
tag and `--generate-notes` builds the changelog from everything since the previous tag. The only thing
forgone is intermediate version numbers for versions that never existed anywhere.

**Do not reintroduce an `AUTO_DEPLOY`-style variable.** #111 added one so tagging could continue during
a freeze, on the premise that freezing discarded changelog history. That premise was wrong, and the one
night it ran produced `v4.2.2` — a Release marked "Latest", deployed nowhere. Reverted in #115. If a
future change makes "release without deploying" look useful again, re-read this paragraph first: the
requirement it was serving did not exist.

**A `deploy` job showing as `skipped` in a release run is normal** and is not a freeze. The job is
gated on `new_tag != ''`, so a night with no new conventional commits produces no tag and nothing to
ship. Most historical runs look like this; the deploys that did fire (18, 19 and 22 August 2026)
appear as `deploy / Build, import to ACR, roll Container App`.

**Automatic deploys do not appear in `deploy.yml`'s run list.** When `release.yml` calls it via
`uses:`, the job runs *inside the caller's run* — `gh run list --workflow=deploy.yml` shows only
manual `workflow_dispatch` runs, which makes the automation look untested when it is not. A second
tell: the release train passes `image_tag: <semver>`, whereas a manual dispatch defaults to
`sha-<short-sha>`, so the deployed image name says which path shipped it
(`az containerapp show ... --query properties.template.containers[0].image`).

Infrastructure-as-code is in this repo at `infra/terraform/` — the table above documents the runtime contract for what `deploy.yml` expects. Anyone replicating can swap Container Apps for App Service, ACR for GHCR, Auth0 for Keycloak, etc., without touching the application code.
