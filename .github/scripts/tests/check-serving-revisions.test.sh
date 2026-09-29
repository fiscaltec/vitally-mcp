#!/usr/bin/env bash
#
# Tests for .github/scripts/check-serving-revisions.sh, run against a stub `az` (tests/fake-az)
# so every failure shape can be produced on demand -- including the ones that matter most and that
# a live app never shows you: a listing that fails, one that succeeds empty, and a revision whose
# template no longer has the shape the query expects.
#
# Run:  bash .github/scripts/tests/check-serving-revisions.test.sh
#
# Exit codes asserted, as documented in the script:
#   0 = every traffic-bearing revision satisfies every assertion
#   1 = at least one assertion definitely failed (and nothing was unassessable)
#   2 = NOT ASSESSED -- something could not be read, so no verdict is possible
#  64 = usage error
set -uo pipefail

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
SCRIPT="$HERE/../check-serving-revisions.sh"

pass=0
fail=0
WORK="$(mktemp -d)"
trap 'rm -rf "$WORK"' EXIT

# A fresh fixture directory plus a bin directory whose `az` is the stub.
new_case() {
  CASE="$WORK/case$((pass + fail + 1))"
  mkdir -p "$CASE/fixtures" "$CASE/bin"
  cp "$HERE/fake-az" "$CASE/bin/az"
  chmod +x "$CASE/bin/az"
  export FAKE_AZ_DIR="$CASE/fixtures"
  unset FAKE_AZ_CRLF
}

# list_fixture "rev1:100" "rev2:0" ...  -> list.json with those traffic weights
list_fixture() {
  local out="[" first=1 spec name weight
  for spec in "$@"; do
    name="${spec%%:*}"; weight="${spec##*:}"
    [ "$first" = 1 ] || out="$out,"
    first=0
    out="$out{\"name\":\"$name\",\"properties\":{\"trafficWeight\":$weight,\"active\":true}}"
  done
  printf '%s]\n' "$out" > "$FAKE_AZ_DIR/list.json"
}

# rev_fixture <rev> <image> <env-json-array>  -> a one-container revision
rev_fixture() {
  printf '{"name":"%s","properties":{"template":{"containers":[{"name":"app","image":"%s","env":%s}]}}}\n' \
    "$1" "$2" "$3" > "$FAKE_AZ_DIR/show-$1.json"
}

# Runs the script with the stub first on PATH. Sets OUT and RC.
run() {
  OUT="$(PATH="$CASE/bin:$PATH" bash "$SCRIPT" "$@" 2>&1)"
  RC=$?
}

check() {  # $1 = description, then a condition evaluated with eval
  local desc="$1"; shift
  if eval "$@"; then
    pass=$((pass + 1))
  else
    fail=$((fail + 1))
    printf 'FAIL: %s\n  condition: %s\n  rc=%s\n  output:\n%s\n\n' "$desc" "$*" "$RC" "$(sed 's/^/    /' <<< "$OUT")"
  fi
}

GUARDED='[{"name":"Authorization__ReadOnly","value":"true"},{"name":"ApplicationInsights__ConnectionString","value":"InstrumentationKey=SECRET-DO-NOT-PRINT"}]'
UNGUARDED='[{"name":"ApplicationInsights__ConnectionString","value":"InstrumentationKey=SECRET-DO-NOT-PRINT"}]'
IMG=registry.example/vitally-mcp:sha-abc1234

# ---------------------------------------------------------------- the pass path

new_case
list_fixture r1:100
rev_fixture r1 "$IMG" "$GUARDED"
run app rg --env Authorization__ReadOnly --equals true
check "a guarded single revision passes" '[ "$RC" -eq 0 ]'
check "the pass names the revision and the value" 'grep -q "r1" <<< "$OUT" && grep -q "Authorization__ReadOnly=true" <<< "$OUT"'
check "the app and resource group reach az" 'grep -q -- "-n app -g rg" "$FAKE_AZ_DIR/calls"'

