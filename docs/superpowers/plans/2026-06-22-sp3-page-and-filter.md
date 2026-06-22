# SP3 — Server-side page-and-filter — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add org name search, activity date-range filters, and a custom-traits catalogue filter — implemented as a bounded, never-silent server-side auto-pager (the filters Vitally's API won't do).

**Architecture:** One reusable bounded auto-pager in `VitallyService` (`GetFilteredAsync`, capped by `Vitally:MaxAutoPageFetches`) with two public wrappers (`GetByNameContainsAsync`, `GetByCreatedRangeAsync`); a separate client-side array filter for the trait catalogue. Filtered calls return a `{results, truncated, pagesFetched}` envelope; unfiltered calls keep today's `GetResourcesAsync` path unchanged.

**Tech Stack:** .NET 10, C#, System.Text.Json, xUnit + FluentAssertions + Moq (`Moq.Protected`).

**Branch:** Implement on `feature/sp3-page-and-filter` (already created off `main`; the spec is already committed there).

**Spec:** [SP3 design](../specs/2026-06-22-sp3-page-and-filter-design.md)

## Global Constraints

- UK English in comments/docs. Repo uses **LF** line endings (`.gitattributes` present).
- All Vitally HTTP must go through `VitallyService.SendAsync` (preserves RBAC + audit + rate-limit handling). The pager fetches every page via `SendAsync`.
- Page cap: `Vitally:MaxAutoPageFetches` (default **10**), page size 100. Truncation is **never silent** — surface `truncated` + `pagesFetched`.
- Filtered tool output envelope: `{ results, truncated, pagesFetched }`. Unfiltered behaviour and shape must be unchanged.
- `created*` only (no `updated*`); org-only name search; date-range on the activity set only (YAGNI).

---

### Task 1: `MaxAutoPageFetches` config

**Files:**
- Modify: `VitallyMcp/VitallyServerOptions.cs`
- Test: `VitallyMcp.Tests/` (new `VitallyServerOptionsTests.cs` if absent; else add to existing options test)

**Interfaces:**
- Produces: `VitallyServerOptions.MaxAutoPageFetches` (int, default 10), validated `>= 1`.

- [ ] **Step 1: Write the failing test**

Create `VitallyMcp.Tests/VitallyServerOptionsTests.cs`:

```csharp
using FluentAssertions;

namespace VitallyMcp.Tests;

public class VitallyServerOptionsTests
{
    private static VitallyServerOptions Valid() => new()
    {
        Region = "EU",
        DevelopmentApiKey = "sk_live_test"
    };

    [Fact]
    public void MaxAutoPageFetches_DefaultsTo10()
    {
        new VitallyServerOptions().MaxAutoPageFetches.Should().Be(10);
    }

    [Fact]
    public void Validate_RejectsNonPositiveMaxAutoPageFetches()
    {
        var opts = Valid();
        opts.MaxAutoPageFetches = 0;
        Action act = () => opts.Validate();
        act.Should().Throw<InvalidOperationException>().WithMessage("*MaxAutoPageFetches*");
    }

    [Fact]
    public void Validate_AcceptsPositiveMaxAutoPageFetches()
    {
        var opts = Valid();
        opts.MaxAutoPageFetches = 5;
        Action act = () => opts.Validate();
        act.Should().NotThrow();
    }
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test --filter "FullyQualifiedName~VitallyServerOptionsTests" --nologo`
Expected: FAIL — `MaxAutoPageFetches` not defined (compile error).

- [ ] **Step 3: Add the property + validation**

In `VitallyMcp/VitallyServerOptions.cs`, add the property after `DevelopmentApiKey`:

```csharp
    public int MaxAutoPageFetches { get; set; } = 10;
```

and at the end of `Validate()` (before the closing brace):

```csharp
        if (MaxAutoPageFetches < 1)
        {
            throw new InvalidOperationException(
                $"Vitally:MaxAutoPageFetches must be >= 1 (got {MaxAutoPageFetches}).");
        }
```

- [ ] **Step 4: Run to verify pass**

Run: `dotnet test --filter "FullyQualifiedName~VitallyServerOptionsTests" --nologo`
Expected: PASS (3 tests).

- [ ] **Step 5: Commit**

```powershell
git add VitallyMcp/VitallyServerOptions.cs VitallyMcp.Tests/VitallyServerOptionsTests.cs
git commit -m @'
feat(paging): add Vitally:MaxAutoPageFetches option (default 10)

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
'@
```

---

### Task 2: Bounded auto-pager + `GetByNameContainsAsync`

**Files:**
- Modify: `VitallyMcp/VitallyService.cs`
- Modify: `VitallyMcp.Tests/TestHelpers.cs` (add a paged-response mock helper)
- Test: `VitallyMcp.Tests/VitallyServiceTests.cs`

