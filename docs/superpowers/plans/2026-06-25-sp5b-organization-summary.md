# SP5b — Composite `Get_organization_summary` — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add a composite `Get_organization_summary(organizationId)` tool that returns one organisation, its curated rollup traits, its open goals, and its product feedback in a single call — collapsing the value report's ~10+ Vitally calls to one.

**Architecture:** A generic, tenant-agnostic orchestration method `VitallyService.GetOrganizationSummaryAsync(organizationId, traitsCsv, goalsObjectName, productFeedbackObjectName)` makes 4 upstream calls (org get-by-id with traits; one `customObjects` list to resolve both object names→ids; two scoped instance searches) and assembles a `{ organization, goals, productFeedback }` JSON object with per-section error capture. A thin `SummaryTools.Get_organization_summary` tool exposes it and owns the tenant-specific defaults (curated trait CSV + the two object names) as constants.

**Tech Stack:** .NET 10, C#, `ModelContextProtocol(.AspNetCore) 1.3.0`, `System.Text.Json` (incl. `System.Text.Json.Nodes`), xUnit + FluentAssertions + Moq.

**Branch:** `feature/sp5b-organization-summary` (created off `main`; spec committed there).

**Spec:** [SP5b design](../specs/2026-06-25-sp5b-organization-summary-design.md)

## Global Constraints

- UK English in comments/docs. Repo uses **LF** line endings (`.gitattributes` present).
- No hardcoded custom-object **ids** — resolve by name at runtime. Tenant-specific literals (curated trait CSV, default object names) live ONLY in `SummaryTools`; `GetOrganizationSummaryAsync` takes them as parameters and contains no tenant literals.
- A failure of one sub-section (`goals`/`productFeedback`) must NOT sink the whole summary — that section becomes `{ "error": "<message>" }`. A failed org get-by-id propagates (surfaced by the existing `ToolErrorResult` call-tool filter).
- Existing `VitallyService` public methods do NOT take a `CancellationToken`; match that — do not add a `ct` parameter to the new method.
- Build/run from repo root; do NOT prefix commands with `cd`.
- Instance searches reuse the existing `SearchCustomObjectInstancesAsync` (which already applies the `customObjectInstances` default-field projection and wraps the bare array as `{results:[...]}`).

---

### Task 1: `VitallyService.GetOrganizationSummaryAsync` + private helpers

**Files:**
- Modify: `VitallyMcp/VitallyService.cs`
- Test: `VitallyMcp.Tests/VitallyServiceTests.cs`

**Interfaces:**
- Consumes (existing, confirmed signatures):
  - `Task<string> GetResourceByIdAsync(string resourceType, string id, string? fields = null, string? traits = null)`
  - `Task<string> GetResourcesAsync(string resourceType, int limit = 20, string? from = null, string? fields = null, string? sortBy = null, Dictionary<string,string>? additionalParams = null, string? traits = null, string? defaultsKey = null)`
  - `Task<string> SearchCustomObjectInstancesAsync(string customObjectId, IReadOnlyDictionary<string,string> criteria, string? fields = null, string? traits = null)`
  - `private static readonly Dictionary<string,string[]> ResourceDefaultFields` (has key `"organizations"`).
- Produces (later task relies on):
  - `Task<string> GetOrganizationSummaryAsync(string organizationId, string? traitsCsv, string goalsObjectName, string productFeedbackObjectName)` — returns a JSON object string `{ "organization": {...}, "goals": {...}, "productFeedback": {...} }`.

- [ ] **Step 1: Write the failing tests**

Append to `VitallyMcp.Tests/VitallyServiceTests.cs` (inside the existing test class). These use an inline URL-routed Moq handler so the 4 calls can return different bodies regardless of order. Add `using System.Text.Json;` / `using Moq;` / `using Moq.Protected;` / `using System.Net;` if not already present at the top of the file (most are).