new_case
list_fixture r1:100
rev_fixture r1 "$IMG" "$GUARDED"
run app rg --env Authorization__ReadOnly --equals true --env ApplicationInsights__ConnectionString
check "two assertions both satisfied pass" '[ "$RC" -eq 0 ]'
check "a set-only assertion never prints the value" '! grep -q "SECRET-DO-NOT-PRINT" <<< "$OUT"'
check "a set-only assertion reports set" 'grep -q "ApplicationInsights__ConnectionString=set" <<< "$OUT"'

# ---------------------------------------------------------------- definite failures (exit 1)

new_case
list_fixture r1:100
rev_fixture r1 "$IMG" "$UNGUARDED"
run app rg --env Authorization__ReadOnly --equals true
check "an absent variable is a definite failure, not a pass" '[ "$RC" -eq 1 ]'
check "an absent variable is printed as <unset>, not as a blank" 'grep -q "Authorization__ReadOnly=<unset>" <<< "$OUT"'

new_case
list_fixture r1:100
rev_fixture r1 "$IMG" '[{"name":"Authorization__ReadOnly","value":"false"}]'
run app rg --env Authorization__ReadOnly --equals true
check "a wrong value fails" '[ "$RC" -eq 1 ]'

new_case
list_fixture r1:100
rev_fixture r1 "$IMG" '[{"name":"Authorization__ReadOnly","value":""}]'
run app rg --env Authorization__ReadOnly
check "a variable present with an empty value is not set" '[ "$RC" -eq 1 ]'

new_case
list_fixture r1:100
rev_fixture r1 "$IMG" "$GUARDED"
run app rg --env Authorization__ReadOnly --equals true --env Missing__Thing
check "one of two assertions failing fails the whole check" '[ "$RC" -eq 1 ]'

new_case
list_fixture r1:50 r2:50
rev_fixture r1 "$IMG" "$GUARDED"
rev_fixture r2 "$IMG" "$UNGUARDED"
run app rg --env Authorization__ReadOnly --equals true
check "a mixed split fails" '[ "$RC" -eq 1 ]'
check "a mixed split prints a line for EACH revision" 'grep -q "^r1" <<< "$OUT" && grep -q "^r2" <<< "$OUT"'

# ---------------------------------------------------------------- secretRef handling

new_case
list_fixture r1:100
rev_fixture r1 "$IMG" '[{"name":"ApplicationInsights__ConnectionString","secretRef":"appi-conn"}]'
run app rg --env ApplicationInsights__ConnectionString
check "a secretRef-backed variable counts as set" '[ "$RC" -eq 0 ]'

new_case
list_fixture r1:100
rev_fixture r1 "$IMG" '[{"name":"Authorization__ReadOnly","secretRef":"ro"}]'
run app rg --env Authorization__ReadOnly --equals true
check "--equals against a secretRef cannot be assessed" '[ "$RC" -eq 2 ] && grep -q "NOT ASSESSED" <<< "$OUT"'

# ---------------------------------------------------------------- NOT ASSESSED (exit 2)

new_case
printf 'ERROR: AADSTS700024: Client assertion is not within its valid time range.\n' > "$FAKE_AZ_DIR/list.fail"
run app rg --env Authorization__ReadOnly --equals true
check "a failed listing is NOT ASSESSED, not a pass" '[ "$RC" -eq 2 ] && grep -q "NOT ASSESSED" <<< "$OUT"'
check "a failed listing surfaces az's own error" 'grep -q "AADSTS700024" <<< "$OUT"'

new_case
printf '[]\n' > "$FAKE_AZ_DIR/list.json"
run app rg --env Authorization__ReadOnly --equals true
check "an empty listing is NOT ASSESSED -- a bare for-loop would exit 0 here" '[ "$RC" -eq 2 ] && grep -q "NOT ASSESSED" <<< "$OUT"'

new_case
list_fixture r1:0 r2:0
run app rg --env Authorization__ReadOnly --equals true
check "revisions with no traffic are NOT ASSESSED" '[ "$RC" -eq 2 ]'

new_case
printf 'WARNING: something\nnot json\n' > "$FAKE_AZ_DIR/list.json"
run app rg --env Authorization__ReadOnly --equals true
check "a non-JSON listing is NOT ASSESSED" '[ "$RC" -eq 2 ]'