**Interfaces:**
- Consumes: `_options.MaxAutoPageFetches` (Task 1); existing `SendAsync`, `ResolveFields`, `ResolveTraits`, `WriteFilteredObject`.
- Produces:
  - `private string BuildListUrl(string resourceType, int limit, string? from, string? sortBy, Dictionary<string,string>? additionalParams)`
  - `private Task<string> GetFilteredAsync(string resourceType, Func<JsonElement,bool> predicate, string? fields, string? sortBy, Dictionary<string,string>? additionalParams, string? traits, string defaultsKey, Func<JsonElement,bool>? stopBefore = null)` → returns `{results, truncated, pagesFetched}`.
  - `public Task<string> GetByNameContainsAsync(string resourceType, string nameContains, string? fields = null, string? traits = null, string? defaultsKey = null, Dictionary<string,string>? additionalParams = null)`

- [ ] **Step 1: Add the paged-response test helper**

In `VitallyMcp.Tests/TestHelpers.cs`, add (the existing file already `using`s `System.Net`, `Moq`, `Moq.Protected`):

```csharp
    /// <summary>
    /// Mock HttpClient that returns the supplied page bodies in order, one per successive request —
    /// for testing the auto-pager's multi-page paging.
    /// </summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Reliability", "CA2000", Justification = "Test mock — see CreateMockHttpClient.")]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Reliability", "cs/local-not-disposed", Justification = "Test mock — see CreateMockHttpClient.")]
    public static (HttpClient client, Mock<HttpMessageHandler> handler) CreateMockHttpClientPaged(params string[] pages)
    {
        var mock = new Mock<HttpMessageHandler>();
        var seq = mock.Protected().SetupSequence<Task<HttpResponseMessage>>(
            "SendAsync", ItExpr.IsAny<HttpRequestMessage>(), ItExpr.IsAny<CancellationToken>());
        foreach (var page in pages)
        {
            seq = seq.ReturnsAsync(new HttpResponseMessage { StatusCode = HttpStatusCode.OK, Content = new StringContent(page) });
        }
        return (new HttpClient(mock.Object), mock);
    }
```

- [ ] **Step 2: Write the failing tests**

Add to `VitallyMcp.Tests/VitallyServiceTests.cs` a new region:

```csharp
    #region Auto-pager (GetByNameContainsAsync) Tests

    private static string Page(string idAndName, string? next) =>
        $$"""
        { "results": [ { "id": "{{idAndName}}", "name": "{{idAndName}}" } ], "next": {{(next is null ? "null" : $"\"{next}\"")}} }
        """;

    [Fact]
    public async Task GetByNameContainsAsync_FiltersAcrossPages_AndReportsNotTruncatedWhenExhausted()
    {
        // Arrange — 2 pages, only one item matches "berdeen"
        var (client, _) = TestHelpers.CreateMockHttpClientPaged(
            Page("Aberdeen", "cursor1"),
            Page("Brighton", null));
        var service = CreateService(client);

        // Act
        var result = await service.GetByNameContainsAsync("organizations", "berdeen");

        // Assert
        var doc = System.Text.Json.JsonDocument.Parse(result);
        doc.RootElement.GetProperty("results").GetArrayLength().Should().Be(1);
        doc.RootElement.GetProperty("results")[0].GetProperty("name").GetString().Should().Be("Aberdeen");
        doc.RootElement.GetProperty("truncated").GetBoolean().Should().BeFalse();
        doc.RootElement.GetProperty("pagesFetched").GetInt32().Should().Be(2);
    }

    [Fact]
    public async Task GetByNameContainsAsync_HitsCap_ReportsTruncated()
    {
        // Arrange — every page has a next cursor, so paging is bounded only by the cap.
        // CreateService uses default options (MaxAutoPageFetches = 10), so supply 11 pages.
        var pages = Enumerable.Range(0, 11).Select(i => Page($"Org{i}", $"cursor{i}")).ToArray();
        var (client, _) = TestHelpers.CreateMockHttpClientPaged(pages);
        var service = CreateService(client);

        // Act
        var result = await service.GetByNameContainsAsync("organizations", "zzz-no-match");

        // Assert
        var doc = System.Text.Json.JsonDocument.Parse(result);
        doc.RootElement.GetProperty("results").GetArrayLength().Should().Be(0);
        doc.RootElement.GetProperty("truncated").GetBoolean().Should().BeTrue();
        doc.RootElement.GetProperty("pagesFetched").GetInt32().Should().Be(10);
    }

    #endregion
```

(`System.Linq` and `System.Text.Json` are available via global usings; the explicit `JsonDocument` qualifier above avoids ambiguity.)

- [ ] **Step 3: Run to verify it fails**

Run: `dotnet test --filter "FullyQualifiedName~GetByNameContainsAsync" --nologo`
Expected: FAIL — `GetByNameContainsAsync` not defined (compile error).

- [ ] **Step 4: Refactor URL building + add the pager**

In `VitallyMcp/VitallyService.cs`, extract the URL builder and reuse it in `GetResourcesAsync`. Replace the body of `GetResourcesAsync` (the query-building block through the `url` assignment, lines ~112-129) so the method becomes:

```csharp
    public async Task<string> GetResourcesAsync(string resourceType, int limit = 20, string? from = null, string? fields = null, string? sortBy = null, Dictionary<string, string>? additionalParams = null, string? traits = null, string? defaultsKey = null)
    {
        var url = BuildListUrl(resourceType, limit, from, sortBy, additionalParams);
        var jsonResponse = await SendAsync(HttpMethod.Get, url);
        return FilterJsonFields(jsonResponse, fields, defaultsKey ?? resourceType, isListResponse: true, traits);
    }

    // Builds a /resources/{type} list URL. RFC 3986 query encoding via Uri.EscapeDataString
    // (spaces -> %20) so filter values with spaces/&/= don't corrupt the URL. Shared by
    // GetResourcesAsync and the auto-pager.
    private string BuildListUrl(string resourceType, int limit, string? from, string? sortBy, Dictionary<string, string>? additionalParams)
    {
        var queryParams = new List<string> { $"limit={limit}" };
        if (!string.IsNullOrEmpty(from))
            queryParams.Add($"from={Uri.EscapeDataString(from)}");
        if (!string.IsNullOrEmpty(sortBy))
            queryParams.Add($"sortBy={Uri.EscapeDataString(sortBy)}");
        if (additionalParams != null)
        {
            foreach (var param in additionalParams)
            {
                queryParams.Add($"{Uri.EscapeDataString(param.Key)}={Uri.EscapeDataString(param.Value)}");
            }
        }
        return $"{_baseUrl}/resources/{resourceType}?{string.Join("&", queryParams)}";
    }
```

Then add the pager + name wrapper (place after `GetResourceByIdAsync`):

```csharp
    /// <summary>
    /// Pages a list endpoint (via from/next) applying a client-side <paramref name="predicate"/> to
    /// each raw item, accumulating matches up to <c>Vitally:MaxAutoPageFetches</c> pages (100/page).
    /// Returns a <c>{results, truncated, pagesFetched}</c> envelope; <c>truncated</c> is true when
    /// the page cap was hit before the endpoint was exhausted. <paramref name="stopBefore"/>
    /// (optional) ends paging early once an item is reached that — given the sort order —
    /// guarantees no further matches.
    /// </summary>
    private async Task<string> GetFilteredAsync(string resourceType, Func<JsonElement, bool> predicate, string? fields, string? sortBy, Dictionary<string, string>? additionalParams, string? traits, string defaultsKey, Func<JsonElement, bool>? stopBefore = null)
    {
        const int pageSize = 100;
        var maxPages = _options.MaxAutoPageFetches;
        var matched = new List<JsonElement>();
        string? from = null;
        var pagesFetched = 0;
        var truncated = false;
        var stopped = false;

        while (true)
        {
            var url = BuildListUrl(resourceType, pageSize, from, sortBy, additionalParams);
            var pageJson = await SendAsync(HttpMethod.Get, url);
            pagesFetched++;

            using (var doc = JsonDocument.Parse(pageJson))
            {
                var root = doc.RootElement;
                if (root.TryGetProperty("results", out var results) && results.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in results.EnumerateArray())
                    {
                        if (stopBefore != null && stopBefore(item)) { stopped = true; break; }
                        if (predicate(item)) matched.Add(item.Clone());
                    }
                }

                from = !stopped && root.TryGetProperty("next", out var next) && next.ValueKind == JsonValueKind.String
                    ? next.GetString()
                    : null;
            }

            if (stopped || string.IsNullOrEmpty(from)) break;     // early-stop or exhausted
            if (pagesFetched >= maxPages) { truncated = true; break; }   // cap hit
        }

        var requestedFields = ResolveFields(fields, defaultsKey);
        var requestedTraits = ResolveTraits(traits);

        using var stream = new MemoryStream();
        using var writer = new Utf8JsonWriter(stream);
        writer.WriteStartObject();
        writer.WritePropertyName("results");
        writer.WriteStartArray();
        foreach (var item in matched)
        {
            WriteFilteredObject(writer, item, requestedFields, requestedTraits);
        }
        writer.WriteEndArray();
        writer.WriteBoolean("truncated", truncated);
        writer.WriteNumber("pagesFetched", pagesFetched);
        writer.WriteEndObject();
        writer.Flush();
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    /// <summary>
    /// Lists a resource filtered to items whose <c>name</c> contains <paramref name="nameContains"/>
    /// (case-insensitive), via the bounded auto-pager. For endpoints Vitally can't filter by name.
    /// </summary>
    public Task<string> GetByNameContainsAsync(string resourceType, string nameContains, string? fields = null, string? traits = null, string? defaultsKey = null, Dictionary<string, string>? additionalParams = null)
    {
        return GetFilteredAsync(
            resourceType,
            item => item.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String
                    && (n.GetString() ?? string.Empty).Contains(nameContains, StringComparison.OrdinalIgnoreCase),
            fields, sortBy: null, additionalParams, traits, defaultsKey ?? resourceType);
    }
```

- [ ] **Step 5: Run to verify pass**

Run: `dotnet test --filter "FullyQualifiedName~GetByNameContainsAsync" --nologo`
Expected: PASS (2 tests).

- [ ] **Step 6: Run the full service test class (refactor safety)**

Run: `dotnet test --filter "FullyQualifiedName~VitallyServiceTests" --nologo`
Expected: PASS (all — the `BuildListUrl` refactor must not change existing behaviour).