```csharp
// ---- SP5b: Get_organization_summary composite ----

// Routes mocked responses by request-URL substring; later setups win in Moq so the most
// specific (instances/search) is registered last. Bodies mirror the REAL Vitally shapes:
// org get = single object; customObjects list = {results:[...]}; instance search = BARE ARRAY.
private static (HttpClient client, Mock<HttpMessageHandler> handler) RoutedClient(
    IReadOnlyList<(string urlContains, HttpStatusCode status, string body)> routes)
{
    var mock = new Mock<HttpMessageHandler>();
    foreach (var route in routes)
    {
        var captured = route;
        mock.Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.Is<HttpRequestMessage>(r => r.RequestUri!.AbsoluteUri.Contains(captured.urlContains)),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(() => new HttpResponseMessage { StatusCode = captured.status, Content = new StringContent(captured.body) });
    }
    return (new HttpClient(mock.Object), mock);
}

private const string SummaryOrgJson = """
{ "id": "org-1", "name": "Acme", "healthScore": 156, "mrr": 1000,
  "traits": { "vitally.custom.countAllSupportTickets": 10, "vitally.custom.countOfOpenCustomerGoals": 5 } }
""";

private const string SummaryCustomObjectsJson = """
{ "results": [
  { "id": "co-goals", "name": "customerGoals" },
  { "id": "co-feedback", "name": "productFeedback" }
] }
""";

private const string SummaryGoalsArrayJson = """
[ { "id": "g-1", "name": "Goal One", "organizationId": "org-1" } ]
""";

private const string SummaryFeedbackArrayJson = "[]";

[Fact]
public async Task GetOrganizationSummary_HappyPath_AssemblesOrgGoalsAndFeedback()
{
    var (client, _) = RoutedClient(new[]
    {
        ("/resources/organizations/org-1", HttpStatusCode.OK, SummaryOrgJson),
        ("/resources/customObjects?", HttpStatusCode.OK, SummaryCustomObjectsJson),
        ("/resources/customObjects/co-goals/instances/search", HttpStatusCode.OK, SummaryGoalsArrayJson),
        ("/resources/customObjects/co-feedback/instances/search", HttpStatusCode.OK, SummaryFeedbackArrayJson),
    });
    var service = TestHelpers.BuildVitallyService(client);

    var json = await service.GetOrganizationSummaryAsync(
        "org-1", "vitally.custom.countAllSupportTickets,vitally.custom.countOfOpenCustomerGoals",
        "customerGoals", "productFeedback");

    using var doc = JsonDocument.Parse(json);
    var root = doc.RootElement;
    root.GetProperty("organization").GetProperty("name").GetString().Should().Be("Acme");
    root.GetProperty("organization").GetProperty("traits")
        .GetProperty("vitally.custom.countAllSupportTickets").GetInt32().Should().Be(10);
    root.GetProperty("goals").GetProperty("results").GetArrayLength().Should().Be(1);
    root.GetProperty("goals").GetProperty("results")[0].GetProperty("id").GetString().Should().Be("g-1");
    root.GetProperty("productFeedback").GetProperty("results").GetArrayLength().Should().Be(0);
}

[Fact]
public async Task GetOrganizationSummary_WhenAGoalsSearchFails_OnlyThatSectionIsAnError()
{
    var (client, _) = RoutedClient(new[]
    {
        ("/resources/organizations/org-1", HttpStatusCode.OK, SummaryOrgJson),
        ("/resources/customObjects?", HttpStatusCode.OK, SummaryCustomObjectsJson),
        ("/resources/customObjects/co-goals/instances/search", HttpStatusCode.InternalServerError, "{\"message\":\"boom\"}"),
        ("/resources/customObjects/co-feedback/instances/search", HttpStatusCode.OK, SummaryFeedbackArrayJson),
    });
    var service = TestHelpers.BuildVitallyService(client);

    var json = await service.GetOrganizationSummaryAsync("org-1", "vitally.custom.countAllSupportTickets", "customerGoals", "productFeedback");

    using var doc = JsonDocument.Parse(json);
    var root = doc.RootElement;
    root.GetProperty("organization").GetProperty("name").GetString().Should().Be("Acme");
    root.GetProperty("goals").TryGetProperty("error", out _).Should().BeTrue();
    root.GetProperty("productFeedback").GetProperty("results").GetArrayLength().Should().Be(0);
}

[Fact]
public async Task GetOrganizationSummary_WhenObjectNameNotFound_SectionIsAnError()
{
    var (client, _) = RoutedClient(new[]
    {
        ("/resources/organizations/org-1", HttpStatusCode.OK, SummaryOrgJson),
        ("/resources/customObjects?", HttpStatusCode.OK, SummaryCustomObjectsJson),
        ("/resources/customObjects/co-feedback/instances/search", HttpStatusCode.OK, SummaryFeedbackArrayJson),
    });
    var service = TestHelpers.BuildVitallyService(client);

    // Ask for a goals object name that is not in the customObjects list.
    var json = await service.GetOrganizationSummaryAsync("org-1", "vitally.custom.npsScore", "noSuchGoals", "productFeedback");

    using var doc = JsonDocument.Parse(json);
    var goalsError = doc.RootElement.GetProperty("goals").GetProperty("error").GetString();
    goalsError.Should().Contain("noSuchGoals");
    doc.RootElement.GetProperty("productFeedback").GetProperty("results").GetArrayLength().Should().Be(0);
}

[Fact]
public async Task GetOrganizationSummary_ScopesInstanceSearchToOrganizationId()
{
    var (client, handler) = RoutedClient(new[]
    {
        ("/resources/organizations/org-1", HttpStatusCode.OK, SummaryOrgJson),
        ("/resources/customObjects?", HttpStatusCode.OK, SummaryCustomObjectsJson),
        ("/resources/customObjects/co-goals/instances/search", HttpStatusCode.OK, SummaryGoalsArrayJson),
        ("/resources/customObjects/co-feedback/instances/search", HttpStatusCode.OK, SummaryFeedbackArrayJson),
    });
    var service = TestHelpers.BuildVitallyService(client);

    await service.GetOrganizationSummaryAsync("org-1", "vitally.custom.npsScore", "customerGoals", "productFeedback");

    handler.Protected().Verify("SendAsync", Times.Once(),
        ItExpr.Is<HttpRequestMessage>(r =>
            r.RequestUri!.AbsoluteUri.Contains("/customObjects/co-goals/instances/search") &&
            r.RequestUri!.AbsoluteUri.Contains("organizationId=org-1")),
        ItExpr.IsAny<CancellationToken>());
}
```

