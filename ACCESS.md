# Vitally MCP — Access & Connection Guide

How to connect to the Vitally MCP server, and how access is granted.

## Connecting

Point your MCP client at:

```
https://vitally.fiscaltec.com/mcp
```

On first use the client opens a Microsoft sign-in. After signing in, the server calls Vitally on your behalf using a service key it holds — you never handle a Vitally API key. (Production currently reaches Entra via Auth0 federation; staging goes to Entra directly, and production will once the #108 switch is made. Either way you sign in with your normal Microsoft account and see the same screen.)

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

> ⚠️ **There are currently two such apps, and a department must be assigned to BOTH.**
>
> | App | Role |
> |---|---|
> | **FISCAL IT Auth0** | gates production sign-in **today** |
> | **Vitally MCP** | gates staging today, and production once the switch is made |
>
> Assigning only one is the single most common way to break access here, and it has happened twice —
> because this page used to name only *FISCAL IT Auth0*. A department assigned to just one app works
> perfectly **until** the server is switched to the other, and then that whole department is refused
> at sign-in with `AADSTS50105`. Neither app tells you the other is missing.
>
> Assign to both until IT confirms the Auth0 app has been retired, at which point this note goes.

So access requires **both**: your department assigned to the sign-in app(s) above, **and** membership
of an `sg-vitally-*` group (permission tier). The assigned departments can be verified live in
Entra → Enterprise applications → *(each app)* → Users and groups, which is the authority — the IT
helpdesk article *Vitally MCP – access & administration (IT)* is a copy and has been wrong before.

> **Direct assignment only (important).** The app requires assignment
> (`appRoleAssignmentRequired = true`), and Entra honours only **direct** members of an
> assigned group for sign-in — **nested groups do not count here**. This is the opposite of the
> permission tier below, which *is* evaluated transitively. Assign the **department group
> itself** to the app (the dynamic `*-Department` groups qualify, since their members are direct
> members). Nesting a department inside an `sg-vitally-*` group grants the *tier* but **not**
> sign-in, so a team needs both: the department nested in `sg-vitally-editors`/etc. **and** that
> same department directly assigned to **each** sign-in app listed above — *FISCAL IT Auth0* and
> *Vitally MCP*. Assigning one is the mistake this page previously caused twice.

## Getting access

**As a user:** ask the **IT & Security team** for the tier you need (most people need read). Once
granted, access appears within about a minute — no need to reconnect or sign in again.

**As an admin:** granting access is two independent steps, and both are required.

### 1. Sign-in (Gate 1) — assign the department to *both* apps

Entra → Enterprise applications → **Vitally MCP** → Users and groups, *and* the same under
**FISCAL IT Auth0**. Assign the department group to **every** app it is missing from; see the
warning above for why missing one is invisible until the day it is not. The scripted version, which
is idempotent and safe to re-run, is in `docs/runbooks/entra-app-registration.md`.

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
> USER=<their-entra-object-id>; TIER=<sg-vitally-readers|editors|admins object id>
> az rest --method get --url "https://graph.microsoft.com/v1.0/groups/$TIER/members?\$select=id" --query "length(value[?id=='$USER'])" -o tsv
> ```
>
> `1` = direct member, use the first row. `0` while they still have access = inherited, use the
> second.

Any of these take effect within about **60 seconds**, with no reconnect: the server re-reads live
group membership on each call rather than trusting the token.

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

- **Sign-in:** Microsoft Entra — reached via Auth0 federation on production today, directly on staging, and directly on production once the #108 switch is made. FISCAL staff sign in with their normal Microsoft account in every case.
- **Authorisation:** the server resolves your `vitally:*` permissions from your **live** Entra group membership (via Microsoft Graph, evaluated transitively so nested groups count) on each call — so access reflects your *current* groups, not a stale token.
- **Auditing:** every action is logged with the acting user's Entra object id (resolvable with `az ad user show --id`; never their email), the operation and the outcome — queryable in Application Insights / Log Analytics.
- **Hosting:** Azure Container Apps + Azure Key Vault (holds the Vitally key) on `vitally.fiscaltec.com`.

Group membership is managed in Entra by the IT & Security team. Questions: contact the Infrastructure team.
