#!/usr/bin/env bash
# PreToolUse (Bash) hook — block `gh pr merge` on a human PR until Copilot's
# review cycle for the current HEAD commit is complete and clean.
#
# Registered in .claude/settings.json with `if: "Bash(gh pr merge*)"`, so it
# only fires on merge commands. Blocks via the PreToolUse permissionDecision
# JSON. Fail-closed: if any check cannot be verified, deny rather than risk a
# premature merge. Dependabot PRs are exempt (CI-gated auto-merge by design).
set -uo pipefail

# Deny the tool call with a reason (PreToolUse JSON mechanism; exit 0).
deny() {
	printf '{"hookSpecificOutput":{"hookEventName":"PreToolUse","permissionDecision":"deny","permissionDecisionReason":%s}}\n' \
		"$(printf 'Copilot merge gate — %s' "$1" | jq -Rs .)"
	exit 0
}
# Allow: emit nothing and exit 0 → defer to Claude Code's normal permission flow.
allow() { exit 0; }

# deny() builds its JSON with jq, so the gate would fail *open* if jq were
# missing OR present-but-broken. Prove jq actually runs (not just that it's on
# PATH) with a static denial fallback (printf is a shell builtin — no external
# tool needed) so a broken toolchain can never wave a merge through.
if ! printf '{}' | jq -e . >/dev/null 2>&1 \
	|| ! printf 'x' | sed 's/x/y/' >/dev/null 2>&1 \
	|| ! awk 'BEGIN { exit 0 }' >/dev/null 2>&1; then
	printf '{"hookSpecificOutput":{"hookEventName":"PreToolUse","permissionDecision":"deny","permissionDecisionReason":"Copilot merge gate — a required tool (jq/sed/awk) is missing or not runnable; cannot verify the gate (failing closed)"}}\n'
	exit 0
fi

input=$(cat)
# Fail closed if the payload can't be parsed or carries no command — we can't
# confirm the call is safe, and the `if` filter means we only get here for merges.
cmd=$(printf '%s' "$input" | jq -er '.tool_input.command' 2>/dev/null) \
	|| deny "could not read the command from hook input (failing closed)"

# Only gate `gh pr merge` (secondary guard; the `if` filter already scopes this).
# Bash regex — no external grep, so a broken grep can't make this fail open.
[[ "$cmd" =~ gh[[:space:]]+pr[[:space:]]+merge ]] || allow

# The gate only reasons about the *current* repo; a cross-repo merge can't be
# verified here, so fail closed if -R/--repo is present.
[[ "$cmd" =~ (^|[[:space:]])(-R|--repo) ]] && deny "cross-repo merge (-R/--repo) is not supported by the gate (failing closed)"

# Current repo — every check below is scoped to it.
nwo=$(gh repo view --json nameWithOwner --jq '.nameWithOwner' 2>/dev/null) \
	|| deny "could not resolve the current repo (failing closed)"
owner=${nwo%%/*}; name=${nwo#*/}

# Resolve the PR: first non-flag token after `merge` (number, URL, or branch —
# all accepted by `gh pr view`), else the current branch's PR.
# Collect positional candidates, skipping flags AND the values consumed by
# value-taking flags (so `--subject Foo` doesn't get mistaken for the PR). One
# candidate → that PR; none → current branch; more than one → ambiguous, deny.
arg=$(printf '%s' "$cmd" | sed -nE 's/.*gh[[:space:]]+pr[[:space:]]+merge[[:space:]]+//p' | awk '
	BEGIN { split("-t --subject -b --body -F --body-file --author-email --match-head-commit", f, " "); for (k in f) vf[f[k]] = 1 }
	{
		n = 0
		for (i = 1; i <= NF; i++) {
			t = $i
			if (t ~ /^-/) { if (t !~ /=/ && (t in vf)) i++; continue }  # skip flag (and its value if it takes one)
			cand[++n] = t
		}
		if (n == 1) print cand[1]
		else if (n > 1) print "__AMBIGUOUS__"
	}')
if [ "$arg" = "__AMBIGUOUS__" ]; then
	deny "ambiguous PR argument in the merge command (failing closed)"
elif [ -n "$arg" ]; then
	# A PR URL must point at the current repo — the checks below assume it.
	case "$arg" in
	*://*) [[ "$arg" == *"/$owner/$name/"* ]] || deny "PR URL targets a different repo than $nwo (failing closed)" ;;
	esac
	pr=$(gh pr view "$arg" --json number --jq '.number' 2>/dev/null || true)
	[ -n "$pr" ] || deny "could not resolve a PR from '$arg' (failing closed)"
else
	pr=$(gh pr view --json number --jq '.number' 2>/dev/null || true)
	[ -n "$pr" ] || deny "no PR resolvable for the current branch to gate (failing closed)"
fi

facts=$(gh pr view "$pr" --json headRefOid,headRefName,author,reviewRequests 2>/dev/null) \
	|| deny "could not query PR #$pr (failing closed)"
author=$(printf '%s' "$facts" | jq -r '.author.login // empty')
head=$(printf '%s' "$facts" | jq -r '.headRefOid // empty')
prbranch=$(printf '%s' "$facts" | jq -r '.headRefName // empty')
[ -n "$head" ] || deny "could not read the head commit of PR #$pr (failing closed)"

# `headRefOid` LAGS after a push — CLAUDE.md records it still naming the previous
# commit seconds after one on #122. Trusting it alone is this gate's worst failure
# mode, and it fails OPEN: right after a push the field still names the commit
# Copilot already reviewed, so the comparison below matches and a newly pushed,
# UNREVIEWED commit merges — precisely the #117 mistake this hook was ported to
# stop. When the PR's branch is the one checked out, git is authoritative and
# fresh, so cross-check and deny on any disagreement rather than picking a winner.
# A different branch checked out means no local reference to compare against, so
# the API value stands — note the residual risk if you merge another PR from here.
localbranch=$(git rev-parse --abbrev-ref HEAD 2>/dev/null) || localbranch=""
if [ -n "$prbranch" ] && [ "$prbranch" = "$localbranch" ]; then
	localhead=$(git rev-parse HEAD 2>/dev/null) \
		|| deny "could not resolve the local HEAD to cross-check PR #$pr (failing closed)"
	[ "$localhead" = "$head" ] \
		|| deny "PR #$pr reports head $head but the local branch is at $localhead — the PR API lags after a push, and the older value would match an already-reviewed commit. Wait for it to catch up, then re-check (failing closed)"
fi
pending=$(printf '%s' "$facts" | jq -r '[.reviewRequests[].login] | index("copilot-pull-request-reviewer") != null')

# Dependabot carve-out. Mind the two forms of the bot's login — the suffix tracks
# which API answered, not which field was read. `$author` comes from `gh pr view
# --json author`, which is GraphQL underneath and reports `app/dependabot`; REST
# (`gh api repos/{owner}/{repo}/pulls/N`) reports `dependabot[bot]`. Matching only
# the REST form meant this carve-out never fired, so every Dependabot PR was gated
# as a human PR — a gate it can never pass, because Copilot doesn't review them.
# Accept both rather than picking one, so the carve-out survives either API's
# spelling.
case "$author" in
dependabot\[bot\] | app/dependabot) allow ;;
esac