- [ ] **Step 2: Run to verify they fail**

Run: `dotnet test --filter "FullyQualifiedName~VitallyServiceTests.GetOrganizationSummary" --nologo`
Expected: FAIL — `GetOrganizationSummaryAsync` not defined (compile error).

- [ ] **Step 3: Implement the method + helpers**

In `VitallyMcp/VitallyService.cs`, ensure the namespaces `System.Text.Json` and `System.Text.Json.Nodes` are imported at the top (add `using System.Text.Json.Nodes;` if absent — `System.Text.Json` is already there at line 3).

Add these members to the `VitallyService` class (place after `GetCustomObjectInstanceByIdAsync`, around line 311):

```csharp
/// <summary>
/// Composite read: returns one organisation with its curated rollup traits, plus its open goals and
/// product-feedback custom-object instances, in a single response. Generic and tenant-agnostic —
/// the curated trait set and the two custom-object names are supplied by the caller (the tool layer
/// owns those tenant-specific defaults). Makes 4 upstream calls: org get-by-id (with traits), one
/// customObjects list to resolve both object names to ids, and two organisation-scoped instance
/// searches. Goals/product-feedback are each captured independently: a failure of one (object name
/// not found, upstream error, malformed body) yields a { "error": ... } for that section only, so a
/// single sub-failure never sinks the whole summary. A failed org get-by-id propagates (no summary
/// without it).
/// </summary>
public async Task<string> GetOrganizationSummaryAsync(
    string organizationId, string? traitsCsv, string goalsObjectName, string productFeedbackObjectName)
{
    // Organisation: default org fields PLUS traits, filtered to the curated/overridden set.
    var orgFields = string.Join(",", ResourceDefaultFields["organizations"]) + ",traits";
    var orgJson = await GetResourceByIdAsync("organizations", organizationId, orgFields, traitsCsv);

    // Resolve both object names to ids in one list call. A failure here is recorded and surfaces as a
    // per-section error below (rather than throwing and losing the organisation we already fetched).
    Dictionary<string, string> nameToId;
    string? resolveError = null;
    try
    {
        nameToId = await ResolveCustomObjectIdsAsync();
    }
    catch (Exception ex)
    {
        nameToId = new Dictionary<string, string>(StringComparer.Ordinal);
        resolveError = ex.Message;
    }

    var goals = await BuildInstanceSectionAsync(nameToId, goalsObjectName, organizationId, resolveError);
    var productFeedback = await BuildInstanceSectionAsync(nameToId, productFeedbackObjectName, organizationId, resolveError);

    var root = new JsonObject
    {
        ["organization"] = JsonNode.Parse(orgJson),
        ["goals"] = goals,
        ["productFeedback"] = productFeedback,
    };
    return root.ToJsonString();
}

// Fetches the customObjects catalogue (single list call) and maps name -> id.
private async Task<Dictionary<string, string>> ResolveCustomObjectIdsAsync()
{
    var json = await GetResourcesAsync("customObjects", limit: 100);
    var map = new Dictionary<string, string>(StringComparer.Ordinal);
    using var doc = JsonDocument.Parse(json);
    if (doc.RootElement.TryGetProperty("results", out var results) && results.ValueKind == JsonValueKind.Array)
    {
        foreach (var element in results.EnumerateArray())
        {
            if (element.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.String &&
                element.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String)
            {
                map[name.GetString()!] = id.GetString()!;
            }
        }
    }
    return map;
}

// Produces one summary section: the organisation-scoped instances of the named custom object, or a
// { "error": ... } node if the object name can't be resolved or the search fails.
private async Task<JsonNode> BuildInstanceSectionAsync(
    IReadOnlyDictionary<string, string> nameToId, string objectName, string organizationId, string? resolveError)
{
    try
    {
        if (resolveError is not null)
        {
            throw new InvalidOperationException($"could not resolve custom objects: {resolveError}");
        }
        if (!nameToId.TryGetValue(objectName, out var objectId))
        {
            throw new InvalidOperationException($"custom object '{objectName}' not found");
        }
        var json = await SearchCustomObjectInstancesAsync(
            objectId, new Dictionary<string, string> { ["organizationId"] = organizationId });
        return JsonNode.Parse(json)!;
    }
    catch (Exception ex)
    {
        return new JsonObject { ["error"] = ex.Message };
    }
}
```

