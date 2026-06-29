# Security hardening — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Apply the approved in-repo security hardenings from the audit — supply-chain (workflows), application-code defence-in-depth (with tests), and doc accuracy — with no behavioural regressions.

**Architecture:** Three independent tasks: (1) workflow/CI hardening (SHA pins, token-via-env, per-job permissions, explicit secrets); (2) C# app-code hardening (OAuth client_id clamp, error-URL stripping, a NoAuth+KeyVault startup guard, query-key escaping, a test-helper dispose nit) with xUnit tests; (3) CLAUDE.md doc corrections.

**Tech Stack:** GitHub Actions + actionlint, .NET 10 / C# / xUnit + FluentAssertions, `gh` CLI for resolving action SHAs.

## Global Constraints

- No behavioural regression: the deploy/release/scan workflows and all tools keep working; full xUnit suite green; `actionlint` clean.
- SHA pins: resolve each third-party action's tag to its real commit SHA at implementation time via `gh api repos/<owner>/<repo>/commits/<tag> --jq .sha` (or `git ls-remote`); never guess. Add a trailing `# vX.Y.Z` comment. Leave first-party `actions/*`, `github/codeql-action/*`, `dependabot/fetch-metadata` on tags.
- UK English; LF line endings. Build/run from repo root; do NOT prefix commands with `cd`.
- actionlint: `MSYS_NO_PATHCONV=1 docker run --rm -v "$(pwd -W):/repo" -w /repo rhysd/actionlint:latest -color <file>` (Docker is up).
- Do NOT change the deploy IAM / identity model, the auto-merge policy, or any live Azure/GitHub settings — out of scope (user decisions).

---

### Task 1: Workflow / supply-chain hardening

**Files:**
- Modify: `.github/workflows/deploy.yml`, `.github/workflows/release.yml`, `.github/workflows/ci.yml`, `.github/workflows/codeql.yml`, `.github/workflows/security-scan.yml`, `.github/workflows/pr-title.yml`, `.github/workflows/dependabot-auto-merge.yml`, `.github/workflows/cleanup-caches.yml` (only those containing the listed third-party actions).

- [ ] **Step 1: Resolve + apply SHA pins for third-party actions**

For each third-party action below, resolve the current tag to its commit SHA and replace the pin with `<owner>/<repo>@<40-char-sha> # <tag>`. Resolve with e.g. `gh api repos/aquasecurity/trivy-action/commits/v0.36.0 --jq .sha`.

Third-party actions to SHA-pin (find their occurrences across the workflow files):
- `aquasecurity/trivy-action@v0.36.0` (security-scan.yml, ×2)
- `mathieudutour/github-tag-action@v6.2` (release.yml)
- `dorny/test-reporter@v3` (ci.yml)
- `docker/login-action@v3` (deploy.yml)
- `docker/build-push-action@v6` (deploy.yml)
- `azure/login@v3` (deploy.yml)
- `amannn/action-semantic-pull-request@v6` (pr-title.yml)

Leave on tags (GitHub-controlled, lower risk): `actions/checkout`, `actions/setup-dotnet`, `actions/cache`, `actions/upload-artifact`, `github/codeql-action/*`, `dependabot/fetch-metadata`.

- [ ] **Step 2: `deploy.yml` — GHCR token via env (not inline) in `az acr import`**

In `.github/workflows/deploy.yml`, the "Import image into the private ACR" step currently passes `--password "${{ secrets.GITHUB_TOKEN }}"` inline. Add a step `env:` and reference it as a shell variable:
```yaml
      - name: Import image into the private ACR (server-side)
        env:
          GHCR_TOKEN: ${{ secrets.GITHUB_TOKEN }}
        run: |
          set -euo pipefail
          az acr import \
            --name "$ACR_NAME" \
            --source "${{ steps.vars.outputs.ghcr }}:${{ steps.vars.outputs.tag }}" \
            --image "$IMAGE_NAME:${{ steps.vars.outputs.tag }}" \
            --username "${{ github.actor }}" \
            --password "$GHCR_TOKEN" \
            --force
```

- [ ] **Step 3: Job-level permissions in `deploy.yml` and `dependabot-auto-merge.yml`**

