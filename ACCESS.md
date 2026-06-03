# Vitally MCP — Access & Connection Guide

How to connect to the Vitally MCP server, and how access is granted.

## Connecting

Point your MCP client at:

```
https://vitally.fiscaltec.com/mcp
```

On first use the client opens a Microsoft sign-in (Auth0 → Microsoft Entra). After signing in, the server calls Vitally on your behalf using a service key it holds — you never handle a Vitally API key.

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

If you sign in successfully but every tool call returns an error, you're likely **authenticated but not yet authorised** — you need to be in one of the access groups below.

## Access model

Authentication alone grants nothing. Every action is checked against your permission tier, which is derived from your **Microsoft Entra group membership**:

| Tier | Permission | What you can do | Entra group |
|---|---|---|---|
| Read | `vitally:read` | List, get and search all resources | `sg-vitally-readers` |
| Write | `vitally:write` | Read **+** create and update | `sg-vitally-editors` |
| Delete | `vitally:delete` | Read + write **+** delete | `sg-vitally-admins` |

Tiers are cumulative (editors can read; admins can do everything). You only need to be in **one** group — the highest tier you require.

Group object IDs (for IT reference):

| Group | Object ID |
|---|---|
| `sg-vitally-readers` | `71451cc9-f5df-44ee-8ed1-3acc41a911eb` |
| `sg-vitally-editors` | `19b9d659-284c-4f93-b1c3-a6354db1027c` |
| `sg-vitally-admins`  | `70b48a20-d4b1-47dc-a132-21bc99272a86` |

## Getting access

**As a user:** request membership of the group for the tier you need (most people need `sg-vitally-readers`) from the **IT & Security team**. Once added, you have access within about a minute — no need to reconnect or sign in again.

**As an admin (granting/changing access):** add or remove the person from the relevant `sg-vitally-*` group in Entra. The server re-reads live group membership on each call (cached ~60 seconds), so:

- **Granting** a tier takes effect within ~60s of adding the user to the group.
- **Changing** tier = move the user to a different group (e.g. readers → editors).
- **Revoking** access takes effect within ~60s of removing the user from the group — no reconnect required.

> **Urgent revocation:** for an immediate cut-off (e.g. a compromised account), remove the user from the group **and** revoke their Auth0 session — that takes effect on their next request rather than waiting for the ~60s window.

## How it's set up (in brief)

- **Sign-in:** Auth0 federates to Microsoft Entra; FISCAL staff sign in with their normal Microsoft account.
- **Authorisation:** the server resolves your `vitally:*` permissions from your **live** Entra group membership (via Microsoft Graph) on each call — so access reflects your *current* groups, not a stale token.
- **Auditing:** every action is logged with the acting user, operation and outcome (queryable in Application Insights / Log Analytics).
- **Hosting:** Azure Container Apps + Azure Key Vault (holds the Vitally key) on `vitally.fiscaltec.com`.

Group membership is managed in Entra by the IT & Security team. Questions: contact the Infrastructure team.
