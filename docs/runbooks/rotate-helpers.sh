# shellcheck shell=bash
#
# Helpers for the Entra client-secret rotation in entra-app-registration.md.
#
# SOURCE this, do not execute it:   . docs/runbooks/rotate-helpers.sh
#
# It is a separate checked-in file rather than a snippet in the runbook because the rotation's
# roll and verify steps run AFTER a browser sign-in, and often in a fresh shell. Anything defined
# only inside a runbook code block is not there when it is needed, and that failure is silent in
# the worst direction: `secret set` succeeds, `roll` is undefined, and the target quietly stays on
# the superseded credential until the delete takes it down.
#
# Invoked by path, never by its executable bit: this repo is authored on Windows with
# core.filemode=false, so an edit can silently drop the mode (same convention as
# .github/scripts/verify-oauth-metadata.sh).

APP=568d8fc4-ebfd-4c5d-8302-ffb0377ac7a4   # Vitally MCP application objectId (appId is c3812e7d-…)
RG=vitally-prod-rg-uksouth

# Restart EVERY traffic-bearing revision, failing closed on a failed or empty listing.
#
# Container Apps can serve several revisions at once during a split, and a single un-restarted
# warm revision keeps the OLD secret. That is invisible until the superseded credential is
# deleted, and then surfaces as INTERMITTENT invalid_client depending on which revision answers —
# far harder to diagnose than a clean failure. Same pattern, and same reasoning, as the staging
# read-only guard in CLAUDE.md.
#
# ⚠️ `for REV in $(az …)` on its own is NOT this check: a failed or empty listing runs the body
#    zero times and exits 0, so an outage or a missing role reads exactly like success.
roll() {  # $1 = container app name
  local CA="$1" REVS rc=0
  if ! REVS=$(az containerapp revision list -n "$CA" -g "$RG" \
       --query '[?properties.trafficWeight > `0`].name' -o tsv) || [ -z "$REVS" ]; then
    echo "NOT ASSESSED — could not list traffic-bearing revisions for $CA; stop here"
    return 1
  fi
  for REV in $REVS; do
    if az containerapp revision restart -n "$CA" -g "$RG" --revision "$REV"; then
      echo "  restarted $REV"
    else
      echo "  FAILED $REV"
      rc=1
    fi
  done
  return $rc
}

# CHECK A — the STORED value is the new one.
#
# Compares hashes, never the secret itself: a SHA-256 of a credential is safe in scrollback, the
# credential is not. Proves `az containerapp secret set` actually landed on this target.
stored_hash() {  # $1 = container app, $2 = secret name
  local out
  if ! out=$(az containerapp secret show -n "$1" -g "$RG" --secret-name "$2" \
       --query value -o tsv); then
    echo "NOT ASSESSED — could not read secret $2 on $1"
    return 1
  fi
  [ -n "$out" ] || { echo "NOT ASSESSED — secret $2 on $1 read back empty"; return 1; }
  printf '%s' "$out" | tr -d '\r\n' | sha256sum | cut -c1-16
}

# CHECK B — every RUNNING replica started AFTER the secret changed, so it must have read the new
# value. This is the assurance the replica table in the runbook says staging cannot otherwise give.
#
# $3 = the MINIMUM number of running replicas this target must have for the check to mean anything.
#
#   staging    -> 0   scale-to-zero is its steady state; the next cold start reads the current value
#   production -> 1   minReplicas is 1, so zero running replicas is NEVER a valid steady state
#
# ⚠️ Why the minimum exists. `az containerapp revision restart` can RETURN BEFORE the replacement
#    replicas are running, so a check made immediately after it can see zero replicas on
#    production — which would otherwise pass Check B while proving nothing at all is serving the
#    new secret, and the procedure would walk on to the irreversible delete. Requiring at least
#    one running replica on production converts that race from a false pass into a wait.
#
# ⚠️ The replica listing is captured with an explicit failure check rather than piped straight
#    into the loop. Process substitution discards the command's exit status, so a failed
#    `replica list` would yield no input, report "0 running replicas" and return success — letting
#    an API or RBAC outage masquerade as a fresh roll while an old replica still holds the
#    superseded secret. That is the same trap as the `for REV in $(az …)` one above, one function
#    later, and it has to be closed the same way.
replicas_are_fresh() {  # $1 = app, $2 = epoch secs before `secret set`, $3 = min running replicas
  local CA="$1" STAMP="$2" MIN="${3:-0}" attempt
  for attempt in $(seq 1 12); do          # up to ~2 minutes, only ever waiting for MIN
    if _replicas_fresh_once "$CA" "$STAMP" "$MIN"; then
      return 0
    elif [ "$?" -eq 2 ]; then             # 2 = "too few replicas yet" — the only retryable case
      echo "  waiting for at least $MIN running replica(s) on $CA (attempt $attempt/12)"
      sleep 10
    else
      return 1                            # a stale replica, or a failed call: do not retry
    fi
  done
  echo "NOT ASSESSED — $CA never reached $MIN running replica(s); the roll may not have completed"
  return 1
}

