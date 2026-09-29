#!/usr/bin/env bash
#
# Asserts something about EVERY revision of a Container App that is taking traffic, and fails
# closed when it cannot tell.
#
# Usage:
#   check-serving-revisions.sh <app> <rg> [--env <NAME> [--equals <VALUE>]]... [--image]
#
#   --env NAME               NAME is set: a non-empty value, or a secretRef. The value is never
#                            printed, only `set` / `<unset>`, so it is safe for connection strings.
#   --env NAME --equals V    NAME's value is exactly V, and is printed. A secretRef cannot be
#                            compared, so it is NOT ASSESSED rather than a pass or a fail.
#   --image                  the image is readable, and is printed. There is no expected value --
#                            compare it yourself. Revisions on different images are called out.
#
# e.g. the staging spin-up check (CLAUDE.md, Staging):
#   bash .github/scripts/check-serving-revisions.sh vitally-staging-ca-uksouth vitally-prod-rg-uksouth \
#     --env Authorization__ReadOnly --equals true --env ApplicationInsights__ConnectionString
#
# Exit codes -- distinct on purpose, because "no" and "could not ask" demand different actions:
#   0   every traffic-bearing revision satisfies every assertion
#   1   at least one assertion definitely failed, and everything else could be read
#   2   NOT ASSESSED: the listing failed or was empty, a revision could not be read, or its template
#       did not have the shape the assertions need. No verdict is possible. Outranks 1.
#  64   usage error -- including no assertion at all, since a check of nothing must not pass
#
# WHY THIS IS A SCRIPT (#167). The loop it replaces was copied into five documents, and the copies
# drifted into fail-OPEN versions within a single PR (#166): one read `az containerapp show` (the
# desired template, not the serving revision), one lost the guard on a failed or empty listing, one
# lost the emptiness check. Each was copied from a correct original, and a drifted copy of a
# fail-closed check fails open. Same reasoning as verify-oauth-metadata.sh: one copy to keep right.
#
# Details that are load-bearing, each of which some earlier copy got wrong:
#
#   * The SERVING revisions, not `az containerapp show`. That returns the desired template, which
#     flips the moment an update is accepted while the previous revision may still take every request.
#   * EVERY revision with trafficWeight > 0, not the newest. One unguarded revision in a split is
#     enough for requests to reach it. Each is printed, so a mixed result is visible.
#   * A failed or EMPTY listing is NOT ASSESSED. `for REV in $(az …)` runs zero times on either and
#     exits 0. An ordinary cause is not an outage: a CLI that has silently reverted to another
#     default subscription lists nothing at all (#167, observed live).
#   * JSON, not `-o tsv` plus a JMESPath query. `az` exits 0 with EMPTY output when a query path
#     stops resolving (an extension bump, a second container), which is indistinguishable from an
#     unset variable. Reading the template whole lets "the variable is absent" -- a definite answer
#     -- be told apart from "the template is not the shape expected" -- no answer.
#   * A secretRef entry has NO `value` key, so reading `.value` alone renders a set variable as
#     absent. `--env NAME` treats a secretRef as set.
#   * CR is stripped from jq's output, defensively. The Windows CLI and jq.exe write CR LF; Git
#     Bash's `$(…)` happens to drop the CR (verified 2026-09-29), but that is a property of one
#     shell, and a stray CR would turn `true` into a mismatch.
#
# Invoked through `bash`, not by its executable bit: this repo is authored on Windows with
# core.filemode=false, so an edit can silently drop the mode.
set -uo pipefail

usage() {
  echo "usage: check-serving-revisions.sh <app> <rg> [--env <NAME> [--equals <VALUE>]]... [--image]" >&2
  exit 64
}

[ $# -ge 2 ] || usage
APP="$1"; RG="$2"; shift 2
case "$APP$RG" in -*) usage ;; esac
[ -n "$APP" ] && [ -n "$RG" ] || usage

# Parallel arrays: ENV_NAMES[i] is asserted set, or equal to ENV_VALUES[i] when ENV_HAS_VALUE[i]=1.
ENV_NAMES=(); ENV_VALUES=(); ENV_HAS_VALUE=()
CHECK_IMAGE=0
while [ $# -gt 0 ]; do
  case "$1" in
    --env)
      [ $# -ge 2 ] && [ -n "$2" ] && [ "${2#--}" = "$2" ] || usage
      ENV_NAMES+=("$2"); ENV_VALUES+=(""); ENV_HAS_VALUE+=(0)
      shift 2 ;;
    --equals)
      n=${#ENV_NAMES[@]}
      [ $# -ge 2 ] && [ "$n" -gt 0 ] && [ "${ENV_HAS_VALUE[$((n - 1))]}" = 0 ] || usage
      ENV_VALUES[$((n - 1))]="$2"; ENV_HAS_VALUE[$((n - 1))]=1
      shift 2 ;;
    --image)
      CHECK_IMAGE=1
      shift ;;
    *)
      usage ;;
  esac
