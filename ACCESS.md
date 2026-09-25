# Vitally MCP — Access & Connection Guide

How to connect to the Vitally MCP server, and how access is granted.

## Connecting

Point your MCP client at:

```
https://vitally.fiscaltec.com/mcp
```

On first use the client opens a Microsoft sign-in. After signing in, the server calls Vitally on your behalf using a service key it holds — you never handle a Vitally API key. (Both production and staging go to Entra directly — production since 2026-09-16. You sign in with your normal Microsoft account.)

**Claude Code** — run:

```bash
claude mcp add --transport http vitally https://vitally.fiscaltec.com/mcp
```

Then trigger any MCP use (e.g. `/mcp`) and Claude Code opens the Microsoft sign-in on first connect. To remove it later: `claude mcp remove vitally`.

**Other clients:**

| Client | How to connect |
|---|---|
| Claude Desktop | Settings → Connectors → Add custom connector → paste `https://vitally.fiscaltec.com/mcp` |
| VS Code / Cursor / other | Add an MCP server entry pointing at the URL; the client handles sign-in |

### "It connected, but there are no tools"

If you sign in successfully but your client shows **no Vitally tools at all**, you are
**authenticated but not yet authorised** — you're not in any of the access groups below. This is the
expected behaviour, not a broken connector: the server only advertises the tools your tier permits,
so with no tier you see an empty list rather than an error message.