- [ ] **Step 4: Run to verify pass**

Run: `dotnet test --filter "FullyQualifiedName~VitallyServiceTests.GetOrganizationSummary" --nologo`
Expected: PASS (4 tests).

- [ ] **Step 5: Commit**

```powershell
git add VitallyMcp/VitallyService.cs VitallyMcp.Tests/VitallyServiceTests.cs
git commit -m @'
feat(summary): VitallyService.GetOrganizationSummaryAsync composite (SP5b)

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_01RWMtSydEfRwSpxDHNY9eH3
'@
```

---

### Task 2: `SummaryTools.Get_organization_summary` tool + tenant defaults

**Files:**
- Create: `VitallyMcp/Tools/SummaryTools.cs`
- Test: `VitallyMcp.Tests/Tools/SummaryToolsTests.cs`

**Interfaces:**
- Consumes: `VitallyService.GetOrganizationSummaryAsync(string, string?, string, string)` (Task 1).
- Produces:
  - `SummaryTools.DefaultGoalsObjectName` / `DefaultProductFeedbackObjectName` / `DefaultRollupTraits` (`public const string`).
  - `Task<string> SummaryTools.Get_organization_summary(VitallyService, string organizationId, string? traits = null, string? goalsObjectName = null, string? productFeedbackObjectName = null)`.

- [ ] **Step 1: Write the failing tests**

Create `VitallyMcp.Tests/Tools/SummaryToolsTests.cs`:

```csharp
using System.Text.Json;
using FluentAssertions;
using VitallyMcp;
using VitallyMcp.Tools;

namespace VitallyMcp.Tests.Tools;

public class SummaryToolsTests
{
    [Theory]
    [InlineData("vitally.custom.countAllSupportTickets")]
    [InlineData("vitally.custom.countOfOpenCustomerGoals")]
    [InlineData("vitally.custom.npsScore")]
    [InlineData("sfdc.Customer_Health_Score__c")]
    public void DefaultRollupTraits_ContainsKeyRollupPaths(string path)
    {
        SummaryTools.DefaultRollupTraits.Should().Contain(path);
    }

    [Fact]
    public void DefaultObjectNames_AreCustomerGoalsAndProductFeedback()
    {
        SummaryTools.DefaultGoalsObjectName.Should().Be("customerGoals");
        SummaryTools.DefaultProductFeedbackObjectName.Should().Be("productFeedback");
    }

    [Fact]
    public async Task Get_organization_summary_ReturnsComposite_UsingDefaults()
    {
        // Routes by URL substring; bodies mirror real shapes (org single object; customObjects
        // {results}; instance search BARE ARRAY).
        var client = BuildRoutedClient();
        var service = TestHelpers.BuildVitallyService(client);

        var json = await SummaryTools.Get_organization_summary(service, "org-1");

        using var doc = JsonDocument.Parse(json);
        doc.RootElement.GetProperty("organization").GetProperty("name").GetString().Should().Be("Acme");
        doc.RootElement.GetProperty("goals").GetProperty("results").GetArrayLength().Should().Be(1);
        doc.RootElement.TryGetProperty("productFeedback", out _).Should().BeTrue();
    }

    private static HttpClient BuildRoutedClient()
    {
        var mock = new Moq.Mock<HttpMessageHandler>();
        void Route(string contains, string body) =>
            mock.Protected().Setup<Task<HttpResponseMessage>>(
                    "SendAsync",
                    Moq.Protected.ItExpr.Is<HttpRequestMessage>(r => r.RequestUri!.AbsoluteUri.Contains(contains)),
                    Moq.Protected.ItExpr.IsAny<CancellationToken>())
                .ReturnsAsync(() => new HttpResponseMessage { StatusCode = System.Net.HttpStatusCode.OK, Content = new StringContent(body) });

        Route("/resources/organizations/org-1", """{ "id":"org-1","name":"Acme","traits":{ "vitally.custom.npsScore": 9 } }""");
        Route("/resources/customObjects?", """{ "results":[ {"id":"co-goals","name":"customerGoals"}, {"id":"co-feedback","name":"productFeedback"} ] }""");
        Route("/resources/customObjects/co-goals/instances/search", """[ {"id":"g-1","name":"Goal","organizationId":"org-1"} ]""");
        Route("/resources/customObjects/co-feedback/instances/search", "[]");
        return new HttpClient(mock.Object);
    }
}
```

