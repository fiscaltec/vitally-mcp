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
#   0 = every serving (active or traffic-bearing) revision satisfies every assertion
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

# list_fixture "rev1:100" "rev2:0:false" ...  -> list.json with those traffic weights and, optionally,
# `active` (default true)
list_fixture() {
  local out="[" first=1 spec name weight active rest
  for spec in "$@"; do
    name="${spec%%:*}"; rest="${spec#*:}"
    weight="${rest%%:*}"
    if [ "$rest" = "$weight" ]; then active=true; else active="${rest#*:}"; fi
    [ "$first" = 1 ] || out="$out,"
    first=0
    out="$out{\"name\":\"$name\",\"properties\":{\"trafficWeight\":$weight,\"active\":$active}}"
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
list_fixture r1:0:false r2:0:false
run app rg --env Authorization__ReadOnly --equals true
check "only inactive revisions with no traffic is NOT ASSESSED" '[ "$RC" -eq 2 ] && grep -q "no active or traffic-bearing" <<< "$OUT"'

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
check "...while still reporting the definite failure" 'grep -qF "Authorization__ReadOnly=\"false\"" <<< "$OUT"'

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

# ---------------------------------------------------------------- the listing must be what it claims

new_case
printf '[{"name":"","properties":{"trafficWeight":100,"active":true}}]\n' > "$FAKE_AZ_DIR/list.json"
run app rg --env Authorization__ReadOnly --equals true
check "an empty revision name is NOT ASSESSED -- word-splitting would check nothing and pass" '[ "$RC" -eq 2 ]'

new_case
printf '[{"name":"","properties":{"trafficWeight":50,"active":true}},{"name":"r2","properties":{"trafficWeight":50,"active":true}}]\n' > "$FAKE_AZ_DIR/list.json"
rev_fixture r2 "$IMG" "$GUARDED"
run app rg --env Authorization__ReadOnly --equals true
check "an empty name beside a good revision is NOT ASSESSED -- half the traffic unchecked" '[ "$RC" -eq 2 ]'

new_case
printf '[{"name":"*","properties":{"trafficWeight":100,"active":true}}]\n' > "$FAKE_AZ_DIR/list.json"
run app rg --env Authorization__ReadOnly --equals true
check "a revision name that is not a revision name is NOT ASSESSED, and is never passed to az" '[ "$RC" -eq 2 ] && ! grep -q -- "--revision" "$FAKE_AZ_DIR/calls"'

new_case
printf '[{"name":"r1","properties":{"trafficWeight":50,"active":true}},{"name":"r2","properties":{"traffic":{"weight":50},"active":true}}]\n' > "$FAKE_AZ_DIR/list.json"
rev_fixture r1 "$IMG" "$GUARDED"
rev_fixture r2 "$IMG" "$UNGUARDED"
run app rg --env Authorization__ReadOnly --equals true
check "a revision with no numeric trafficWeight is NOT ASSESSED, not silently dropped" '[ "$RC" -eq 2 ]'

new_case
printf '[{"name":"r1","properties":{"trafficWeight":"100","active":true}}]\n' > "$FAKE_AZ_DIR/list.json"
rev_fixture r1 "$IMG" "$GUARDED"
run app rg --env Authorization__ReadOnly --equals true
check "a string trafficWeight is NOT ASSESSED" '[ "$RC" -eq 2 ]'

new_case
printf '[{"name":"r1","properties":{"trafficWeight":100,"active":true}}][]\n' > "$FAKE_AZ_DIR/list.json"
rev_fixture r1 "$IMG" "$UNGUARDED"
run app rg --env Authorization__ReadOnly --equals true
check "more than one JSON document in the listing is NOT ASSESSED" '[ "$RC" -eq 2 ]'

new_case
printf '{"value":[]}\n' > "$FAKE_AZ_DIR/list.json"
run app rg --env Authorization__ReadOnly --equals true
check "a listing that is an object, not an array, is NOT ASSESSED" '[ "$RC" -eq 2 ]'

new_case
printf '[{"name":"r1","properties":{"trafficWeight":100,"active":true}},{"name":"r2","properties":{"trafficWeight":0,"active":true}}]\n' > "$FAKE_AZ_DIR/list.json"
rev_fixture r1 "$IMG" "$GUARDED"
rev_fixture r2 "$IMG" "$UNGUARDED"
run app rg --env Authorization__ReadOnly --equals true
check "an ACTIVE revision at 0% weight is still checked -- it answers on its own FQDN" '[ "$RC" -eq 1 ] && grep -q "^r2" <<< "$OUT"'

new_case
printf '[{"name":"r1","properties":{"trafficWeight":100,"active":true}},{"name":"r0","properties":{"trafficWeight":0,"active":false}}]\n' > "$FAKE_AZ_DIR/list.json"
rev_fixture r1 "$IMG" "$GUARDED"
run app rg --env Authorization__ReadOnly --equals true
check "an inactive 0% revision is not checked -- it serves nothing" '[ "$RC" -eq 0 ] && ! grep -q "^r0" <<< "$OUT"'
check "...and is never read" '! grep -q -- "--revision r0" "$FAKE_AZ_DIR/calls"'

new_case
printf '[{"name":"r1","properties":{"trafficWeight":100}}]\n' > "$FAKE_AZ_DIR/list.json"
rev_fixture r1 "$IMG" "$GUARDED"
run app rg --env Authorization__ReadOnly --equals true
check "a revision with no boolean active field is NOT ASSESSED" '[ "$RC" -eq 2 ]'

# ---------------------------------------------------------------- the template must be what it claims

new_case
list_fixture r1:100
rev_fixture r1 "$IMG" '[{"name":"Authorization__ReadOnly","value":"true"},{"name":"Authorization__ReadOnly","value":"false"}]'
run app rg --env Authorization__ReadOnly --equals true
check "a duplicated env name is NOT ASSESSED -- which entry wins is not ours to guess" '[ "$RC" -eq 2 ]'

new_case
list_fixture r1:100
rev_fixture r1 "$IMG" '{"Authorization__ReadOnly":"true"}'
run app rg --env Authorization__ReadOnly --equals true
check "an env that is an object, not an array, is NOT ASSESSED rather than <unset>" '[ "$RC" -eq 2 ]'

new_case
list_fixture r1:100
rev_fixture r1 "$IMG" '[{"name":"Authorization__ReadOnly","value":true}]'
run app rg --env Authorization__ReadOnly --equals true
check "a non-string value is NOT ASSESSED rather than <unset>" '[ "$RC" -eq 2 ]'

# .NET's environment configuration provider matches keys case-insensitively and reads `:` and `__`
# as the same separator, so these are all the same setting to the app -- and which one it honours is
# not ours to guess.
new_case
list_fixture r1:100
rev_fixture r1 "$IMG" '[{"name":"Authorization__ReadOnly","value":"true"},{"name":"authorization__readonly","value":"false"}]'
run app rg --env Authorization__ReadOnly --equals true
check "a case-variant duplicate is NOT ASSESSED" '[ "$RC" -eq 2 ]'

new_case
list_fixture r1:100
rev_fixture r1 "$IMG" '[{"name":"Authorization__ReadOnly","value":"true"},{"name":"Authorization:ReadOnly","value":"false"}]'
run app rg --env Authorization__ReadOnly --equals true
check "a colon-separated duplicate is NOT ASSESSED" '[ "$RC" -eq 2 ]'

new_case
list_fixture r1:100
rev_fixture r1 "$IMG" '[{"name":"authorization__readonly","value":"true"}]'
run app rg --env Authorization__ReadOnly --equals true
check "a single case-variant entry is the setting the app reads, so it is checked" '[ "$RC" -eq 0 ]'

new_case
list_fixture r1:100
rev_fixture r1 "$IMG" '[{"name":5,"value":"x"},{"name":"Authorization__ReadOnly","value":"true"}]'
run app rg --env Authorization__ReadOnly --equals true
check "an env entry with no string name is NOT ASSESSED" '[ "$RC" -eq 2 ]'

new_case
list_fixture r1:100
printf '{"name":"other","properties":{"template":{"containers":[{"name":"a","image":"x","env":[{"name":"Authorization__ReadOnly","value":"true"}]}]}}}\n' > "$FAKE_AZ_DIR/show-r1.json"
run app rg --env Authorization__ReadOnly --equals true
check "a revision show that answers for a different revision is NOT ASSESSED" '[ "$RC" -eq 2 ]'

new_case
printf '[{"name":"r1\\n","properties":{"trafficWeight":100,"active":true}}]\n' > "$FAKE_AZ_DIR/list.json"
rev_fixture r1 "$IMG" "$GUARDED"
run app rg --env Authorization__ReadOnly --equals true
check "a revision name with a trailing newline is NOT ASSESSED" '[ "$RC" -eq 2 ]'

# Exact means exact. The comparison happens inside jq, because a value carried out through `$(…)`
# loses its trailing newlines and, through the CR strip, its embedded CRs -- `true\n` and `tr\rue`
# would then compare equal to `true`.
new_case
list_fixture r1:100
rev_fixture r1 "$IMG" '[{"name":"Authorization__ReadOnly","value":"true\n"}]'
run app rg --env Authorization__ReadOnly --equals true
check "a value with a trailing newline does not equal the value without it" '[ "$RC" -eq 1 ]'
check "...and is printed escaped, so the difference is visible" 'grep -qF "Authorization__ReadOnly=\"true\\n\"" <<< "$OUT"'

new_case
list_fixture r1:100
rev_fixture r1 "$IMG" '[{"name":"Authorization__ReadOnly","value":"tr\rue"}]'
run app rg --env Authorization__ReadOnly --equals true
check "a value with an embedded CR does not equal the value without it" '[ "$RC" -eq 1 ]'

new_case
list_fixture r1:100
rev_fixture r1 "$IMG" '[{"name":"Authorization__ReadOnly","value":"true "}]'
run app rg --env Authorization__ReadOnly --equals true
check "a value with trailing whitespace does not equal the value without it" '[ "$RC" -eq 1 ]'

new_case
list_fixture r1:100
rev_fixture r1 "$IMG" '[{"name":"ApplicationInsights__ConnectionString","value":"   "}]'
run app rg --env ApplicationInsights__ConnectionString
check "a whitespace-only value is not set -- the app reads it with IsNullOrWhiteSpace" '[ "$RC" -eq 1 ]'

new_case
list_fixture r1:100
printf '{"name":"r1","properties":{"template":{"containers":[{"name":"a","image":"x","env":[]}]}}}\n{"name":"r1"}\n' > "$FAKE_AZ_DIR/show-r1.json"
run app rg --env Authorization__ReadOnly --equals true
check "more than one JSON document from revision show is NOT ASSESSED" '[ "$RC" -eq 2 ]'

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

# The CR that matters is the one jq.exe writes on its OUTPUT, so model that with a jq wrapper that
# appends CR to every line -- on Linux, too, where the real jq writes none and a test that only fed
# CR into jq's input would pass with the strip removed.
new_case
export FAKE_AZ_CRLF=1
REAL_JQ="$(command -v jq)"
printf '#!/usr/bin/env bash\nset -o pipefail\n"%s" "$@" | sed '"'"'s/$/\\r/'"'"'\n' "$REAL_JQ" > "$CASE/bin/jq"
chmod +x "$CASE/bin/jq"
list_fixture r1:100
rev_fixture r1 "$IMG" "$GUARDED"
run app rg --env Authorization__ReadOnly --equals true --image
check "CR LF from both az and jq (as on Windows) still passes" '[ "$RC" -eq 0 ]'
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

new_case
run app rg --env A --equals ""
check "--equals with an empty value can never pass, so is a usage error" '[ "$RC" -eq 64 ]'

new_case
run app --image --image
check "a resource group that looks like an option is a usage error" '[ "$RC" -eq 64 ]'

new_case
run app rg --env A --equals --image
check "an option-shaped --equals value is a usage error, not a swallowed --image" '[ "$RC" -eq 64 ]'

new_case
run app rg --env -A
check "an option-shaped --env name is a usage error" '[ "$RC" -eq 64 ]'

echo
echo "check-serving-revisions: $pass passed, $fail failed"
[ "$fail" -eq 0 ]