In `deploy.yml`, remove the workflow-level `permissions:` block and add the same block under the `deploy:` job (above `runs-on:` is fine):
```yaml
jobs:
  deploy:
    name: Build, import to ACR, roll Container App
    permissions:
      id-token: write
      contents: read
      packages: write
    runs-on: ubuntu-latest
    environment: production
```
In `dependabot-auto-merge.yml`, remove the workflow-level `permissions:` block and add it under the `auto-merge:` job:
```yaml
jobs:
  auto-merge:
    if: ${{ github.event.pull_request.user.login == 'dependabot[bot]' }}
    permissions:
      contents: write
      pull-requests: write
    runs-on: ubuntu-latest
```

- [ ] **Step 4: `release.yml` — explicit secrets instead of `secrets: inherit`**

In `.github/workflows/release.yml`, the `deploy` caller job uses `secrets: inherit`. Replace with an explicit map:
```yaml
    secrets:
      AZURE_CLIENT_ID: ${{ secrets.AZURE_CLIENT_ID }}
      AZURE_TENANT_ID: ${{ secrets.AZURE_TENANT_ID }}
      AZURE_SUBSCRIPTION_ID: ${{ secrets.AZURE_SUBSCRIPTION_ID }}
```
(`GITHUB_TOKEN` is provided to the called workflow automatically; do not list it.)

- [ ] **Step 5: Validate + commit**

Run actionlint on every changed workflow (expect exit 0 each):
```
for f in deploy release ci codeql security-scan pr-title dependabot-auto-merge cleanup-caches; do
  MSYS_NO_PATHCONV=1 docker run --rm -v "$(pwd -W):/repo" -w /repo rhysd/actionlint:latest -color ".github/workflows/$f.yml" || echo "LINT FAIL: $f"
done
```
```powershell
git add .github/workflows
git commit -m @'
ci(security): SHA-pin third-party actions, env-pass GHCR token, per-job perms, explicit secrets

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_01RWMtSydEfRwSpxDHNY9eH3
'@
```

---

### Task 2: Application-code hardening (with tests)

**Files:**
- Create: `VitallyMcp/StartupGuards.cs`
- Modify: `VitallyMcp/Program.cs`, `VitallyMcp/VitallyService.cs`, `VitallyMcp.Tests/TestHelpers.cs`
- Test: `VitallyMcp.Tests/StartupGuardsTests.cs` (new), `VitallyMcp.Tests/VitallyServiceTests.cs` (additions)

**Interfaces:**
- Produces: `StartupGuards.EnsureSafeAuthConfig(bool noAuth, string? keyVaultUri)` — throws `InvalidOperationException` when `noAuth && keyVaultUri` is non-blank; otherwise returns void.

- [ ] **Step 1: NoAuth+KeyVault startup guard — failing test first**

Create `VitallyMcp.Tests/StartupGuardsTests.cs`:
```csharp
using FluentAssertions;
using VitallyMcp;

namespace VitallyMcp.Tests;

public class StartupGuardsTests
{
    [Fact]
    public void EnsureSafeAuthConfig_NoAuthWithKeyVault_Throws()
    {
        var act = () => StartupGuards.EnsureSafeAuthConfig(noAuth: true, keyVaultUri: "https://kv.vault.azure.net/");
        act.Should().Throw<InvalidOperationException>().WithMessage("*NoAuth*");
    }

    [Theory]
    [InlineData(false, "https://kv.vault.azure.net/")] // auth on + KV: fine (production)
    [InlineData(true, null)]                            // NoAuth + no KV: fine (local dev)
    [InlineData(true, "")]                              // NoAuth + blank KV: fine
    [InlineData(false, null)]                           // auth on + no KV: fine (dev key)
    public void EnsureSafeAuthConfig_SafeCombinations_DoNotThrow(bool noAuth, string? keyVaultUri)
    {
        var act = () => StartupGuards.EnsureSafeAuthConfig(noAuth, keyVaultUri);
        act.Should().NotThrow();
    }
}
```

- [ ] **Step 2: Run to verify fail**

Run: `dotnet test --filter "FullyQualifiedName~StartupGuardsTests" --nologo`
Expected: FAIL — `StartupGuards` not defined (compile error).

- [ ] **Step 3: Implement `StartupGuards`**

