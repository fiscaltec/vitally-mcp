# SP1 — Custom-object-instance scoping & ergonomics — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Let callers scope custom-object-instance listing to a single organisation/customer/externalId/custom-field, read one instance by id, subset traits, and get a useful default field set — and remove the broken free-text search tool.

**Architecture:** Add typed scope params to `List_custom_object_instances` that route to Vitally's `/search` endpoint (which accepts exactly one criterion) when a scope is given, else keep the plain-list path. Add `Get_custom_object_instance` implemented as `/search?id=…` with single-object unwrapping. Give instances their own default-fields key, decoupled from the URL path. All calls still funnel through `VitallyService.SendAsync` (RBAC + audit preserved).

**Tech Stack:** .NET 10, C#, xUnit + FluentAssertions + Moq (`Moq.Protected` for request-URL verification).

**Branch:** Implement on a new branch off `main`, e.g. `feature/sp1-custom-object-instances` (the planning docs live on `docs/vitally-feedback-decomposition`; do **not** build on that branch). Create it before Task 1.

**Spec:** [SP1 design](../specs/2026-06-10-sp1-custom-object-instances-design.md)

---

## File Structure

- **Modify** `VitallyMcp/VitallyService.cs` — add `customObjectInstances` to `ResourceDefaultFields`; add optional `defaultsKey` to `GetResourcesAsync`; add `SearchCustomObjectInstancesAsync`, `GetCustomObjectInstanceByIdAsync`, and private `FilterSingleFromResults` + `ResolveFields`/`ResolveTraits` helpers.
- **Modify** `VitallyMcp/Tools/CustomObjectsTools.cs` — add scope params + `BuildInstanceSearchCriteria` to `ListCustomObjectInstances`; add `GetCustomObjectInstance`; remove `SearchCustomObjectInstances`.
- **Modify** `VitallyMcp.Tests/TestHelpers.cs` — add `GetSampleRichCustomObjectInstanceJson()`.
- **Modify** `VitallyMcp.Tests/VitallyServiceTests.cs` — add `defaultsKey` and search/get-by-id service tests.
- **Modify** `VitallyMcp.Tests/Tools/CustomObjectsToolsTests.cs` — add scope/throw/get-by-id tests; remove the old search test.
- **Modify** `CLAUDE.md` — default-fields table row + tool-surface note.

---

### Task 0: Create the implementation branch

- [ ] **Step 1: Branch off `main`**

```powershell
git checkout main
git checkout -b feature/sp1-custom-object-instances
```

---

### Task 1: Instance default fields + `defaultsKey` plumbing

**Files:**
- Modify: `VitallyMcp/VitallyService.cs`
- Modify: `VitallyMcp.Tests/TestHelpers.cs`
- Test: `VitallyMcp.Tests/VitallyServiceTests.cs`

- [ ] **Step 1: Add a rich instance sample to TestHelpers**

Add this method to `VitallyMcp.Tests/TestHelpers.cs` (next to `GetSampleCustomObjectInstanceJson`):

```csharp
    /// <summary>
    /// Sample custom object instance JSON with the full field set (for default-field and
    /// trait-subsetting tests). Includes a large field that should be excluded by default.
    /// </summary>
    public static string GetSampleRichCustomObjectInstanceJson() => """
    {
      "results": [
        {
          "id": "inst-123",
          "name": "Annual Goal",
          "externalId": "ext-inst-123",
          "createdAt": "2024-01-01T00:00:00Z",
          "updatedAt": "2024-01-15T00:00:00Z",
          "organizationId": "org-456",
          "customerId": "cust-789",
          "archivedAt": null,
          "descriptionBody": "A very long description body we do not want by default",
          "traits": {
            "target": "100",
            "owner": "CSM"
          }
        }
      ],
      "next": "cursor-inst-next"
    }
    """;
```

- [ ] **Step 2: Write the failing test**

Add to `VitallyMcp.Tests/VitallyServiceTests.cs` (inside the `Default Field Tests` region):

```csharp
    [Fact]
    public async Task GetResourcesAsync_WithDefaultsKey_UsesThatKeysDefaultFields()
    {
        // Arrange
        var mockClient = TestHelpers.CreateMockHttpClient(TestHelpers.GetSampleRichCustomObjectInstanceJson());
        var service = CreateService(mockClient);

        // Act — instance URL path is not an exact-match defaults key, so we pass defaultsKey explicitly
        var result = await service.GetResourcesAsync(
            "customObjects/cobj-1/instances", defaultsKey: "customObjectInstances");

        // Assert — new instance default fields present, large field excluded
        result.Should().Contain("\"organizationId\"");
        result.Should().Contain("\"customerId\"");
        result.Should().Contain("\"name\"");
        result.Should().NotContain("descriptionBody");
    }
```