- [ ] **Step 7: Commit**

```powershell
git add VitallyMcp/VitallyService.cs VitallyMcp.Tests/TestHelpers.cs VitallyMcp.Tests/VitallyServiceTests.cs
git commit -m @'
feat(paging): bounded auto-pager + GetByNameContainsAsync

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
'@
```

---

### Task 3: Org name search — `List_organizations` gains `nameContains`

**Files:**
- Modify: `VitallyMcp/Tools/OrganizationsTools.cs`
- Test: `VitallyMcp.Tests/Tools/OrganizationsToolsTests.cs`

**Interfaces:**
- Consumes: `VitallyService.GetByNameContainsAsync` (Task 2).

- [ ] **Step 1: Write the failing test**

Add to `VitallyMcp.Tests/Tools/OrganizationsToolsTests.cs` (add `using Moq;` / `using Moq.Protected;` if not present):

```csharp
    [Fact]
    public async Task ListOrganizations_WithNameContains_FiltersViaPager()
    {
        // Arrange — two single-item pages; only "Aberdeen City Council" matches.
        var page1 = """{ "results": [ { "id": "o1", "name": "Aberdeen City Council" } ], "next": "c1" }""";
        var page2 = """{ "results": [ { "id": "o2", "name": "Brighton" } ], "next": null }""";
        var (client, _) = TestHelpers.CreateMockHttpClientPaged(page1, page2);
        var service = CreateService(client);

        // Act
        var result = await OrganizationsTools.ListOrganizations(service, nameContains: "aberdeen");

        // Assert
        result.Should().Contain("Aberdeen City Council");
        result.Should().NotContain("Brighton");
        result.Should().Contain("\"truncated\"");
    }
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test --filter "FullyQualifiedName~ListOrganizations_WithNameContains" --nologo`
Expected: FAIL — `ListOrganizations` has no `nameContains` parameter.

- [ ] **Step 3: Add the parameter + routing**

In `VitallyMcp/Tools/OrganizationsTools.cs`, replace the `ListOrganizations` method with (adds the final `nameContains` param + routing; keeps existing params/order):

```csharp
    [McpServerTool(Name = "List_organizations", Title = "List organizations", ReadOnly = true, Destructive = false), Description("List Vitally organisations with optional pagination and field selection")]
    public static async Task<string> ListOrganizations(
        VitallyService vitallyService,
        [Description("Maximum number of organisations to return (default: 20, max: 100). Ignored when nameContains is supplied.")] int limit = 20,
        [Description("Pagination cursor from previous response (use the 'next' value). Ignored when nameContains is supplied.")] string? from = null,
        [Description("Comma-separated list of fields to include (e.g., 'id,name,createdAt'). Defaults to: id,name,createdAt,updatedAt. Client-side filtering.")] string? fields = null,
        [Description("Sort by field: 'createdAt' or 'updatedAt' (default: updatedAt). Ignored when nameContains is supplied.")] string? sortBy = null,
        [Description("Comma-separated list of trait names to include (e.g., 'paymentMethod,customField'). If specified, must also include 'traits' in fields parameter. Client-side filtering.")] string? traits = null,
        [Description("Case-insensitive substring to match against organisation name. The server pages and filters client-side (Vitally has no name filter) and returns {results, truncated, pagesFetched}; if 'truncated' is true, narrow the search.")] string? nameContains = null)
    {
        if (!string.IsNullOrWhiteSpace(nameContains))
        {
            return await vitallyService.GetByNameContainsAsync("organizations", nameContains, fields, traits);
        }

        return await vitallyService.GetResourcesAsync("organizations", limit, from, fields, sortBy, null, traits);
    }
```

- [ ] **Step 4: Run to verify pass**

Run: `dotnet test --filter "FullyQualifiedName~OrganizationsToolsTests" --nologo`
Expected: PASS (all, incl. the new test).

- [ ] **Step 5: Commit**

```powershell
git add VitallyMcp/Tools/OrganizationsTools.cs VitallyMcp.Tests/Tools/OrganizationsToolsTests.cs
git commit -m @'
feat(organizations): add nameContains search to List_organizations (P1.4)

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
'@
```

---

### Task 4: `GetByCreatedRangeAsync` (date predicate + early-stop + validation)

**Files:**
- Modify: `VitallyMcp/VitallyService.cs`
- Test: `VitallyMcp.Tests/VitallyServiceTests.cs`

**Interfaces:**
- Consumes: `GetFilteredAsync` (Task 2).
- Produces: `public Task<string> GetByCreatedRangeAsync(string resourceType, string? createdAfter, string? createdBefore, string? fields = null, string? traits = null, string? defaultsKey = null, Dictionary<string,string>? additionalParams = null)`.

- [ ] **Step 1: Write the failing tests**

Add to `VitallyMcp.Tests/VitallyServiceTests.cs`:

