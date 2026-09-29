#!/usr/bin/env bash
#
# Asserts something about EVERY revision of a Container App that can serve a request, and fails
# closed when it cannot tell.
#
# Usage:
#   check-serving-revisions.sh <app> <rg> [--env <NAME> [--equals <VALUE>]]... [--image]
#
#   --env NAME               NAME is set: a non-blank value, or a secretRef. The value is never
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
#   0   every serving revision satisfies every assertion
#   1   at least one assertion definitely failed, and everything else could be read
#   2   NOT ASSESSED: the listing failed, was empty or was not the shape expected, a revision could
#       not be read, or its template did not have the shape the assertions need. No verdict is
#       possible. Outranks 1.
#  64   usage error -- including no assertion at all, since a check of nothing must not pass
#
# WHY THIS IS A SCRIPT (#167). The loop it replaces was copied into five places, and the copies
# drifted into fail-OPEN versions within a single PR (#166): one read `az containerapp show` (the
# desired template, not the serving revision), one lost the guard on a failed or empty listing, one
# lost the emptiness check. Each was copied from a correct original, and a drifted copy of a
# fail-closed check fails open. Same reasoning as verify-oauth-metadata.sh: one copy to keep right.
#
# Details that are load-bearing, each of which some earlier copy -- or an earlier draft of this
# script -- got wrong:
#
#   * The SERVING revisions, not `az containerapp show`. That returns the desired template, which
#     flips the moment an update is accepted while the previous revision may still take every request.
#   * EVERY revision that is active OR has trafficWeight > 0, not the newest and not only the
#     weighted ones. One unguarded revision in a split is enough for requests to reach it, and an
#     active revision at 0% still answers on its own revision FQDN (and on any label). Each is
#     printed, so a mixed result is visible.
#   * A failed or EMPTY listing is NOT ASSESSED. `for REV in $(az …)` runs zero times on either and
#     exits 0. An ordinary cause is not an outage: a CLI that has silently reverted to another
#     default subscription lists nothing at all (#167, observed live).
#   * The listing is validated, not filtered. A revision with no numeric `trafficWeight` or boolean
#     `active`, or with a name that is not a revision name (empty, `*`), makes the whole result
#     NOT ASSESSED -- a filter with a `// 0` default would drop it and pass on the rest, and an
#     empty name word-splits away to nothing. Names are read line by line, never word-split or
#     glob-expanded.
#   * JSON, not `-o tsv` plus a JMESPath query. `az` exits 0 with EMPTY output when a query path
#     stops resolving (an extension bump, a second container), which is indistinguishable from an
#     unset variable. Reading the template whole lets "the variable is absent" -- a definite answer
#     -- be told apart from "the template is not the shape expected" -- no answer.
#   * A secretRef entry has NO `value` key, so reading `.value` alone renders a set variable as
#     absent. `--env NAME` treats a secretRef as set.
#   * A name that appears twice in `env` is NOT ASSESSED. Which entry the runtime honours is not
#     this script's to guess, and guessing the first would pass `true` beside a later `false`.
#   * CR is stripped from jq's output. jq.exe on Windows writes CR LF, and although Git Bash's `$(…)`
#     drops a CR from a single `az -o tsv` value, it does not save this script: with the strip
#     removed, four cases in the test suite fail on Windows (measured 2026-09-29).
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
for a in "$APP" "$RG"; do
  case "$a" in ''|-*) usage ;; esac
