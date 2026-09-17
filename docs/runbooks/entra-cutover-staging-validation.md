# Staging validation — the Entra cutover (#108)

`https://vitally-staging.fiscaltec.com` runs Entra-direct. This is the part of the acceptance suite
that needs a real sign-in, and therefore a person.

Written for the #108 cutover, but it is the standing checklist for **any** identity-provider change:
staging is the pre-production target for exactly this, so re-run it whenever the authority, the app
registration or the entitlement wiring moves.

**Nothing here touches the production Container App or its OAuth configuration.** Staging is a
separate app with its own settings, and no step below changes production's, whatever state it is in.

⚠️ **That is not the same as "no production blast radius."** Staging reads the **production**
`vitally-shared` Vitally key — one tenant, no sandbox — so any write or delete tool call here
mutates **real customer data**. What isolates you is `Authorization__ReadOnly`, not the environment
boundary, which is why the steps that switch it off are fenced the way they are below.

⚠️ **Staging reads the production `vitally-shared` Vitally key**, so its write and delete tools
mutate **real customer data**. There is one Vitally tenant, no sandbox, and no read-scoped API key
available (checked 2026-09-15).

`Authorization__ReadOnly=true` is therefore staging's standing guard and is set in
`containerapps-staging.tf`. **Steps 3 and 4 are the exception.** The guard hides every destructive
tool from *every* tier, so with it on, step 3's editor and admin expectations (`Create_`/`Update_`,
and all 93) cannot be observed — an admin would see the 56-tool reader catalogue and the check would
report a failure that is really the guard working. Unset it before step 3 and **put it back
immediately after step 4**:

**Do not unset it in one command and trust yourself to run the other.** Between those two commands
staging is writable against real customer data, and anything that ends the session in between — a
failed step, Ctrl-C, closing the terminal, going to lunch — leaves it that way indefinitely, with
nothing anywhere to notice. Two independent restores, because neither alone is enough:

```bash
APP=(-n vitally-staging-ca-uksouth -g vitally-prod-rg-uksouth)
RG=vitally-prod-rg-uksouth; CA=vitally-staging-ca-uksouth

# The SERVING value, not the desired one. `az containerapp show` returns the spec you just asked
# for, so it flips to `true` the instant the update is accepted — while the previous, WRITABLE
# revision can still be taking every request. Read it off the revision actually carrying traffic.
# EVERY revision taking traffic, one per line — not just the newest. Both apps are in Single
# revision mode today, where exactly one revision holds 100%, so this returns one name. It is
# written as a set anyway because the mode is one `--revisions-mode multiple` away, nothing
# would flag that change, and the failure it would cause is silent: an older UNGUARDED revision
# still taking a share while the newest reports `true`. Staging writes reach real customer data.
serving() { az containerapp revision list "${APP[@]}" \
  --query '[?properties.trafficWeight > `0`].name' -o tsv; }
state_of() { az containerapp revision show "${APP[@]}" --revision "$1" \
  --query "properties.template.containers[0].env[?name=='Authorization__ReadOnly'].value|[0]" -o tsv; }
# The guard as the outside world sees it: the value only if EVERY traffic-bearing revision
# agrees, otherwise `MIXED`. One revision disagreeing means some requests are unguarded, which
# is not a state to report as either guarded or open.
state() {
  local r v first="" n=0
  for r in $(serving); do
    v=$(state_of "$r") || return 1
    n=$((n + 1))
    if [ "$n" = "1" ]; then first=$v; elif [ "$v" != "$first" ]; then echo "MIXED"; return 0; fi
  done
  [ "$n" -gt 0 ] || return 1
  printf '%s\n' "$first"
}

# Wait until the revision we produced is taking traffic AND every traffic-bearing revision
# carries the value expected of it. Both directions need this and for the same reason: an
# accepted `az containerapp update` changes the desired spec, not what is answering requests.
# Up to 5 minutes, far longer than a swap needs.
#   $1 = the revision our update produced, $2 = expected guard value ("true" guarded, "" not).
await_serving() {
  local v
  for _ in $(seq 1 20); do
    # `[ "$(state)" = "$2" ]` alone is a trap when $2 is the empty string: command substitution
    # discards state()'s exit status, so a FAILED lookup also yields "" and compares equal —
    # the unguard direction would then announce readiness having verified nothing. Capture the
    # status separately and treat a failure as not-ready.
    if serving | grep -qxF "$1" && v=$(state) && [ "$v" = "$2" ]; then return 0; fi
    sleep 15
  done
  return 1
}
# Returns the revision the removal produced, so the caller can wait for it. Announcing
# "guard REMOVED" on acceptance alone would send the operator into steps 3 and 4 while the
# OLD read-only revision was still serving — every destructive tool still hidden, an admin
# seeing the 56-tool reader catalogue, and the check reporting a tier failure that is really
# the guard it was told had been lifted.
unguard() { az containerapp update "${APP[@]}" --remove-env-vars Authorization__ReadOnly \
  --query properties.latestRevisionName -o tsv; }

# Retries, then VERIFIES, then shouts, then RETURNS A STATUS. Three separate things, and each
# one has been the bug here at some point:
#   * `az … && echo "restored"` is not a restore — one failed call is swallowed by the &&.
#   * A successful `az` is not a restored guard — read the value back and check it.
#   * A printed warning is not a failure — a function ending in `echo` returns 0, so the
#     detached `bash -c "…; guard"` below would exit SUCCESSFULLY while staging stayed writable,
#     and nothing monitoring that process could tell.
guard() {
  # Capture the revision OUR update produces, and wait for that one specifically. Asking only
  # "does the serving revision say true" passes on the revision that was serving BEFORE the
  # unguard — which still carries the guard for the seconds before the unguarded one takes
  # over. Every restore attempt could fail in that window and this would still report success.
  local target=""
  for _ in 1 2 3 4 5; do
    target=$(az containerapp update "${APP[@]}" --set-env-vars Authorization__ReadOnly=true \
      --query properties.latestRevisionName -o tsv) && [ -n "$target" ] && break
    target=""; sleep 10
  done
  if [ -z "$target" ]; then
    echo "$(date -u +%FT%TZ) !!! GUARD NOT RESTORED — every restore attempt failed. staging is WRITABLE against real customer data. Run now:"
    echo "    az containerapp update -n $CA -g $RG --set-env-vars Authorization__ReadOnly=true"
    return 1
  fi
  # A successful update means the SPEC was accepted, not that the guarded revision is serving.
  if await_serving "$target" "true"; then
    echo "$(date -u +%FT%TZ) guard RESTORED (serving revision $target)"
    return 0
  fi
  echo "$(date -u +%FT%TZ) !!! GUARD NOT RESTORED — $target never took traffic. staging is WRITABLE against real customer data. Run now:"
  echo "    az containerapp update -n $CA -g $RG --set-env-vars Authorization__ReadOnly=true"
  return 1
}

# (a) This shell. EXIT alone is not enough: Ctrl-C at an interactive prompt does not exit the
#     shell, and without `set -e` a failed step does not either — so trap the signals too.
#     On a clean restore this also cancels the detached timer below. Leaving it to fire half an
#     hour later would roll a second revision for nothing, and would re-assert the guard over
#     whatever the app had been deliberately set to by then. Cancelling only AFTER a successful
#     restore is the whole point: if guard() failed, that timer is the remaining protection.
cleanup() {
  if guard; then
    [ -n "${FAILSAFE_PID:-}" ] && kill "$FAILSAFE_PID" 2>/dev/null \
      && echo "$(date -u +%FT%TZ) failsafe $FAILSAFE_PID cancelled (guard already restored)"
  else
    echo "$(date -u +%FT%TZ) leaving failsafe ${FAILSAFE_PID:-?} armed — it is the remaining protection"
  fi
}
trap cleanup EXIT INT TERM HUP

# (b) A detached failsafe, because (a) dies with the terminal, the SSH session or the laptop.
#     It reuses guard() verbatim via `declare -f` rather than carrying a second, simpler copy of
#     the restore — a detached one-shot `az` call is the version that can fail silently 30
#     minutes from now, when nobody is watching and the pre-exit check has long since passed.
#
#     unguard runs in the FOREGROUND. `unguard && nohup … &` backgrounds the whole list, so the
#     success line prints before unguard has run, and a failed unguard silently skips the timer.
LOG=~/vitally-staging-guard-failsafe.log
if target=$(unguard) && [ -n "$target" ]; then
  # Arm the failsafe the moment the REMOVAL IS ACCEPTED, before waiting for the swap. The spec
  # is already unguarded here, so that revision will take traffic whether or not anyone is
  # still watching — a failsafe gated on the wait succeeding would be absent in exactly the
  # case that needs it most.
  #
  # Every function guard() reaches TRANSITIVELY has to be in this list, and every variable they
  # read in `declare -p`. The child shell inherits nothing else. Omitting `serving` here once
  # cost a failsafe that restored the guard correctly and then logged GUARD NOT RESTORED every
  # single time, because its verification step could not run — a permanent false alarm, which is
  # how a real one stops being read. `await_serving` joined that list when guard() started
  # calling it, and `state_of` when state() did.
  nohup bash -c "$(declare -p APP CA RG); $(declare -f serving state_of state await_serving guard); sleep 1800; guard" >>"$LOG" 2>&1 &
  FAILSAFE_PID=$!
  echo "removal accepted (revision $target) — failsafe PID $FAILSAFE_PID, logging to $LOG"
  if await_serving "$target" ""; then
    echo "guard REMOVED and $target is serving — run steps 3 and 4 now, then exit this shell"
  else
    echo "!!! $target has NOT taken traffic yet. Do not start steps 3 and 4: they would run"
    echo "    against the old read-only revision and report tier failures that are really the"
    echo "    guard. Wait, re-check with the state command below, then begin."
  fi
else
  echo "unguard FAILED — the guard is still ON and NO failsafe was started."
  echo "Nothing to clean up. Fix your az session and re-run this block."
fi
```

