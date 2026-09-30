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
#   --env NAME --equals V    NAME's value is exactly V -- byte for byte, so `true\n` is not `true`.
#                            A match prints V (which you supplied). A mismatch NEVER prints the
#                            live value, only `<mismatch>`, or `<mismatch: differs only in case>` /
#                            `... in surrounding whitespace>`. A secretRef cannot be compared, so it
#                            is NOT ASSESSED rather than a pass or a fail.
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
#   3   ABSENT: az reported that this app does not exist in this resource group -- its exact
#       `(ResourceNotFound) The Resource 'Microsoft.App/containerApps/<app>' under resource group
#       '<rg>' was not found`, matched as a fixed string. Nothing looser: a missing resource group, a
#       missing role, a lapsed login or another app's not-found are all NOT ASSESSED (2), because
#       mistaking any of them for absence would skip a check that should have failed.
#       ⚠️ Only meaningful for a caller with READ AT RESOURCE-GROUP SCOPE. Without it Azure answers
#       AuthorizationFailed for an app that does not exist -- the same answer as a lost role -- so
#       absence cannot be observed and every lookup of a missing app is 2. The deploy identity holds
#       `ContainerApp Reader` on vitally-prod-rg-uksouth for exactly this reason (#171).
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
#   * A name that appears twice in `env` is NOT ASSESSED -- matched as .NET configuration matches
#     it, case-insensitively and with `:` equal to `__`, so `authorization__readonly` and
#     `Authorization:ReadOnly` are duplicates of `Authorization__ReadOnly`. Which entry the app
#     honours is not this script's to guess, and guessing would pass `true` beside a `false`.
#   * The revision `show` answers for is checked to be the one asked for.
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
      [ $# -ge 2 ] && [ -n "$2" ] && [ "${2#-}" = "$2" ] || usage
      ENV_NAMES+=("$2"); ENV_VALUES+=(""); ENV_HAS_VALUE+=(0)
      shift 2 ;;
    --equals)
      # An empty expected value can never pass -- an empty value is treated as unset -- so it is a
      # mistake in the invocation, not a check. So is an option-shaped one: `--equals --image` would
      # otherwise swallow the --image and silently drop that assertion.
      n=${#ENV_NAMES[@]}
      [ $# -ge 2 ] && [ -n "$2" ] && [ "${2#--}" = "$2" ] && [ "$n" -gt 0 ] \
        && [ "${ENV_HAS_VALUE[$((n - 1))]}" = 0 ] || usage
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

ERR_FILE="$(mktemp)"
trap 'rm -f "$ERR_FILE"' EXIT
if ! LISTING=$(az containerapp revision list -n "$APP" -g "$RG" -o json 2> "$ERR_FILE"); then
  cat "$ERR_FILE" >&2
  # ABSENT only on az's own not-found for THIS app in THIS group, matched as a fixed string. See the
  # exit-code table for why nothing looser will do.
  if grep -qF "(ResourceNotFound) The Resource 'Microsoft.App/containerApps/$APP' under resource group '$RG' was not found" "$ERR_FILE"; then
    echo "ABSENT — $APP does not exist in $RG"
    exit 3
  fi
  not_assessed "could not list revisions of $APP in $RG (az failed — see its error)"
  exit 2
fi
cat "$ERR_FILE" >&2

# -s slurps, so a body holding more than one JSON document is caught rather than processed piecewise.
# Every element is validated BEFORE any is selected, so one malformed revision cannot be dropped
# while the rest pass.
if ! REVS=$(printf '%s' "$LISTING" | jqr -r -s '
    if length != 1 then error("expected exactly one JSON document") else .[0] end
    | if type != "array" then error("the listing is not an array") else . end
    | map(
        if (.name | type) != "string" or (.name | test("\\A[a-z0-9][a-z0-9-]*\\z") | not)
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
REV_LIST=()
mapfile -t REV_LIST <<< "$REVS" || { not_assessed "could not read the revision names (bash without mapfile?)"; exit 2; }
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
  if ! C=$(printf '%s' "$DOC" | jqr -c -s --arg rev "$REV" '
      if length != 1 then error("expected exactly one JSON document") else .[0] end
      | if .name != $rev then error("asked for \($rev) but az answered for \(.name | tojson)") else . end
      | .properties.template.containers
      | if type != "array" then error("the revision template has no containers array")
        elif length != 1 then error("expected exactly one container, found \(length)")
        else .[0] end
      | if (.env == null) or ((.env | type) == "array") then .
        else error("the container env is not an array") end
      | if any((.env // [])[]; (.name | type) != "string") then error("an env entry has no string name")
        else . end' 2>&1); then
    echo "$REV  NOT ASSESSED — $C"
    note 2; continue
  fi
  checked=$((checked + 1))

  line="$REV"
  for i in "${!ENV_NAMES[@]}"; do
    name="${ENV_NAMES[$i]}"
    # One of: eq  ne:[<how>]  set  secretref  unset. A blank value is unset -- the app reads its
    # settings with IsNullOrWhiteSpace, so to it blank means "not configured".
    #
    # The --equals comparison is made HERE, inside jq, never on a value carried out into the shell:
    # `$(…)` drops trailing newlines and the CR strip drops embedded CRs, so `true\n` and `tr\rue`
    # would compare equal to `true` out there. The live value never leaves jq at all: a mismatch
    # reports only HOW it differs, so --equals used against a credential by mistake cannot write
    # that credential into a terminal or CI log.
    if ! state=$(printf '%s' "$C" | jqr -r --arg n "$name" --arg want "${ENV_VALUES[$i]}" \
        --argjson compare "${ENV_HAS_VALUE[$i]}" '
        # The key as .NET configuration sees it: case-insensitive, with `:` and `__` the same
        # separator. A match on the literal name alone would pass `true` beside a later
        # `authorization__readonly=false`, which the app may well be the one it honours.
        def k: ascii_downcase | gsub(":"; "__");
        [(.env // [])[] | select((.name | k) == ($n | k))]
        | if length > 1 then error("\($n) appears \(length) times")
          elif length == 0 then "unset"
          else .[0]
            | if (.secretRef != null) and ((.secretRef | type) != "string")
                then error("\($n) has a non-string secretRef")
              elif (.secretRef // "") != "" then "secretref"
              elif .value == null then "unset"
              elif (.value | type) != "string" then error("\($n) has a non-string value")
              elif (.value | test("\\S") | not) then "unset"
              elif $compare != 1 then "set"
              elif .value == $want then "eq"
              elif (.value | gsub("^\\s+|\\s+$"; "")) == $want then "ne:differs only in surrounding whitespace"
              elif (.value | ascii_downcase) == ($want | ascii_downcase) then "ne:differs only in case"
              else "ne:" end
          end' 2>&1); then
      line="$line  $name=<NOT ASSESSED: $state>"
      note 2; continue
    fi
    if [ "${ENV_HAS_VALUE[$i]}" = 1 ]; then
      case "$state" in
        eq)
          line="$line  $name=${ENV_VALUES[$i]}" ;;
        ne:?*)
          line="$line  $name=<mismatch: ${state#ne:}>"
          note 1 ;;
        ne:)
          line="$line  $name=<mismatch>"
          note 1 ;;
        secretref)
          line="$line  $name=<secretRef: value not readable, NOT ASSESSED>"
          note 2 ;;
        *)
          line="$line  $name=<unset>"
          note 1 ;;
      esac
    else
      case "$state" in
        set|secretref) line="$line  $name=set" ;;
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