- [ ] **Step 3: Run test to verify it fails**

Run: `dotnet test --filter "FullyQualifiedName~GetResourcesAsync_WithDefaultsKey_UsesThatKeysDefaultFields"`
Expected: FAIL — `GetResourcesAsync` has no `defaultsKey` parameter (compile error).

- [ ] **Step 4: Add the instance defaults entry**

In `VitallyMcp/VitallyService.cs`, add to the `ResourceDefaultFields` dictionary (after the `meetingTranscripts` line):

```csharp
        ["customObjectInstances"] = ["id", "name", "externalId", "createdAt", "updatedAt", "organizationId", "customerId", "archivedAt"],
```

- [ ] **Step 5: Add the `defaultsKey` parameter to `GetResourcesAsync`**

Change the signature and the final filter call:

```csharp
    public async Task<string> GetResourcesAsync(string resourceType, int limit = 20, string? from = null, string? fields = null, string? sortBy = null, Dictionary<string, string>? additionalParams = null, string? traits = null, string? defaultsKey = null)
    {
```

and the return line at the end of the method:

```csharp
        var jsonResponse = await SendAsync(HttpMethod.Get, url);
        return FilterJsonFields(jsonResponse, fields, defaultsKey ?? resourceType, isListResponse: true, traits);
```

- [ ] **Step 6: Run test to verify it passes**

Run: `dotnet test --filter "FullyQualifiedName~GetResourcesAsync_WithDefaultsKey_UsesThatKeysDefaultFields"`
Expected: PASS

- [ ] **Step 7: Commit**

```powershell
git add VitallyMcp/VitallyService.cs VitallyMcp.Tests/TestHelpers.cs VitallyMcp.Tests/VitallyServiceTests.cs
git commit -m @'
feat(instances): add customObjectInstances default fields + defaultsKey plumbing

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
'@
```

---

### Task 2: `SearchCustomObjectInstancesAsync` service method

**Files:**
- Modify: `VitallyMcp/VitallyService.cs`
- Test: `VitallyMcp.Tests/VitallyServiceTests.cs`

- [ ] **Step 1: Write the failing test**

Add to `VitallyMcp.Tests/VitallyServiceTests.cs`:

```csharp
    [Fact]
    public async Task SearchCustomObjectInstancesAsync_BuildsSearchUrlWithCriterionAndNoLimit()
    {
        // Arrange
        var (client, handler) = TestHelpers.CreateMockHttpClientWithHandler(
            TestHelpers.GetSampleRichCustomObjectInstanceJson());
        var service = CreateService(client);
        var criteria = new Dictionary<string, string> { ["organizationId"] = "org-456" };

        // Act
        var result = await service.SearchCustomObjectInstancesAsync("cobj-123", criteria);

        // Assert — routes to /search with the criterion and NO limit param
        handler.Protected().Verify(
            "SendAsync",
            Times.Once(),
            ItExpr.Is<HttpRequestMessage>(req =>
                req.Method == HttpMethod.Get
                && req.RequestUri!.AbsolutePath == "/resources/customObjects/cobj-123/instances/search"
                && req.RequestUri.Query.Contains("organizationId=org-456")
                && !req.RequestUri.Query.Contains("limit")),
            ItExpr.IsAny<CancellationToken>());

        // Assert — list envelope is filtered and preserved
        result.Should().Contain("\"results\"");
        result.Should().Contain("\"organizationId\"");
    }
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test --filter "FullyQualifiedName~SearchCustomObjectInstancesAsync_BuildsSearchUrlWithCriterionAndNoLimit"`
Expected: FAIL — `SearchCustomObjectInstancesAsync` is not defined (compile error).

- [ ] **Step 3: Implement the method**

In `VitallyMcp/VitallyService.cs`, add after `GetResourceByIdAsync`:

