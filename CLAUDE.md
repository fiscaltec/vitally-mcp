# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project Overview

This is a Model Context Protocol (MCP) server implementation in C# that provides full CRUD access to the Vitally customer success platform. The server is a **remote HTTP MCP server** whose OAuth façade is built for Microsoft Entra directly; users connect to it by URL rather than installing a binary. **Both targets authenticate against Entra directly.** #108 merged the cutover code on 2026-09-03 and the production configuration flip was applied on 2026-09-16; staging had run Entra since 2026-09-03. Auth0 is no longer in the sign-in path. **This server's** Auth0 objects — the client, both Resource Servers and the `Vitally MCP claims` Action — are retained as the rollback, see *Rollback* below. The `fiscal-it.uk.auth0.com` tenant itself stays regardless: it hosts Simple Asset System, its API and a Terraform client, and is not ours to delete. Read the provider off the live metadata (`curl https://vitally.fiscaltec.com/.well-known/oauth-authorization-server | jq .jwks_uri`) rather than trusting this file: it asserted the wrong state for twelve days once already, in the other direction.

**Key characteristics:**
- Full CRUD API access to Vitally resources (accounts, organisations, users, conversations, notes, projects, tasks, admins, NPS responses, project templates, project categories, messages, custom objects, meetings — including participants and transcripts — custom traits, custom surveys)
- Permission management via `ReadOnly` and `Destructive` flags on every tool, for MCP clients to enforce per-category permissions
- **Streamable HTTP transport** (MCP 2026-07-28) on the `ModelContextProtocol.AspNetCore` package, stateless mode
- **OAuth 2.1 protection** via JwtBearer on `/mcp`, against Entra on both targets; publishes RFC 9728 protected-resource metadata at `/.well-known/oauth-protected-resource`. An in-process OAuth proxy fronts whichever upstream provider `OAuth:Authority` names when `OAuth:SharedClientId` is set — Entra on both targets — and it implements an RFC 7591 DCR shim so every MCP client converges on one pre-registered first-party app (skipping the per-session consent screen and accepting any RFC 8252 loopback port). Non-loopback `redirect_uri` values must be in `OAuth:AllowedClientRedirectUris`. The proxy *validates* the RFC 8707 `resource` parameter (#105) and then **terminates** it, naming the API upstream by scope instead. `OAuth:UpstreamResourceScope` is the switch that does this — empty would relay the parameter instead, which is what a rollback to Auth0 would need — see the `resource` section under Architecture for why relaying it to Entra is a hard failure.
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

# Start the server in dev mode (no identity provider, no Key Vault)
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

FISCAL employees point their MCP client at `https://vitally.fiscaltec.com/mcp`. The client handles the OAuth flow automatically on first use via the protected-resource metadata document. Everyone signs in with their FISCAL Entra account, and since the 2026-09-16 flip both targets redirect to **Entra directly** — Auth0 is no longer in the path on either. See the current-state note at the top of this file.

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
      n=<PR>; head=$(git ls-remote origin "refs/pull/$n/head" | awk '{print $1}')
      gh pr view "$n" --json reviewRequests \
        --jq '[.reviewRequests[].login] | index("copilot-pull-request-reviewer") != null'   # false = not pending
      gh api graphql -f owner=fiscaltec -f name=vitally-mcp -F number="$n" \
        -f query='query($owner:String!,$name:String!,$number:Int!){repository(owner:$owner,name:$name){pullRequest(number:$number){reviews(last:100){nodes{author{login} commit{oid} submittedAt}}}}}' \
        --jq '[.data.repository.pullRequest.reviews.nodes[] | select(.author.login=="copilot-pull-request-reviewer")] | sort_by(.submittedAt) | last | .commit.oid'
      ```

      ⚠️ **Read the review commit from GraphQL, never from REST.** The REST reviews
      collection (`…/pulls/N/reviews`) lags for this bot by hours, not seconds. Measured on
      #129 on 2026-09-15: REST reported Copilot's latest review as `ee1770c` submitted
      `14:45:44Z` while GraphQL reported `1559962` at `18:43:14Z` — nine reviews and nearly
      four hours apart, from the same `gh` session seconds apart. The earlier version of this
      block used REST, so following it would have parked the PR indefinitely: the SHA never
      matches, re-requesting does not help, and the obvious reading is "Copilot hasn't
      reviewed the head yet" when it has. `.claude/hooks/pre-merge-copilot-gate.sh` had the
      same defect and was fixed with it — a fail-closed gate reading a stale source is not
      conservative, it is stuck, and a gate that cannot pass is a gate someone switches off.

      ⚠️ **Take the head SHA from `refs/pull/$n/head`, never from `gh pr view --json headRefOid`,
      in the seconds after a push.** The GraphQL field lags: on #122 it still reported the previous
      commit right after a push, so the comparison matched Copilot's *old* review and the gate read
      as passing. Two consequences, and the second is the expensive one: re-requesting in that
      window gets a review of the previous commit (that happened on #122 too — a review arrived four
      minutes after the request, on the superseded SHA), so **wait until the API reports the new head
      before re-requesting**.

      `refs/pull/$n/head` rather than `git rev-parse HEAD`, which was what this said until the hook
      moved to the remote ref: the local checkout is only the right answer when it *is* that PR's
      branch and has nothing unpushed, and merging a second PR from another branch is normal. The
      manual check and the hook must read the same source or they disagree exactly when it matters.

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

   **Pin the merge to the SHA you verified** — the hook requires it and denies without it — and
   **write the PR number and the SHA out literally**:

   ```bash
   gh pr merge 129 --squash --match-head-commit 4384e311ed03c94e79134bd7a7435b62d2e124e9
   ```

   ⚠️ **Not `"$n"` / `"$head"`.** The hook is a `PreToolUse` hook: it sees the command *text*,
   before the shell expands anything. So it reads `"$n"` as the PR argument and fails to resolve a
   PR from it, and `"$head"` as a non-hex pin — the variable form is denied outright. Verified by
   feeding both forms to the hook: `could not resolve a PR from '"$n"'` versus a clean pass.

   Everything checked above is true of *one moment*; `gh pr merge` runs after it, so a push landing
   in that window merges a commit nothing verified. The hook cannot see that race — it has already
   returned by then — so `--match-head-commit` hands the check to GitHub, which refuses the merge
   outright if the head moved. Without it the gate is a snapshot, which is #117's shape again.

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
- **It resolves the head from `refs/pull/N/head` via `git ls-remote`, not from the PR API.**
  `headRefOid` lags after a push (see the warning above), and a gate that trusts it fails *open* —
  the stale value names the commit Copilot already reviewed. `ls-remote` reads the git server
  directly, so it neither lags nor cares which branch is checked out, which matters because merging
  a second PR from another branch is normal. GitHub publishes `refs/pull/N/head` on the base repo
  for **every** PR including forks, so there is no branch-name path and no local-checkout fallback:
  an earlier version had both, and the fallback compared an unrelated commit whenever a local branch
  shared a fork PR's branch name. If that ref cannot be read, the gate denies.
- A newly added hook needs `/hooks` opened once (or a restart) to activate.
- **It requires `--match-head-commit <verified sha>`** and denies a merge without one, with a
  too-short abbreviation (under 7 characters), or with one naming a different commit. That is not
  belt-and-braces: every other check here describes the moment the hook ran, and the merge happens
  afterwards.
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
- Binds `OAuthOptions` from `OAuth:` (provider-agnostic — works with Entra, Auth0, Keycloak, etc.; only `OAuth:UpstreamResourceScope` differs between them, and it is a value rather than a branch).
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
`ProtectedResourceMetadataBuilder` publishes as `authorization_servers`. Entra still issues the
tokens and `jwks_uri` / `userinfo_endpoint` still point there; the façade covers identity and
discovery, not issuance.

It previously declared the **upstream provider's** issuer while being served from our origin, which violates RFC 8414
§3.3 (an anti-mix-up control: a metadata document can only ever speak for itself) and made strict
clients — including **MCP Inspector** — abort before reaching DCR. Fixed 2026-08-21 (#90).

**Three pieces that must stay together.** Changing any one alone breaks the flow:

| Piece | Why |
|---|---|
| `issuer` = our origin | RFC 8414 §3.3. Must equal `authorization_servers` in the RFC 9728 document byte for byte — the check is simple string equality, tolerating only a trailing slash *on the expected value*. |
| `/oauth/callback` strips any upstream `iss` and appends our own | RFC 9207. **Mandatory, not defensive.** Clients compare a *present* `iss` against the metadata `issuer` even when support is not advertised — so an upstream `iss` forwarded through would now be a hard failure, and appending ours alongside would leave two values. |
| `authorization_response_iss_parameter_supported: true` | Honest only because of the row above. Advertising it and then omitting `iss` reads as a stripped-parameter attack and the client aborts. |

**Verified 2026-08-21** against `@modelcontextprotocol/client` 2.0.0 — the package MCP Inspector 2.3.0
actually depends on — driving a local container: the RFC 9728 document parses, `discoverAuthorizationServerMetadata`
passes §3.3, **DCR is reached and returns the shared `client_id`** (it was never called before), and
`validateAuthorizationResponseIssuer` accepts the `iss` we emit. A control assertion confirms the same
function still rejects the upstream issuer, so the passes are not a skipped check.

Two things were verified rather than assumed, both of which the design had flagged as open:

- **No client validates the access token's `iss` against the metadata `issuer`.** The SDK's client
  auth module never decodes a JWT or fetches JWKS — access tokens are opaque to it — so `jwks_uri`
  pointing upstream while we claim the issuer is safe. Our own `JwtBearer` validates against the
  provider's `Authority` internally and is unaffected either way.
- **The published SDK 1.x does not enforce §3.3 at all**; the enforcement ships in the 2.x packages.
  So this was latent for Claude Desktop / Claude Code and immediately fatal for anything on 2.x.

**Validated against the live Auth0 tenant on 2026-08-22**, which the local run could not cover. MCP
Inspector 2.3.0 completed the whole flow against the server behind an HTTPS tunnel with
`PublicBaseUrl` set: metadata 200, DCR `201`, real Auth0→Entra sign-in, `/oauth/callback` 302 carrying
`iss=<our origin>` which the client *accepted*, `/oauth/token` 200, and authenticated `POST /mcp` 200.
That is the acceptance test in #90, and it is past the point where the flow previously aborted.

**If you touch this,** re-run that validation rather than reasoning about it — `npx @modelcontextprotocol/inspector`
against a local container is now a working end-to-end client, which is the practical payoff.

**Validate identity-provider changes against staging, not against a tunnel.** An identifier URI is
immutable and must equal the server origin (the client throws when the RFC 9728 `resource` does not
match what it fetched), so an ephemeral tunnel URL orphans one registration per run — which cost two
sessions during #90. `https://vitally-staging.fiscaltec.com` has a stable hostname and is already
carried in the Entra app's `identifierUris`/redirect URIs, so it can be reused run after run. Reach
for a tunnel only for something staging genuinely cannot cover, and budget for the cleanup if you do.

**How far a validation can get without a browser** — worth knowing before assuming the whole flow
needs a human. Everything up to the credential prompt is scriptable, and that covers most of what
breaks: the metadata documents (`verify-oauth-metadata.sh`), the proxy's upstream redirect (read the
`Location` and assert on its query), and **Entra's own acceptance of that redirect** — follow it and
Entra answers with either its sign-in page or an `AADSTS` error, which is a real test of `client_id`,
`redirect_uri`, `scope` and `resource` handling. What genuinely needs a person is a token: `az account
get-access-token --scope https://vitally.fiscaltec.com/mcp.access` fails with `AADSTS65001`, because
Azure CLI is not a pre-authorised client on this API, and adding it would grant a broad first-party
client permanent silent access — not a change to make for a test.