```csharp
    #region GetByCreatedRangeAsync Tests

    private static string DatedPage(string id, string createdAt, string? next) =>
        $$"""
        { "results": [ { "id": "{{id}}", "createdAt": "{{createdAt}}" } ], "next": {{(next is null ? "null" : $"\"{next}\"")}} }
        """;

    [Fact]
    public async Task GetByCreatedRangeAsync_KeepsOnlyInRange_AndEarlyStops()
    {
        // Sorted createdAt desc. Range lower bound 2026-02-01. Page 2 item is older -> early stop,
        // page 3 must never be fetched.
        var (client, handler) = TestHelpers.CreateMockHttpClientPaged(
            DatedPage("in", "2026-03-01T00:00:00Z", "c1"),
            DatedPage("old", "2026-01-01T00:00:00Z", "c2"),
            DatedPage("never", "2026-03-15T00:00:00Z", null));
        var service = CreateService(client);

        var result = await service.GetByCreatedRangeAsync("notes", createdAfter: "2026-02-01", createdBefore: null);

        var doc = System.Text.Json.JsonDocument.Parse(result);
        doc.RootElement.GetProperty("results").GetArrayLength().Should().Be(1);
        doc.RootElement.GetProperty("results")[0].GetProperty("id").GetString().Should().Be("in");
        doc.RootElement.GetProperty("truncated").GetBoolean().Should().BeFalse();
        // Early-stop on page 2 => exactly 2 fetches, page 3 untouched.
        handler.Protected().Verify("SendAsync", Times.Exactly(2),
            ItExpr.IsAny<HttpRequestMessage>(), ItExpr.IsAny<CancellationToken>());
    }

    [Fact]
    public async Task GetByCreatedRangeAsync_InvalidDate_ThrowsBeforeAnyCall()
    {
        var (client, handler) = TestHelpers.CreateMockHttpClientPaged("""{"results":[],"next":null}""");
        var service = CreateService(client);

        Func<Task> act = () => service.GetByCreatedRangeAsync("notes", createdAfter: "not-a-date", createdBefore: null);

        await act.Should().ThrowAsync<ArgumentException>();
        handler.Protected().Verify("SendAsync", Times.Never(),
            ItExpr.IsAny<HttpRequestMessage>(), ItExpr.IsAny<CancellationToken>());
    }

    #endregion
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test --filter "FullyQualifiedName~GetByCreatedRangeAsync" --nologo`
Expected: FAIL — method not defined.

- [ ] **Step 3: Implement**

In `VitallyMcp/VitallyService.cs`, add after `GetByNameContainsAsync`:

```csharp
    /// <summary>
    /// Lists a resource filtered to items whose <c>createdAt</c> falls within
    /// [<paramref name="createdAfter"/>, <paramref name="createdBefore"/>] (either bound optional),
    /// via the bounded auto-pager. Pages sorted by <c>createdAt</c> (descending) so paging stops
    /// early once items fall below the lower bound. Bounds must be ISO-8601; throws
    /// <see cref="ArgumentException"/> otherwise (before any HTTP call).
    /// </summary>
    public Task<string> GetByCreatedRangeAsync(string resourceType, string? createdAfter, string? createdBefore, string? fields = null, string? traits = null, string? defaultsKey = null, Dictionary<string, string>? additionalParams = null)
    {
        var after = ParseDateBound(createdAfter, nameof(createdAfter));
        var before = ParseDateBound(createdBefore, nameof(createdBefore));

        bool InRange(JsonElement item)
        {
            if (!item.TryGetProperty("createdAt", out var c) || c.ValueKind != JsonValueKind.String
                || !DateTimeOffset.TryParse(c.GetString(), out var dt))
            {
                return false;
            }
            if (after.HasValue && dt < after.Value) return false;
            if (before.HasValue && dt > before.Value) return false;
            return true;
        }

        // createdAt descending => once an item is older than the lower bound, all later items are too.
        Func<JsonElement, bool>? stopBefore = after.HasValue
            ? item => item.TryGetProperty("createdAt", out var c) && c.ValueKind == JsonValueKind.String
                      && DateTimeOffset.TryParse(c.GetString(), out var dt) && dt < after.Value
            : null;

        return GetFilteredAsync(resourceType, InRange, fields, sortBy: "createdAt", additionalParams, traits, defaultsKey ?? resourceType, stopBefore);
    }

    private static DateTimeOffset? ParseDateBound(string? value, string paramName)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        if (!DateTimeOffset.TryParse(value, System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AssumeUniversal, out var dt))
        {
            throw new ArgumentException($"{paramName} must be an ISO-8601 date/time (got '{value}').", paramName);
        }
        return dt;
    }
```

- [ ] **Step 4: Run to verify pass**

Run: `dotnet test --filter "FullyQualifiedName~GetByCreatedRangeAsync" --nologo`
Expected: PASS (2 tests).

- [ ] **Step 5: Commit**

```powershell
git add VitallyMcp/VitallyService.cs VitallyMcp.Tests/VitallyServiceTests.cs
git commit -m @'
feat(paging): add GetByCreatedRangeAsync (date-range filter + early-stop)

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
'@
```

---

### Task 5: Wire `createdAfter`/`createdBefore` into the activity tools