new_case
list_fixture r1:100
printf 'ERROR: (AuthorizationFailed) no read on this revision\n' > "$FAKE_AZ_DIR/show-r1.fail"
run app rg --env Authorization__ReadOnly --equals true
check "a failed per-revision read is NOT ASSESSED" '[ "$RC" -eq 2 ] && grep -q "NOT ASSESSED" <<< "$OUT"'

new_case
list_fixture r1:50 r2:50
rev_fixture r1 "$IMG" '[{"name":"Authorization__ReadOnly","value":"false"}]'
printf 'ERROR: boom\n' > "$FAKE_AZ_DIR/show-r2.fail"
run app rg --env Authorization__ReadOnly --equals true
check "NOT ASSESSED outranks a definite failure elsewhere" '[ "$RC" -eq 2 ]'
check "...while still reporting the definite failure" 'grep -q "Authorization__ReadOnly=false" <<< "$OUT"'

new_case
list_fixture r1:100
printf '{"name":"r1","properties":{"template":{}}}\n' > "$FAKE_AZ_DIR/show-r1.json"
run app rg --env Authorization__ReadOnly --equals true
check "a template with no containers (schema drift) is NOT ASSESSED, not <unset>" '[ "$RC" -eq 2 ]'

new_case
list_fixture r1:100
printf '{"name":"r1","properties":{"template":{"containers":[{"name":"a","image":"x","env":[]},{"name":"b","image":"y","env":[]}]}}}\n' > "$FAKE_AZ_DIR/show-r1.json"
run app rg --env Authorization__ReadOnly --equals true
check "a multi-container template is NOT ASSESSED" '[ "$RC" -eq 2 ]'

new_case
list_fixture r1:100
printf '{"name":"r1","properties":{"template":{"containers":[{"name":"a","image":"x"}]}}}\n' > "$FAKE_AZ_DIR/show-r1.json"
run app rg --env Authorization__ReadOnly --equals true
check "a container with no env array at all is <unset>, a definite answer" '[ "$RC" -eq 1 ] && grep -q "<unset>" <<< "$OUT"'

# ---------------------------------------------------------------- --image

new_case
list_fixture r1:100
rev_fixture r1 "$IMG" "$GUARDED"
run app rg --image
check "--image passes and prints the image" '[ "$RC" -eq 0 ] && grep -q "image=$IMG" <<< "$OUT"'

new_case
list_fixture r1:100
rev_fixture r1 "" "$GUARDED"
run app rg --image
check "an empty image is NOT ASSESSED" '[ "$RC" -eq 2 ]'

new_case
list_fixture r1:50 r2:50
rev_fixture r1 "$IMG" "$GUARDED"
rev_fixture r2 "registry.example/vitally-mcp:sha-def5678" "$GUARDED"
run app rg --image
check "two images in a split still exit 0 (no expected value)" '[ "$RC" -eq 0 ]'
check "...but are called out" 'grep -q "different images" <<< "$OUT"'

# ---------------------------------------------------------------- Windows CLI line endings

new_case
export FAKE_AZ_CRLF=1
list_fixture r1:100
rev_fixture r1 "$IMG" "$GUARDED"
run app rg --env Authorization__ReadOnly --equals true --image
check "CR LF output from the Windows CLI still passes" '[ "$RC" -eq 0 ]'
check "...and no CR leaks into the printed line" '! grep -q $'"'"'\r'"'"' <<< "$OUT"'

# ---------------------------------------------------------------- usage (exit 64)

new_case
run app rg
check "no assertion is a usage error -- a check of nothing must not pass" '[ "$RC" -eq 64 ]'

new_case
run app rg --equals true
check "--equals without --env is a usage error" '[ "$RC" -eq 64 ]'

new_case
run app
check "a missing resource group is a usage error" '[ "$RC" -eq 64 ]'

new_case
run app rg --env
check "--env without a name is a usage error" '[ "$RC" -eq 64 ]'

new_case
run app rg --bogus
check "an unknown option is a usage error" '[ "$RC" -eq 64 ]'

new_case
run app rg --env A --equals x --equals y
check "two --equals for one --env is a usage error" '[ "$RC" -eq 64 ]'

echo
echo "check-serving-revisions: $pass passed, $fail failed"
[ "$fail" -eq 0 ]