done
[ ${#ENV_NAMES[@]} -gt 0 ] || [ "$CHECK_IMAGE" = 1 ] || usage

not_assessed() { echo "NOT ASSESSED — $*"; }

command -v jq > /dev/null 2>&1 || { not_assessed "jq is not installed"; exit 2; }
command -v az > /dev/null 2>&1 || { not_assessed "az is not installed"; exit 2; }

# jq with CR stripped from its output: jq.exe on Windows writes CR LF unless given --binary, and
# --binary is not accepted everywhere.
jqr() { jq "$@" | tr -d '\r'; }

# ---------------------------------------------------------------- the listing

if ! LISTING=$(az containerapp revision list -n "$APP" -g "$RG" -o json); then
  not_assessed "could not list revisions of $APP in $RG (az failed — see its error)"
  exit 2
fi
if ! REVS=$(printf '%s' "$LISTING" | jqr -er '.[] | select((.properties.trafficWeight // 0) > 0) | .name'); then
  # -e makes jq exit non-zero when it outputs nothing, which covers an empty listing, revisions
  # with no traffic, and a body that is not JSON at all.
  not_assessed "no traffic-bearing revisions of $APP in $RG (empty listing, no traffic, or unparseable output)"
  exit 2
fi

# ---------------------------------------------------------------- each revision

worst=0            # 0 pass, 1 definite failure, 2 not assessed -- the highest wins
note() { [ "$1" -gt "$worst" ] && worst="$1"; return 0; }
images=""

for REV in $REVS; do
  if ! DOC=$(az containerapp revision show -n "$APP" -g "$RG" --revision "$REV" -o json); then
    echo "$REV  NOT ASSESSED — could not read this revision (az failed — see its error)"
    note 2; continue
  fi

  # Exactly one container, or no assertion here means anything: every copy this replaced read
  # containers[0], and on a second container the variable may sit on the other one.
  if ! count=$(printf '%s' "$DOC" | jqr -e '.properties.template.containers | if type == "array" then length else error("no containers") end' 2> /dev/null); then
    echo "$REV  NOT ASSESSED — the revision template has no containers array (schema drift?)"
    note 2; continue
  fi
  if [ "$count" != 1 ]; then
    echo "$REV  NOT ASSESSED — expected exactly one container, found $count"
    note 2; continue
  fi
  C=$(printf '%s' "$DOC" | jq -c '.properties.template.containers[0]')

  line="$REV"
  for i in "${!ENV_NAMES[@]}"; do
    name="${ENV_NAMES[$i]}"
    # One of: value:<v>  secretref  unset. An entry with an empty value is unset -- the app would
    # read it as empty, which is what "not configured" means to it.
    state=$(printf '%s' "$C" | jqr -r --arg n "$name" '
      [(.env // [])[] | select(.name == $n)] | first
      | if . == null then "unset"
        elif (.secretRef // "") != "" then "secretref"
        elif (.value // "") != "" then "value:" + .value
        else "unset" end')
    if [ "${ENV_HAS_VALUE[$i]}" = 1 ]; then
      case "$state" in
        value:*)
          v="${state#value:}"
          line="$line  $name=$v"
          [ "$v" = "${ENV_VALUES[$i]}" ] || note 1 ;;
        secretref)
          line="$line  $name=<secretRef: value not readable, NOT ASSESSED>"
          note 2 ;;
        *)
          line="$line  $name=<unset>"
          note 1 ;;
      esac
    else
      case "$state" in
        value:*|secretref) line="$line  $name=set" ;;
        *)                 line="$line  $name=<unset>"; note 1 ;;
      esac
    fi
  done

  if [ "$CHECK_IMAGE" = 1 ]; then
    img=$(printf '%s' "$C" | jqr -r '.image // ""')
    if [ -n "$img" ]; then
      line="$line  image=$img"
      case $'\n'"$images"$'\n' in *$'\n'"$img"$'\n'*) ;; *) images="${images:+$images$'\n'}$img" ;; esac
    else
      line="$line  image=<none: NOT ASSESSED>"
      note 2
    fi
  fi

  echo "$line"
done

if [ "$CHECK_IMAGE" = 1 ] && [ "$(printf '%s\n' "$images" | grep -c .)" -gt 1 ]; then
  echo "NOTE — traffic-bearing revisions are running different images; treat that as a finding"
fi

case "$worst" in
  0) echo "PASS — every traffic-bearing revision of $APP satisfies every assertion" ;;
  1) echo "FAIL — at least one traffic-bearing revision of $APP does not satisfy an assertion" ;;
  2) echo "NOT ASSESSED — at least one traffic-bearing revision of $APP could not be checked; no verdict" ;;
esac
exit "$worst"