**Files:**
- Modify: `VitallyMcp/Tools/ConversationsTools.cs`, `VitallyMcp/Tools/NotesTools.cs`, `VitallyMcp/Tools/TasksTools.cs`, `VitallyMcp/Tools/MeetingsTools.cs`
- Test: `VitallyMcp.Tests/Tools/ConversationsToolsTests.cs` (+ a smoke test per other tool)

**Interfaces:**
- Consumes: `VitallyService.GetByCreatedRangeAsync` (Task 4).

The change is the same shape for each tool: add `createdAfter`/`createdBefore` params; when either is set, route to `GetByCreatedRangeAsync(<resourceType>, createdAfter, createdBefore, fields, traits, defaultsKey: "<defaultsKey>", additionalParams)`; otherwise the existing `GetResourcesAsync` call. Apply to these tool methods with these resourceType / defaultsKey values:

| Tool method | resourceType | defaultsKey |
|---|---|---|
| `ListConversations` | `conversations` | `conversations` |
| `ListConversationsByAccount` | `accounts/{accountId}/conversations` | `conversations` |
| `ListConversationsByOrganization` | `organizations/{organizationId}/conversations` | `conversations` |
| `ListNotes` | `notes` | `notes` |
| `ListTasks` | `tasks` | `tasks` |
| `ListMeetings` | `meetings` | `meetings` (pass the existing `archived` additionalParams) |

These two `[Description]` strings are reused for every `createdAfter`/`createdBefore` param added:
- `createdAfter`: `"ISO-8601 lower bound on createdAt. When set, the server pages and filters by date client-side (Vitally has no date filter) and returns {results, truncated, pagesFetched}; limit/from/sortBy are ignored."`
- `createdBefore`: `"ISO-8601 upper bound on createdAt. See createdAfter."`

- [ ] **Step 1: Write the failing test (representative — conversations-by-organization, the headline use case)**

Add to `VitallyMcp.Tests/Tools/ConversationsToolsTests.cs` (add `using Moq;`/`using Moq.Protected;` if missing):

```csharp
    [Fact]
    public async Task ListConversationsByOrganization_WithCreatedRange_FiltersByDate()
    {
        // createdAt desc; only the first is in range; second is older -> early stop.
        var page1 = """{ "results": [ { "id": "c-in", "subject": "Ticket", "createdAt": "2026-03-01T00:00:00Z" } ], "next": "n1" }""";
        var page2 = """{ "results": [ { "id": "c-old", "subject": "Old", "createdAt": "2026-01-01T00:00:00Z" } ], "next": "n2" }""";
        var (client, handler) = TestHelpers.CreateMockHttpClientPaged(page1, page2);
        var service = CreateService(client);

        var result = await ConversationsTools.ListConversationsByOrganization(
            service, "org-1", createdAfter: "2026-02-01", createdBefore: null);

        result.Should().Contain("c-in");
        result.Should().NotContain("c-old");
        result.Should().Contain("\"truncated\"");
        // routed to the org-scoped conversations path
        handler.Protected().Verify("SendAsync", Times.AtLeastOnce(),
            ItExpr.Is<HttpRequestMessage>(req => req.RequestUri!.AbsolutePath == "/resources/organizations/org-1/conversations"),
            ItExpr.IsAny<CancellationToken>());
    }

    [Fact]
    public async Task ListConversations_NoDateRange_UsesPlainListPath()
    {
        var (client, handler) = TestHelpers.CreateMockHttpClientWithHandler(TestHelpers.GetSampleConversationJson());
        var service = CreateService(client);

        await ConversationsTools.ListConversations(service);

        handler.Protected().Verify("SendAsync", Times.Once(),
            ItExpr.Is<HttpRequestMessage>(req => req.RequestUri!.AbsolutePath == "/resources/conversations"
                && req.RequestUri.Query.Contains("limit=20")),
            ItExpr.IsAny<CancellationToken>());
    }
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test --filter "FullyQualifiedName~ListConversationsByOrganization_WithCreatedRange" --nologo`
Expected: FAIL — no `createdAfter` parameter.

- [ ] **Step 3: Implement the six tool methods**

For each tool in the table, add the two params (as the last params) and the routing guard. Example — `ListConversationsByOrganization` in `VitallyMcp/Tools/ConversationsTools.cs`:

```csharp
    [McpServerTool(Name = "List_conversations_by_organization", Title = "List conversations by organization", ReadOnly = true, Destructive = false), Description("List Vitally conversations for a specific organisation")]
    public static async Task<string> ListConversationsByOrganization(
        VitallyService vitallyService,
        [Description("The organisation ID")] string organizationId,
        [Description("Maximum number of conversations to return (default: 20, max: 100). Ignored when a created date range is supplied.")] int limit = 20,
        [Description("Pagination cursor from previous response (use the 'next' value). Ignored when a created date range is supplied.")] string? from = null,
        [Description("Comma-separated list of fields to include. Defaults to: id,name,createdAt,updatedAt. Client-side filtering.")] string? fields = null,
        [Description("Sort by field: 'createdAt' or 'updatedAt' (default: updatedAt). Ignored when a created date range is supplied.")] string? sortBy = null,
        [Description("ISO-8601 lower bound on createdAt. When set, the server pages and filters by date client-side (Vitally has no date filter) and returns {results, truncated, pagesFetched}; limit/from/sortBy are ignored.")] string? createdAfter = null,
        [Description("ISO-8601 upper bound on createdAt. See createdAfter.")] string? createdBefore = null)
    {
        var resourceType = $"organizations/{organizationId}/conversations";
        if (!string.IsNullOrWhiteSpace(createdAfter) || !string.IsNullOrWhiteSpace(createdBefore))
        {
            return await vitallyService.GetByCreatedRangeAsync(resourceType, createdAfter, createdBefore, fields, defaultsKey: "conversations");
        }

        return await vitallyService.GetResourcesAsync(resourceType, limit, from, fields, sortBy);
    }
```