### The RFC 8707 `resource` parameter is validated here — and terminated here

`OAuthOptions.IsResourceIndicatorAllowed(value)` is the single check, reading
`PublishedResourceIdentifier` (`OAuth:Resource`, falling back to `OAuth:Audience`) — the same
property `ProtectedResourceMetadataBuilder` publishes, so the value validated against is by
construction the value clients were told to send. `/oauth/authorize` (query) and `/oauth/token`
(form body) both apply it and reject a mismatch with `invalid_target`; every value is checked when
the parameter repeats, and a present-but-empty one is a mismatch rather than an absence. An absent
`resource` is untouched — RFC 8707 is optional for clients.

**Why it is a real control and not paperwork.** It governs what audience a caller may ask to be
bound to. Until #105 the parameter was relayed unvalidated, so a client could name an audience this
server never published and the proxy would pass the request on. That check is unchanged by the
cutover and is deliberately independent of what happens to the value afterwards — the two questions
are separate, and conflating them is how the validation would quietly become "ignore it".

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

**What happens to a value that passes: `OAuth:UpstreamResourceScope` decides.** It is one setting
because neither half is usable alone.

| | unset | set (production and staging) |
|---|---|---|
| Provider it suits | Auth0 | Entra |
| A matching `resource` is | relayed verbatim | **dropped at the façade** |
| The API is named upstream by | that relayed `resource` | this scope, merged into `scope` |

`Program.cs` sends **no `audience` parameter anywhere**, so something has to name the API. On Auth0
that was the relayed `resource`, consumed locally by the tenant's *Resource Parameter Compatibility
Profile*; under Entra it is the scope. Dropping `resource` without the scope leaves the token bound
to nothing; adding the scope while still relaying `resource` is the failure below. Hence one switch,
and hence it is **configuration rather than a check on `OAuth:Authority`** — which is what keeps a
rollback a revert of environment variables.

**Relaying `resource` to Entra is a hard failure, and the reason is not the one #105/#107 recorded.**
Verified against the live tenant on 2026-09-02 by driving `/oauth2/v2.0/authorize` directly:

```
error=invalid_target&error_description=AADSTS9010010: The resource parameter provided in the
request doesn't match with the requested scopes.
```

Two corrections to what was written before the cutover:

- **Both spellings fail, not just the trailing-slash one.** `https://vitally.fiscaltec.com/` and
  `https://vitally.fiscaltec.com` both return **400**, as does a resource naming nothing at all. The
  v2 endpoint cross-checks `resource` against `scope`; it is not comparing against `identifierUris`,
  so the slash is beside the point. Dropping the parameter is therefore *required*, not merely
  tidier — a version that only normalised the slash would still be broken.
- **A bare `mcp.access` scope does not fail at `/authorize`** — Entra returns its sign-in page. It
  resolves an unqualified scope against Microsoft Graph, so the flow completes and hands back a token
  for the wrong resource. That is why both metadata documents advertise the App ID URI-qualified form
  in `scopes_supported`: the failure it prevents is a silent wrong audience, not a visible rejection.

The scope is merged on `/oauth/token` as well as `/oauth/authorize`. On a code exchange that is
redundant; on a **refresh** it is load-bearing, because Entra issues the new access token for
whatever resource `scope` names — so a client refreshing with only the OIDC scopes would silently be
handed a Graph-audience token, and the failure would surface at `/mcp` an hour after a sign-in that
looked fine.

The Auth0 tenant's compatibility profile is now irrelevant to this server. Leave it as it is; it is
harmless, and the Auth0 client stays in place for the rollback window regardless.

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

> **The targets now agree on the five IDENTITY settings** — `Authority`, `Audience`,
> `UpstreamResourceScope`, `SharedClientId` and the secret *value*. They diverged while #108 was
> half-applied and the 2026-09-16 flip reunified them, so those five are what #102 collapses.
>
> ⚠️ **`Resource` and `PublicBaseUrl` are not among them and must not be swept into that collapse.**
> Each names its own origin: give staging production's `Resource` and it publishes an RFC 9728
> document naming a server it is not, which every strict client rejects. Same for `PublicBaseUrl` — and
> so does the *Container App secret name* behind `SharedClientSecret`: production references
> `entra-oauth-client-secret`, staging `oauth-shared-client-secret`, for the same underlying value.
> That third difference is deliberate, not drift: production kept the old name for the retained Auth0
> credential instead of overwriting it, which is what makes its rollback free of a Key Vault window.
>
> | | Production (Entra, since 2026-09-16) | Staging (Entra, since 2026-09-03) |
> |---|---|---|
> | `Authority` | `https://login.microsoftonline.com/75bd…/v2.0` | *same* |
> | `Audience` | `https://vitally.fiscaltec.com` (no slash) | *same* |
> | `Resource` | `https://vitally.fiscaltec.com/` | `https://vitally-staging.fiscaltec.com/` |
> | `UpstreamResourceScope` | `https://vitally.fiscaltec.com/mcp.access` | *same* |
> | `SharedClientId` | `c3812e7d-a413-4169-b57e-803326611ba3` | *same* |
> | `SharedClientSecret` | `secretref:entra-oauth-client-secret` | `secretref:oauth-shared-client-secret` — same value, different Container App secret name (see Rollback) |
>
> `infra/terraform/variables.tf` still defines these as `oauth_*` / `staging_oauth_*` pairs, now holding
> the same values — collapsing them is tracked in #102. The Auth0 values these replaced, and the
> command to revert to them, are in *The Auth0 → Entra cutover and its rollback*.