# Returns 0 = fresh, 1 = stale or could not assess, 2 = fewer than MIN replicas running (retryable).
_replicas_fresh_once() {
  local CA="$1" STAMP="$2" MIN="$3" REVS REPS rc=0 n=0 created epoch
  if ! REVS=$(az containerapp revision list -n "$CA" -g "$RG" \
       --query '[?properties.trafficWeight > `0`].name' -o tsv) || [ -z "$REVS" ]; then
    echo "NOT ASSESSED — could not list traffic-bearing revisions for $CA"
    return 1
  fi
  for REV in $REVS; do
    # ⚠️ Filter to runningState == 'Running'. The unfiltered list includes replicas that are
    #    still provisioning or have stopped, and a provisioning replica would satisfy
    #    production's MIN=1 while serving nothing — which would end the retry-on-zero wait
    #    early, exactly when it is the only thing standing between a half-finished roll and the
    #    irreversible delete. Field verified against the live API (values: Running / NotRunning
    #    / Unknown), and a bogus value returns zero rows, so the filter is genuinely applied.
    if ! REPS=$(az containerapp replica list -n "$CA" -g "$RG" --revision "$REV" \
         --query "[?properties.runningState=='Running'].properties.createdTime" -o tsv); then
      echo "NOT ASSESSED — could not list replicas for $REV on $CA"
      return 1
    fi
    while read -r created; do
      [ -z "$created" ] && continue
      n=$((n + 1))

      # ⚠️ Fail closed if the timestamp cannot be converted. An unparseable value (or a `date`
      #    without -d, e.g. BSD/macOS) yields an empty string, and `[ "" -le N ]` is simply
      #    false — so the replica would be reported FRESH, immediately before the deletion gate.
      if ! epoch=$(date -u -d "$created" +%s 2>/dev/null) || ! [ "$epoch" -eq "$epoch" ] 2>/dev/null; then
        echo "NOT ASSESSED — could not parse replica createdTime '$created' on $REV"
        return 1
      fi

      # ⚠️ -le, not -lt, and deliberately conservative. Both sides are whole seconds, so a
      #    replica created BEFORE its target's `secret set` but within the same second as the
      #    post-set stamp would compare EQUAL — and `-lt` would pass it as fresh while it is
      #    still running the old credential. Treating the equal second as suspect costs at most
      #    a spurious re-roll; the other direction costs an outage after the delete.
      if [ "$epoch" -le "$STAMP" ]; then
        echo "  STALE replica on $REV (started $created, not strictly after the secret change)"
        rc=1
      fi
    done <<< "$REPS"
  done
  [ "$rc" -eq 0 ] || return 1
  [ "$n" -ge "$MIN" ] || return 2
  echo "  $n running replica(s) (minimum $MIN), none older than the secret change"
  return 0
}

# The whole per-target verification as ONE exit code, so a caller cannot forget to check one part.
#
# ⚠️ This compares the hash rather than printing it for the operator to eyeball. An earlier draft
#    printed `stored_hash` next to a "# must equal NEWHASH" comment, which is not a check — it is
#    a hope, at the step before an irreversible delete.
verify_target() {  # $1 = app, $2 = secret name, $3 = expected hash, $4 = stamp, $5 = min replicas
  local got
  got=$(stored_hash "$1" "$2") || return 1
  if [ "$got" != "$3" ]; then
    echo "  HASH MISMATCH on $1/$2: stored=$got expected=$3 — the secret did not land; stop here"
    return 1
  fi
  echo "  stored secret on $1 matches NEWHASH ($got)"
  replicas_are_fresh "$1" "$4" "$5" || return 1
}