Apply the equivalent change to the other five methods using their `resourceType`/`defaultsKey` from the table. For `ListMeetings`, keep its `archived` handling and pass the built `additionalParams` to **both** paths:

```csharp
        var additionalParams = new Dictionary<string, string>();
        if (!string.IsNullOrEmpty(archived)) additionalParams["archived"] = archived;

        if (!string.IsNullOrWhiteSpace(createdAfter) || !string.IsNullOrWhiteSpace(createdBefore))
        {
            return await vitallyService.GetByCreatedRangeAsync("meetings", createdAfter, createdBefore, fields, traits, defaultsKey: "meetings", additionalParams: additionalParams);
        }

        return await vitallyService.GetResourcesAsync("meetings", limit, from, fields, sortBy, additionalParams, traits);
```

For tools that currently pass `traits` (notes, tasks, conversations base, meetings), pass `traits` to `GetByCreatedRangeAsync` too; the conversations base list does not pass traits today — keep it unset there.

- [ ] **Step 4: Run to verify pass**

Run: `dotnet test --filter "FullyQualifiedName~ConversationsToolsTests|FullyQualifiedName~NotesToolsTests|FullyQualifiedName~TasksToolsTests|FullyQualifiedName~MeetingsToolsTests" --nologo`
Expected: PASS (all, incl. the two new tests).

- [ ] **Step 5: Commit**

```powershell
git add VitallyMcp/Tools/ConversationsTools.cs VitallyMcp/Tools/NotesTools.cs VitallyMcp/Tools/TasksTools.cs VitallyMcp/Tools/MeetingsTools.cs VitallyMcp.Tests/Tools/ConversationsToolsTests.cs
git commit -m @'
feat(activity): add createdAfter/createdBefore date-range filters (P1.3)

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
'@
```

---

### Task 6: Custom-traits catalogue filter — `List_custom_traits` gains `nameContains`

**Files:**
- Modify: `VitallyMcp/VitallyService.cs` (add a client-side array filter helper) and `VitallyMcp/Tools/CustomTraitsTools.cs`
- Test: `VitallyMcp.Tests/Tools/CustomTraitsToolsTests.cs`

**Interfaces:**
- Produces: `public Task<string> GetRawArrayFilteredAsync(string path, Dictionary<string,string> queryParams, string nameContains)` — GETs a bare-array endpoint and returns only elements whose `label` or `path` contains the needle (case-insensitive).

- [ ] **Step 1: Write the failing test**

Add to `VitallyMcp.Tests/Tools/CustomTraitsToolsTests.cs`:

```csharp
    [Fact]
    public async Task ListCustomTraits_WithNameContains_FiltersCatalogue()
    {
        var raw = """
        [
          { "label": "Payment Method", "path": "paymentMethod", "type": "STRING" },
          { "label": "Employees", "path": "employees", "type": "NUMBER" }
        ]
        """;
        var mockClient = TestHelpers.CreateMockHttpClient(raw);
        var service = CreateService(mockClient);

        var result = await CustomTraitsTools.ListCustomTraits(service, "accounts", nameContains: "payment");

        result.Should().Contain("Payment Method");
        result.Should().NotContain("Employees");
    }
```

(If `CustomTraitsToolsTests` has no `CreateService` helper, mirror the one in `ConversationsToolsTests`.)

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test --filter "FullyQualifiedName~ListCustomTraits_WithNameContains" --nologo`
Expected: FAIL — no `nameContains` parameter.

- [ ] **Step 3: Implement the service helper + tool param**

In `VitallyMcp/VitallyService.cs`, add (near `GetRawAsync`):

```csharp
    /// <summary>
    /// GETs a bare-array endpoint (e.g. customFields) and returns only the elements whose
    /// <c>label</c> or <c>path</c> contains <paramref name="nameContains"/> (case-insensitive).
    /// No paging — the whole array is one response. Returns the filtered array unchanged in shape.
    /// </summary>
    public async Task<string> GetRawArrayFilteredAsync(string path, Dictionary<string, string> queryParams, string nameContains)
    {
        var json = await GetRawAsync(path, queryParams);
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Array)
        {
            return json; // unexpected shape — pass through unchanged
        }

        using var stream = new MemoryStream();
        using var writer = new Utf8JsonWriter(stream);
        writer.WriteStartArray();
        foreach (var item in document.RootElement.EnumerateArray())
        {
            var label = item.TryGetProperty("label", out var l) && l.ValueKind == JsonValueKind.String ? l.GetString() : null;
            var p = item.TryGetProperty("path", out var pp) && pp.ValueKind == JsonValueKind.String ? pp.GetString() : null;
            if ((label ?? string.Empty).Contains(nameContains, StringComparison.OrdinalIgnoreCase)
                || (p ?? string.Empty).Contains(nameContains, StringComparison.OrdinalIgnoreCase))
            {
                item.WriteTo(writer);
            }
        }
        writer.WriteEndArray();
        writer.Flush();
        return Encoding.UTF8.GetString(stream.ToArray());
    }
