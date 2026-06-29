# Security hardening pass

**Date:** 2026-06-29
**Status:** Design (approved) — ready for implementation plan.
**Origin:** A four-dimension security audit (application code, CI/CD supply chain, Azure IAM,
repo/secrets/data) of the Vitally MCP server. The audit found **no Critical issues** and a strong
baseline. This spec captures the **in-repo hardenings the user approved** to implement now. Items
requiring prod-IAM/GitHub-settings/Vitally action were triaged out (the user chose to leave the deploy
IAM as-is; the git-history key check and GitHub environment settings are the user's to action).

## 1. Scope (approved in-repo hardenings)

Three areas; all changes stay in the repo (no live Azure/GitHub-settings mutations).

### A. Workflows / CI-CD supply chain
1. **SHA-pin third-party actions** (with a trailing `# vX.Y.Z` comment) in all workflows:
   `aquasecurity/trivy-action`, `mathieudutour/github-tag-action`, `dorny/test-reporter`,
   `docker/login-action`, `docker/build-push-action`, `azure/login`,
   `amannn/action-semantic-pull-request`. First-party `actions/*` and `github/codeql-action/*` and
   `dependabot/fetch-metadata` may stay on tags (GitHub-controlled). Resolve each tag to its commit
   SHA at implementation time (do not guess). Dependabot's `github-actions` ecosystem keeps SHA pins
   current.
2. **`az acr import` token via env, not inline interpolation** (`deploy.yml`): pass `GITHUB_TOKEN`
   through the step `env:` and reference it as a shell variable in the `--password` argument, so the
   secret is not rendered into the literal step command. (It remains masked in logs regardless; this
   is defence-in-depth + best practice.)
3. **Job-level permissions** instead of workflow-level in `deploy.yml` and
   `dependabot-auto-merge.yml` (consistency + future-proofing; functionally equivalent for the
   current single-job shape but prevents a future added job inheriting broad scopes).
4. **Explicit secret enumeration** in `release.yml`'s reusable-deploy call: replace `secrets: inherit`
   with an explicit `secrets:` map of only `AZURE_CLIENT_ID`, `AZURE_TENANT_ID`,
   `AZURE_SUBSCRIPTION_ID` (and `GITHUB_TOKEN` is available to the called workflow automatically).

### B. Application code (C#) — each with a test
5. **Clamp `/oauth/token` `client_id` to `SharedClientId`** (`Program.cs` token proxy): before
   forwarding to Auth0 with the injected `SharedClientSecret`, remove any caller-supplied `client_id`
   and set it to the configured `SharedClientId`, so the proxy never pairs the secret with a foreign
   client id (don't rely on Auth0 to reject the mismatch).
6. **Strip the Vitally URL from client-surfaced error messages** (`VitallyService.SendAsync`): the
   `HttpRequestException` message currently includes the full upstream URL. Keep the status code and a
   (bounded) response-body snippet for the LLM to act on, but remove the full URL from the message
   surfaced to the client; log the full URL/details server-side via `ILogger`. Apply the same
   trimming to the `{ "error": ... }` sections built in `GetOrganizationSummaryAsync`.
7. **Fail-fast startup guard for `NoAuth` + Key Vault** (`Program.cs` / options validation): if
   `OAuth:NoAuth` is true **and** `Vitally:KeyVaultUri` is set (a production-looking config), refuse
   to start (throw) — prevents an accidental unauthenticated production deployment. `NoAuth` with no
   Key Vault (local dev) stays allowed.
8. **Escape query-parameter keys in `GetRawAsync`** (`VitallyService.cs`): the helper escapes values
   but not keys; escape keys with `Uri.EscapeDataString` for consistency with `BuildListUrl` /
   `SearchCustomObjectInstancesAsync` (latent-risk hygiene; no current untrusted key).
9. **Dispose hygiene in `TestHelpers`**: clear the outstanding CodeQL `cs/local-not-disposed` warning
   on the test mock helper (add the same `SuppressMessage`/`using` pattern already used by the other
   helpers in that file). Test-only.

### C. Docs
10. **`CLAUDE.md` accuracy:** correct the Deployment section — IaC **is** in this repo at
    `infra/terraform/` (not a separate repo), and the ACR SKU is **Premium** (not Basic). These are
    security-relevant operational facts (an operator could otherwise provision a Basic ACR that can't
    do private endpoints, or miss reviewing Terraform changes).

## 2. Explicitly out of scope (triaged, user-actioned or accepted)

- Deploy-identity least-privilege (custom roles / dedicated identity) — **user chose to leave IAM
  as-is**; documented accepted trade-off.
- GitHub `production` environment branch-restriction; `required_approving_review_count` — user's
  settings call (the 0-approval + auto-merge model is their deliberate SP2 choice).
- Confirming the partial `sk_live_51f45f82…` in deleted `QUICKSTART.md` git history is rotated/invalid
  in Vitally — user action (cannot be done from the repo; GitHub secret scanning shows 0 alerts).
- Remote Terraform state backend, scanner image pinning, NSG on `snet-app`, secret-expiry attribute
  verification — infra-ops items for the user.
- Auto-merge of major Dependabot bumps — deliberate user decision (CI is the gate).
- The 5 glibc base-image CVEs (Trivy `warning`, below the High/Critical gate) — picked up on rebuild
  when Microsoft publishes patched base images; not blocking, monitored via the Security tab.
- Auth0 `SharedClientId` in committed config — a public identifier, not a secret; left as-is.

## 3. Testing

- **Workflows (A):** `actionlint` clean on every changed workflow; SHA pins resolve to real commits
  (verified at implementation). No behavioural change intended; the existing deploy/release proofs
  stand.
- **App code (B):** xUnit tests for each behavioural change — `client_id` clamp (token proxy forwards
  the configured id even when a foreign one is supplied), error-message no longer contains the URL
  (but still contains status + body), the `NoAuth`+`KeyVault` startup guard throws, `GetRawAsync`
  escapes keys. Full suite stays green.
- **Docs (C):** N/A.

## 4. Acceptance criteria

- All 10 approved items implemented; full xUnit suite green; `actionlint` clean.
- No third-party action left on a mutable tag; the GHCR token is no longer inline-interpolated into a
  run command; `release.yml` enumerates secrets explicitly.
- A `NoAuth=true` + `KeyVaultUri` configuration fails to start; the OAuth token proxy clamps
  `client_id`; surfaced Vitally errors no longer leak the full URL.
- `CLAUDE.md` Deployment section is factually correct (in-repo Terraform, Premium ACR).
- Delivered as PR(s) into `main`, assigned to the user, through the standard CI + review gate.