```csharp
    /// <summary>
    /// Searches custom object instances via Vitally's <c>/instances/search</c> endpoint, which
    /// accepts exactly one criterion (where <c>customFieldId</c>+<c>customFieldValue</c> are a
    /// single paired criterion). Only the criterion is sent — no <c>limit</c>/<c>from</c>/<c>sortBy</c>,
    /// since the search endpoint's pagination support is not documented. The standard
    /// <c>{results, next}</c> field/trait filtering is applied; <c>next</c> is passed through if present.
    /// </summary>
    public async Task<string> SearchCustomObjectInstancesAsync(string customObjectId, IReadOnlyDictionary<string, string> criteria, string? fields = null, string? traits = null)
    {
        var query = string.Join("&", criteria.Select(kv => $"{Uri.EscapeDataString(kv.Key)}={Uri.EscapeDataString(kv.Value)}"));
        var url = $"{_baseUrl}/resources/customObjects/{customObjectId}/instances/search?{query}";

        var jsonResponse = await SendAsync(HttpMethod.Get, url);
        return FilterJsonFields(jsonResponse, fields, "customObjectInstances", isListResponse: true, traits);
    }
```

(`System.Linq` is available via implicit global usings — `Select` will resolve.)

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test --filter "FullyQualifiedName~SearchCustomObjectInstancesAsync_BuildsSearchUrlWithCriterionAndNoLimit"`
Expected: PASS

- [ ] **Step 5: Commit**

```powershell
git add VitallyMcp/VitallyService.cs VitallyMcp.Tests/VitallyServiceTests.cs
git commit -m @'
feat(instances): add SearchCustomObjectInstancesAsync (single-criterion /search)

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
'@
```

---

### Task 3: `GetCustomObjectInstanceByIdAsync` + single-result filtering

**Files:**
- Modify: `VitallyMcp/VitallyService.cs`
- Test: `VitallyMcp.Tests/VitallyServiceTests.cs`

- [ ] **Step 1: Write the failing tests**

Add to `VitallyMcp.Tests/VitallyServiceTests.cs`:

```csharp
    [Fact]
    public async Task GetCustomObjectInstanceByIdAsync_WithMatch_ReturnsSingleUnwrappedObject()
    {
        // Arrange
        var (client, handler) = TestHelpers.CreateMockHttpClientWithHandler(
            TestHelpers.GetSampleRichCustomObjectInstanceJson());
        var service = CreateService(client);

        // Act
        var result = await service.GetCustomObjectInstanceByIdAsync("cobj-123", "inst-123");

        // Assert — searched by id, returned a single object (not a {results} envelope)
        handler.Protected().Verify(
            "SendAsync",
            Times.Once(),
            ItExpr.Is<HttpRequestMessage>(req =>
                req.RequestUri!.AbsolutePath == "/resources/customObjects/cobj-123/instances/search"
                && req.RequestUri.Query.Contains("id=inst-123")),
            ItExpr.IsAny<CancellationToken>());
        result.Should().NotContain("\"results\"");
        result.Should().Contain("inst-123");
        result.Should().Contain("\"organizationId\"");
    }

    [Fact]
    public async Task GetCustomObjectInstanceByIdAsync_NoMatch_ReturnsNotFoundMessage()
    {
        // Arrange
        var mockClient = TestHelpers.CreateMockHttpClient(TestHelpers.GetEmptyResultsJson());
        var service = CreateService(mockClient);

        // Act
        var result = await service.GetCustomObjectInstanceByIdAsync("cobj-123", "inst-999");

        // Assert
        result.Should().Contain("No custom object instance found with id inst-999");
    }
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test --filter "FullyQualifiedName~GetCustomObjectInstanceByIdAsync"`
Expected: FAIL — method not defined (compile error).

- [ ] **Step 3: Extract field/trait resolution helpers**

In `VitallyMcp/VitallyService.cs`, add these two helpers (place them just above `FilterJsonFields`):

```csharp
    private static string[] ResolveFields(string? fields, string defaultsKey) =>
        string.IsNullOrWhiteSpace(fields)
            ? (ResourceDefaultFields.TryGetValue(defaultsKey, out var defaults) ? defaults : FallbackDefaultFields)
            : fields.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static string[]? ResolveTraits(string? traits) =>
        string.IsNullOrWhiteSpace(traits)
            ? null
            : traits.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
```

Then replace the first two statements inside `FilterJsonFields` (the `requestedFields` / `requestedTraits` assignments) with:

```csharp
        var requestedFields = ResolveFields(fields, resourceType);
        var requestedTraits = ResolveTraits(traits);