done

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
      # An empty expected value can never pass -- an empty value is treated as unset -- so it is a
      # mistake in the invocation, not a check.
      n=${#ENV_NAMES[@]}
      [ $# -ge 2 ] && [ -n "$2" ] && [ "$n" -gt 0 ] && [ "${ENV_HAS_VALUE[$((n - 1))]}" = 0 ] || usage
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
# --binary is not accepted everywhere. pipefail carries jq's exit status through the `tr`.
jqr() { jq "$@" | tr -d '\r'; }

# ---------------------------------------------------------------- the listing

if ! LISTING=$(az containerapp revision list -n "$APP" -g "$RG" -o json); then
  not_assessed "could not list revisions of $APP in $RG (az failed — see its error)"
  exit 2
fi

# -s slurps, so a body holding more than one JSON document is caught rather than processed piecewise.
# Every element is validated BEFORE any is selected, so one malformed revision cannot be dropped
# while the rest pass.
if ! REVS=$(printf '%s' "$LISTING" | jqr -r -s '
    if length != 1 then error("expected exactly one JSON document") else .[0] end
    | if type != "array" then error("the listing is not an array") else . end
    | map(
        if (.name | type) != "string" or (.name | test("^[a-z0-9][a-z0-9-]*$") | not)
          then error("a revision has no valid name")
        elif (.properties.trafficWeight | type) != "number"
          then error("revision \(.name) has no numeric trafficWeight")
        elif (.properties.active | type) != "boolean"
          then error("revision \(.name) has no boolean active")
        else . end)
    | .[] | select(.properties.active or .properties.trafficWeight > 0) | .name' 2>&1); then
  not_assessed "the listing of $APP in $RG was not the shape expected: $REVS"
  exit 2
fi
mapfile -t REV_LIST <<< "$REVS"
[ -n "$REVS" ] && [ ${#REV_LIST[@]} -gt 0 ] || {
  not_assessed "no active or traffic-bearing revisions of $APP in $RG"
  exit 2
}

# ---------------------------------------------------------------- each revision

worst=0            # 0 pass, 1 definite failure, 2 not assessed -- the highest wins
note() { [ "$1" -gt "$worst" ] && worst="$1"; return 0; }
images=""
checked=0

for REV in "${REV_LIST[@]}"; do
  if ! DOC=$(az containerapp revision show -n "$APP" -g "$RG" --revision "$REV" -o json); then
    echo "$REV  NOT ASSESSED — could not read this revision (az failed — see its error)"
    note 2; continue
  fi

  # Exactly one JSON document holding exactly one container, or no assertion here means anything:
  # every copy this replaced read containers[0], and on a second container the variable may sit on
  # the other one.
  if ! C=$(printf '%s' "$DOC" | jqr -c -s '
      if length != 1 then error("expected exactly one JSON document") else .[0] end
      | .properties.template.containers
      | if type != "array" then error("the revision template has no containers array")
        elif length != 1 then error("expected exactly one container, found \(length)")
        else .[0] end
      | if (.env == null) or ((.env | type) == "array") then .
        else error("the container env is not an array") end' 2>&1); then
    echo "$REV  NOT ASSESSED — $C"
    note 2; continue
  fi
  checked=$((checked + 1))

  line="$REV"
  for i in "${!ENV_NAMES[@]}"; do
    name="${ENV_NAMES[$i]}"
    # One of: value:<v>  secretref  unset. A blank value is unset -- the app reads its settings
    # with IsNullOrWhiteSpace, so to it blank means "not configured".
    if ! state=$(printf '%s' "$C" | jqr -r --arg n "$name" '
        [(.env // [])[] | select(.name == $n)]
        | if length > 1 then error("\($n) appears \(length) times")
          elif length == 0 then "unset"
          else .[0]
            | if (.secretRef // "") != "" then "secretref"
              elif .value == null then "unset"
              elif (.value | type) != "string" then error("\($n) has a non-string value")
              elif (.value | test("\\S")) then "value:" + .value
              else "unset" end
          end' 2>&1); then
      line="$line  $name=<NOT ASSESSED: $state>"
      note 2; continue
    fi
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
    if img=$(printf '%s' "$C" | jqr -r 'if (.image | type) == "string" then .image else "" end') && [ -n "$img" ]; then
      line="$line  image=$img"
      case $'\n'"$images"$'\n' in *$'\n'"$img"$'\n'*) ;; *) images="${images:+$images$'\n'}$img" ;; esac
    else
      line="$line  image=<none: NOT ASSESSED>"
      note 2
    fi
  fi

  echo "$line"
done

# Belt and braces: every listed revision was either read or reported NOT ASSESSED. Zero read means
# nothing was checked, which must never reach the PASS line.
[ "$checked" -gt 0 ] || note 2

if [ "$CHECK_IMAGE" = 1 ] && [ "$(printf '%s\n' "$images" | grep -c .)" -gt 1 ]; then
  echo "NOTE — serving revisions are running different images; treat that as a finding"
fi

case "$worst" in
  0) echo "PASS — every serving revision of $APP satisfies every assertion" ;;
  1) echo "FAIL — at least one serving revision of $APP does not satisfy an assertion" ;;
  2) echo "NOT ASSESSED — at least one serving revision of $APP could not be checked; no verdict" ;;
esac
exit "$worst"