Create `VitallyMcp/StartupGuards.cs` (UK English):
```csharp
namespace VitallyMcp;

/// <summary>
/// Fail-fast configuration guards checked at startup, before the app serves traffic.
/// </summary>
public static class StartupGuards
{
    /// <summary>
    /// Refuses a configuration that disables authentication while a Key Vault is configured — that
    /// combination looks like a production deployment accidentally running unauthenticated. NoAuth is
    /// for local development only (which has no Key Vault and uses a development API key instead).
    /// </summary>
    public static void EnsureSafeAuthConfig(bool noAuth, string? keyVaultUri)
    {
        if (noAuth && !string.IsNullOrWhiteSpace(keyVaultUri))
        {
            throw new InvalidOperationException(
                "OAuth:NoAuth=true together with a Vitally:KeyVaultUri is refused — this looks like a " +
                "production deployment running unauthenticated. NoAuth is for local development only " +
                "(no Key Vault). Remove NoAuth in any environment that uses Key Vault.");
        }
    }
}
```

- [ ] **Step 4: Run to verify pass**

Run: `dotnet test --filter "FullyQualifiedName~StartupGuardsTests" --nologo`
Expected: PASS (5 cases).

- [ ] **Step 5: Call the guard from `Program.cs`**

In `VitallyMcp/Program.cs`, just after `var noAuth = oauthSection.GetValue<bool>("NoAuth");` (around line 70), add:
```csharp
// Fail fast on an unauthenticated production-looking config (NoAuth + Key Vault).
StartupGuards.EnsureSafeAuthConfig(noAuth, vitallySection["KeyVaultUri"]);
```
(`vitallySection` is the existing `builder.Configuration.GetSection("Vitally")` used elsewhere in Program.cs — reuse it; if the local variable has a different name, use the existing accessor that Step's neighbours use to read `KeyVaultUri`.)

- [ ] **Step 6: OAuth token proxy — clamp `client_id`**

In `VitallyMcp/Program.cs`, in the `/oauth/token` handler, inside the existing `if (!string.IsNullOrWhiteSpace(o.SharedClientSecret))` block (the confidential-client secret injection, ~line 360-364), add the client_id clamp after the secret injection:
```csharp
    if (!string.IsNullOrWhiteSpace(o.SharedClientSecret))
    {
        pairs.RemoveAll(p => p.Key == "client_secret");
        pairs.Add(new KeyValuePair<string, string>("client_secret", o.SharedClientSecret));
        // Never pair the injected secret with a caller-supplied client_id — clamp to our shared app.
        pairs.RemoveAll(p => p.Key == "client_id");
        pairs.Add(new KeyValuePair<string, string>("client_id", o.SharedClientId));
    }
```
(No dedicated unit test — the endpoint forwards to upstream Auth0 and isn't unit-isolatable without a large refactor; it is exercised by the existing `OAuthProxyEndpointsTests` wiring and verified by inspection. The reviewer may flag this; that is acceptable.)

- [ ] **Step 7: VitallyService — strip URL from surfaced error + escape query keys (failing tests first)**

Add to `VitallyMcp.Tests/VitallyServiceTests.cs` (inside the class):
```csharp
[Fact]
public async Task SendAsync_OnError_MessageHasStatusAndBodyButNotUrl()
{
    using var client = TestHelpers.CreateMockHttpClient("{\"message\":\"externalId is required\"}", HttpStatusCode.BadRequest);
    var service = TestHelpers.BuildVitallyService(client);

    var act = async () => await service.GetResourceByIdAsync("accounts", "acc-123");

    var ex = await act.Should().ThrowAsync<HttpRequestException>();
    ex.Which.Message.Should().Contain("400");
    ex.Which.Message.Should().Contain("externalId is required");
    ex.Which.Message.Should().NotContain("acc-123");           // resource path/id not leaked
    ex.Which.Message.Should().NotContainEquivalentOf("vitally"); // base host not leaked
}

[Fact]
public async Task GetRawAsync_EscapesQueryKeysAndValues()
{
    var (client, handler) = TestHelpers.CreateMockHttpClientWithHandler("[]");
    var service = TestHelpers.BuildVitallyService(client);

    await service.GetRawAsync("customFields", new Dictionary<string, string> { ["a b"] = "c d" });

    handler.Protected().Verify("SendAsync", Times.Once(),
        ItExpr.Is<HttpRequestMessage>(r => r.RequestUri!.Query.Contains("a%20b=c%20d")),
        ItExpr.IsAny<CancellationToken>());
}
```
Run: `dotnet test --filter "FullyQualifiedName~VitallyServiceTests.SendAsync_OnError_MessageHasStatusAndBodyButNotUrl|FullyQualifiedName~VitallyServiceTests.GetRawAsync_EscapesQueryKeysAndValues" --nologo`
Expected: FAIL (message still contains the path/host; query key not escaped).

- [ ] **Step 8: Implement the VitallyService changes**

In `VitallyMcp/VitallyService.cs`, change the error message in `SendAsync` (drop `for {method} {url}` — the URL is already recorded by `_audit.LogAction`/`LogDenied` server-side):
```csharp
            throw new HttpRequestException(
                $"Vitally API returned {(int)response.StatusCode} {response.ReasonPhrase}. Body: {bodySnippet}",
                inner: null,
                statusCode: response.StatusCode);
```
And in `GetRawAsync`, escape the key as well as the value:
```csharp
            var query = string.Join("&", queryParams.Select(kv => $"{Uri.EscapeDataString(kv.Key)}={Uri.EscapeDataString(kv.Value)}"));
```
(No change needed in `GetOrganizationSummaryAsync` — its `{ "error": ex.Message }` sections now inherit the URL-free message automatically.)

- [ ] **Step 9: Run to verify pass**

Run: `dotnet test --filter "FullyQualifiedName~VitallyServiceTests.SendAsync_OnError_MessageHasStatusAndBodyButNotUrl|FullyQualifiedName~VitallyServiceTests.GetRawAsync_EscapesQueryKeysAndValues" --nologo`
Expected: PASS. (If a pre-existing test asserted the URL was present in the error message, update it to match the new URL-free message — the body+status assertions stay.)

- [ ] **Step 10: TestHelpers dispose nit**

In `VitallyMcp.Tests/TestHelpers.cs`, find the mock-HttpClient helper that CodeQL flags with `cs/local-not-disposed` (the one lacking the suppression the others have) and add the same attributes used on `CreateMockHttpClient` in that file:
```csharp
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Reliability", "CA2000",
        Justification = "Test mock — HttpResponseMessage lifetime is bounded by the test run.")]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Reliability", "cs/local-not-disposed",
        Justification = "Test mock — owned by the Moq setup, bounded by the test method.")]
```
(If all helpers already carry it and the flagged one is `RoutedClient`/`BuildRoutedClient` in `VitallyServiceTests.cs`/`SummaryToolsTests.cs`, apply the same two attributes there instead. Identify the exact flagged member from the CodeQL alert / by matching the pattern.)

- [ ] **Step 11: Full suite + commit**

Run: `dotnet test VitallyMcp.sln -c Debug --nologo --verbosity minimal` — all green.
```powershell
git add VitallyMcp/StartupGuards.cs VitallyMcp/Program.cs VitallyMcp/VitallyService.cs VitallyMcp.Tests/StartupGuardsTests.cs VitallyMcp.Tests/VitallyServiceTests.cs VitallyMcp.Tests/TestHelpers.cs
git commit -m @'
harden(app): NoAuth+KeyVault startup guard, OAuth client_id clamp, no URL in surfaced errors, escape query keys

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_01RWMtSydEfRwSpxDHNY9eH3
'@
```

---

### Task 3: Docs — CLAUDE.md accuracy

**Files:**
- Modify: `CLAUDE.md`

- [ ] **Step 1: Correct the Deployment section**

In `CLAUDE.md`, in the Deployment area:
- The IaC row/paragraph that says Terraform lives in a **separate repo** is wrong — change it to state the IaC is **in this repo at `infra/terraform/`** (adopted via import blocks; see `infra/terraform/README.md`).
- The ACR row that says **Basic SKU** is wrong — change it to **Premium** (required for private endpoints + CMK).

Make the minimal edits to those two facts; keep the surrounding table/structure.

- [ ] **Step 2: Commit**

```powershell
git add CLAUDE.md
git commit -m @'
docs: correct CLAUDE.md — Terraform is in-repo (infra/terraform), ACR is Premium

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_01RWMtSydEfRwSpxDHNY9eH3
'@
```

---

## Final verification

- [ ] `dotnet test VitallyMcp.sln -c Debug --nologo --verbosity minimal` — all green.
- [ ] `actionlint` clean on all changed workflows; every SHA pin resolves to a real commit (`gh api repos/<owner>/<repo>/commits/<sha>` 200).
- [ ] Open a PR from `feature/security-hardening` into `main`, **assigned to the user**; CI green; resolve Copilot/CodeQL threads; merge (squash).
- [ ] Sanity: the deploy/release workflows still parse and (since pins/permissions only changed) remain functional — the next scheduled/dispatched run is the live confirmation; no live run is required to merge.