```

- [ ] **Step 4: Add `FilterSingleFromResults` and `GetCustomObjectInstanceByIdAsync`**

In `VitallyMcp/VitallyService.cs`, add the service method after `SearchCustomObjectInstancesAsync`:

```csharp
    /// <summary>
    /// Reads a single custom object instance by id. Vitally has no direct single-instance GET, so
    /// this uses <c>/instances/search?id=…</c> and unwraps the single result. Returns a clear
    /// not-found message (not an exception) when the search yields no match (HTTP 200, empty results).
    /// </summary>
    public async Task<string> GetCustomObjectInstanceByIdAsync(string customObjectId, string instanceId, string? fields = null, string? traits = null)
    {
        var url = $"{_baseUrl}/resources/customObjects/{customObjectId}/instances/search?id={Uri.EscapeDataString(instanceId)}";
        var jsonResponse = await SendAsync(HttpMethod.Get, url);
        return FilterSingleFromResults(jsonResponse, fields, "customObjectInstances", traits,
            notFoundMessage: $"No custom object instance found with id {instanceId}");
    }
```

and the private helper after `FilterJsonFields`:

```csharp
    /// <summary>
    /// Extracts the first element of a <c>{results: [...]}</c> response and returns it as a single
    /// filtered object. Returns <c>{"message": notFoundMessage}</c> when results are absent or empty.
    /// </summary>
    private static string FilterSingleFromResults(string jsonResponse, string? fields, string defaultsKey, string? traits, string notFoundMessage)
    {
        using var document = JsonDocument.Parse(jsonResponse);
        if (!document.RootElement.TryGetProperty("results", out var results)
            || results.ValueKind != JsonValueKind.Array
            || results.GetArrayLength() == 0)
        {
            return JsonSerializer.Serialize(new { message = notFoundMessage });
        }

        var requestedFields = ResolveFields(fields, defaultsKey);
        var requestedTraits = ResolveTraits(traits);

        using var stream = new MemoryStream();
        using var writer = new Utf8JsonWriter(stream);
        WriteFilteredObject(writer, results[0], requestedFields, requestedTraits);
        writer.Flush();

        return Encoding.UTF8.GetString(stream.ToArray());
    }