Keep that shell open for steps 3 and 4, then exit it. The failsafe logs its own outcome, so
`cat ~/vitally-staging-guard-failsafe.log` says whether it fired and whether it worked.

**Neither mechanism survives the machine losing power, so the check is not optional — run it
before you walk away, from any shell:**

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

It must print `true`. **Empty output means UNGUARDED**, not "defaulted to safe" — the application
default is `false`. This reads the revision that is *serving traffic*, deliberately: a plain
`az containerapp show` returns the desired template, which reports `true` from the moment the
update is accepted even while the previous, writable revision is still answering every request.

Steps 1, 2 and 5 are unaffected — they touch metadata, the token and the logs, not the tool
catalogue — so leave the guard on for those.

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

Staging rolls back in **two** steps. Both are required, and the order matters:

1. **Replace the Container App secret** `oauth-shared-client-secret` with the **Auth0** client secret.
   It is **not** in Key Vault — that vault holds only `entra-mcp-client-secret` and `vitally-shared`.
   It is on *production's* Container App and is readable, so this needs no vault window. The two
   commands are in *The Auth0 → Entra cutover and its rollback* in `CLAUDE.md`, which is the only
   place they are written down.
2. **Then** revert the `OAuth__*` variables — same section for the values, and note that staging's
   `OAuth__Audience` is its **own** Auth0 Resource Server, not production's.

**In that order, and the reason is the revision model.** `az containerapp secret set` does **not**
roll a revision — the running one keeps serving with the value it already loaded — whereas
`--set-env-vars` does. The two orderings therefore fail differently:

- **Variables first** breaks sign-in *immediately*: the update rolls a new revision carrying Auth0's
  client id alongside the Entra secret still in the store, and every token exchange returns
  `invalid_client` while the app boots clean and `/health` returns 200.
- **Secret first** breaks nothing now — the running revision is untouched — but leaves a *latent*
  mismatch: the next revision rolled for any reason would pair the Auth0 secret with Entra variables.
  Step 2 rolls that revision seconds later with both values correct, which closes the window.

So secret first is not a preference. It is the ordering whose failure mode is latent and immediately
resolved, rather than live and user-visible.

⚠️ **"It is only staging" is true of the configuration and false of the data.** Nothing here
changes production's Container App or its OAuth settings. But staging reads the **production**
`vitally-shared` Vitally key — one tenant, no sandbox — so any write or delete tool call made during
this validation mutates real customer records. `Authorization__ReadOnly=true` is what prevents that,
and steps 3 and 4 deliberately remove it. Put it back the moment they are done, and verify it is
back on the revision serving traffic rather than on the desired template.