- `Authority` — the provider's OIDC **issuer** identifier (see the per-target table above; Entra's is `https://login.microsoftonline.com/75bd6050-92a8-4bde-a406-50000b310c86/v2.0`). It is *not* a prefix the endpoint URLs are built from: `{Authority}/.well-known/openid-configuration` is fetched and the endpoints come from that document. The trailing slash is whatever the provider's own issuer carries — Entra's has none, Auth0's had one.
- `Audience` — the identifier validated against the JWT `aud`. Under **Entra** that is the App ID URI `https://vitally.fiscaltec.com` — **with NO trailing slash**, because Entra refuses to register a slash-suffixed `identifierUris` value; under **Auth0**, which nothing now uses but a rollback would, it is the Resource Server identifier *with* the slash. Validated against the JWT `aud` claim, though not alone: `OAuthOptions.ValidAudiences` also accepts `SharedClientId`, and that is what a **v2** access token actually carries (a v1 token carries this App ID URI). One registration is both the OAuth client and the API resource, so the two are the same object. See the divergence warning below for why this must not be reconciled with `Resource`.
- `Resource` — canonical resource identifier published in `/.well-known/oauth-protected-resource` (falls back to `Audience` if empty). Set explicitly when MCP clients validate metadata `resource` against the server URL/origin (RFC 8707 + RFC 9728 compliance) — the published client rejects the whole document on a mismatch. **It must not be reconciled with `Audience`** — see the divergence warning below.  Second role: `PublishedResourceIdentifier` (this value, falling back to `Audience`) is what an incoming RFC 8707 `resource` parameter is validated *against* on `/oauth/authorize` and `/oauth/token` — so it is now a control on what audience a caller may ask to be bound to, not only a value published. `PublicBaseUrl` is the odd one out: it is an origin, so no trailing slash (and `Validate()` trims one anyway).