```

- [ ] **Step 5: Run tests to verify they pass**

Run: `dotnet test --filter "FullyQualifiedName~GetCustomObjectInstanceByIdAsync"`
Expected: PASS (both tests)

- [ ] **Step 6: Run the full service test class to confirm the helper extraction broke nothing**

Run: `dotnet test --filter "FullyQualifiedName~VitallyServiceTests"`
Expected: PASS (all)

- [ ] **Step 7: Commit**

```powershell
git add VitallyMcp/VitallyService.cs VitallyMcp.Tests/VitallyServiceTests.cs
git commit -m @'
feat(instances): add GetCustomObjectInstanceByIdAsync via search-by-id

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
'@
```

---

### Task 4: Scope params + validation on `List_custom_object_instances`

**Files:**
- Modify: `VitallyMcp/Tools/CustomObjectsTools.cs`
- Test: `VitallyMcp.Tests/Tools/CustomObjectsToolsTests.cs`

- [ ] **Step 1: Add Moq imports to the tool test file**

At the top of `VitallyMcp.Tests/Tools/CustomObjectsToolsTests.cs`, add to the using block:

```csharp
using Moq;
using Moq.Protected;
```

- [ ] **Step 2: Write the failing tests**

Add to `VitallyMcp.Tests/Tools/CustomObjectsToolsTests.cs`:

```csharp
    [Fact]
    public async Task ListCustomObjectInstances_WithOrganizationId_RoutesToSearchWithoutLimit()
    {
        // Arrange
        var (client, handler) = TestHelpers.CreateMockHttpClientWithHandler(
            TestHelpers.GetSampleRichCustomObjectInstanceJson());
        var service = CreateService(client);

        // Act
        var result = await CustomObjectsTools.ListCustomObjectInstances(service, "cobj-123", organizationId: "org-456");

        // Assert
        handler.Protected().Verify(
            "SendAsync",
            Times.Once(),
            ItExpr.Is<HttpRequestMessage>(req =>
                req.Method == HttpMethod.Get
                && req.RequestUri!.AbsolutePath == "/resources/customObjects/cobj-123/instances/search"
                && req.RequestUri.Query.Contains("organizationId=org-456")
                && !req.RequestUri.Query.Contains("limit")),
            ItExpr.IsAny<CancellationToken>());
        result.Should().Contain("\"results\"");
    }

    [Fact]
    public async Task ListCustomObjectInstances_WithCustomFieldPair_RoutesToSearchWithBothParams()
    {
        // Arrange
        var (client, handler) = TestHelpers.CreateMockHttpClientWithHandler(
            TestHelpers.GetSampleRichCustomObjectInstanceJson());
        var service = CreateService(client);

        // Act
        await CustomObjectsTools.ListCustomObjectInstances(
            service, "cobj-123", customFieldId: "cf-1", customFieldValue: "Gold");

        // Assert
        handler.Protected().Verify(
            "SendAsync",
            Times.Once(),
            ItExpr.Is<HttpRequestMessage>(req =>
                req.RequestUri!.AbsolutePath == "/resources/customObjects/cobj-123/instances/search"
                && req.RequestUri.Query.Contains("customFieldId=cf-1")
                && req.RequestUri.Query.Contains("customFieldValue=Gold")),
            ItExpr.IsAny<CancellationToken>());
    }

    [Fact]
    public async Task ListCustomObjectInstances_WithTwoScopeCriteria_ThrowsAndMakesNoCall()
    {
        // Arrange
        var (client, handler) = TestHelpers.CreateMockHttpClientWithHandler("""{"results":[]}""");
        var service = CreateService(client);

        // Act
        Func<Task> act = () => CustomObjectsTools.ListCustomObjectInstances(
            service, "cobj-123", organizationId: "org-1", customerId: "cust-1");

        // Assert
        await act.Should().ThrowAsync<ArgumentException>();
        handler.Protected().Verify("SendAsync", Times.Never(),
            ItExpr.IsAny<HttpRequestMessage>(), ItExpr.IsAny<CancellationToken>());
    }

    [Fact]
    public async Task ListCustomObjectInstances_WithCustomFieldIdOnly_Throws()
    {
        // Arrange
        var mockClient = TestHelpers.CreateMockHttpClient("""{"results":[]}""");
        var service = CreateService(mockClient);

        // Act
        Func<Task> act = () => CustomObjectsTools.ListCustomObjectInstances(
            service, "cobj-123", customFieldId: "cf-1");

        // Assert
        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task ListCustomObjectInstances_Unscoped_SendsLimitToPlainListPath()
    {
        // Arrange
        var (client, handler) = TestHelpers.CreateMockHttpClientWithHandler(
            TestHelpers.GetSampleRichCustomObjectInstanceJson());
        var service = CreateService(client);

        // Act
        await CustomObjectsTools.ListCustomObjectInstances(service, "cobj-123");

        // Assert
        handler.Protected().Verify(
            "SendAsync",
            Times.Once(),
            ItExpr.Is<HttpRequestMessage>(req =>
                req.RequestUri!.AbsolutePath == "/resources/customObjects/cobj-123/instances"
                && req.RequestUri.Query.Contains("limit=20")),
            ItExpr.IsAny<CancellationToken>());
    }

    [Fact]
    public async Task ListCustomObjectInstances_Unscoped_AppliesInstanceDefaultFields()
    {
        // Arrange
        var mockClient = TestHelpers.CreateMockHttpClient(TestHelpers.GetSampleRichCustomObjectInstanceJson());
        var service = CreateService(mockClient);

        // Act
        var result = await CustomObjectsTools.ListCustomObjectInstances(service, "cobj-123");

        // Assert — new defaults applied, large field excluded
        result.Should().Contain("\"organizationId\"");
        result.Should().Contain("\"name\"");
        result.Should().NotContain("descriptionBody");
    }

    [Fact]
    public async Task ListCustomObjectInstances_WithTraits_SubsetsTraits()
    {
        // Arrange
        var mockClient = TestHelpers.CreateMockHttpClient(TestHelpers.GetSampleRichCustomObjectInstanceJson());
        var service = CreateService(mockClient);

        // Act — request only the 'target' trait
        var result = await CustomObjectsTools.ListCustomObjectInstances(
            service, "cobj-123", fields: "id,traits", traits: "target");

        // Assert — only the requested trait survives
        result.Should().Contain("target");
        result.Should().NotContain("owner");
    }
```

- [ ] **Step 3: Run tests to verify they fail**

Run: `dotnet test --filter "FullyQualifiedName~CustomObjectsToolsTests.ListCustomObjectInstances_With|FullyQualifiedName~CustomObjectsToolsTests.ListCustomObjectInstances_Unscoped"`
Expected: FAIL — `ListCustomObjectInstances` has no `organizationId`/`customFieldId`/etc. parameters (compile error).

- [ ] **Step 4: Replace `ListCustomObjectInstances` and add the criteria builder**

In `VitallyMcp/Tools/CustomObjectsTools.cs`, replace the existing `ListCustomObjectInstances` method with:

```csharp
    [McpServerTool(Name = "List_custom_object_instances", Title = "List custom object instances", ReadOnly = true, Destructive = false), Description("List instances of a Vitally custom object. Optionally scope to a single organisation, customer, external id, or custom-field value — Vitally allows exactly ONE scope criterion. When a scope is supplied the limit/from/sortBy paging params are ignored (the matching set is returned as Vitally provides it).")]
    public static async Task<string> ListCustomObjectInstances(
        VitallyService vitallyService,
        [Description("The custom object ID")] string customObjectId,
        [Description("Maximum number of instances for an UNSCOPED list (default: 20, max: 100). Ignored when a scope criterion is supplied.")] int limit = 20,
        [Description("Pagination cursor for an UNSCOPED list (use the 'next' value). Ignored when a scope criterion is supplied.")] string? from = null,
        [Description("Comma-separated list of fields to include. Defaults to: id,name,externalId,createdAt,updatedAt,organizationId,customerId,archivedAt. Client-side filtering.")] string? fields = null,
        [Description("Sort an UNSCOPED list by 'createdAt' or 'updatedAt' (default: updatedAt). Ignored when a scope criterion is supplied.")] string? sortBy = null,
        [Description("Comma-separated trait names to include (requires 'traits' in fields). Client-side filtering.")] string? traits = null,
        [Description("Scope to a single organisation ID. Mutually exclusive with the other scope criteria.")] string? organizationId = null,
        [Description("Scope to a single customer/account ID. Mutually exclusive with the other scope criteria.")] string? customerId = null,
        [Description("Scope to a single external ID. Mutually exclusive with the other scope criteria.")] string? externalId = null,
        [Description("Find instances where this custom field ID equals customFieldValue. Must be supplied together with customFieldValue.")] string? customFieldId = null,
        [Description("The value to match for customFieldId. Must be supplied together with customFieldId.")] string? customFieldValue = null)
    {
        var criteria = BuildInstanceSearchCriteria(organizationId, customerId, externalId, customFieldId, customFieldValue);

        if (criteria.Count == 0)
        {
            return await vitallyService.GetResourcesAsync(
                $"customObjects/{customObjectId}/instances", limit, from, fields, sortBy,
                additionalParams: null, traits: traits, defaultsKey: "customObjectInstances");
        }

        return await vitallyService.SearchCustomObjectInstancesAsync(customObjectId, criteria, fields, traits);
    }

    /// <summary>
    /// Builds the single-criterion search dictionary for instance scoping and validates Vitally's
    /// "exactly one criterion" rule (customFieldId+customFieldValue count as one paired criterion).
    /// Throws <see cref="ArgumentException"/> with an actionable message if the rule is violated.
    /// Returns an empty dictionary when no scope is supplied (caller uses the plain-list path).
    /// </summary>
    internal static Dictionary<string, string> BuildInstanceSearchCriteria(
        string? organizationId, string? customerId, string? externalId,
        string? customFieldId, string? customFieldValue)
    {
        var criteria = new Dictionary<string, string>();
        if (!string.IsNullOrWhiteSpace(organizationId)) criteria["organizationId"] = organizationId;
        if (!string.IsNullOrWhiteSpace(customerId)) criteria["customerId"] = customerId;
        if (!string.IsNullOrWhiteSpace(externalId)) criteria["externalId"] = externalId;

        var hasFieldId = !string.IsNullOrWhiteSpace(customFieldId);
        var hasFieldValue = !string.IsNullOrWhiteSpace(customFieldValue);
        if (hasFieldId != hasFieldValue)
        {
            throw new ArgumentException("customFieldId and customFieldValue must be supplied together.");
        }

        var criterionGroups = criteria.Count + (hasFieldId ? 1 : 0);
        if (criterionGroups > 1)
        {
            throw new ArgumentException(
                "Vitally instance search accepts exactly one of organizationId, customerId, externalId, or customFieldId+customFieldValue.");
        }

        if (hasFieldId)
        {
            criteria["customFieldId"] = customFieldId!;
            criteria["customFieldValue"] = customFieldValue!;
        }

        return criteria;
    }
```

- [ ] **Step 5: Run tests to verify they pass**

Run: `dotnet test --filter "FullyQualifiedName~CustomObjectsToolsTests.ListCustomObjectInstances_With|FullyQualifiedName~CustomObjectsToolsTests.ListCustomObjectInstances_Unscoped"`
Expected: PASS (all six)

- [ ] **Step 6: Commit**

```powershell
git add VitallyMcp/Tools/CustomObjectsTools.cs VitallyMcp.Tests/Tools/CustomObjectsToolsTests.cs
git commit -m @'
feat(instances): scope List_custom_object_instances by single /search criterion

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
'@
```

---

### Task 5: `Get_custom_object_instance` tool

**Files:**
- Modify: `VitallyMcp/Tools/CustomObjectsTools.cs`
- Test: `VitallyMcp.Tests/Tools/CustomObjectsToolsTests.cs`

- [ ] **Step 1: Write the failing tests**

Add to `VitallyMcp.Tests/Tools/CustomObjectsToolsTests.cs`:

```csharp
    [Fact]
    public async Task GetCustomObjectInstance_WithMatch_ReturnsSingleObject()
    {
        // Arrange
        var (client, handler) = TestHelpers.CreateMockHttpClientWithHandler(
            TestHelpers.GetSampleRichCustomObjectInstanceJson());
        var service = CreateService(client);

        // Act
        var result = await CustomObjectsTools.GetCustomObjectInstance(service, "cobj-123", "inst-123");

        // Assert
        handler.Protected().Verify(
            "SendAsync",
            Times.Once(),
            ItExpr.Is<HttpRequestMessage>(req =>
                req.RequestUri!.AbsolutePath == "/resources/customObjects/cobj-123/instances/search"
                && req.RequestUri.Query.Contains("id=inst-123")),
            ItExpr.IsAny<CancellationToken>());
        result.Should().NotContain("\"results\"");
        result.Should().Contain("inst-123");
    }

    [Fact]
    public async Task GetCustomObjectInstance_NoMatch_ReturnsNotFoundMessage()
    {
        // Arrange
        var mockClient = TestHelpers.CreateMockHttpClient(TestHelpers.GetEmptyResultsJson());
        var service = CreateService(mockClient);

        // Act
        var result = await CustomObjectsTools.GetCustomObjectInstance(service, "cobj-123", "inst-999");

        // Assert
        result.Should().Contain("No custom object instance found with id inst-999");
    }
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test --filter "FullyQualifiedName~CustomObjectsToolsTests.GetCustomObjectInstance"`
Expected: FAIL — `GetCustomObjectInstance` not defined (compile error).

- [ ] **Step 3: Add the tool method**

In `VitallyMcp/Tools/CustomObjectsTools.cs`, add after `ListCustomObjectInstances` (before the `Create_custom_object` method):

```csharp
    [McpServerTool(Name = "Get_custom_object_instance", Title = "Get custom object instance", ReadOnly = true, Destructive = false), Description("Get a single custom object instance by its ID. Implemented via Vitally's instance search (Vitally has no direct single-instance GET). Returns a not-found message if the ID does not match.")]
    public static async Task<string> GetCustomObjectInstance(
        VitallyService vitallyService,
        [Description("The custom object ID")] string customObjectId,
        [Description("The instance ID")] string instanceId,
        [Description("Comma-separated list of fields to include. Defaults to: id,name,externalId,createdAt,updatedAt,organizationId,customerId,archivedAt. Client-side filtering.")] string? fields = null,
        [Description("Comma-separated trait names to include (requires 'traits' in fields). Client-side filtering.")] string? traits = null)
    {
        return await vitallyService.GetCustomObjectInstanceByIdAsync(customObjectId, instanceId, fields, traits);
    }
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test --filter "FullyQualifiedName~CustomObjectsToolsTests.GetCustomObjectInstance"`
Expected: PASS (both)

- [ ] **Step 5: Commit**

```powershell
git add VitallyMcp/Tools/CustomObjectsTools.cs VitallyMcp.Tests/Tools/CustomObjectsToolsTests.cs
git commit -m @'
feat(instances): add Get_custom_object_instance (search-by-id)

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
'@
```

---

### Task 6: Remove the broken `Search_custom_object_instances` tool

**Files:**
- Modify: `VitallyMcp/Tools/CustomObjectsTools.cs`
- Modify: `VitallyMcp.Tests/Tools/CustomObjectsToolsTests.cs`

- [ ] **Step 1: Delete the obsolete test**

In `VitallyMcp.Tests/Tools/CustomObjectsToolsTests.cs`, delete the entire
`SearchCustomObjectInstances_WithQuery_ShouldReturnMatchingInstances` test method (lines ~90–103).

- [ ] **Step 2: Delete the tool method**

In `VitallyMcp/Tools/CustomObjectsTools.cs`, delete the entire `SearchCustomObjectInstances`
method (the `[McpServerTool(Name = "Search_custom_object_instances", …)]` method and its inner
`SafeDecode` local function — the whole method block).

- [ ] **Step 3: Run the full test suite**

Run: `dotnet test VitallyMcp.sln -c Debug --nologo --verbosity minimal`
Expected: PASS — no reference to the removed method remains; all tests green.

- [ ] **Step 4: Commit**

```powershell
git add VitallyMcp/Tools/CustomObjectsTools.cs VitallyMcp.Tests/Tools/CustomObjectsToolsTests.cs
git commit -m @'
refactor(instances): remove broken Search_custom_object_instances tool

Capabilities are now covered by scoped List_custom_object_instances and
Get_custom_object_instance.

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
'@
```

---

### Task 7: Documentation (`CLAUDE.md`)

**Files:**
- Modify: `CLAUDE.md`

- [ ] **Step 1: Add the default-fields table row**

In `CLAUDE.md`, in the "Resource-Specific Default Fields" table, add a row after the
**Meeting Transcripts** row:

```markdown
| **Custom Object Instances** | id, name, externalId, createdAt, updatedAt, organizationId, customerId, archivedAt |
```

- [ ] **Step 2: Add a tool-surface note**

In `CLAUDE.md`, under the custom-objects discussion (near the Tool Structure section), add:

```markdown
**Custom object instances:** `List_custom_object_instances` accepts an optional single scope
criterion — `organizationId`, `customerId`, `externalId`, or `customFieldId`+`customFieldValue`
— which routes to Vitally's `customObjects/:id/instances/search` endpoint (exactly one criterion;
paging params are ignored when scoped). `Get_custom_object_instance` reads one instance by id via
the same search endpoint (Vitally has no direct single-instance GET). The legacy free-text
`Search_custom_object_instances` tool has been removed in favour of these typed paths.
```

- [ ] **Step 3: Commit**

```powershell
git add CLAUDE.md
git commit -m @'
docs: document custom-object-instance scoping & get-by-id in CLAUDE.md

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
'@
```

---

### Task 8: Live `/search` smoke test (manual — before merge)

> This is a manual verification, not an automated test. It resolves the one open item in the spec
> (§6): whether `/instances/search` returns the `{results, next}` envelope and whether it honours
> `from`/`limit`. Requires a Vitally dev API key.

- [ ] **Step 1: Start the server in dev mode**

```powershell
$env:OAuth__NoAuth = "true"
$env:Vitally__Region = "EU"
$env:Vitally__DevelopmentApiKey = "sk_live_your_key"
$env:ASPNETCORE_URLS = "http://localhost:5099"
dotnet run --project VitallyMcp/VitallyMcp.csproj
```

- [ ] **Step 2: Call the scoped list against a known custom object + organisation**

Use a real `customObjectId` and `organizationId` (e.g. the `customerGoals` object and an org with
a handful of goals). Through your MCP client or a direct `tools/call`, invoke
`List_custom_object_instances` with `organizationId` set, and confirm:
- only that organisation's instances come back,
- the response is the `{results, next}` shape (or note if `next` is absent),
- the payload is small (one cheap call — the headline win).

- [ ] **Step 3: Record the finding**

If `/search` **does** honour `from` (i.e. `next` is populated and a follow-up call with it pages),
open a follow-up task to wire `from` (and a client-side `limit` cap) into
`SearchCustomObjectInstancesAsync`. If it does not, no action — the scoped set is returned whole.
Note the outcome in the PR description.

---

## Final verification

- [ ] Run the full suite: `dotnet test VitallyMcp.sln -c Debug --nologo --verbosity minimal` — all green.
- [ ] Confirm `Search_custom_object_instances` no longer appears in `tools/list` (smoke-test `tools/list` per the CLAUDE.md instructions).
- [ ] Open a PR from `feature/sp1-custom-object-instances` into `main`.
