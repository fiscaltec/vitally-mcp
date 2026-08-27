#!/usr/bin/env bash
#
# Verifies the OAuth metadata documents a running Vitally MCP server publishes.
#
# WHY THIS EXISTS. deploy.yml's other smoke assertions are `/health` == 200 and unauthenticated
# `/mcp` == 401. Both of those pass while the metadata documents are wrong -- wrong `issuer`, a
# broken `issuer` <-> `authorization_servers` pairing, a bad `jwks_uri`, `/oauth/authorize` pointed
# at the wrong upstream, a dropped `iss` flag, failing DCR. Every one of those breaks MCP clients at
# their next re-authentication while the deploy reports success, so the auto-rollback used to read as
# assurance it did not provide. See issue #110.
#
# Usage:  verify-oauth-metadata.sh <public-origin>
#   e.g.  verify-oauth-metadata.sh https://vitally.fiscaltec.com
#         verify-oauth-metadata.sh http://localhost:5099      # local run, for testing this script
#
# Exits non-zero with a ::error:: annotation listing every failed assertion. Deliberately runnable
# outside CI against any origin: a check that can only be exercised by deploying to production is a
# check nobody validates before relying on it.
set -euo pipefail

ORIGIN="${1:?usage: verify-oauth-metadata.sh <public-origin>}"
ORIGIN="${ORIGIN%/}"          # tolerate a trailing slash on the argument only

ATTEMPTS="${ATTEMPTS:-6}"
SLEEP_SECONDS="${SLEEP_SECONDS:-10}"

WORK_DIR="$(mktemp -d)"
trap 'rm -rf "$WORK_DIR"' EXIT

failures=0

fail() {
  echo "::error::$*"
  failures=$((failures + 1))
}

# Fetch a document, retrying because a freshly-swapped revision can take a moment to serve on the
# public FQDN. curl emits "000" as the http_code on a connection failure; `|| true` stops `set -e`
# aborting the loop so the retry can happen. No sleep after the final attempt.
#
# A 200 carrying a non-JSON body is retried too, not failed on the spot. An ingress or edge can answer
# 200 with an HTML error or a truncated body while a revision is still swapping in, and this script can
# revert a deploy — so a momentary bad body must not be mistaken for a metadata regression. Retrying
# costs seconds; a spurious rollback of a good deploy costs a lot more.
fetch_json() {
  local url="$1" out="$2" code="" reason=""
  local i
  for i in $(seq 1 "$ATTEMPTS"); do
    code=$(curl -s -o "$out" -w "%{http_code}" --max-time 20 "$url" || true)
    if [ "$code" = "200" ]; then
      if jq -e . "$out" >/dev/null 2>&1; then
        return 0
      fi
      reason="200 but the body is not valid JSON"
    else
      reason="HTTP ${code:-000}"
    fi
    echo "  GET $url -> $reason (attempt $i/$ATTEMPTS)"
    if [ "$i" -lt "$ATTEMPTS" ]; then sleep "$SLEEP_SECONDS"; fi
  done
  fail "$url did not return a usable JSON document after $ATTEMPTS attempts (last: $reason)"
  return 1
}

# Compare a JSON string field against an expected value with plain equality. No normalisation is
# applied anywhere in this script, deliberately: clients compare these strings literally, so a check
# that tolerated a trailing slash or a case difference would pass configurations clients reject.
assert_field() {
  local file="$1" path="$2" expected="$3" label="$4"
  local actual
  actual=$(jq -r "$path // \"<absent>\"" "$file")
  if [ "$actual" = "$expected" ]; then
    echo "  ok   $label = $actual"
  else
    fail "$label is '$actual', expected '$expected'"
  fi
}

# An endpoint republished to clients must be an absolute https URI with no fragment. Mirrors
# UpstreamOidcMetadata.RequireEndpoint (#109): a fragment silently swallows any query appended after
# it, and these values are handed to every client as fact.
assert_upstream_endpoint() {
  local file="$1" path="$2" label="$3"
  local actual
  actual=$(jq -r "$path // \"<absent>\"" "$file")
  case "$actual" in
    "<absent>")
      fail "$label is absent"
      return
      ;;
    https://*)
      ;;
    *)
      fail "$label is '$actual', which is not an absolute https URI"
      return
      ;;
  esac
  case "$actual" in
    *"#"*)
      fail "$label is '$actual', which contains a URI fragment (RFC 6749 3.1 forbids one)"
      return
      ;;
  esac
  echo "  ok   $label = $actual"
}

echo "Verifying OAuth metadata at $ORIGIN"