Add `using Moq.Protected;` at the top if you prefer the unqualified `ItExpr` form; the code above qualifies it inline so it compiles either way.

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test --filter "FullyQualifiedName~SummaryToolsTests" --nologo`
Expected: FAIL — `SummaryTools` / `Tools.SummaryTools` not defined (compile error).

- [ ] **Step 3: Implement the tool**

Create `VitallyMcp/Tools/SummaryTools.cs` (UK English; mirrors the attribute pattern in `CustomObjectsTools.cs`):

```csharp
using System.ComponentModel;
using ModelContextProtocol.Server;

namespace VitallyMcp.Tools;

/// <summary>
/// Composite, read-only summary tool. Collapses the common "give me everything about this customer"
/// shape into one call. Owns the tenant-specific policy (the curated rollup-trait set and the two
/// custom-object names) as constants; the orchestration in
/// <see cref="VitallyService.GetOrganizationSummaryAsync"/> is generic and takes these as arguments,
/// so retuning them — or promoting them to configuration for a second tenant — touches only this file.
/// </summary>
[McpServerToolType]
public static class SummaryTools
{
    /// <summary>Default custom object treated as the customer's goals (resolved to an id by name at runtime).</summary>
    public const string DefaultGoalsObjectName = "customerGoals";

    /// <summary>Default custom object treated as the customer's product feedback (resolved by name at runtime).</summary>
    public const string DefaultProductFeedbackObjectName = "productFeedback";

    /// <summary>
    /// Curated organisation rollup traits surfaced by default, as full trait-key paths (the org
    /// traits object is keyed by path, e.g. <c>vitally.custom.countAllSupportTickets</c>). Tuned to
    /// the FISCAL tenant; overridable per call via the <c>traits</c> parameter.
    /// </summary>
    public const string DefaultRollupTraits =
        "vitally.custom.countAllSupportTickets,vitally.custom.countAllProductFeedback," +
        "vitally.custom.countOfOpenCustomerGoals,vitally.custom.openZendeskTickets," +
        "vitally.custom.closedZendeskTickets,vitally.custom.npsScore,vitally.custom.npsGroup," +
        "vitally.custom.lastNpsFeedbackRollup,sfdc.Customer_Health_Score__c," +
        "sfdc.Health_Score_Status__c,sfdc.Renew_Date__c,vitally.custom.mostRecentRenewalDate";