⚠️ **Staging's secret revert is not a one-liner, unlike production's.** Staging carries a single
Container App secret, `oauth-shared-client-secret`, and since its 2026-09-03 flip that one holds the
**Entra** value — the Auth0 value is not sitting there waiting. Reverting the variables without first
re-fetching the Auth0 client secret from Key Vault pairs the Auth0 client id with the Entra secret.
That is not caught at startup — the app boots clean and `/health` returns 200 — and surfaces only at
the token exchange, where the provider returns an authentication error (`invalid_client`) and sign-in
fails for everyone. Late, not silent: if you are debugging one, the token endpoint's response is where
the answer is. So a staging rollback needs the
two-switch Key Vault window (`docs/runbooks/entra-app-registration.md`, driven under a
`trap … EXIT INT TERM HUP`) *before* the variables move.

Production is the opposite: its flip added `entra-oauth-client-secret` alongside the retained
`oauth-shared-client-secret` rather than overwriting it, so its rollback is variables only, with no
vault window at all.

## After it passes

**Both targets were flipped — staging 2026-09-03, production 2026-09-16 — so there is no outstanding
flip. Do not read anything below as a step still to perform.**

What this runbook is now: the **Entra acceptance suite**. Its checks assert Entra v2 endpoints,
`mcp.access`, `AADSTS` responses and Graph, so it does not generalise to another provider.

**Re-run everything above while Entra is the active provider** — after any change to this app
registration, to the tenant, or to the `mcp.access` scope, and after a re-flip *to* Entra.

⚠️ **After a rollback to Auth0, most of this suite still applies — but not all of it, and the parts
that do not will produce failures that mean nothing.** The table below says which is which; read the
inapplicable rows as inapplicable, not as a broken rollback. The risk is that someone "fixes" the
configuration back to Entra and undoes, during an incident, the rollback they had just deliberately
performed.

| after an Auth0 rollback | |
|---|---|
| `verify-oauth-metadata.sh` | **still applies** — it names no provider, asserting only the façade contract (our own `issuer`, its byte-for-byte match with `authorization_servers`, the `iss` flag, `jwks_uri` absolute https, no null optionals), all of which hold under either |
| the app booting at all | **still applies** — it proves OIDC discovery resolved and matched whatever `OAuth:Authority` now names |
| `POST /mcp` unauthenticated / bad token | **still applies** — exactly 401 with `resource_metadata` and `error="invalid_token"` is our own behaviour |
| `resource` we do not publish / do publish | **still applies** — 400 `invalid_target` / 302 is our own validation, unchanged by provider |
| `POST /oauth/register` | **still applies**, but expect the **Auth0** client id back, not `c3812e7d-…` |
| `/oauth/authorize` → upstream | **inapplicable** — it asserts Entra's v2 endpoint, `resource` **absent** and `mcp.access` merged. On Auth0 the endpoint differs and `resource` is *relayed*, which is the correct rollback behaviour |
| following that to the provider | **inapplicable** — there is no `AADSTS` code to assert |
| the token-claim table | **inapplicable** — `iss`, `aud` and `scp` all name Entra values |

Add one check the Entra suite does not need: `jwks_uri` should now read `fiscal-it.uk.auth0.com`. If
it still names `login.microsoftonline.com`, the variables did not take — check the revision actually
serving traffic, not the desired template. And one real sign-in per tier, which is the only check
that proves the client secret matches the client id — the failure a rollback most often hits.

If a *future* target ever needs the five `OAuth__*` variables applied, they are in `CLAUDE.md`, and
its Container App secret comes from the Key Vault secret `entra-mcp-client-secret` through the
two-switch network window in `docs/runbooks/entra-app-registration.md` — driven under a
`trap … EXIT INT TERM HUP` so an interrupted run cannot leave a private vault reachable. The code
ships ahead of the configuration and is inert until `OAuth__UpstreamResourceScope` is set, so setting
the variables is the whole change and reverting them is the whole rollback.