# (1) Copilot must not be mid-review.
[ "$pending" = "false" ] || deny "Copilot is still a requested reviewer on PR #$pr (review pending)"

# (2) Copilot's latest review must target the current head commit.
#
#     READ THIS VIA GraphQL, NOT REST. The REST reviews collection
#     (`repos/{owner}/{repo}/pulls/N/reviews`) lags badly for this bot: on PR #129
#     on 2026-09-15 it reported Copilot's latest review as ee1770c submitted at
#     14:45:44Z while GraphQL reported 1559962 at 18:43:14Z — nearly four hours and
#     nine reviews behind. Because this gate fails closed, reading the stale view
#     does not make it conservative, it makes it STUCK: every merge is denied with
#     "not on the current head" however clean the review is, and no amount of
#     re-requesting clears it. A gate that cannot pass gets switched off, which is
#     worse than the mistake it was written to prevent.
#
#     sort_by(submittedAt) first — response order isn't guaranteed either way.
#     `// empty` coerces the no-reviews case (null) to an empty string.
#     Mind the login spelling: GraphQL omits the `[bot]` suffix that REST carries
#     (see the Dependabot note above). Exact match — `contains` would match
#     unrelated logins.
last=$(gh api graphql -f owner="$owner" -f name="$name" -F number="$pr" \
	-f query='query($owner:String!,$name:String!,$number:Int!){repository(owner:$owner,name:$name){pullRequest(number:$number){reviews(last:100){nodes{author{login} commit{oid} submittedAt}}}}}' \
	--jq '[.data.repository.pullRequest.reviews.nodes[] | select(.author.login == "copilot-pull-request-reviewer")] | sort_by(.submittedAt) | last | .commit.oid // empty' 2>/dev/null) \
	|| deny "could not read reviews for PR #$pr (failing closed)"
[ -n "$last" ] || deny "Copilot has not reviewed PR #$pr yet"
[ "$last" = "$head" ] || deny "Copilot's latest review ($last) is not on the current head ($head) — re-review pending on PR #$pr"

# (3) Zero unresolved review threads. Fetch hasNextPage too and fail closed if a
#     PR ever exceeds one page (100) — we can't verify the tail otherwise.
#     owner/name were resolved up front (current repo).
threads=$(gh api graphql -f owner="$owner" -f name="$name" -F number="$pr" \
	-f query='query($owner:String!,$name:String!,$number:Int!){repository(owner:$owner,name:$name){pullRequest(number:$number){reviewThreads(first:100){nodes{isResolved} pageInfo{hasNextPage}}}}}' \
	--jq '.data.repository.pullRequest.reviewThreads | "\([.nodes[] | select(.isResolved == false)] | length) \(.pageInfo.hasNextPage)"' 2>/dev/null) \
	|| deny "could not read review threads for PR #$pr (failing closed)"
unresolved=${threads% *}
morepages=${threads#* }
[ "$morepages" = "false" ] || deny "PR #$pr has more than 100 review threads — cannot verify the tail (failing closed)"
[ "$unresolved" = "0" ] || deny "$unresolved unresolved review thread(s) on PR #$pr"

allow