AS_DOC="$WORK_DIR/as.json"
expected_as_issuer=""
if fetch_json "$ORIGIN/.well-known/oauth-authorization-server" "$AS_DOC"; then
  # RFC 8414 section 3.3 -- an anti-mix-up control. The document may only speak for the issuer it was
  # fetched from, and we front authorize/token/register ourselves, so our own origin is the honest
  # answer. Declaring the upstream provider's issuer here is the exact regression #90/#100 fixed, and
  # it made strict clients (the TypeScript MCP SDK, hence MCP Inspector) abort before reaching DCR.
  assert_field "$AS_DOC" '.issuer' "$ORIGIN" "issuer"

  # The facade's own endpoints must keep naming us. If any of these ever pointed upstream, DCR and
  # the iss contract would both break while /health and the 401 stayed perfectly green.
  assert_field "$AS_DOC" '.authorization_endpoint' "$ORIGIN/oauth/authorize" "authorization_endpoint"
  assert_field "$AS_DOC" '.token_endpoint' "$ORIGIN/oauth/token" "token_endpoint"
  assert_field "$AS_DOC" '.registration_endpoint' "$ORIGIN/oauth/register" "registration_endpoint"

  # RFC 9207. Honest only because /oauth/callback injects `iss` unconditionally -- advertising
  # support and then omitting the parameter reads to a client as a stripped-parameter attack, so the
  # flag and the injection ship together, and this pins the half observable from outside.
  iss_flag=$(jq -r '.authorization_response_iss_parameter_supported // "<absent>"' "$AS_DOC")
  if [ "$iss_flag" = "true" ]; then
    echo "  ok   authorization_response_iss_parameter_supported = true"
  else
    fail "authorization_response_iss_parameter_supported is '$iss_flag', expected true"
  fi

  # These two point at the upstream provider and are read from its discovery document (#109), so a
  # misconfigured OAuth:Authority surfaces here as a wrong value advertised to clients as fact.
  assert_upstream_endpoint "$AS_DOC" '.jwks_uri' "jwks_uri"
  assert_upstream_endpoint "$AS_DOC" '.userinfo_endpoint' "userinfo_endpoint"

  expected_as_issuer=$(jq -r '.issuer // ""' "$AS_DOC")
fi

# RFC 9728, served from the canonical path and the resource-path-suffixed variant. Clients probe
# either, so both must serve -- and both must agree with the authorization-server document.
pr_index=0
for path in "/.well-known/oauth-protected-resource" "/.well-known/oauth-protected-resource/mcp"; do
  pr_index=$((pr_index + 1))
  PR_DOC="$WORK_DIR/pr-$pr_index.json"
  if ! fetch_json "$ORIGIN$path" "$PR_DOC"; then
    continue
  fi

  # The pairing a strict client actually checks: it reads authorization_servers out of this document,
  # uses that string verbatim to build the well-known URL, and requires the returned issuer to equal
  # it. Asserting each document alone would let the two drift while both still looked correct.
  advertised=$(jq -r '.authorization_servers[0] // "<absent>"' "$PR_DOC")
  if [ -z "$expected_as_issuer" ]; then
    fail "$path: cannot check authorization_servers -- the authorization-server document was unusable"
  elif [ "$advertised" = "$expected_as_issuer" ]; then
    echo "  ok   $path authorization_servers[0] = $advertised (matches issuer)"
  else
    fail "$path: authorization_servers[0] is '$advertised' but the authorization-server document declares issuer '$expected_as_issuer' -- clients compare these two literally"
  fi

  # RFC 9728 section 3.2 requires an unused metadata parameter to be *omitted*, not null. The
  # ASP.NET Core default serialiser writes every unset optional as an explicit null, and the
  # published @modelcontextprotocol/client types jwks_uri as a string and rejects the whole document
  # on a null -- before any part of the OAuth flow is reached. Regression guard for the fix in #100,
  # and invisible to any check that only reads the properties it expects to find.
  null_paths=$(jq -c '[paths(. == null)]' "$PR_DOC")
  if [ "$null_paths" = "[]" ]; then
    echo "  ok   $path has no null-valued properties"
  else
    fail "$path serialises null at $null_paths -- RFC 9728 section 3.2 requires an unused parameter to be omitted, and strict clients reject the document over the difference"
  fi
done

if [ "$failures" -ne 0 ]; then
  echo "::error::OAuth metadata verification failed with $failures problem(s) at $ORIGIN"
  exit 1
fi

echo "OAuth metadata verification passed at $ORIGIN"