> ⚠️ **`Audience` and `Resource` differ by exactly one character and must stay that way.** They were
> equal under Auth0 — that Resource Server identifier happened to carry a trailing slash, as does the
> form clients normalise to — so they read as one setting, and `infra/terraform/` fed both from a
> single `oauth_audience` variable. #108 split that variable, which is what makes the difference
> structural rather than a convention someone has to remember.
>
> | | Auth0 posture (rollback only) | Entra posture (both targets, live) |
> |---|---|---|
> | `OAuth:Audience` — validated against JWT `aud` | `https://vitally.fiscaltec.com/` | `https://vitally.fiscaltec.com` — **no slash**, Entra refuses to register one on `identifierUris` |
> | `OAuth:Resource` — published in RFC 9728, and validated against | `https://vitally.fiscaltec.com/` | `https://vitally.fiscaltec.com/` — **unchanged**, Claude Code normalises to it |
>
> Reconciling them "for consistency" breaks token validation in one direction and the metadata
> document in the other. On staging they diverge by *host* as well, because one app registration
> serves both origins (#107), so a staging token's `aud` is production's App ID URI — expected, not
> drift. `OAuthOptions.IsResourceIndicatorAllowed` tolerating exactly one trailing slash is what lets
> the two forms name one resource — see `docs/runbooks/entra-app-registration.md`.
- `UpstreamResourceScope` — `https://vitally.fiscaltec.com/mcp.access` on **both** deployed targets. Empty is the Auth0 posture, which now only a rollback would use. Setting it makes the proxy **terminate** the RFC 8707 `resource` parameter and name the API by this scope instead; leaving it empty relays `resource` (the Auth0 posture). One switch, because neither half works alone — see the `resource` section above. Validated at boot as a single whitespace-free token.
- `SharedClientId` — the pre-registered client every MCP client converges on via the DCR shim: the Entra app registration `c3812e7d-a413-4169-b57e-803326611ba3` on **both** targets. When set, the OAuth proxy endpoints become active. It is also a valid `aud` — see `Audience`.
- `SharedClientSecret` — confidential-client secret for `SharedClientId`, injected server-side at `/oauth/token`. On both targets its value comes from the Key Vault secret `entra-mcp-client-secret`, but the app does **not** read the vault for it: the value was **copied** into a Container App secret (`entra-oauth-client-secret` on production, `oauth-shared-client-secret` on staging) and `OAuth__SharedClientSecret` is a `secretRef` to that copy. That is not the `vitally-shared` pattern, and it means **rotating the Key Vault secret alone changes nothing the app sends** — see the corrected rotation procedure in the runbook and #138. It **expires 2027-03-01** — a hard outage date, but not for the reason the vault pattern would suggest: the app never reads the vault for it, so the failure is **Entra** rejecting the expired credential at `/oauth/token` with `invalid_client`, and no Key Vault state affects it. Rotation is in `docs/runbooks/entra-app-registration.md`.
- `AllowedClientRedirectUris` — non-loopback `redirect_uri` allowlist for the OAuth proxy. Loopback URIs (`localhost`, `127.0.0.1`, `[::1]`) on any port are always allowed per RFC 8252 §7.3; this list covers hosted MCP clients like `https://claude.ai/api/mcp/auth_callback`. `OAuthOptions.IsRedirectUriAllowed(uri)` is the single check; `/oauth/authorize` and `/oauth/register` both use it. **This is the only thing standing between the proxy and an open redirector with authorisation-code theft — never bypass it.**
- `PublicBaseUrl` — canonical public origin (e.g. `https://vitally.fiscaltec.com`). When set, `/.well-known/*` metadata and the OAuth proxy callback are built from this instead of the request `Host`, defending against Host-header injection into the metadata documents. Empty in local dev (falls back to request scheme+host so loopback works). Validated as absolute https.
- `NoAuth` — local-only dev flag that bypasses JWT validation entirely.

`ToolAuthorizationOptions` (singleton, bound from `Authorization:` section):
- `Enabled` (default `true`), `ReadPermission` (`vitally:read`), `WritePermission` (`vitally:write`), `DeletePermission` (`vitally:delete`), `CustomPermissionsClaim` (default `https://vitally.fiscaltec.com/permissions`).
- `ReadOnly` (default `false`) — deployment-level read-only kill switch. When true, **every** mutating tool call (create/update/delete) is denied in `ToolAuthorizer` (checked before the `Enabled`/`NoAuth` gate, so it holds even with RBAC off), and the destructive tools are hidden from `tools/list` via an `AddListToolsFilter`. A blunt safety net for read-only deployments that doesn't depend on the per-user Entra-group RBAC. Denials are audited via `LogDenied`.
- `LiveGroupCheck` (default `false`), `LiveGroupCacheSeconds` (default `60`), `LiveGroupStaleSeconds` (default `3600`; `0` disables stale serving), `ReaderGroupId`/`EditorGroupId`/`AdminGroupId` (Entra group object ids).
- **Entitlement comes from Entra group membership, resolved live from Graph — nothing in the token grants access.** `LiveGroupCheck` is `true` on every deployed target, and that flag alone chooses the mode: on, permissions come from Graph; off, from the token's `permissions` / `CustomPermissionsClaim` / `scope` claims. The claim path is a *different mode*, not a fallback beneath the live one, and there is no route from one to the other — including when the resolver is missing entirely, which denies rather than quietly selecting the claim mode. Those claim settings are inert on every deployed target — not because the Auth0 post-login Action was removed (it still exists, though nothing reaches it now that neither target signs in through Auth0) but because #108 removed the code path that read any claim while `LiveGroupCheck` is on (PR #125, `45a51db`). The Action is retained with the rest of the Auth0 configuration for the rollback window, and deleting it early is what the Deployment table warns against. Its output is simply no longer consulted.
- **Live group check (preferred for prompt propagation):** when `LiveGroupCheck=true`, `ToolAuthorizer` resolves permissions from the caller's *current* Entra group membership via `GraphGroupPermissionResolver` (Microsoft Graph — it lists each configured group's `transitiveMembers` filtered to the caller's object id, using the managed identity, cached `LiveGroupCacheSeconds` per user) instead of the token claim — so grants and **revocations** take effect within the cache window regardless of token/refresh age (a claim is frozen at login and does not refresh with the token). **`transitiveMembers` expands nested groups**, so a user who gets a tier via a department group nested inside an `sg-vitally-*` group is authorised, not only users assigned to the `sg-vitally-*` group directly. The object id is taken from the `oid` claim or the trailing GUID of `sub`. Requires the managed identity to hold Graph `GroupMember.Read.All`.
- **A Graph failure degrades in one step, then denies** (#106, #108). The order is **fresh Graph → stale Graph → deny**: `GraphGroupPermissionResolver` keeps each successful lookup with the time it was resolved and, when a Graph call fails, serves that caller's last known-good set for up to `LiveGroupStaleSeconds` (default 1 h) before giving up and returning null — at which point `ToolAuthorizer` denies, explicitly and with a log line saying which of the two fail-closed routes was taken. Serving stale logs **one** warning carrying the caller's Entra **object id** — the same `oid` `CallerIdentity` resolves for the audit record, not the JWT `sub`, so a stale-serve line joins to the audit trail and to the membership that caused it — and how stale the result is in seconds. Never the email, never two lines per call.
  - **Why it exists:** the stale copy is the only thing between a Graph outage and a total denial, now that no claim can authorise. Bounded staleness is the trade — a revoked user could retain access for up to the window, but only while Graph is unavailable, which is strictly tighter than the 8-hour frozen claim the design tolerated before the live check existed. `LiveGroupStaleSeconds: 0` turns the protection off and denies immediately.
  - **The two windows are separate on purpose, and must stay separate.** `LiveGroupCacheSeconds` governs answering *without asking Graph*; `LiveGroupStaleSeconds` is consulted *only after a Graph call has failed*. Collapsing them — e.g. by simply lengthening the cache TTL to an hour — would stop revocations propagating, which is the whole reason the live check exists. `ReHitsGraph_OnceTheFreshTtlLapses_DespiteRetainingAStaleCopy` is the regression guard.
  - Age is measured against an injected `TimeProvider`, not by cache expiry: the warning needs the age itself, and `IMemoryCache` expiry cannot be wound forward in a test. The cache entry is keyed per user, which is what stops one caller's retained tier being served to another during an outage (`DoesNotServeOneUsersStaleResult_ToAnother`).
  - **The degraded path is observed, not just unit-tested** (`StaleEntitlementCompositionTests`). #106 shipped it with unit coverage alone and it had never been seen working; staging cannot induce a Graph failure in isolation, because it shares the managed identity and CAE with production. #108 closed that gap with a composed-host test that drives the real wiring — DI, the typed Graph client, the singleton `IMemoryCache` that carries the retained copy *between requests*, the authorizer, the policy handler, the SDK filter — and reads the outcome off `tools/list` across an outage that starts, is survived, and then outlasts its window. A resolver handed a fresh cache per request would pass the unit tests and fail that one.
  - **The claim tier used to sit beneath the stale cache and was removed at the cutover (#108).** While Auth0 minted the claim it genuinely authorised, which is why #106 added the stale cache *beneath* rather than in place of it. The Action itself is **not** retired — it is retained for the rollback window — but nothing reaches it while both targets sign in against Entra, and #108 removed the code that reads any claim while `LiveGroupCheck` is on anyway, so no claim it mints can authorise anyone. A fall-through could therefore only ever have denied, leaving code that read like a working fallback and behaved like a silent denial. Do not reinstate it "as a safety net" on the grounds that the Action still exists: its output is not consulted, and a rollback to Auth0 does not change that.
- Server-side RBAC backstop. `ToolAuthorizer.EnsureAuthorizedAsync(method, ct)` is awaited from **`VitallyService.SendAsync`** — the single point every Vitally call funnels through — so all 93 tools are covered without per-tool annotation. The HTTP verb maps to the tier: GET → read, POST/PUT/PATCH → write, DELETE → delete (unknown verbs fall back to the strictest). Resolution is `HasEffectivePermissionAsync`, exactly as for discovery filtering (see above), so the two cannot disagree. Bypassed when `Enabled=false` or `OAuth:NoAuth=true`. **The `ReadOnly`/`Destructive` tool attributes are advisory client hints; this is the actual enforcement — when adding a new call path, route it through `VitallyService.SendAsync` so it stays covered, and never call the Vitally API around it.**
- **Per-caller discovery filtering.** All 93 tools carry `[Authorize(Policy = "vitally:read|write|delete")]` (56 read / 25 write / 12 delete). `mcpBuilder.AddAuthorizationFilters()` makes the SDK evaluate that attribute on each tool, so `tools/list` shows only the tools the caller may actually invoke and an unauthorised call is rejected before the handler runs. **It and `AddAuthorizationBuilder()` are registered unconditionally — never guarded on `OAuth:NoAuth`.** Once any tool carries `[Authorize]`, the SDK *fails closed*: it throws ("Authorization filter was not invoked for tools/call operation, but authorization metadata was found on the tool") so a guarded registration yields a dev server that can neither list nor call any tool. Dev mode stays unfiltered instead via `VitallyPermissionHandler`, which succeeds when `ToolAuthorizer.IsAuthorizationBypassedAsync()` reports RBAC disabled or `NoAuth`. `VitallyPermissionHandler` resolves those policies through `ToolAuthorizer.HasEffectivePermissionAsync`, so discovery and the `VitallyService.SendAsync` backstop cannot drift apart. This is **discovery filtering** — the security boundary remains `SendAsync`. Distinct from the deployment-wide `Authorization:ReadOnly` switch, which hides destructive tools from everyone.
- A denial refused at this SDK authorisation checkpoint is audited separately: see `LogToolCallDenied` under `AuditOptions` below — `SendAsync`'s own `LogDenied` never fires for a tier mismatch, because the SDK rejects the call before `SendAsync` runs.

> ⚠️ **Log levels are a security control here, and they live in `Program.cs`. Do not move them.**
>
> ⚠️ **#143 raised `builder.Logging.AddFilter("System.Net.Http.HttpClient", LogLevel.Warning)` as a
> PII control. That premise was wrong**, and it is recorded here because the mistaken version is the
> intuitive one: the concern was that `Search_users` / `Search_admins` terms reach logs via the
> outbound request URI. **.NET redacts query values by default** — the logged form is
> `GET .../users/search?*`, where `?*` is the redaction marker, not a truncation. Verified 2026-09-18
> by probe and against live production logs, in which every query is `?*`; nothing here disables it.
>
> So the filter is **noise reduction** (19.5% of console bytes), with defence-in-depth as a footnote
> should redaction ever be turned off. Path segments are *not* redacted, but they carry record ids,
> which `AuditLogger` deliberately records anyway.
> Four `Microsoft.AspNetCore.*` noise filters sit beside it, all `Warning` rather than `None` so
> framework *warnings* still surface — this application has exactly **one** `LogError` call site of
> its own, so those are most of what reports a fault.
>
> ⚠️ **But two framework signals are logged at `Information`, and these filters do suppress them.**
> An earlier version of this note claimed all faults stay visible; that was wrong, and the exception
> matters when reading an incident:
>
> | Suppressed | Replacement |
> |---|---|
> | `Microsoft.AspNetCore.Authorization` — *"Authorization failed. These requirements were not met…"* | For an **authenticated** caller, `AuditLogger.LogToolCallDenied` at `Warning` with the object id, tool and required permission. For an **anonymous** one, **nothing** — that is the normal MCP probe, and it still returns a 401 |
> | `JwtBearerHandler` — *"Bearer was not authenticated. Failure message…"* | `Program.cs`'s own `OnAuthenticationFailed`, at `Warning` under `VitallyMcp.Authentication`, logging the exception **type only** |
>
> The authentication one is re-emitted because nothing else would record it: an unauthenticated
> caller never reaches `SendAsync` or the `[Authorize]` checkpoint, so a signing-key rotation, clock
> skew or a run of forged tokens would otherwise be indistinguishable from silence. It logs the type
> and **never** `Exception.Message` — IdentityModel builds those from the token's own claims, so the
> text carries caller-controlled values that may contain newlines.
>
> What is genuinely given up: a flood of anonymous 401s is invisible in logs. If that needs watching
> it belongs in a counter or `ContainerAppHTTPLogs`, not Information-level framework text.
>
> **Two places it must not move to, both of which look reasonable and silently do nothing:**
>
> - **`appsettings.json`** — `.gitignore` and `.dockerignore` both exclude it (under *"Strong-name
>   keys, certificates and other secrets - do NOT commit"*, beside `*.pfx` and `.env`). A file added
>   there works on a developer machine and never reaches the image. `appsettings.Example.json` *does*
>   carry a `Logging` section; it is a template ASP.NET Core never loads, and is marked **NOT IN
>   FORCE** for this reason.
> - **Environment variables** — a Container App recreate does not inherit them, so the control would
>   lapse on any target someone forgot. Same reasoning as the `IncludeReads` default (#139).
>
> `LoggingFilterTests` pins all of it against the composed host, including that `AuditLogger` still
> logs at `Information`: a filter on the wrong prefix would delete the audit trail while looking like
> tidying.

`AuditOptions` (singleton, bound from `Audit:` section):
- `Enabled` (default `true`), `IncludeReads` (default **`true`** since 2026-09-17 — see below).
- **`IncludeReads` defaults to true, and the default is the control.** Reads are 56 of the 93 tools, so a target that does not audit them has no meaningful trail — and `AuditLogger` is the only attribution mechanism, because the shared Vitally key means Vitally's own log cannot name a FISCAL user. It defaulted to `false` until 2026-09-17 and no deployed target ever overrode it, so no read had ever been recorded (#139). It stays configurable as the ingest-cost lever, but **do not re-solve this with a per-deployment environment variable**: a Container App recreate does not inherit them, so coverage would lapse silently — the same trap this file records for `Authorization__ReadOnly` on staging.
- `AuditLogger` is invoked from **`VitallyService.SendAsync`** (same choke point): `LogAction` after each upstream response and `LogDenied` on an RBAC denial. Records the caller's identity, HTTP verb, resource path (query string stripped) and status code via structured logging. **The identity is resolved in a specific order and only the first step yields an Entra object id** — `CallerIdentity.TryGetObjectId` (the `oid` claim, or the trailing GUID of a federated `sub`), then the raw `sub`, then `ClaimTypes.NameIdentifier`, then the literal `unknown`; an unauthenticated caller is `anonymous`. The fallbacks are load-bearing rather than defensive: a rolled-back Auth0 token carries no `oid`, and a consistent-but-opaque key beats none. Read a record keyed on anything but a GUID as *"this token shape had no object id"*, not as a defect — so the named properties are *shaped* to become queryable dimensions — **though nothing can query them today**, because no record has ever reached Log Analytics or Application Insights (see the Logs row in the Deployment table, and #142). **Never log upstream request/response *bodies* here — they can contain customer PII (traits, transcripts).** Note the boundary precisely, because the two rules meet at writes: what is excluded is the **HTTP payload** `VitallyService` exchanges with Vitally. **Tool arguments are in scope** under the policy below, including a create/update `jsonBody` — *"alice set these fields on this account"* is the audit record for a modification, and omitting it would lose the thing the trail exists to show. This is the attribution mechanism while a single shared Vitally key is in use.

  > ⚠️ **The "keep personal data out of telemetry" rule was withdrawn on 2026-09-17** (decision: dsearle), because it left the trail unable to answer the question it exists for. The replacement is an outcome, not a field list: *the audit trail must show that **this user** called **this tool** and accessed, modified or deleted data for **these customers***. Tool arguments — including free-text search terms that may carry names or email addresses — are in scope. **Response bodies remain excluded**, and that is not the old rule returning: Vitally holds meeting transcripts and arbitrary traits, so logging bodies would put a second copy of the customer database into telemetry under weaker access control, which is a different thing from an audit trail.
  >
  > **This describes the agreed target, not current behaviour.** The code below already records the object id, HTTP verb, resource path and status — and, on a denial, the tool name and required permission. What is **not** implemented is the tool-call record: arguments, returned record ids and a correlation id. The query string is still stripped. Design: `docs/superpowers/specs/2026-09-17-logging-observability-design.md`. Until it lands, the trail names a customer only where the *upstream path* carries an id — get-by-id, and scoped lists like `organizations/{id}/users` or `accounts/{id}/users`, which `ResourcePath` preserves. It cannot name one on an **unscoped** list or search (`/resources/organizations`, `/resources/users/search`), where the identities are only in the response body — and note that nothing reaches Log Analytics at all today (#142), so there is no trail to read regardless.
  >
  > The reversal is **conditional on access control**, which is therefore part of the design rather than an operational afterthought: the workspace carries **no** role assignments of its own and inherits from the subscription and management group. **Reviewed 2026-09-21 (#146)**, superseding the 2026-09-17 estimate of "5 users and 28 service principals", which counted a group and an external principal as users and counted assignments rather than principals. Actual: **4** named IT administrators, whose `Owner`/`Contributor` is **PIM-eligible rather than permanent**, plus **2** by-design break-glass accounts; **21** live service principals of which ~13 are Microsoft platform automation and **8** are FISCAL-controlled (two Azure DevOps connections holding `Owner`, one a test app); **4** orphaned assignments for deleted principals; and **one external MSP holding `Owner`** through delegated administration. ⚠️ **Table-level RBAC cannot fence the audit table off** — Azure RBAC is allow-only, so it adds narrow readers and never subtracts from an inherited `*/read`; the design said otherwise until this review. The data is acceptable to store because it is restricted; if that stops being true, so does the policy.
  - **It records `oid`, not `sub`, and the difference is not cosmetic.** An Entra v2 `sub` is a *pairwise* identifier — unique per (user, application), and **not resolvable to a person by any Entra lookup**. Keying the audit trail on it gives you records that are consistent and unattributable, which defeats the purpose of keeping one. `oid` is the directory object id: a GUID, stable across every app in the tenant, resolvable with `az ad user show --id`, and carrying no more personal data than the pairwise value. Found by decoding a real staging token during the #108 validation, *before* the production flip could start writing such records.
  - **`CallerIdentity` is shared with `ToolAuthorizer` on purpose.** The identifier in an audit record has to be the one the authorisation decision was made against, or a denial cannot be joined to the group membership that caused it. Two components reading claims independently is how those drift apart.
  - **Auth0-era records still join.** An Auth0 federated subject is shaped `waad|connection|{objectId}`, so `TryGetObjectId` returns the same GUID for both providers going forward; historical records that stored the whole `sub` map onto current ones by taking the trailing GUID.
- A separate call, `AuditLogger.LogToolCallDenied`, is invoked from `VitallyPermissionHandler` for a denial at the SDK's per-tool `[Authorize]` checkpoint (see the discovery-filtering note above) — it records the caller's Entra object id (resolved by the same `CallerIdentity` the authorizer used, so a denial joins to the membership that caused it), the tool name and the required permission, never the email, request bodies or tool arguments. This exists because that checkpoint rejects out-of-tier calls before `VitallyService.SendAsync` runs, so `LogDenied` there would never see a tier mismatch.

### API key resolution (VitallyApiKeyProvider.cs)

Scoped. Resolution order on each call to `GetApiKeyAsync()`:

1. If no `SecretClient` is registered (i.e. `KeyVaultUri` not set) and `DevelopmentApiKey` is set, return it. If neither is set, throw.
2. Check `IMemoryCache` for `"vitally-api-key::{DefaultSecretRef}"`. Return if hit.
3. Call `SecretClient.GetSecretAsync(DefaultSecretRef)` (uses the Container App's user-assigned managed identity), cache the value for `SecretCacheDuration`, return.

This means: rotating the Vitally key is a `Set-AzKeyVaultSecret` away (cache expires on its own). Per-user keys could be re-introduced by extending the provider to select a different secret name per caller, keyed off a claim or the caller's group membership — no other architecture changes needed. (An Auth0 Action used to mint a `secret_ref` claim for this. The Action still **exists** — it is retained with the rest of the Auth0 configuration for the rollback window, see the rollback appendix — but nothing reaches it while both targets sign in against Entra, and this code stopped reading that claim at the #108 cutover regardless. Vitally API keys are tenant-global anyway, so the idea needs a new motivation before it needs a mechanism.)

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
- `OAuthProxyResourceTerminationTests` — the proxy in the Entra posture (`OAuth:UpstreamResourceScope` set): `resource` dropped whatever its casing, still rejected when unpublished, the API scope merged into `scope` on both endpoints without duplicating or displacing the client's own, and advertised in both metadata documents. A separate fixture from the sibling proxy classes because the switch is composition-time and **both** postures must stay pinned — the relay is what a rollback returns to
- `StaleEntitlementCompositionTests` — the serve-stale-on-Graph-failure path through a composed host, across an outage that starts, is survived and then outlasts its window; also pins that a token claim cannot authorise once the live check is on
- `UpstreamOidcMetadataTests` / `UpstreamOidcStartupFailFastTests` — the OIDC-discovery resolver (all four endpoints, cache reuse, last-known-good on a failed refresh, rejection of an incomplete or malformed document) and the startup fail-fast wired into `Program.cs`
- `Tools/*ToolsTests` — one test class per `Tools/*Tools.cs`, covering every public `[McpServerTool]` method (list/get/create/update/delete plus sub-resources)

**When adding a new tool method:** add a matching test in the appropriate `*ToolsTests.cs` file. Use `TestHelpers.BuildVitallyService(httpClient)` — it builds a `VitallyService` with a stub `VitallyApiKeyProvider` that returns a fixed test API key (no Key Vault required).

**Manual testing considerations** (require live Vitally credentials and a real Entra-issued token in production):
- Test pagination by using low limit values (e.g., `limit=5`) and verify `from` parameter works with `next` cursor
- Test client-side field filtering by specifying various field combinations
- Test trait filtering by combining `fields="traits"` with `traits="trait1,trait2"`
- For accounts, test the status filter with: `active`, `churned`, `activeOrChurned`
- For meetings, test the `archived` filter
- For local dev without an identity provider, set `OAuth__NoAuth=true` and `Vitally__DevelopmentApiKey=<your key>`
- Verify error handling with invalid IDs / missing config

## Deployment

The deployment shape is **Azure Container Apps + Azure Key Vault + Microsoft Entra**, and the container image hosted in Azure Container Registry. `.github/workflows/deploy.yml` builds the image, imports it into the private ACR (via GitHub OIDC, no long-lived credentials) and rolls a Container App to the new revision.

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
| Logs | Log Analytics `vitally-prod-law-uksouth` | **Working since 2026-09-17 — and it had never worked before that.** For the workspace's entire prior lifetime no log from this server reached it: `ContainerAppConsoleLogs_CL` held **0 rows, ever**. **Root cause, and it was never going to work:** the CAE shipped logs with `logs_destination = log-analytics`, which writes to the workspace using its **shared key**, while the workspace has `local_authentication_enabled = false` and refuses exactly that. Microsoft also documents direct-to-workspace as **unsupported over Private Link**. The workspace is not empty — Key Vault and ACR arrive via *diagnostic settings*, which use the Azure Monitor control plane and are governed by neither local auth nor the network settings. **Fixed and VERIFIED 2026-09-17 (#142): `logs_destination` changed to `azure-monitor` plus a diagnostic setting (`infra/terraform/diagnostics.tf`) — the two are useless apart, and each fails silently alone. First records landed seconds later, from both apps; this is the first telemetry this server has ever delivered.** Only `ContainerAppSystemLogs` is enabled; console logs are deliberately gated until the audit records move off stdout. **Application Insights `vitally-prod-appi-uksouth` still receives nothing** — no connection string variable, no SDK package, no code referencing it; "for traces" was aspirational, and its local auth was disabled 2026-09-17 so the planned SDK must use the managed identity. Query access was opened 2026-09-17 so the workspace can be read at all. See `docs/superpowers/specs/2026-09-17-logging-observability-design.md` |
| Auth (Entra — live on both targets) | Entra app registration `Vitally MCP` `c3812e7d-a413-4169-b57e-803326611ba3` | Both the OAuth client and the API resource in one registration, which is why `SharedClientId` is also a valid `aud`. App ID URI `https://vitally.fiscaltec.com` (no slash), exposes `mcp.access`, and carries **both** origins' `/oauth/callback` so it *can* serve both targets. **Both targets point at it** since the 2026-09-16 flip, so `SharedClientId` and `SharedClientSecret` hold the same values on each — the five **identity** variables in `infra/terraform/variables.tf` (`authority`, `audience`, `upstream_resource_scope`, `shared_client_id`, `shared_client_secret`) are now duplicated across `oauth_*` / `staging_oauth_*` and can be collapsed — but **`oauth_resource` and `public_base_url` must not be**, because each target publishes its own origin and collapsing them makes staging advertise production's, which strict RFC 9728 clients reject. They still reference that secret under different *Container App secret names* (production `entra-oauth-client-secret`, staging `oauth-shared-client-secret`), a leftover of flipping without overwriting the retained Auth0 value. `appRoleAssignmentRequired` with nine department groups assigned **directly** (nesting does not grant sign-in) — the list must equal `FISCAL IT Auth0`'s exactly for as long as the rollback is retained, and has drifted from it **twice** (2026-09-03, 2026-09-15), each time an onboarded department that would have lost access at the cutover. The cause was `ACCESS.md` naming only the Auth0 app in its onboarding steps, so both were onboarded exactly as documented — corrected, along with a parity command that compares object ids and actually diffs. Compare the two apps before any cutover or rollback rather than trusting a document; see the runbook and #134. Secret `entra-mcp-client-secret` in the vault, expires **2027-03-01** — 180 days, which is a convention rather than an enforced rule: `scan/run.py` warns when an **enabled** Key Vault secret **that has an expiry** comes within **30 days** of it, and its alert text repeats the 180-day wording — but nothing validates the interval, a secret with no expiry set is not covered at all, and the scanner cannot see Entra credentials. A rotation commitment Auth0 did not carry, and a hard outage date — though the mechanism is Entra rejecting the expired credential at `/oauth/token`, not Key Vault refusing a read: the app never reads the vault for this secret, only for `vitally-shared` (for which the Key Vault mechanism does apply). The expiry is set on the *Key Vault secret* as well as the Entra credential, because the scanner alerts on the former and knows nothing about Entra. Rotating the vault copy alone does not rotate what the app sends — see #138. `vitally-shared` is on the same standard (2027-02-14). See `docs/runbooks/entra-app-registration.md` |
| Auth (Auth0 — rollback only) | Tenant `fiscal-it.uk.auth0.com` | **No longer in the sign-in path** since the 2026-09-16 flip. Its client, both Resource Servers and the `Vitally MCP claims` Action are retained **only** as the rollback and stay in place until production has soaked on Entra; deleting them early turns a one-command rollback into an outage. The tenant stays regardless: it hosts Simple Asset System, its API and a Terraform client |
| CI/CD | GitHub Actions → OIDC federation → Azure | Reusable `deploy.yml` (build → GHCR → `az acr import` → roll, with smoke + rollback — the smoke covers `/health`, the exact-401 challenge **and** the OAuth metadata documents); nightly `release.yml` cuts a semver tag + GitHub Release, then deploys it — freeze by disabling the workflow, see the deploy-freeze note below; OIDC, no long-lived secrets in GitHub |
| IaC | Terraform (`infra/terraform/`) | A **back-filled as-built capture**, not the active source of truth: adoption via the `imports.tf` blocks has never been performed, `terraform apply` is never run here, and the live resources are managed with `az cli`. `deploy.yml` rolls those resources directly and never invokes Terraform. Keep the capture in step with reality by hand; see `infra/terraform/README.md`, which describes adoption as a deliberate future step rather than a routine. |
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
(#102 / #108) was validated here first — staging ran Entra from 2026-09-03, production followed on
2026-09-16 — and staging remains the pre-production target for the next identity change. The alternatives were rejected: a local server
behind an ephemeral HTTPS tunnel orphans one identity-provider app registration per run — identifier
URIs are immutable and must equal the server origin — which cost two sessions during #90; and
validating straight against production is the failure mode a staging-first design exists to prevent.
**The hostname being stable is the load-bearing property, not a convenience:** it is what lets one app
registration be reused across a multi-run validation.

**What it shares with production, deliberately:** the CAE, the managed identity, the ACR, the Key
Vault *and its `vitally-shared` secret*, and the `sg-vitally-*` tier group ids. Sharing is the point —
a staging environment that differs in more than the thing under test cannot tell you whether a
failure is the change or the environment.

**The identity provider is shared again.** Both targets point at the same Entra app registration with
the same client secret; they diverged only while #108 was half-applied and reunified at the 2026-09-16 flip.
One caveat left over from the flip: the two targets reference that secret under *different Container App
secret names* — production `entra-oauth-client-secret`, staging `oauth-shared-client-secret` — because
production's was added alongside the retained Auth0 value rather than overwriting it, which is what kept
the rollback free of a Key Vault window. Normalise the names when Auth0 is retired.

**What diverges:** `OAuth__Resource` and `OAuth__PublicBaseUrl` (both naming the staging origin) and
`minReplicas: 0`. During an identity-provider migration `OAuth__Authority` diverges too, while
staging runs the new provider ahead of production — that is what it is for.

**`OAuth__Audience` on staging names *production*, and that is correct.** One app registration serves
both origins, so a staging token's `aud` is production's App ID URI while its `OAuth__Resource` must
still be the staging origin (clients reject a metadata document whose `resource` is not the server
they fetched it from). So the two diverge by **host as well as by slash** here. It looks like a
copy-paste error and is not; see the divergence warning under *Configuration*.

**One divergence that will cost you time if you meet it cold: staging reads the *production*
`vitally-shared` secret**, so its write and delete tools mutate real customer data. There is one
Vitally tenant and no sandbox. Vitally *does* allow additional API keys, but its REST API
documentation describes no **read-scoped** key (checked 2026-09-15), so a second key would be
revocable and separately attributable while carrying the same write access — it would not fix this.
Revisit if scoped keys ever ship; a read-only key at the boundary beats any switch of ours.

**So `Authorization__ReadOnly=true` is staging's guard, and it is the one live use for that switch.**
Set it whenever staging is up, and unset it only for the tier-enforcement test, which has to see the
write tools to prove a reader is denied one. Live state: **`true` on staging**, **unset on production**.

⚠️ **A recreate does NOT inherit it.** `containerapps-staging.tf` records it — grep the file for
`Authorization__ReadOnly` rather than a line number, which moves — but `infra/terraform/` is an as-built
capture and **`terraform apply` is never run here** — staging is stood up through `deploy.yml`
and `az containerapp`. So a fresh app comes up on the application default, `false`, writing to
the shared production Vitally tenant until someone sets the variable. Set it as part of the
spin-up and verify it, rather than reading the capture as a guarantee:

```bash
CA=vitally-staging-ca-uksouth; RG=vitally-prod-rg-uksouth
# EVERY revision taking traffic, not just the newest: a single unguarded one is enough for
# requests to reach it. `for REV in $(az …)` on its own is NOT this check — a failed or empty
# listing runs the body zero times and exits 0, so an Azure outage or a missing role would
# print nothing and read exactly like the "unguarded" case the text below describes.
if ! REVS=$(az containerapp revision list -n $CA -g $RG \
     --query '[?properties.trafficWeight > `0`].name' -o tsv) || [ -z "$REVS" ]; then
  echo "NOT ASSESSED — could not list traffic-bearing revisions"; false
else
  rc=0
  for REV in $REVS; do
    if V=$(az containerapp revision show -n $CA -g $RG --revision "$REV" \
         --query "properties.template.containers[0].env[?name=='Authorization__ReadOnly'].value|[0]" -o tsv); then
      printf '%s\t%s\n' "$REV" "${V:-<unset>}"
      [ "$V" = "true" ] || rc=1
    else
      echo "NOT ASSESSED — could not read $REV"; rc=1
    fi
  done
  [ "$rc" -eq 0 ] && echo "GUARDED — every traffic-bearing revision has Authorization__ReadOnly=true"
  [ "$rc" -eq 0 ]
fi
```

Empty output means unguarded, not "defaulted to safe". It reads the **serving** revision on
purpose: `az containerapp show` returns the desired template, which flips the moment an update is
accepted, while the previous — unguarded — revision may still be taking traffic.

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
| The staging `/oauth/callback` on the shared Entra app registration | **Identifiers and redirect URIs are the thing a recreate must not have to re-agree.** It is inert while no app answers there, and deleting it per teardown is precisely the orphaning cost the stable hostname exists to avoid |
| The Auth0 Resource Server `https://vitally-staging.fiscaltec.com/` | Only while the Auth0 rollback path is retained; it goes with the rest of the Auth0 configuration when that is retired |
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
Entra tenant and DNS are all genuinely shared but carry `prod` names. The cheap version, if it is ever
picked up, is **not** full duplication: the VNet is `10.80.0.0/23` with only `10.80.0.0-.127`
allocated, so a second `/27` app subnet and its own CAE drop in with no re-addressing, one NAT gateway
serves multiple subnets in the same VNet, and both CAEs resolve the same private endpoints through the
shared DNS zone links. That closes the one gap the shared model cannot: **CAE-level and platform
changes cannot be rehearsed before production sees them.**

What no topology fixes: there is one Vitally tenant and no sandbox, and Vitally offers no read-scoped
API key, so any staging or dev environment reads — and can write — real customer data.

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
- **A first manual run against staging can fail spuriously, and it is not a regression.** Staging is
  `minReplicas: 0`, the script retries each *fetch* only twice, and a cold start can burn both — the
  symptom is `200 but the body is not valid JSON` on the protected-resource documents, which look
  perfectly valid the moment you curl them by hand. CI never sees it because the `/health` smoke runs
  first and warms the replica. Warm it yourself (`curl <origin>/health`) before reading anything into
  a manual failure.
- **It is invoked through `bash`**, not by its executable bit. The repo is authored on Windows with
  `core.filemode=false`, so an edit can silently drop the mode; relying on it would surface as
  "Permission denied" during a deploy rather than in review.

Verified when written by running it both ways round: it **passes** against a local server built from
`main`, and **fails with 6 problems** against the then-current production revision (which predated
#100) — including `jwks_uri: null` in the RFC 9728 document, the exact defect that makes the published
`@modelcontextprotocol/client` reject the whole document.

### The Auth0 → Entra cutover (#108) and its rollback

Config-only, and deliberately so: the code shipped ahead of the switch and behaves identically until
`OAuth__UpstreamResourceScope` is set — no redeploy, no revision pin. Keep it that way. If a future
change ends up gated on the authority value, say so loudly rather than letting the rollback quietly
stop being a config revert.

⚠️ **"Rolling back is reverting environment variables" is true of production only.** Production kept
its Auth0 secret under the original name at the flip, so reverting its five variables is the whole
operation. **Staging overwrote that secret**, so a staging rollback has to put the Auth0 value back
*first* — revert the variables alone and the new revision pairs the Auth0 client id with the Entra
secret, and every sign-in fails `invalid_client` while `/health` stays 200. Both steps, in that
order, are in the rollback appendix below.

Five variables per target, and the secret behind the sixth:

| Variable | Auth0 | Entra |
|---|---|---|
| `OAuth__Authority` | `https://fiscal-it.uk.auth0.com/` | `https://login.microsoftonline.com/75bd6050-92a8-4bde-a406-50000b310c86/v2.0` |
| `OAuth__Audience` | `https://vitally.fiscaltec.com/` | `https://vitally.fiscaltec.com` (**no slash**; staging uses this same production value) |
| `OAuth__Resource` | origin + `/` | **unchanged** — each target keeps its own origin |
| `OAuth__UpstreamResourceScope` | *(unset)* | `https://vitally.fiscaltec.com/mcp.access` |
| `OAuth__SharedClientId` | `VgB00WSYN2V0KkhtYx3WZXYH9XRBvK1D` | `c3812e7d-a413-4169-b57e-803326611ba3` |

`OAuth__SharedClientSecret` moved to a **new** `secretRef`. Production now points at
`entra-oauth-client-secret`, added at the flip alongside the retained `oauth-shared-client-secret`
rather than overwriting it. Staging kept the original name and overwrote the value, so on a rollback
staging needs its Auth0 secret put back first — **copied from production's Container App, not from
Key Vault, which does not hold it.** The two commands are in the rollback appendix below.

Reading `entra-mcp-client-secret` from the vault — needed to stage that value at the flip, and again
at rotation — requires the two-switch Key Vault window described in
`docs/runbooks/entra-app-registration.md`. That is the *Entra* credential; no rollback needs it.
Note the egress IP must be resolved with `curl -4`: this workstation egresses over IPv6 by default and
Key Vault network ACLs are IPv4-only, so the rule add fails outright rather than degrading. Drive the
window from a shell with a `trap … EXIT INT TERM HUP` that closes it, so an interrupted run cannot
leave a private vault reachable.

**Order matters in one place only:** deploy the code before flipping the variables. With the scope
unset the new code is the old behaviour, so the deploy is a no-op and the flip is the whole change.

#### What a cutover can be verified against without a browser

Everything below was run against staging before production; re-run it after any identity change.
Together it covers every failure mode except "a real user cannot sign in", which needs a person.

1. `bash .github/scripts/verify-oauth-metadata.sh <origin>` — 11 assertions. `jwks_uri` and
   `userinfo_endpoint` coming back on `login.microsoftonline.com` / `graph.microsoft.com` is #104's
   payoff visible: they are read from the provider's discovery document, and no choice of
   `OAuth:Authority` could concatenate them.
2. The app **starting at all** proves `StartupGuards.EnsureUpstreamOidcEndpointsAsync` fetched that
   document and matched its `issuer` to `OAuth:Authority`. A wrong authority is a boot failure, not a
   sign-in failure.
3. `GET /oauth/authorize?…` — read the `Location` and assert on its query: upstream endpoint is the
   provider's, `resource` is **absent**, `scope` carries the API scope appended to the client's own,
   `redirect_uri` is our fixed callback.
4. **Follow that `Location` to the provider.** Its sign-in page (HTTP 200, `urlLogin` in the body)
   means every parameter was accepted; an `AADSTS` code means one was not. This is the check that
   catches a wrong `client_id`, an unregistered `redirect_uri` or a malformed scope, and it needs no
   credentials.
5. `POST /mcp` unauthenticated → exactly **401** with `WWW-Authenticate: Bearer resource_metadata=…`;
   with a junk token → 401 plus `error="invalid_token"`. `deploy.yml` smoke-tests the status and
   rolls back if it changes.
6. `/oauth/authorize` with a `resource` we do not publish → **400 `invalid_target`**; with the
   published one → **302**. Terminating the parameter must not have become ignoring it.
7. `POST /oauth/register` → the DCR shim returns the new `client_id`.

**What is left for a person**, because it needs a real token: sign-in per tier, `tools/list` as a
**department-nested** user (not a directly-assigned admin — every tier but `sg-vitally-admins` is
granted by nesting, and the #102 spike produced three wrong conclusions by reasoning about nesting
instead of testing it), a reader being denied a write tool, and decoding the token for `aud`, `iss`
and `oid`. Written out as a checklist in `docs/runbooks/entra-cutover-staging-validation.md`, which
also records what has already been machine-verified so it is not repeated.

#### Rollback — the retained Auth0 values

**This section is the canonical record of the retained Auth0 configuration, and the source of truth
for the *values*. Delete it only when the tenant objects go.** The staging runbook carries a runnable
copy of staging's own command; if the two ever disagree, this section wins. Auth0 survives elsewhere
as rollback and parity context — ACCESS.md's onboarding rule, the RBAC runbook's audit-identity note,
the Terraform capture, and the Deployment table's note that the tenant serves other applications —
plus historical records in `docs/superpowers/` kept deliberately as dated artefacts. Reconstructing
the values from the tenant mid-incident is not a plan.

Rollback of **production** is one `az containerapp update` — no redeploy, no Key Vault window, no
revision pin — because the Auth0 client secret was never overwritten. It is still on that Container
App under its original name, `oauth-shared-client-secret`, alongside the Entra one added at the flip.

⚠️ **Staging needs one extra step that production does not.** It carries a single Container App
secret, `oauth-shared-client-secret`, and that one now holds the **Entra** value — verified against
the live app — so its Auth0 credential has to be put back before the variables move.

**The Auth0 client secret is NOT in Key Vault.** That vault holds exactly two secrets,
`entra-mcp-client-secret` and `vitally-shared` — verified 2026-09-17 by listing it. Earlier drafts of
this section told you to re-fetch it from the vault through the two-switch window. That was
impossible to follow and would have stranded whoever tried it mid-incident.

It is on **production's Container App**, under the name the flip deliberately left alone, and it is
readable — so a staging rollback needs **no Key Vault window at all**:

**Run it as the subshell it is written as.** The `( set -e )` wrapper is load-bearing, not style:
pasted as bare lines, a failed or empty read would `echo` its complaint and the *next* command
would still run, writing an empty secret over staging's Auth0 credential — destroying the thing the
procedure exists to restore, during an incident. The subshell also keeps the secret out of the
parent shell's environment without depending on an `unset` that an early exit would skip.

```bash
(
  set -euo pipefail

  # 1. read the retained Auth0 secret off production (no vault, no firewall change)
  S=$(az containerapp secret show -n vitally-prod-ca-uksouth -g vitally-prod-rg-uksouth \
        --secret-name oauth-shared-client-secret --query value -o tsv)
  [ -n "$S" ] || { echo "NOT ASSESSED — could not read the retained secret; stop here" >&2; exit 1; }

  # 2. put it on staging. Reverting staging's OAuth__* variables next rolls the revision that picks
  #    up both — see the staging runbook for why that order matters.
  az containerapp secret set -n vitally-staging-ca-uksouth -g vitally-prod-rg-uksouth \
    --secrets "oauth-shared-client-secret=$S"
)
```

If production has itself been rolled back first, that secret is still the same value: a rollback
changes which secret the env var *references*, not the secret's contents.

```bash
az containerapp update -n vitally-prod-ca-uksouth -g vitally-prod-rg-uksouth \
  --set-env-vars \
    "OAuth__Authority=https://fiscal-it.uk.auth0.com/" \
    "OAuth__Audience=https://vitally.fiscaltec.com/" \
    "OAuth__SharedClientId=VgB00WSYN2V0KkhtYx3WZXYH9XRBvK1D" \
    "OAuth__SharedClientSecret=secretref:oauth-shared-client-secret" \
  --remove-env-vars OAuth__UpstreamResourceScope
```

`OAuth__Resource` and `OAuth__PublicBaseUrl` are unchanged by a rollback — each target keeps its own
origin under either provider.

| | Auth0 (rollback) | Entra (live since 2026-09-16) |
|---|---|---|
| `OAuth__Authority` | `https://fiscal-it.uk.auth0.com/` | `https://login.microsoftonline.com/75bd6050-92a8-4bde-a406-50000b310c86/v2.0` |
| `OAuth__Audience` | `https://vitally.fiscaltec.com/` *(slash)* | `https://vitally.fiscaltec.com` *(none)* |
| `OAuth__SharedClientId` | `VgB00WSYN2V0KkhtYx3WZXYH9XRBvK1D` | `c3812e7d-a413-4169-b57e-803326611ba3` |
| `OAuth__SharedClientSecret` | `secretref:oauth-shared-client-secret` | `secretref:entra-oauth-client-secret` |
| `OAuth__UpstreamResourceScope` | *(absent — `resource` is relayed)* | `https://vitally.fiscaltec.com/mcp.access` |

⚠️ **That table is PRODUCTION's. Staging's Auth0 values are not identical, and using production's
would leave staging unusable.** Under Auth0 each target had its **own Resource Server**, so each had
its own audience — unlike Entra, where both share one App ID URI. Two rows differ for staging:

| | staging, rolled back to Auth0 |
|---|---|
| `OAuth__Audience` | `https://vitally-staging.fiscaltec.com/` — **its own** Resource Server, retained per the teardown table |
| `OAuth__SharedClientSecret` | `secretref:oauth-shared-client-secret` — the only name staging has, and it currently holds the **Entra** value, so it must be overwritten first (see the staging rollback steps in `docs/runbooks/entra-cutover-staging-validation.md`) |

`Authority`, `SharedClientId` and the absent `UpstreamResourceScope` are assumed to be the same as
production's.

⚠️ **Production's values above are known-good; staging's two rows are not.** Production ran on exactly
those five values until 2026-09-16, so they are a record of a working configuration rather than a
reconstruction. Staging's are assembled from what is recorded here — its audience from the retained
Resource Server in the teardown table, its `SharedClientId` and `Authority` assumed to match
production's — and nothing has exercised that path since staging flipped on 2026-09-03. Confirm them
in the tenant first.

**They are two different Auth0 objects, and it matters when you go looking.** The *audience* is a
**Resource Server** identifier (Auth0 → Applications → APIs). The *client id* is an **Application**
(Auth0 → Applications → Applications) — the retained native client. Looking for the client id among
the APIs, mid-incident, finds nothing.

**What must still exist for this to work**, and must therefore not be deleted before you have decided
to abandon the rollback: the Auth0 client above, both Resource Servers
(`https://vitally.fiscaltec.com/` and `https://vitally-staging.fiscaltec.com/`), the
`Vitally MCP claims` Action, and the `FISCAL IT Auth0` app's Gate 1 group assignments — which must
stay at parity with the Entra app's nine, or a rollback locks out whichever department drifted. The
parity check is in `docs/runbooks/entra-app-registration.md`; #134 tracks automating it.

Two things a rollback does **not** restore, so do not plan around them: the `permissions` claim
cannot authorise anyone (#108 removed the code that reads it while `LiveGroupCheck` is on —
entitlement comes from Graph either way), and `CallerIdentity` must keep its Auth0-`sub` fallback
until this section is deleted, because a rolled-back token carries no `oid` and Graph needs the GUID.

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

Infrastructure-as-code is in this repo at `infra/terraform/` — the table above documents the runtime contract for what `deploy.yml` expects. Anyone replicating can swap Container Apps for App Service, ACR for GHCR, Entra for Keycloak, etc., without touching the application code — `OAuth:UpstreamResourceScope` is the one setting whose value is provider-shaped, and it is a value rather than a branch.
