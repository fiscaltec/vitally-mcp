<!--
Title format: <type>: <short description>
Allowed types: feat, fix, docs, style, refactor, perf, test, build, ci, chore, revert
Examples:
  feat: add EU region support
  fix: handle 429 retries when Retry-After is absent
  docs: refresh README install instructions for Claude Code
-->

## Summary

<!-- 1-3 bullet points describing what changes and why. -->

-
-

## Breaking changes

<!-- Anything that requires action from existing users (env var changes,
     default behaviour shifts, removed tools, renamed config keys). Delete
     this section if there are none. -->

-

## Test plan

- [ ] `dotnet test VitallyMcp.sln -c Release` passes locally
- [ ] CI is green on this PR
- [ ] Manual smoke test against a real Vitally tenant (note region used):
  - [ ] EU:
  - [ ] US:

## Documentation

- [ ] `CLAUDE.md` reflects the change (if applicable)
- [ ] `README.md` reflects the change (if applicable)
- [ ] `CHANGELOG.md` updated under `[Unreleased]`

## Linked issues

<!-- e.g. Closes #123, Fixes #456 -->