```

In `VitallyMcp/Tools/CustomTraitsTools.cs`, add the `nameContains` param + routing to `ListCustomTraits`:

```csharp
        [Description("Required when model='customObjects': the ID of the custom object whose traits should be returned")] string? customObjectId = null,
        [Description("Case-insensitive substring to filter the trait catalogue by label or path (client-side). Useful to trim the otherwise large catalogue.")] string? nameContains = null)
    {
        var queryParams = new Dictionary<string, string> { ["model"] = model };
        if (!string.IsNullOrEmpty(customObjectId))
        {
            queryParams["customObjectId"] = customObjectId;
        }

        if (!string.IsNullOrWhiteSpace(nameContains))
        {
            return await vitallyService.GetRawArrayFilteredAsync("customFields", queryParams, nameContains);
        }

        return await vitallyService.GetRawAsync("customFields", queryParams);
    }
```

(Adjust the closing of the existing method accordingly — the existing body builds `queryParams` then calls `GetRawAsync`; replace with the above.)

- [ ] **Step 4: Run to verify pass**

Run: `dotnet test --filter "FullyQualifiedName~CustomTraitsToolsTests" --nologo`
Expected: PASS (all, incl. the new test).

- [ ] **Step 5: Commit**

```powershell
git add VitallyMcp/VitallyService.cs VitallyMcp/Tools/CustomTraitsTools.cs VitallyMcp.Tests/Tools/CustomTraitsToolsTests.cs
git commit -m @'
feat(traits): add nameContains filter to List_custom_traits (P2.10)

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
'@
```

---

### Task 7: Documentation (`CLAUDE.md`)

**Files:**
- Modify: `CLAUDE.md`

- [ ] **Step 1: Add a page-and-filter note**

In `CLAUDE.md`, near the Tool Structure / client-side filtering discussion, add:

```markdown
**Server-side page-and-filter (SP3):** Vitally's list endpoints can't filter by name or date, so a
bounded auto-pager (`VitallyService.GetByNameContainsAsync` / `GetByCreatedRangeAsync`, capped by
`Vitally:MaxAutoPageFetches`, default 10 pages × 100) pages and filters client-side. The tools that
use it — `List_organizations` (`nameContains`) and the activity lists (`createdAfter`/`createdBefore`
on conversations incl. by-account/by-organization, notes, tasks, meetings) — return a
`{results, truncated, pagesFetched}` envelope; `truncated: true` means the page cap was hit before
exhaustion (narrow the query). `List_custom_traits` takes a `nameContains` that filters the single
trait-catalogue array client-side (no paging). Unfiltered calls keep the plain `{results, next}` path.
```

- [ ] **Step 2: Add the config key**

In the configuration section of `CLAUDE.md` (the `VitallyServerOptions` bullets), add:

```markdown
- `MaxAutoPageFetches` — hard cap on page fetches per server-side filtered call (default 10; 100 items/page). Bounds fan-out against Vitally's 1000 req/min budget.
```

- [ ] **Step 3: Commit**

```powershell
git add CLAUDE.md
git commit -m @'
docs: document SP3 page-and-filter + MaxAutoPageFetches

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
'@
```

---

### Task 8: Live validation (manual — before merge)

> Needs the local server + a Vitally dev key. Per the SP1 lesson, confirm real behaviour.

- [ ] **Step 1: Start the server** (`OAuth__NoAuth=true`, `Vitally__Region=EU`, `Vitally__DevelopmentApiKey=<key>`, `ASPNETCORE_URLS=http://localhost:5099`, `dotnet run --project VitallyMcp/VitallyMcp.csproj`).
- [ ] **Step 2:** `List_organizations(nameContains:"Aberdeen")` → confirm matching org(s) returned, `truncated:false`.
- [ ] **Step 3:** `List_conversations_by_organization(organizationId:<an org>, createdAfter:<date>, createdBefore:<date>)` → confirm only in-window conversations; check `pagesFetched`/`truncated`.
- [ ] **Step 4:** `List_custom_traits(model:"organizations", nameContains:<substr>)` → confirm the catalogue is filtered.
- [ ] **Step 5:** Confirm the `createdAt` sort direction is descending (so early-stop is valid). If Vitally sorts `createdAt` ascending, note it — early-stop would need `sortBy` direction handling; the cap still bounds cost regardless.

---

## Final verification

- [ ] `dotnet test VitallyMcp.sln -c Debug --nologo --verbosity minimal` — all green.
- [ ] Open a PR from `feature/sp3-page-and-filter` into `main`; CI green; merge (squash).