Ask the IT & Security team for membership of the group matching the tier you need (see
[Access model](#access-model)); the tools appear within about a minute, with no need to reconnect.

**You will also see fewer tools than a colleague on a higher tier** — that is by design. A reader
sees the list/get/search tools only; editors additionally see create and update; admins see
everything. If a tool you expect is missing, it usually means your tier doesn't include it rather
than that anything is wrong.

## Access model

Authentication alone grants nothing. Every action is checked against your permission tier, which is derived from your **Microsoft Entra group membership**:

| Tier | Permission | What you can do | Tools you see | Entra group |
|---|---|---|---|---|
| *(none)* | — | Nothing | **None** — an empty tool list | — |
| Read | `vitally:read` | List, get and search all resources | List / get / search | `sg-vitally-readers` |
| Write | `vitally:write` | Read **+** create and update | Read tools + create / update | `sg-vitally-editors` |
| Delete | `vitally:delete` | Read + write **+** delete | All tools | `sg-vitally-admins` |

Tiers are cumulative (editors can read; admins can do everything). You only need to be in **one** group — the highest tier you require.

Your tier determines not just what you may *do* but what your client is *shown*: the server
advertises only the tools your tier permits, so you will not see — or be able to invoke — tools above
it.

Group object IDs (for IT reference):

| Group | Object ID |
|---|---|
| `sg-vitally-readers` | `71451cc9-f5df-44ee-8ed1-3acc41a911eb` |
| `sg-vitally-editors` | `19b9d659-284c-4f93-b1c3-a6354db1027c` |
| `sg-vitally-admins`  | `70b48a20-d4b1-47dc-a132-21bc99272a86` |

### Departments currently granted a tier

Tiers are granted to whole teams by **nesting a department group** inside the relevant
`sg-vitally-*` group, rather than adding people one by one. **Entra is the source of truth and this
table is a copy** — read it as indicative and verify there, because it has been stale before (it
omitted two reader departments between July and September 2026). As of 2026-09-15:

| Tier | Departments |
|---|---|
| Read (`sg-vitally-readers`) | Product, Development, Data Science |
| Write (`sg-vitally-editors`) | Customer Account Management, Customer Operations, Executive Leadership Team, Project Management |
| Delete (`sg-vitally-admins`) | *(granted to individuals, not by department)* |

### Signing in is gated too (department group)

Before a permission tier even applies, you must be able to **authenticate**. Sign-in is gated by an
Entra enterprise application: only users whose **department group** is assigned to it can sign in. If
your department isn't assigned, the Microsoft sign-in fails and you never reach the server —
regardless of any `sg-vitally-*` membership.

> ✅ **There is exactly ONE such app: `Vitally MCP`.** It gates sign-in on both production and
> staging.
>
> This used to be two apps that had to be kept identical by hand, and assigning only one was the
> single most common way to break access here — it happened twice, because this page named the wrong
> one. A department assigned to just one app worked perfectly **until** the server was switched to
> the other, and then that whole department was refused at sign-in with `AADSTS50105`.
>
> The second app was decommissioned in September 2026, so that failure mode no longer exists. If you
> find another app that looks like it gates this server, it does not — check with the Infrastructure
> team before assigning anything to it.

So access requires **both**: your department assigned to the sign-in app above, **and** membership
of an `sg-vitally-*` group (permission tier). The assigned departments can be verified live in
Entra → Enterprise applications → **Vitally MCP** → Users and groups, which is the authority — the IT
helpdesk article *Vitally MCP – access & administration (IT)* is a copy and has been wrong before.

> **Direct assignment only (important).** The app requires assignment
> (`appRoleAssignmentRequired = true`), and Entra honours only **direct** members of an
> assigned group for sign-in — **nested groups do not count here**. This is the opposite of the
> permission tier below, which *is* evaluated transitively. Assign the **department group
> itself** to the app (the dynamic `*-Department` groups qualify, since their members are direct
> members). Nesting a department inside an `sg-vitally-*` group grants the *tier* but **not**
> sign-in, so a team needs both: the department nested in `sg-vitally-editors`/etc. **and** that
> same department directly assigned to the **Vitally MCP** app.

## Getting access

**As a user:** ask the **IT & Security team** for the tier you need (most people need read). Once
granted, access appears within about a minute — no need to reconnect or sign in again.

**As an admin:** granting access is two independent steps, and both are required.

### 1. Sign-in (Gate 1) — assign the department to the app

Entra → Enterprise applications → **Vitally MCP** → Users and groups. Assign the department group
there. The scripted version, which is idempotent and safe to re-run, is in
`docs/runbooks/entra-app-registration.md`.

⚠️ **Nothing detects an omission.** This assignment list is the only record of who can sign in, so a
department left off it simply cannot reach the server. The failure is at least immediate and
visible now — that department's next sign-in fails with `AADSTS50105` — rather than lying dormant
until a provider switch, which is how the two historical incidents stayed hidden.

### 2. Tier (Gate 2) — grant, change or revoke

Membership is evaluated **transitively**, so a tier can be held two different ways, and *which one
decides what you have to change*:

| How they hold it | To grant | To change | To revoke |
|---|---|---|---|
| **Directly** in `sg-vitally-*` | add the user to the group | move them to a different tier group | remove them from the group |
| **Inherited** via a department nested in `sg-vitally-*` — the common case | nest their department in the tier group | move the *department's* nesting, or move the person between departments | remove them from the **department**, or un-nest that department (which affects everyone in it) |

> ⚠️ **Removing an inheriting user from the `sg-vitally-*` group does nothing** — they were never in
> it. This is the single most likely way to believe you have revoked access and not have. Establish
> which row applies before acting:
>
> ```bash
> export MSYS_NO_PATHCONV=1
> SUBJECT=<their-entra-object-id>; TIER=<sg-vitally-readers|editors|admins object id>
> az rest --method get --url "https://graph.microsoft.com/v1.0/groups/$TIER/members?\$count=true&\$filter=id eq '$SUBJECT'" --headers "ConsistencyLevel=eventual" --query "length(value)" -o tsv
> ```
>
> `1` = direct member. `0` means **not a direct member** — which is not the same as "inherited":
> `/members` returns direct membership only, so `0` covers both a user who holds the tier through a
> department *and* one who does not hold it at all. This count alone cannot tell those apart; the
> transitive query below can, which is why it is not optional.
>
> And **a `1` does not mean the direct membership is their only route**. Both can be true of the
> same person, so removing the direct one can leave a department path intact and the account still
> authorised.
>
> So do not stop at the count. Clear every path the table gives you, then **confirm with a
> path-independent check** that they are actually out:
>
> ```bash
> export MSYS_NO_PATHCONV=1
> TIER=<sg-vitally-readers|editors|admins object id>; SUBJECT=<their-entra-object-id>
> az rest --method get --url "https://graph.microsoft.com/v1.0/groups/$TIER/transitiveMembers?\$count=true&\$filter=id eq '$SUBJECT'" --headers "ConsistencyLevel=eventual" --query "length(value)" -o tsv
> ```
>
> (`SUBJECT`, not `USER` — most shells already define `USER` as your own login name, so reusing it
> silently queries for a user id that is really a username and reports a confident `0`.)
>
> `0` from **that** query is the only thing that means revoked — it is the same lookup the server
> itself makes, so it answers the question the server will answer. Repeat it for each tier group they
> might hold.
>
> The query filters **server-side** on the user id rather than fetching the member list and searching
> it. That matters during a revocation: `/members` is paginated, so reading only the first page would
> report `0` for a direct member further down the list and send you to remove a department membership
> they do not have — leaving them authorised. `$filter` with `ConsistencyLevel: eventual` returns a
> complete answer whatever the group's size.

Any of these take effect within about **60 seconds**, with no reconnect — and the 60 seconds is
`Authorization:LiveGroupCacheSeconds`, not a round number. The server resolves entitlement from
live Entra group membership rather than from the token, caching each caller's result for that
window; a call inside the window is answered from cache, and the next one after it re-reads Graph.
So a revocation bites at the end of the current window, not on the very next call.

⚠️ **While Graph is unreachable that window is longer.** The last known-good set is served for up
to `Authorization:LiveGroupStaleSeconds` (default **1 hour**) before calls are denied, so a
revocation during a Graph outage can take that long. For a compromised account, use the urgent
procedure below rather than relying on the 60 seconds.

### Urgent revocation (compromised account)

Do all three, in this order, and understand what each does *not* cover:

1. **Remove the correct group membership** (per the table above) — this is the control that actually
   stops them using the server, normally within ~60s.
2. **Disable the Entra account** — stops new sign-ins and refreshes.
3. **Revoke their sessions** — same effect, at whichever provider is live for that target, or both.

**None of these invalidates an access token they already hold.** This server validates bearer tokens
locally against the provider's signing keys, so an issued token remains valid until it expires
(Entra: roughly 60–90 minutes) regardless of the account or session state. Steps 2 and 3 close off
getting a *new* one.

**And the ~60s in step 1 assumes Microsoft Graph is reachable.** During a Graph outage the server
serves each caller's last known-good tier for up to `Authorization:LiveGroupStaleSeconds` (**1 hour**
by default) rather than denying everyone — so a revoked user can retain access for that long. The
trade is deliberate; see the entitlement section in `CLAUDE.md`.

So the honest worst case is **the remaining token lifetime plus the stale window**. If that is not
acceptable, escalate — but pick the right lever, because the two cases differ:

- **Graph healthy, and you want to stop writes without knowing who is compromised:**
  `Authorization__ReadOnly=true` on the Container App. Denies every create/update/delete for
  everyone on the new revision, consulting neither Graph nor any token. Reads keep working.
- **Graph is down (the case this paragraph is about):** `az containerapp ingress disable`. Do **not**
  reach for `ReadOnly` here — setting it rolls a new revision, a new revision has an empty permission
  cache, and with Graph unreachable nobody can be resolved at all, so every caller is denied
  everything including reads. The restart is a harder outage than the one you were trying to contain,
  and it arrives by surprise. If that is the outcome you want, disable ingress and know you chose it.

**Do not reach for "scale to zero".** A Container App with HTTP ingress scales back up on the next
request: staging runs `minReplicas: 0` and serves `/health` 200 on demand. It is not a halt.

## How it's set up (in brief)

- **Sign-in:** Microsoft Entra, directly, on both production and staging (production since 2026-09-16). FISCAL staff sign in with their normal Microsoft account.
- **Authorisation:** the server resolves your `vitally:*` permissions from your **live** Entra group membership (via Microsoft Graph, evaluated transitively so nested groups count) on each call — so access reflects your *current* groups, not a stale token.
- **Auditing:** every action is logged against the acting user. That is their Entra object id where one can be resolved — a GUID, resolvable with `az ad user show --id` — falling back to the raw token subject, then `NameIdentifier`, then `unknown`; an unauthenticated caller records as `anonymous`. Those fallbacks should not fire in practice, since an Entra token always carries an object id; a record keyed on anything but a GUID means an unexpected token shape. The records are exported to Application Insights `AppEvents` and are queryable, on both production and staging, since 2026-09-25. **This is now built, not planned:** each executed tool call is recorded with **the arguments you passed** — which include names or email addresses you searched for — the tool name, the ids of the records it touched, the permission tier the decision used, the outcome, and a correlation id. That is deliberate: without the arguments the trail cannot say *which customer* was accessed. Upstream **response bodies are never recorded**. There are four record shapes: a **tool call** (the above); an **action** (per upstream call — actor, method, resource path, status); a **service denial** (actor, method, path, *no status* — the call never happened); and a **tier denial**, rejected before any upstream call, carrying the actor, tool name and required permission but no arguments or touched records. See `docs/superpowers/specs/2026-09-17-logging-observability-design.md`.
- **Hosting:** Azure Container Apps + Azure Key Vault (holds the Vitally key) on `vitally.fiscaltec.com`.

Group membership is managed in Entra by the IT & Security team. Questions: contact the Infrastructure team.