    [McpServerTool(Name = "Get_organization_summary", Title = "Get organisation summary", ReadOnly = true, Destructive = false),
     Description("One-call customer summary for an organisation: the organisation with its curated rollup traits (support-ticket / product-feedback / open-goal counts, NPS, health, renewal), plus its open goals and product-feedback custom-object instances. Collapses the ~10+ calls a full customer review otherwise needs. Returns { organization, goals, productFeedback }; goals/productFeedback are each { results: [...] } or { error: ... } if that part could not be fetched.")]
    public static async Task<string> Get_organization_summary(
        VitallyService vitallyService,
        [Description("Organisation id (Vitally id or externalId).")] string organizationId,
        [Description("Optional comma-separated trait keys to override the curated default rollup set.")] string? traits = null,
        [Description("Optional custom-object name to use as the customer's goals (default 'customerGoals'). Resolved to an id by name.")] string? goalsObjectName = null,
        [Description("Optional custom-object name to use as product feedback (default 'productFeedback'). Resolved to an id by name.")] string? productFeedbackObjectName = null)
    {
        var effectiveTraits = string.IsNullOrWhiteSpace(traits) ? DefaultRollupTraits : traits;
        var effectiveGoals = string.IsNullOrWhiteSpace(goalsObjectName) ? DefaultGoalsObjectName : goalsObjectName;
        var effectiveFeedback = string.IsNullOrWhiteSpace(productFeedbackObjectName) ? DefaultProductFeedbackObjectName : productFeedbackObjectName;

        return await vitallyService.GetOrganizationSummaryAsync(
            organizationId, effectiveTraits, effectiveGoals, effectiveFeedback);
    }
}
```

- [ ] **Step 4: Run to verify pass**

Run: `dotnet test --filter "FullyQualifiedName~SummaryToolsTests" --nologo`
Expected: PASS (6 cases).

- [ ] **Step 5: Full suite + commit**

Run: `dotnet test VitallyMcp.sln -c Debug --nologo --verbosity minimal` — all green.

```powershell
git add VitallyMcp/Tools/SummaryTools.cs VitallyMcp.Tests/Tools/SummaryToolsTests.cs
git commit -m @'
feat(summary): Get_organization_summary composite tool (SP5b)

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_01RWMtSydEfRwSpxDHNY9eH3
'@
```

---

### Task 3: Docs (`CLAUDE.md`)

**Files:**
- Modify: `CLAUDE.md`

- [ ] **Step 1: Document the composite**

In `CLAUDE.md`, under the section describing custom-object instance tools (just after the "Custom object instances:" paragraph that mentions `List_custom_object_instances` / `Get_custom_object_instance`), add a paragraph:

```markdown
**Organisation summary (SP5b):** `Get_organization_summary(organizationId)` is a read-only composite
that collapses the common "everything about this customer" shape into one call. It makes 4 upstream
calls — org get-by-id (with a curated set of rollup traits), one `customObjects` list to resolve the
goals/product-feedback object names to ids, and two organisation-scoped instance searches — and
returns `{ organization, goals, productFeedback }`. `goals`/`productFeedback` are each `{results:[...]}`
or `{error:...}` (a single sub-failure never sinks the summary; a bad org id surfaces an error). The
tenant-specific policy (the curated trait CSV and the default object names `customerGoals` /
`productFeedback`) lives as constants in `Tools/SummaryTools.cs`; `VitallyService.GetOrganizationSummaryAsync`
is generic and takes them as parameters. Object ids are resolved by name at runtime (never hardcoded);
the trait set and object names are overridable per call.
```

- [ ] **Step 2: Commit**

```powershell
git add CLAUDE.md
git commit -m @'
docs: document Get_organization_summary composite (SP5b)

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_01RWMtSydEfRwSpxDHNY9eH3
'@
```

---

## Final verification

- [ ] `dotnet test VitallyMcp.sln -c Debug --nologo --verbosity minimal` — all green.
- [ ] **Live validation (temp key, EU):** start the local server in NoAuth dev mode (`OAuth__NoAuth=true`, `Vitally__Region=EU`, `Vitally__DevelopmentApiKey=<temp key>`, `ASPNETCORE_URLS=http://localhost:5099`, `dotnet run --project VitallyMcp/VitallyMcp.csproj` in the background; poll readiness). Drive a `tools/call` for `Get_organization_summary` with `organizationId=d3639d2d-bf12-4848-b446-764f834bcd2a` (Enfield) at `localhost:5099/mcp` (responses are SSE `data:` lines). Confirm: `organization.traits` includes `vitally.custom.countAllSupportTickets=10`, `vitally.custom.countOfOpenCustomerGoals=5`, `sfdc.Customer_Health_Score__c=156`; `goals.results` has 1 instance; `productFeedback.results` is empty. Stop the server (`Get-Process -Name VitallyMcp | Stop-Process -Force`).
- [ ] Open a PR from `feature/sp5b-organization-summary` into `main`; CI green; resolve any Copilot/CodeQL review threads (the `main` ruleset requires resolution); merge (squash).
