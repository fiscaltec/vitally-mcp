# MCP SDK 2.0 / spec 2026-07-28 adoption Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Adopt the MCP SDK 2.0 capabilities that benefit this server — per-caller tool authorisation, spec-compliant OAuth discovery, `tools/list` cache hints and complete tool annotations — each proven by in-process tests before any deployment.

**Architecture:** All changes are additive to the existing composition root in `VitallyMcp/Program.cs` and follow the established filter/options patterns. Authorisation moves from *enforcement only* (in `VitallyService.SendAsync`) to *enforcement plus discovery filtering*, by bridging the existing permission resolution into ASP.NET Core authorization policies that the SDK's `AddAuthorizationFilters()` consumes. `VitallyService.SendAsync` remains the backstop and is not weakened.

**Tech Stack:** .NET 10, ASP.NET Core, `ModelContextProtocol` + `ModelContextProtocol.AspNetCore` 2.1.0, xUnit v3 + FluentAssertions + Moq, `WebApplicationFactory<Program>`.

## Global Constraints

- **UK English** in all comments and documentation (organisation, authorisation, behaviour).
- **LF line endings.** Normalise every touched file before committing: `sed -i 's/\r$//' <file>`.
- **Zero build warnings.** `dotnet build` must stay at 0 warnings. A new `MCP9004`/`MCP9005`/`MCP9006` warning means a deprecated SDK API was used — fix, do not suppress.
- **Never call the Vitally API outside `VitallyService.SendAsync`** — it is the single RBAC + audit choke point.
- **`Vitally__DevelopmentApiKey` only for local dev.** Never hardcode credentials.
- Verified SDK wire facts (confirmed by reflection against 2.0.0, do not re-derive):
  - `ListToolsResult.TimeToLive` is `TimeSpan?`, serialises as **`ttlMs`** (integer milliseconds).
  - `ListToolsResult.CacheScope` is `CacheScope?`, serialises as **`cacheScope`** with values **`"public"`** / **`"private"`**.
  - `ProtectedResourceMetadata` fields: `resource`, `authorization_servers` (`IList<string>`), `bearer_methods_supported`, `scopes_supported`, `resource_name`.
  - `McpAuthenticationOptions` has `ResourceMetadata` (`ProtectedResourceMetadata`) and `ResourceMetadataUri` (`Uri`).
- **Existing suites must stay green**, in particular `ReadOnlyToolsListTests`, `ToolAuthorizerTests`, `VitallyServiceAuthorizationTests`, `OAuthProxyEndpointsTests`.
- **Every integration test class that sets environment variables MUST carry
  `[Collection(IntegrationTestCollection.Name)]`.** `Program.cs` reads `OAuth:NoAuth` and
  `Authorization:ReadOnly` at composition time, so the fixtures can only override them through
  process-wide environment variables. xUnit runs test classes in parallel by default and the test
  project has no parallelisation control, so two fixtures setting `OAuth__NoAuth` to different
  values will race and produce order-dependent flakiness. The shared collection serialises them.
  For the same reason **each fixture must set every variable it depends on explicitly**, never
  relying on a default, so a value leaked by a sibling class is deterministically overwritten.
  This applies to `ToolsListCachingTests`, `AuthorizationFilterToolsListTests`,
  `ResourceMetadataDiscoveryTests` and the existing `ReadOnlyToolsListTests`.
- Validation gates are defined in `docs/superpowers/specs/2026-08-10-mcp-sdk2-validation-design.md`. This plan delivers **Layer 1** in full; Layers 2 and 3 are manual and covered by Task 8.

## Out of scope

MRTR / elicitation confirmation on destructive tools (change 4 of the spec) is **deliberately excluded**. It requires design decisions the spec does not settle: which destructive tools require confirmation, the prompt copy, and the behaviour when a client does not support MRTR (whether the operation fails closed or proceeds). It needs its own brainstorm and plan.

## File Structure

| File | Responsibility | Action |
|---|---|---|
| `VitallyMcp/VitallyMcp.csproj` | Package versions | Modify — bump SDK to 2.1.0 |
| `VitallyMcp/ToolsListCacheOptions.cs` | TTL + cache scope config for `tools/list` | **Create** |
| `VitallyMcp/VitallyPermissionRequirement.cs` | Authorization requirement carrying a `vitally:*` permission | **Create** |
| `VitallyMcp/VitallyPermissionHandler.cs` | Bridges the requirement to the existing permission resolution | **Create** |
| `VitallyMcp/ToolAuthorizer.cs` | Add a public resolution method the handler can call | Modify |
| `VitallyMcp/ProtectedResourceMetadataBuilder.cs` | Builds the RFC 9728 document from `OAuthOptions` | **Create** |
| `VitallyMcp/Program.cs` | Wiring: policies, `AddAuthorizationFilters()`, TTL filter, auth scheme | Modify |
| `VitallyMcp/Tools/*.cs` | `[Authorize]` + `Idempotent`/`OpenWorld` annotations | Modify (16 files) |
| `VitallyMcp.Tests/ToolsListCachingTests.cs` | Asserts `ttlMs` / `cacheScope` on the wire | **Create** |
| `VitallyMcp.Tests/ToolAnnotationCoverageTests.cs` | Reflection sweep: every tool correctly annotated | **Create** |
| `VitallyMcp.Tests/AuthorizationFilterToolsListTests.cs` | Per-tier `tools/list` filtering | **Create** |
| `VitallyMcp.Tests/VitallyPermissionHandlerTests.cs` | Handler unit tests | **Create** |
| `VitallyMcp.Tests/ResourceMetadataDiscoveryTests.cs` | 401 challenge + metadata endpoints | **Create** |
| `VitallyMcp.Tests/TestAuthHandler.cs` | Shared synthetic-principal auth scheme for tests | **Create** |
| `CLAUDE.md` | Correct stale SDK/protocol versions | Modify |
| `docs/runbooks/mcp-sdk2-staging-validation.md` | Staging provisioning + teardown | **Create** |

---

### Task 1: Bump the SDK to 2.1.0

**Files:**
- Modify: `VitallyMcp/VitallyMcp.csproj:12-13`

**Interfaces:**
- Consumes: nothing.
- Produces: SDK 2.1.0 available to all later tasks.

- [ ] **Step 1: Bump both package versions**

In `VitallyMcp/VitallyMcp.csproj`, change both MCP package references from `2.0.0` to `2.1.0`:

```xml
    <PackageReference Include="ModelContextProtocol" Version="2.1.0" />
    <PackageReference Include="ModelContextProtocol.AspNetCore" Version="2.1.0" />
```

- [ ] **Step 2: Build and confirm zero warnings**

Run: `dotnet build VitallyMcp.sln -c Debug --nologo -v minimal`
Expected: `Build succeeded. 0 Warning(s) 0 Error(s)`

If any `MCP9004`/`MCP9005`/`MCP9006` warning appears, stop and report it — it means 2.1.0 deprecated something in current use.

- [ ] **Step 3: Run the full suite**

Run: `dotnet test VitallyMcp.sln -c Debug --nologo --verbosity minimal`
Expected: all tests pass.

- [ ] **Step 4: Commit**

```bash
sed -i 's/\r$//' VitallyMcp/VitallyMcp.csproj
git add VitallyMcp/VitallyMcp.csproj
git commit -m "build: Bump MCP SDK to 2.1.0"
```

---

### Task 2: Add `tools/list` cache hints

The 2026-07-28 spec lets a server tell clients how long to cache `tools/list`. With 95 tools this is the largest response the server emits, and re-sending it on every session costs model context.

**Files:**
- Create: `VitallyMcp/ToolsListCacheOptions.cs`
- Modify: `VitallyMcp/Program.cs` (options binding near line 28; list-tools filter near line 121)
- Create: `VitallyMcp.Tests/IntegrationTestCollection.cs`
- Create: `VitallyMcp.Tests/ToolsListCachingTests.cs`
- Modify: `VitallyMcp.Tests/ReadOnlyToolsListTests.cs` (add the collection attribute)

**Interfaces:**
- Consumes: nothing.
- Produces: `ToolsListCacheOptions` with `SectionName = "ToolsListCache"`, `TimeToLive` (`TimeSpan`, default 5 min), `Scope` (`CacheScope`, default `Private`), `Enabled` (`bool`, default `true`); and `IntegrationTestCollection.Name` — the xUnit collection name every env-var-mutating integration test class must join (used again in Tasks 5 and 6).

- [ ] **Step 0: Create the shared test collection**

Environment variables are process-wide, so the integration fixtures must not run concurrently. Create `VitallyMcp.Tests/IntegrationTestCollection.cs`:

```csharp
namespace VitallyMcp.Tests;

/// <summary>
/// Serialises every integration test class that overrides configuration through environment
/// variables. <c>Program.cs</c> reads <c>OAuth:NoAuth</c> and <c>Authorization:ReadOnly</c> at
/// composition time — before <see cref="Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory{T}"/>
/// can inject configuration — so environment variables are the only override that works. They are
/// process-wide, and xUnit runs test classes in parallel by default, so without this collection two
/// fixtures setting <c>OAuth__NoAuth</c> to different values race and fail depending on scheduling.
/// </summary>
[CollectionDefinition(Name)]
public class IntegrationTestCollection
{
    public const string Name = "Integration (serialised: mutates environment variables)";
}
```

Then add `[Collection(IntegrationTestCollection.Name)]` to the existing `ReadOnlyToolsListTests` class declaration in `VitallyMcp.Tests/ReadOnlyToolsListTests.cs`:

```csharp
[Collection(IntegrationTestCollection.Name)]
public class ReadOnlyToolsListTests : IClassFixture<ReadOnlyToolsListTests.Factory>
```

- [ ] **Step 1: Write the failing test**

Create `VitallyMcp.Tests/ToolsListCachingTests.cs`:

```csharp
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;

namespace VitallyMcp.Tests;

/// <summary>
/// Asserts the serialised wire form of the tools/list cache hints added in the 2026-07-28 spec.
/// The property names are deliberately asserted as raw JSON (`ttlMs`, `cacheScope`) rather than
/// via the SDK's CLR properties, so a future SDK rename is caught here rather than by clients.
/// </summary>
[Collection(IntegrationTestCollection.Name)]
public class ToolsListCachingTests : IClassFixture<ToolsListCachingTests.Factory>
{
    private readonly Factory _factory;

    public ToolsListCachingTests(Factory factory) => _factory = factory;

    [Fact]
    public async Task ToolsList_AdvertisesTtlAndPrivateCacheScope()
    {
        using var client = _factory.CreateClient();

        var body = new StringContent(
            """{"jsonrpc":"2.0","id":1,"method":"tools/list"}""",
            Encoding.UTF8,
            "application/json");
        body.Headers.Remove("Content-Type");
        body.Headers.TryAddWithoutValidation("Content-Type", "application/json");

        var request = new HttpRequestMessage(HttpMethod.Post, "/mcp") { Content = body };
        request.Headers.TryAddWithoutValidation("Accept", "application/json, text/event-stream");

        var response = await client.SendAsync(request);
        var text = await response.Content.ReadAsStringAsync();
        var json = ExtractJson(text);

        using var doc = JsonDocument.Parse(json);
        var result = doc.RootElement.GetProperty("result");

        result.TryGetProperty("ttlMs", out var ttl).Should().BeTrue("the 2026-07-28 cache hint must be present");
        ttl.GetInt64().Should().Be(300_000, "the default TTL is 5 minutes expressed in milliseconds");

        result.TryGetProperty("cacheScope", out var scope).Should().BeTrue();
        scope.GetString().Should().Be("private",
            "the list is per-caller once authorization filtering is enabled, so it must not be cached publicly");
    }

    /// <summary>Streamable HTTP may frame the response as SSE; pull the JSON payload out either way.</summary>
    private static string ExtractJson(string raw)
    {
        if (!raw.Contains("data:", StringComparison.Ordinal))
        {
            return raw;
        }

        var line = raw.Split('\n').First(l => l.StartsWith("data:", StringComparison.Ordinal));
        return line["data:".Length..].Trim();
    }

    public class Factory : WebApplicationFactory<Program>
    {
        protected override IHost CreateHost(IHostBuilder builder)
        {
            // Program.cs reads these at composition time, before WebApplicationFactory can inject
            // configuration, so environment variables are the only reliable override. Same
            // constraint documented in ReadOnlyToolsListTests. Every variable this fixture depends
            // on is set explicitly — they are process-wide, so a value left behind by a sibling
            // class must be overwritten rather than inherited.
            Environment.SetEnvironmentVariable("OAuth__NoAuth", "true");
            Environment.SetEnvironmentVariable("Authorization__ReadOnly", "false");
            Environment.SetEnvironmentVariable("Vitally__DevelopmentApiKey", "sk_test_dummy");
            Environment.SetEnvironmentVariable("Vitally__Region", "EU");
            return base.CreateHost(builder);
        }
    }
}
```

Add the missing usings at the top if the build complains: `using Microsoft.Extensions.Hosting;`

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test --filter "FullyQualifiedName~ToolsListCachingTests" -v minimal`
Expected: FAIL — `ttlMs` is absent, because nothing sets it yet.

- [ ] **Step 3: Create the options class**

Create `VitallyMcp/ToolsListCacheOptions.cs`:

```csharp
using ModelContextProtocol.Protocol;

namespace VitallyMcp;

/// <summary>
/// Cache hints advertised on tools/list, per the 2026-07-28 MCP spec. With 95 tools this is the
/// largest response the server emits, so letting clients cache it avoids re-sending the whole
/// catalogue every session.
///
/// <para>
/// The scope defaults to <see cref="CacheScope.Private"/> because the list is filtered per caller
/// once authorisation filtering is active — a shared cache would leak one tier's tool catalogue to
/// another. Only set <see cref="CacheScope.Public"/> if per-caller filtering is ever removed.
/// </para>
/// </summary>
public class ToolsListCacheOptions
{
    public const string SectionName = "ToolsListCache";

    /// <summary>Set false to advertise no cache hints at all (clients then re-list every session).</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>How long clients may cache tools/list. Serialised as `ttlMs`.</summary>
    public TimeSpan TimeToLive { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>Cache scope. Serialised as `cacheScope` ("private"/"public").</summary>
    public CacheScope Scope { get; set; } = CacheScope.Private;

    public void Validate()
    {
        if (TimeToLive < TimeSpan.Zero)
        {
            throw new InvalidOperationException(
                $"{SectionName}:TimeToLive must not be negative (was {TimeToLive}).");
        }
    }
}
```

- [ ] **Step 4: Bind the options in Program.cs**

In `VitallyMcp/Program.cs`, immediately after the `AuditOptions` binding (around line 28-29), add:

```csharp
builder.Services.AddOptions<ToolsListCacheOptions>()
    .Bind(builder.Configuration.GetSection(ToolsListCacheOptions.SectionName))
    .PostConfigure(o => o.Validate());
```

- [ ] **Step 5: Read the options at composition time**

In `VitallyMcp/Program.cs`, next to the existing `readOnlyMode` local (around line 95), add:

```csharp
// Bound separately for the list-tools filter below, which is constructed at composition time.
var toolsListCache = builder.Configuration.GetSection(ToolsListCacheOptions.SectionName)
    .Get<ToolsListCacheOptions>() ?? new ToolsListCacheOptions();
toolsListCache.Validate();
```

- [ ] **Step 6: Set the hints in the list-tools filter**

In `VitallyMcp/Program.cs`, inside `mcpBuilder.WithRequestFilters(...)`, add a new filter *before* the existing read-only filter:

```csharp
    // Advertise how long clients may cache tools/list (2026-07-28 spec). Private scope because
    // the list is per-caller once authorisation filtering is on.
    if (toolsListCache.Enabled)
    {
        filters.AddListToolsFilter(next => async (context, cancellationToken) =>
        {
            var result = await next(context, cancellationToken);
            result.TimeToLive = toolsListCache.TimeToLive;
            result.CacheScope = toolsListCache.Scope;
            return result;
        });
    }
```

- [ ] **Step 7: Run the test to verify it passes**

Run: `dotnet test --filter "FullyQualifiedName~ToolsListCachingTests" -v minimal`
Expected: PASS.

- [ ] **Step 8: Run the full suite and build**

Run: `dotnet test VitallyMcp.sln -c Debug --nologo --verbosity minimal`
Expected: all pass, 0 warnings.

- [ ] **Step 9: Commit**

```bash
sed -i 's/\r$//' VitallyMcp/ToolsListCacheOptions.cs VitallyMcp/Program.cs VitallyMcp.Tests/ToolsListCachingTests.cs
git add VitallyMcp/ToolsListCacheOptions.cs VitallyMcp/Program.cs VitallyMcp.Tests/ToolsListCachingTests.cs
git commit -m "feat: Advertise tools/list cache hints per MCP 2026-07-28"
```

---

### Task 3: Complete the tool annotations

Every tool sets `ReadOnly` and `Destructive`, but none sets `Idempotent` or `OpenWorld`. Clients use these to decide whether a failed call is safe to retry. A reflection coverage test enforces the rule rather than trusting 95 hand edits.

**Files:**
- Create: `VitallyMcp.Tests/ToolAnnotationCoverageTests.cs`
- Modify: all 16 files in `VitallyMcp/Tools/`

**Interfaces:**
- Consumes: nothing.
- Produces: an invariant later tasks rely on — every `[McpServerTool]` sets `ReadOnly`, `Destructive`, `Idempotent` and `OpenWorld` explicitly.

**Annotation rules** (apply exactly; these are the semantics MCP defines):

| Tool prefix | `ReadOnly` | `Destructive` | `Idempotent` | `OpenWorld` |
|---|---|---|---|---|
| `List_*`, `Get_*` | `true` | `false` | `true` | `false` |
| `Create_*` | `false` | `true` | `false` | `false` |
| `Update_*` | `false` | `true` | `true` | `false` |
| `Delete_*` | `false` | `true` | `true` | `false` |

`Idempotent = false` for creates only, because repeating a create makes a second record. Updates and deletes are idempotent — repeating them lands the same final state. `OpenWorld = false` throughout: the tools address one closed Vitally tenant, not the open internet.

- [ ] **Step 1: Write the failing coverage test**

Create `VitallyMcp.Tests/ToolAnnotationCoverageTests.cs`:

```csharp
using System.Reflection;
using FluentAssertions;
using ModelContextProtocol.Server;

namespace VitallyMcp.Tests;

/// <summary>
/// Reflection sweep over every [McpServerTool] method, asserting the four annotation hints are set
/// consistently with the tool's name prefix. This is enforcement rather than documentation: a new
/// tool added without annotations fails here instead of shipping with misleading retry semantics.
/// </summary>
public class ToolAnnotationCoverageTests
{
    private static IEnumerable<(string Name, McpServerToolAttribute Attr)> AllTools()
    {
        var assembly = typeof(VitallyService).Assembly;
        foreach (var type in assembly.GetTypes().Where(t => t.GetCustomAttribute<McpServerToolTypeAttribute>() is not null))
        {
            foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            {
                var attr = method.GetCustomAttribute<McpServerToolAttribute>();
                if (attr is not null)
                {
                    yield return (attr.Name ?? method.Name, attr);
                }
            }
        }
    }

    [Fact]
    public void EveryToolIsDiscovered()
    {
        AllTools().Should().HaveCountGreaterThan(90, "the server exposes ~95 tools; a big drop means discovery broke");
    }

    [Theory]
    [InlineData("List_")]
    [InlineData("Get_")]
    public void ReadTools_AreReadOnlyIdempotentAndClosedWorld(string prefix)
    {
        var tools = AllTools().Where(t => t.Name.StartsWith(prefix, StringComparison.Ordinal)).ToList();
        tools.Should().NotBeEmpty($"there must be tools named {prefix}*");

        foreach (var (name, attr) in tools)
        {
            attr.ReadOnly.Should().Be(true, $"{name} only reads");
            attr.Destructive.Should().Be(false, $"{name} only reads");
            attr.Idempotent.Should().Be(true, $"{name} is safe to repeat");
            attr.OpenWorld.Should().Be(false, $"{name} addresses one closed Vitally tenant");
        }
    }

    [Fact]
    public void CreateTools_AreDestructiveAndNotIdempotent()
    {
        var tools = AllTools().Where(t => t.Name.StartsWith("Create_", StringComparison.Ordinal)).ToList();
        tools.Should().NotBeEmpty();

        foreach (var (name, attr) in tools)
        {
            attr.ReadOnly.Should().Be(false, $"{name} mutates");
            attr.Destructive.Should().Be(true, $"{name} mutates");
            attr.Idempotent.Should().Be(false, $"repeating {name} would create a second record");
            attr.OpenWorld.Should().Be(false);
        }
    }

    [Theory]
    [InlineData("Update_")]
    [InlineData("Delete_")]
    public void UpdateAndDeleteTools_AreDestructiveAndIdempotent(string prefix)
    {
        var tools = AllTools().Where(t => t.Name.StartsWith(prefix, StringComparison.Ordinal)).ToList();
        tools.Should().NotBeEmpty($"there must be tools named {prefix}*");

        foreach (var (name, attr) in tools)
        {
            attr.ReadOnly.Should().Be(false, $"{name} mutates");
            attr.Destructive.Should().Be(true, $"{name} mutates");
            attr.Idempotent.Should().Be(true, $"repeating {name} lands the same final state");
            attr.OpenWorld.Should().Be(false);
        }
    }
}
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test --filter "FullyQualifiedName~ToolAnnotationCoverageTests" -v minimal`
Expected: FAIL — `Idempotent`/`OpenWorld` are `null`, not the expected booleans.

- [ ] **Step 3: Enumerate every tool needing an edit**

Run this to list all tool declarations with their current flags, so none is missed:

```bash
grep -rn 'McpServerTool(' VitallyMcp/Tools/*.cs
```

- [ ] **Step 4: Add the two flags to every tool declaration**

For each `[McpServerTool(...)]`, append `Idempotent` and `OpenWorld` per the rules table. Worked examples of each of the four shapes:

```csharp
// Read (List_/Get_)
[McpServerTool(Name = "List_accounts", Title = "List accounts", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false), Description("List Vitally accounts")]

// Create
[McpServerTool(Name = "Create_account", Title = "Create account", ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = false), Description("Create a new Vitally account")]

// Update
[McpServerTool(Name = "Update_account", Title = "Update account", ReadOnly = false, Destructive = true, Idempotent = true, OpenWorld = false), Description("Update an existing Vitally account")]

// Delete
[McpServerTool(Name = "Delete_account", Title = "Delete account", ReadOnly = false, Destructive = true, Idempotent = true, OpenWorld = false), Description("Delete a Vitally account")]
```

Do not change any `Name`, `Title` or `Description` value — renaming a tool breaks existing client configurations.

- [ ] **Step 5: Run the coverage test to verify it passes**

Run: `dotnet test --filter "FullyQualifiedName~ToolAnnotationCoverageTests" -v minimal`
Expected: PASS. If a specific tool fails, the assertion message names it.

- [ ] **Step 6: Run the full suite**

Run: `dotnet test VitallyMcp.sln -c Debug --nologo --verbosity minimal`
Expected: all pass. `ReadOnlyToolFilterTests` and `ReadOnlyToolsListTests` must be unaffected — they key off `ReadOnlyHint`, which is unchanged.

- [ ] **Step 7: Commit**

```bash
sed -i 's/\r$//' VitallyMcp/Tools/*.cs VitallyMcp.Tests/ToolAnnotationCoverageTests.cs
git add VitallyMcp/Tools VitallyMcp.Tests/ToolAnnotationCoverageTests.cs
git commit -m "feat: Add Idempotent and OpenWorld hints to all tools, enforced by a coverage test"
```

---

### Task 4: Expose permission resolution for policy evaluation

`ToolAuthorizer.HasEffectivePermissionAsync` is private and reads the caller from `IHttpContextAccessor`. The authorization handler in Task 5 needs the same resolution against a `ClaimsPrincipal` it is handed. Extract it without duplicating the logic.

**Files:**
- Modify: `VitallyMcp/ToolAuthorizer.cs:71-89`
- Modify: `VitallyMcp.Tests/ToolAuthorizerTests.cs` (add cases)

**Interfaces:**
- Consumes: nothing.
- Produces: two public members on `ToolAuthorizer` — `Task<bool> HasEffectivePermissionAsync(ClaimsPrincipal user, string required, CancellationToken cancellationToken = default)` and `Task<bool> IsAuthorizationBypassedAsync()`. Both are consumed by `VitallyPermissionHandler` in Task 5.

- [ ] **Step 1: Write the failing test**

Append to `VitallyMcp.Tests/ToolAuthorizerTests.cs` (inside the existing class):

```csharp
    [Fact]
    public async Task HasEffectivePermissionAsync_IsPublicAndHonoursTokenClaim()
    {
        var authorizer = BuildAuthorizer(new ToolAuthorizationOptions { Enabled = true });
        var user = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim("permissions", "vitally:read")], "test"));

        (await authorizer.HasEffectivePermissionAsync(user, "vitally:read")).Should().BeTrue();
        (await authorizer.HasEffectivePermissionAsync(user, "vitally:write")).Should().BeFalse();
    }

    [Theory]
    [InlineData(true, false, true)]   // RBAC disabled => bypassed
    [InlineData(false, true, true)]   // NoAuth dev mode => bypassed
    [InlineData(false, false, false)] // both on => enforced
    public async Task IsAuthorizationBypassedAsync_ReflectsEnabledAndNoAuth(bool disabled, bool noAuth, bool expected)
    {
        var authorizer = BuildAuthorizer(
            new ToolAuthorizationOptions { Enabled = !disabled },
            new OAuthOptions { NoAuth = noAuth });

        (await authorizer.IsAuthorizationBypassedAsync()).Should().Be(expected);
    }
```

**Before writing these tests, read `VitallyMcp.Tests/ToolAuthorizerTests.cs` and reuse its existing
construction helper.** If it has none, add one and use it for both new tests:

```csharp
    private static ToolAuthorizer BuildAuthorizer(
        ToolAuthorizationOptions options,
        OAuthOptions? oauth = null)
        => new(
            Microsoft.Extensions.Options.Options.Create(options),
            Microsoft.Extensions.Options.Options.Create(oauth ?? new OAuthOptions { NoAuth = false }),
            httpContextAccessor: null,
            groupResolver: null);
```

Do not introduce a second construction style alongside an existing one — match what the file already does.

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test --filter "FullyQualifiedName~ToolAuthorizerTests" -v minimal`
Expected: FAIL — `HasEffectivePermissionAsync` is private, and `IsAuthorizationBypassedAsync` does not exist.

- [ ] **Step 3: Make the method public and add the name properties**

In `VitallyMcp/ToolAuthorizer.cs`, change the signature on line 71 from `private` to `public` and add a doc comment:

```csharp
    /// <summary>
    /// Resolves whether <paramref name="user"/> effectively holds <paramref name="required"/>, using
    /// the live Entra group lookup when enabled and falling back to the token claim. Public so the
    /// ASP.NET Core authorization policy handler can share exactly this resolution — the discovery
    /// filter and the <see cref="VitallyService"/> enforcement backstop must never disagree.
    /// </summary>
    public async Task<bool> HasEffectivePermissionAsync(ClaimsPrincipal user, string required, CancellationToken cancellationToken = default)
```

Then add, next to `RequiredPermission` (after line 137):

```csharp
    /// <summary>
    /// True when authorisation is switched off entirely (RBAC disabled or NoAuth dev mode), in which
    /// case discovery filtering must be a pass-through — otherwise local development would see an
    /// empty tool list. Async purely to keep the policy handler's call site uniform.
    /// </summary>
    public Task<bool> IsAuthorizationBypassedAsync() => Task.FromResult(!_options.Enabled || _noAuth);
```

- [ ] **Step 4: Run to verify it passes**

Run: `dotnet test --filter "FullyQualifiedName~ToolAuthorizerTests" -v minimal`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
sed -i 's/\r$//' VitallyMcp/ToolAuthorizer.cs VitallyMcp.Tests/ToolAuthorizerTests.cs
git add VitallyMcp/ToolAuthorizer.cs VitallyMcp.Tests/ToolAuthorizerTests.cs
git commit -m "refactor: Expose permission resolution on ToolAuthorizer for policy evaluation"
```

---

### Task 5: Per-caller `tools/list` filtering via `AddAuthorizationFilters()`

Today `ReadOnlyToolFilter` hides destructive tools *deployment-wide* only. A reader-tier user on the read-write production deployment still sees all 95 tools and only discovers the denial at call time. `AddAuthorizationFilters()` makes the SDK evaluate `[Authorize]` on each tool and filter `tools/list` per caller.

**Files:**
- Create: `VitallyMcp/VitallyPermissionRequirement.cs`
- Create: `VitallyMcp/VitallyPermissionHandler.cs`
- Create: `VitallyMcp.Tests/VitallyPermissionHandlerTests.cs`
- Create: `VitallyMcp.Tests/TestAuthHandler.cs`
- Create: `VitallyMcp.Tests/AuthorizationFilterToolsListTests.cs`
- Modify: `VitallyMcp/Program.cs` (policy registration near line 90; `AddAuthorizationFilters()` near line 99)
- Modify: all 16 files in `VitallyMcp/Tools/`

**Interfaces:**
- Consumes: `ToolAuthorizer.HasEffectivePermissionAsync(ClaimsPrincipal, string, CancellationToken)` and `ToolAuthorizer.IsAuthorizationBypassedAsync()` from Task 4; the `Idempotent`/`OpenWorld` annotations from Task 3 (the same `[McpServerTool(...)]` lines are edited, so do Task 3 first to avoid conflicting edits).
- Produces: `VitallyPermissionRequirement(string permission)` with a `Permission` property; `VitallyPermissionHandler : AuthorizationHandler<VitallyPermissionRequirement>`; DI policies named `"vitally:read"`, `"vitally:write"`, `"vitally:delete"`; `[Authorize(Policy = ...)]` on every tool; `TestAuthHandler.SchemeName` and `TestAuthHandlerOptions.Permissions` for reuse by later test classes.

- [ ] **Step 1: Write the failing handler unit test**

Create `VitallyMcp.Tests/VitallyPermissionHandlerTests.cs`:

```csharp
using System.Security.Claims;
using FluentAssertions;
using Microsoft.AspNetCore.Authorization;

namespace VitallyMcp.Tests;

public class VitallyPermissionHandlerTests
{
    private static AuthorizationHandlerContext ContextFor(ClaimsPrincipal user, string permission)
    {
        var requirement = new VitallyPermissionRequirement(permission);
        return new AuthorizationHandlerContext([requirement], user, resource: null);
    }

    [Fact]
    public async Task Succeeds_WhenCallerHoldsThePermission()
    {
        var handler = new VitallyPermissionHandler(TestHelpers.BuildToolAuthorizer(enabled: true));
        var user = new ClaimsPrincipal(new ClaimsIdentity([new Claim("permissions", "vitally:read")], "test"));
        var context = ContextFor(user, "vitally:read");

        await handler.HandleAsync(context);

        context.HasSucceeded.Should().BeTrue();
    }

    [Fact]
    public async Task DoesNotSucceed_WhenCallerLacksThePermission()
    {
        var handler = new VitallyPermissionHandler(TestHelpers.BuildToolAuthorizer(enabled: true));
        var user = new ClaimsPrincipal(new ClaimsIdentity([new Claim("permissions", "vitally:read")], "test"));
        var context = ContextFor(user, "vitally:delete");

        await handler.HandleAsync(context);

        context.HasSucceeded.Should().BeFalse();
    }

    [Fact]
    public async Task Succeeds_WhenAuthorizationDisabled()
    {
        // With RBAC off (or NoAuth dev mode) discovery must not be filtered — otherwise local
        // development sees an empty tool list.
        var handler = new VitallyPermissionHandler(TestHelpers.BuildToolAuthorizer(enabled: false));
        var user = new ClaimsPrincipal(new ClaimsIdentity());
        var context = ContextFor(user, "vitally:delete");

        await handler.HandleAsync(context);

        context.HasSucceeded.Should().BeTrue();
    }
}
```

Add a `BuildToolAuthorizer(bool enabled)` helper to `VitallyMcp.Tests/TestHelpers.cs` following the existing `BuildVitallyService` style, constructing a `ToolAuthorizer` with `ToolAuthorizationOptions { Enabled = enabled }` and `OAuthOptions { NoAuth = false }`.

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test --filter "FullyQualifiedName~VitallyPermissionHandlerTests" -v minimal`
Expected: FAIL — `VitallyPermissionRequirement` and `VitallyPermissionHandler` do not exist.

- [ ] **Step 3: Create the requirement**

Create `VitallyMcp/VitallyPermissionRequirement.cs`:

```csharp
using Microsoft.AspNetCore.Authorization;

namespace VitallyMcp;

/// <summary>
/// Authorization requirement carrying one <c>vitally:*</c> permission. Exists so the MCP SDK's
/// <c>AddAuthorizationFilters()</c> can evaluate tool-level <c>[Authorize]</c> attributes through the
/// standard ASP.NET Core policy pipeline while still resolving permissions the way this server
/// always has (live Entra groups, falling back to the token claim).
/// </summary>
/// <param name="permission">The required permission, e.g. <c>vitally:write</c>.</param>
public class VitallyPermissionRequirement(string permission) : IAuthorizationRequirement
{
    public string Permission { get; } = permission;
}
```

- [ ] **Step 4: Create the handler**

Create `VitallyMcp/VitallyPermissionHandler.cs`:

```csharp
using Microsoft.AspNetCore.Authorization;

namespace VitallyMcp;

/// <summary>
/// Evaluates <see cref="VitallyPermissionRequirement"/> by delegating to <see cref="ToolAuthorizer"/>,
/// so tools/list discovery filtering and the <see cref="VitallyService.SendAsync"/> enforcement
/// backstop share one resolution path and cannot drift apart.
///
/// <para>
/// This filters <b>discovery</b>. It is not the security boundary — that remains
/// <c>VitallyService.SendAsync</c>, which authorises every upstream call regardless of what the
/// client managed to see or invoke.
/// </para>
/// </summary>
public class VitallyPermissionHandler(ToolAuthorizer authorizer)
    : AuthorizationHandler<VitallyPermissionRequirement>
{
    protected override async Task HandleRequirementAsync(
        AuthorizationHandlerContext context,
        VitallyPermissionRequirement requirement)
    {
        if (await authorizer.IsAuthorizationBypassedAsync())
        {
            // RBAC disabled or NoAuth dev mode: don't filter discovery, or local dev sees no tools.
            context.Succeed(requirement);
            return;
        }

        if (context.User.Identity?.IsAuthenticated == true
            && await authorizer.HasEffectivePermissionAsync(context.User, requirement.Permission))
        {
            context.Succeed(requirement);
        }
        // Otherwise leave unsucceeded — the SDK filter drops the tool from tools/list.
    }
}
```

- [ ] **Step 5: Confirm the ToolAuthorizer members from Task 4 are present**

`VitallyPermissionHandler` calls `IsAuthorizationBypassedAsync()` and `HasEffectivePermissionAsync(...)`, both added in Task 4. Verify before building:

Run: `grep -n "IsAuthorizationBypassedAsync\|public async Task<bool> HasEffectivePermissionAsync" VitallyMcp/ToolAuthorizer.cs`
Expected: both appear. If either is missing, Task 4 was not completed — go back and finish it rather than duplicating the members here.

- [ ] **Step 6: Run the handler tests to verify they pass**

Run: `dotnet test --filter "FullyQualifiedName~VitallyPermissionHandlerTests" -v minimal`
Expected: PASS.

- [ ] **Step 7: Register the policies and the SDK filters**

In `VitallyMcp/Program.cs`, replace the bare `builder.Services.AddAuthorization();` (line 90) with:

```csharp
    // Scoped, not singleton: VitallyPermissionHandler depends on the scoped ToolAuthorizer, and a
    // singleton capturing it would be a captive-dependency bug.
    builder.Services.AddScoped<IAuthorizationHandler, VitallyPermissionHandler>();

    // Policy *names* must be compile-time constants for [Authorize(Policy = "...")], so they are
    // literals here. The permission *values* carried by each requirement come from
    // ToolAuthorizationOptions, so a deployment that renames a permission stays consistent.
    var permissions = builder.Configuration.GetSection(ToolAuthorizationOptions.SectionName)
        .Get<ToolAuthorizationOptions>() ?? new ToolAuthorizationOptions();

    builder.Services.AddAuthorizationBuilder()
        .AddPolicy("vitally:read", p => p.AddRequirements(new VitallyPermissionRequirement(permissions.ReadPermission)))
        .AddPolicy("vitally:write", p => p.AddRequirements(new VitallyPermissionRequirement(permissions.WritePermission)))
        .AddPolicy("vitally:delete", p => p.AddRequirements(new VitallyPermissionRequirement(permissions.DeletePermission)));
```

- [ ] **Step 8: Enable the SDK authorization filters**

In `VitallyMcp/Program.cs`, after `.WithToolsFromAssembly()` (line 99), add the call **only when auth is on**:

```csharp
// Applies [Authorize] on tools: filters tools/list per caller and rejects unauthorised calls
// before they reach the handler. Guarded on !noAuth because with NoAuth there is no authentication
// and every policy would fail, leaving local development with an empty tool list.
if (!noAuth)
{
    mcpBuilder.AddAuthorizationFilters();
}
```

- [ ] **Step 9: Annotate every tool with its policy**

Add `[Authorize(Policy = "...")]` to each tool method, above the existing `[McpServerTool...]` line, mapping by prefix:

| Prefix | Attribute |
|---|---|
| `List_*`, `Get_*` | `[Authorize(Policy = "vitally:read")]` |
| `Create_*`, `Update_*` | `[Authorize(Policy = "vitally:write")]` |
| `Delete_*` | `[Authorize(Policy = "vitally:delete")]` |

Worked example in `VitallyMcp/Tools/AccountsTools.cs`:

```csharp
using Microsoft.AspNetCore.Authorization;

// ...

    [Authorize(Policy = "vitally:read")]
    [McpServerTool(Name = "List_accounts", Title = "List accounts", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false), Description("List Vitally accounts")]
    public static async Task<string> ListAccounts(
        VitallyService vitallyService,
        ...
```

Add `using Microsoft.AspNetCore.Authorization;` to each of the 16 tool files.

- [ ] **Step 10: Write the per-tier integration test**

Create `VitallyMcp.Tests/TestAuthHandler.cs`:

```csharp
using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace VitallyMcp.Tests;

/// <summary>
/// Options carrying the permissions the synthetic test principal should hold.
/// </summary>
public class TestAuthHandlerOptions : AuthenticationSchemeOptions
{
    public string[] Permissions { get; set; } = [];
}

/// <summary>
/// Authenticates every request as a fixed principal holding <see cref="TestAuthHandlerOptions.Permissions"/>.
/// Lets the integration tests exercise real policy evaluation and tools/list filtering without an
/// Auth0 tenant or a real token.
/// </summary>
public class TestAuthHandler(
    IOptionsMonitor<TestAuthHandlerOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder)
    : AuthenticationHandler<TestAuthHandlerOptions>(options, logger, encoder)
{
    public const string SchemeName = "TestScheme";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, "test-subject"),
            new("sub", "test-subject")
        };
        claims.AddRange(Options.Permissions.Select(p => new Claim("permissions", p)));

        var identity = new ClaimsIdentity(claims, SchemeName);
        var principal = new ClaimsPrincipal(identity);
        var ticket = new AuthenticationTicket(principal, SchemeName);
        return Task.FromResult(AuthenticateResult.Success(ticket));
    }
}
```

Create `VitallyMcp.Tests/AuthorizationFilterToolsListTests.cs`:

```csharp
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace VitallyMcp.Tests;

/// <summary>
/// Proves per-caller tools/list filtering: a reader must not be shown mutating tools, an editor
/// must see create/update but not delete, and an admin must see everything.
///
/// This is the load-bearing test for the AddAuthorizationFilters() adoption. Staging only confirms
/// it against real Entra groups; correctness is established here.
/// </summary>
[Collection(IntegrationTestCollection.Name)]
public class AuthorizationFilterToolsListTests
{
    private static async Task<IReadOnlyList<string>> ToolNamesForAsync(params string[] permissions)
    {
        // All process-wide, so set every one explicitly. Authorization__ReadOnly MUST be false here:
        // if a sibling class leaves it true, ReadOnlyToolFilter strips every destructive tool and the
        // editor/admin assertions below fail for the wrong reason.
        Environment.SetEnvironmentVariable("OAuth__NoAuth", "false");
        Environment.SetEnvironmentVariable("Authorization__ReadOnly", "false");
        Environment.SetEnvironmentVariable("Vitally__DevelopmentApiKey", "sk_test_dummy");
        Environment.SetEnvironmentVariable("Vitally__Region", "EU");
        Environment.SetEnvironmentVariable("OAuth__Authority", "https://example.auth0.com/");
        Environment.SetEnvironmentVariable("OAuth__Audience", "https://example.test/");

        using var factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(b => b.ConfigureServices(services =>
            {
                // Replace the JwtBearer default with a scheme that authenticates as our synthetic
                // principal, so real policy evaluation runs against known permissions.
                services.AddAuthentication(TestAuthHandler.SchemeName)
                    .AddScheme<TestAuthHandlerOptions, TestAuthHandler>(
                        TestAuthHandler.SchemeName, o => o.Permissions = permissions);

                services.Configure<AuthorizationOptions>(o =>
                    o.DefaultPolicy = new AuthorizationPolicyBuilder(TestAuthHandler.SchemeName)
                        .RequireAuthenticatedUser().Build());
            }));

        using var client = factory.CreateClient();

        var request = new HttpRequestMessage(HttpMethod.Post, "/mcp")
        {
            Content = new StringContent("""{"jsonrpc":"2.0","id":1,"method":"tools/list"}""", Encoding.UTF8, "application/json")
        };
        request.Headers.TryAddWithoutValidation("Accept", "application/json, text/event-stream");

        var response = await client.SendAsync(request);
        var raw = await response.Content.ReadAsStringAsync();
        var json = raw.Contains("data:", StringComparison.Ordinal)
            ? raw.Split('\n').First(l => l.StartsWith("data:", StringComparison.Ordinal))["data:".Length..].Trim()
            : raw;

        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.GetProperty("result").GetProperty("tools")
            .EnumerateArray().Select(t => t.GetProperty("name").GetString()!).ToList();
    }

    [Fact]
    public async Task Reader_SeesOnlyReadTools()
    {
        var names = await ToolNamesForAsync("vitally:read");

        names.Should().Contain(n => n.StartsWith("List_", StringComparison.Ordinal));
        names.Should().NotContain(n => n.StartsWith("Create_", StringComparison.Ordinal));
        names.Should().NotContain(n => n.StartsWith("Update_", StringComparison.Ordinal));
        names.Should().NotContain(n => n.StartsWith("Delete_", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Editor_SeesWriteToolsButNotDeletes()
    {
        var names = await ToolNamesForAsync("vitally:read", "vitally:write");

        names.Should().Contain(n => n.StartsWith("Create_", StringComparison.Ordinal));
        names.Should().Contain(n => n.StartsWith("Update_", StringComparison.Ordinal));
        names.Should().NotContain(n => n.StartsWith("Delete_", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Admin_SeesEveryTool()
    {
        var names = await ToolNamesForAsync("vitally:read", "vitally:write", "vitally:delete");

        names.Should().Contain(n => n.StartsWith("Delete_", StringComparison.Ordinal));
        names.Should().HaveCountGreaterThan(90);
    }
}
```

- [ ] **Step 11: Run the integration test**

Run: `dotnet test --filter "FullyQualifiedName~AuthorizationFilterToolsListTests" -v minimal`
Expected: PASS.

If every tool is filtered out, the policy is not resolving the synthetic principal — check that the test scheme is actually the default and that `AddAuthorizationFilters()` ran (it is guarded on `!noAuth`, and the test sets `OAuth__NoAuth=false`).

- [ ] **Step 12: Confirm local dev is not broken**

Run: `dotnet test --filter "FullyQualifiedName~ReadOnlyToolsListTests" -v minimal`
Expected: PASS — that fixture sets `OAuth__NoAuth=true`, so `AddAuthorizationFilters()` is skipped and the read-only filter still governs the list. This is the regression guard for the NoAuth path.

- [ ] **Step 13: Run the full suite and build**

Run: `dotnet test VitallyMcp.sln -c Debug --nologo --verbosity minimal`
Expected: all pass, 0 warnings.

- [ ] **Step 14: Commit**

```bash
sed -i 's/\r$//' VitallyMcp/VitallyPermissionRequirement.cs VitallyMcp/VitallyPermissionHandler.cs VitallyMcp/ToolAuthorizer.cs VitallyMcp/Program.cs VitallyMcp/Tools/*.cs VitallyMcp.Tests/*.cs
git add VitallyMcp VitallyMcp.Tests
git commit -m "feat: Filter tools/list per caller via AddAuthorizationFilters"
```

---

### Task 6: Spec-compliant OAuth discovery metadata

Two gaps. First, the 401 challenge is JwtBearer's bare `WWW-Authenticate: Bearer`, with no `resource_metadata` parameter — the MCP spec requires that pointer, and it currently works only because clients fall back to probing the well-known root. Second, only the root metadata path is served, not the `/mcp`-suffixed path the SDK and RFC 9728 prefer. `scopes_supported` is also absent.

**Files:**
- Create: `VitallyMcp/ProtectedResourceMetadataBuilder.cs`
- Create: `VitallyMcp.Tests/ResourceMetadataDiscoveryTests.cs`
- Modify: `VitallyMcp/Program.cs:187-200` (metadata endpoint) and the JwtBearer configuration at lines 77-88

**Interfaces:**
- Consumes: `OAuthOptions` (`Resource`, `Audience`, `Authority`, `SharedClientId`, `PublicBaseUrl`).
- Produces: `ProtectedResourceMetadataBuilder.Build(OAuthOptions oauth, string serverBaseUrl) -> ProtectedResourceMetadata` and `ProtectedResourceMetadataBuilder.MetadataPath` = `"/.well-known/oauth-protected-resource"`.

- [ ] **Step 1: Write the failing test**

Create `VitallyMcp.Tests/ResourceMetadataDiscoveryTests.cs`:

```csharp
using System.Net;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Hosting;

namespace VitallyMcp.Tests;

/// <summary>
/// MCP requires an unauthenticated request to a protected endpoint to answer 401 with a
/// WWW-Authenticate header pointing at the protected-resource metadata document. Without that
/// pointer, clients can only guess the well-known location — which is how discovery silently
/// depends on a fallback today.
/// </summary>
[Collection(IntegrationTestCollection.Name)]
public class ResourceMetadataDiscoveryTests : IClassFixture<ResourceMetadataDiscoveryTests.Factory>
{
    private readonly Factory _factory;

    public ResourceMetadataDiscoveryTests(Factory factory) => _factory = factory;

    [Fact]
    public async Task UnauthenticatedMcpCall_Returns401WithResourceMetadataPointer()
    {
        using var client = _factory.CreateClient();

        var request = new HttpRequestMessage(HttpMethod.Post, "/mcp")
        {
            Content = new StringContent("""{"jsonrpc":"2.0","id":1,"method":"tools/list"}""", Encoding.UTF8, "application/json")
        };
        request.Headers.TryAddWithoutValidation("Accept", "application/json, text/event-stream");

        var response = await client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized,
            "the deploy.yml smoke check asserts 401 — this must not change");

        var challenge = string.Join(", ", response.Headers.WwwAuthenticate.Select(h => h.ToString()));
        challenge.Should().Contain("resource_metadata",
            "MCP clients use this parameter to locate the protected-resource metadata document");
        challenge.Should().Contain("/.well-known/oauth-protected-resource");
    }

    [Theory]
    [InlineData("/.well-known/oauth-protected-resource")]
    [InlineData("/.well-known/oauth-protected-resource/mcp")]
    public async Task BothMetadataPaths_ReturnTheSameDocument(string path)
    {
        using var client = _factory.CreateClient();

        var response = await client.GetAsync(path);
        response.StatusCode.Should().Be(HttpStatusCode.OK, $"{path} must be served");

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = doc.RootElement;

        root.GetProperty("resource").GetString().Should().NotBeNullOrWhiteSpace();
        root.GetProperty("authorization_servers").EnumerateArray().Should().NotBeEmpty();
        root.GetProperty("bearer_methods_supported").EnumerateArray()
            .Select(e => e.GetString()).Should().Contain("header");
        root.TryGetProperty("scopes_supported", out var scopes).Should().BeTrue(
            "clients use this to request the right scopes up front");
        scopes.EnumerateArray().Should().NotBeEmpty();
    }

    public class Factory : WebApplicationFactory<Program>
    {
        protected override IHost CreateHost(IHostBuilder builder)
        {
            // Auth ON — this fixture is specifically about the 401 challenge. Every variable set
            // explicitly because they are process-wide (see IntegrationTestCollection).
            Environment.SetEnvironmentVariable("OAuth__NoAuth", "false");
            Environment.SetEnvironmentVariable("Authorization__ReadOnly", "false");
            Environment.SetEnvironmentVariable("Vitally__DevelopmentApiKey", "sk_test_dummy");
            Environment.SetEnvironmentVariable("Vitally__Region", "EU");
            Environment.SetEnvironmentVariable("OAuth__Authority", "https://example.auth0.com/");
            Environment.SetEnvironmentVariable("OAuth__Audience", "https://example.test/");
            Environment.SetEnvironmentVariable("OAuth__Resource", "https://example.test/");
            Environment.SetEnvironmentVariable("OAuth__PublicBaseUrl", "https://example.test");
            return base.CreateHost(builder);
        }
    }
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test --filter "FullyQualifiedName~ResourceMetadataDiscoveryTests" -v minimal`
Expected: FAIL on both — no `resource_metadata` in the challenge, and the `/mcp`-suffixed path 404s.

- [ ] **Step 3: Create the metadata builder**

Create `VitallyMcp/ProtectedResourceMetadataBuilder.cs`:

```csharp
using ModelContextProtocol.Authentication;

namespace VitallyMcp;

/// <summary>
/// Builds the RFC 9728 protected-resource metadata document from <see cref="OAuthOptions"/>.
/// Extracted from Program.cs so the same document is served from both well-known paths and can be
/// asserted directly in tests.
/// </summary>
public static class ProtectedResourceMetadataBuilder
{
    /// <summary>Canonical metadata path. RFC 9728 also allows a resource-path suffix (…/mcp).</summary>
    public const string MetadataPath = "/.well-known/oauth-protected-resource";

    /// <summary>Scopes advertised to clients so they can request them at the authorize step.</summary>
    public static readonly string[] SupportedScopes = ["openid", "profile", "email", "offline_access", "mcp.access"];

    public static ProtectedResourceMetadata Build(OAuthOptions oauth, string serverBaseUrl)
    {
        // When the OAuth proxy is active we are the authorization server clients talk to (so we can
        // intercept registration); otherwise point straight at the upstream authority.
        var authorizationServer = string.IsNullOrWhiteSpace(oauth.SharedClientId)
            ? oauth.Authority?.TrimEnd('/')
            : serverBaseUrl;

        return new ProtectedResourceMetadata
        {
            Resource = string.IsNullOrWhiteSpace(oauth.Resource) ? oauth.Audience : oauth.Resource,
            AuthorizationServers = { authorizationServer ?? string.Empty },
            BearerMethodsSupported = { "header" },
            ScopesSupported = [.. SupportedScopes],
            ResourceName = "Vitally MCP"
        };
    }
}
```

If `AuthorizationServers` / `BearerMethodsSupported` are read-only collection properties, assign with collection expressions instead:

```csharp
            AuthorizationServers = [authorizationServer ?? string.Empty],
            BearerMethodsSupported = ["header"],
```

- [ ] **Step 4: Serve the document from both paths**

In `VitallyMcp/Program.cs`, replace the existing `/.well-known/oauth-protected-resource` handler (lines 187-200) with:

```csharp
// RFC 9728 — Protected Resource Metadata, served from both the canonical path and the
// resource-path-suffixed variant (…/mcp) that RFC 9728 and the MCP SDK prefer. Clients probe
// either, so serving both removes a discovery failure mode.
var resourceMetadataHandler = (HttpContext ctx, IOptions<OAuthOptions> oauth) =>
    Results.Json(ProtectedResourceMetadataBuilder.Build(
        oauth.Value,
        GetServerBaseUrl(ctx, oauth.Value.PublicBaseUrl)));

app.MapGet(ProtectedResourceMetadataBuilder.MetadataPath, resourceMetadataHandler);
app.MapGet($"{ProtectedResourceMetadataBuilder.MetadataPath}/mcp", resourceMetadataHandler);
```

- [ ] **Step 5: Add the `resource_metadata` pointer to the 401 challenge**

In `VitallyMcp/Program.cs`, extend the JwtBearer configuration (lines 83-88) to set the challenge header:

```csharp
    builder.Services.AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme)
        .Configure<IOptions<OAuthOptions>>((jwt, oauth) =>
        {
            jwt.Authority = oauth.Value.Authority;
            jwt.Audience = oauth.Value.Audience;

            // MCP requires the 401 to point at the protected-resource metadata document. Without
            // this, clients can only guess the well-known location. Built from PublicBaseUrl so a
            // Host header cannot inject the pointer; falls back to the request origin in local dev.
            jwt.Events = new JwtBearerEvents
            {
                OnChallenge = context =>
                {
                    var baseUrl = string.IsNullOrWhiteSpace(oauth.Value.PublicBaseUrl)
                        ? $"{context.Request.Scheme}://{context.Request.Host}"
                        : oauth.Value.PublicBaseUrl;

                    var metadataUrl = $"{baseUrl}{ProtectedResourceMetadataBuilder.MetadataPath}/mcp";
                    context.Response.Headers.WWWAuthenticate =
                        $"Bearer resource_metadata=\"{metadataUrl}\"";
                    return Task.CompletedTask;
                }
            };
        });
```

- [ ] **Step 6: Run the test to verify it passes**

Run: `dotnet test --filter "FullyQualifiedName~ResourceMetadataDiscoveryTests" -v minimal`
Expected: PASS.

- [ ] **Step 7: Confirm the deploy smoke contract still holds**

The `deploy.yml` verification asserts unauthenticated `/mcp` returns 401. The first test above asserts exactly that, so it is now covered by the suite. Confirm no test expects a bare `WWW-Authenticate: Bearer`:

Run: `grep -rn "WwwAuthenticate\|WWW-Authenticate" VitallyMcp.Tests/`
Expected: only `ResourceMetadataDiscoveryTests` matches.

- [ ] **Step 8: Run the full suite and build**

Run: `dotnet test VitallyMcp.sln -c Debug --nologo --verbosity minimal`
Expected: all pass, 0 warnings. `OAuthProxyEndpointsTests` must remain green — the proxy endpoints are untouched.

- [ ] **Step 9: Commit**

```bash
sed -i 's/\r$//' VitallyMcp/ProtectedResourceMetadataBuilder.cs VitallyMcp/Program.cs VitallyMcp.Tests/ResourceMetadataDiscoveryTests.cs
git add VitallyMcp VitallyMcp.Tests
git commit -m "feat: Point the 401 challenge at protected-resource metadata and serve both well-known paths"
```

---

### Task 7: Correct the stale documentation

**Files:**
- Modify: `CLAUDE.md`

**Interfaces:**
- Consumes: the changes from Tasks 1-6.
- Produces: nothing.

- [ ] **Step 1: Fix the SDK and protocol version claims**

In `CLAUDE.md`, update these statements:

- "**Streamable HTTP transport** (MCP 2025-06-18)" → "**Streamable HTTP transport** (MCP 2026-07-28, stateless)"
- "Built on the official `ModelContextProtocol` C# SDK (1.3.0 GA)" → "Built on the official `ModelContextProtocol` C# SDK 2.1.0"
- "Using `ModelContextProtocol` 1.3.0 GA plus `ModelContextProtocol.AspNetCore` 1.3.0 for HTTP hosting." → "Using `ModelContextProtocol` 2.1.0 plus `ModelContextProtocol.AspNetCore` 2.1.0 for HTTP hosting."

- [ ] **Step 2: Document the new behaviour**

In the `ToolAuthorizationOptions` section of `CLAUDE.md`, after the sentence ending "never call the Vitally API around it.", add:

```markdown
- **Per-caller discovery filtering.** `mcpBuilder.AddAuthorizationFilters()` (registered only when
  `OAuth:NoAuth=false`) makes the SDK evaluate the `[Authorize(Policy = "vitally:read|write|delete")]`
  attribute on each tool, so `tools/list` shows only the tools the caller may actually invoke and an
  unauthorised call is rejected before the handler runs. `VitallyPermissionHandler` resolves those
  policies through `ToolAuthorizer.HasEffectivePermissionAsync`, so discovery and the
  `VitallyService.SendAsync` backstop cannot drift apart. This is **discovery filtering** — the
  security boundary remains `SendAsync`. Distinct from the deployment-wide `Authorization:ReadOnly`
  switch, which hides destructive tools from everyone.
```

In the `Important Notes` section, add:

```markdown
- **`tools/list` cache hints**: bound from `ToolsListCache:` (`Enabled`, `TimeToLive` default 5 min,
  `Scope` default `Private`) and serialised as `ttlMs` / `cacheScope` per MCP 2026-07-28. Scope must
  stay `Private` while per-caller filtering is active — a public cache would leak one tier's tool
  catalogue to another.
- **Tool annotations**: every tool sets `ReadOnly`, `Destructive`, `Idempotent` and `OpenWorld`.
  `ToolAnnotationCoverageTests` enforces this by reflection, so a new tool cannot ship unannotated.
```

- [ ] **Step 3: Commit**

```bash
sed -i 's/\r$//' CLAUDE.md
git add CLAUDE.md
git commit -m "docs: Update CLAUDE.md for SDK 2.1.0 and the 2026-07-28 adoption"
```

---

### Task 8: Staging validation runbook

Layers 2 and 3 of the validation design are manual. This task records them as a runbook so they are repeatable and so teardown is not left to memory.

**Files:**
- Create: `docs/runbooks/mcp-sdk2-staging-validation.md`

**Interfaces:**
- Consumes: the validation design at `docs/superpowers/specs/2026-08-10-mcp-sdk2-validation-design.md`.
- Produces: nothing.

- [ ] **Step 1: Write the runbook**

Create `docs/runbooks/mcp-sdk2-staging-validation.md` covering, in this order:

1. **Layer 2 — local container.** The exact commands:

```powershell
docker build -t vitally-mcp:local .
docker run --rm -p 5099:8080 `
  -e OAuth__NoAuth=true `
  -e Authorization__ReadOnly=true `
  -e Vitally__Region=EU `
  -e Vitally__DevelopmentApiKey=$env:VITALLY_DEV_KEY `
  vitally-mcp:local
```

Then: point MCP Inspector and Claude Code at `http://localhost:5099/mcp`. Record the negotiated
protocol version, the tool count, whether `ttlMs` appears on `tools/list`, and that a read tool
round-trips.

2. **Layer 3 — staging provisioning.** State plainly at the top of this section that
   **`terraform apply` must not be run**: the live resources are managed manually, so a plan against
   shared state would try to reconcile production drift as a side effect. Then these commands
   (run as `dsearle.adm`, PIM activated for the Graph grant):

```bash
set -euo pipefail
RG=vitally-prod-rg-uksouth
CAE=vitally-prod-cae-uksouth
ACR=vitallyproducruksouth
KV=vitally-prod-kv-uksouth
APP=vitally-staging-ca-uksouth
ID=vitally-staging-id-uksouth
SUB=$(az account show --query id -o tsv)

# 1. Identity
az identity create -g "$RG" -n "$ID" -l uksouth
ID_PRINCIPAL=$(az identity show -g "$RG" -n "$ID" --query principalId -o tsv)
ID_CLIENT=$(az identity show -g "$RG" -n "$ID" --query clientId -o tsv)
ID_RESOURCE=$(az identity show -g "$RG" -n "$ID" --query id -o tsv)

# 2. Role grants — pull images, read the Vitally secret
az role assignment create --assignee-object-id "$ID_PRINCIPAL" --assignee-principal-type ServicePrincipal \
  --role AcrPull --scope "/subscriptions/$SUB/resourceGroups/$RG/providers/Microsoft.ContainerRegistry/registries/$ACR"
az role assignment create --assignee-object-id "$ID_PRINCIPAL" --assignee-principal-type ServicePrincipal \
  --role "Key Vault Secrets User" --scope "/subscriptions/$SUB/resourceGroups/$RG/providers/Microsoft.KeyVault/vaults/$KV"

# 3. Graph GroupMember.Read.All (application permission) — needs Global Administrator via PIM.
#    00000003-0000-0000-c000-000000000000 is Microsoft Graph; the role id is GroupMember.Read.All.
az ad app permission grant --id "$ID_PRINCIPAL" --api 00000003-0000-0000-c000-000000000000 \
  --scope GroupMember.Read.All 2>/dev/null \
  || echo "Grant GroupMember.Read.All to $ID_PRINCIPAL via the portal (Enterprise applications > Permissions) if this fails"

# 4. Container App. ReadOnly is hard-wired true — this is the only guard against mutating
#    real customer data, since there is one live Vitally tenant.
az containerapp create -g "$RG" -n "$APP" --environment "$CAE" \
  --image "$ACR.azurecr.io/vitally-mcp:PLACEHOLDER_BASELINE_TAG" \
  --registry-server "$ACR.azurecr.io" --registry-identity "$ID_RESOURCE" \
  --user-assigned "$ID_RESOURCE" \
  --ingress external --target-port 8080 --transport http \
  --min-replicas 0 --max-replicas 1 \
  --env-vars \
    "Vitally__Region=EU" \
    "Vitally__KeyVaultUri=https://$KV.vault.azure.net/" \
    "AZURE_CLIENT_ID=$ID_CLIENT" \
    "Authorization__ReadOnly=true" \
    "Authorization__LiveGroupCheck=true" \
    "Authorization__ReaderGroupId=71451cc9-f5df-44ee-8ed1-3acc41a911eb" \
    "Authorization__EditorGroupId=19b9d659-284c-4f93-b1c3-a6354db1027c" \
    "Authorization__AdminGroupId=70b48a20-d4b1-47dc-a132-21bc99272a86" \
    "OAuth__NoAuth=false" \
    "OAuth__Authority=https://fiscal-it.uk.auth0.com/"

# 5. Read the assigned FQDN, then set the origin-dependent settings to match it.
FQDN=$(az containerapp show -g "$RG" -n "$APP" --query properties.configuration.ingress.fqdn -o tsv)
echo "Staging FQDN: https://$FQDN"
```

   Substitute the real baseline image tag for `PLACEHOLDER_BASELINE_TAG` — read it from production
   with `az containerapp show -g "$RG" -n vitally-prod-ca-uksouth --query "properties.template.containers[0].image" -o tsv`.

   Then create the Auth0 Resource Server (identifier `https://$FQDN`) and a native client whose only
   allowed callback is `https://$FQDN/oauth/callback`, and apply the remaining settings:

```bash
az containerapp secret set -g "$RG" -n "$APP" \
  --secrets "oauth-shared-client-secret=<staging client secret>"

az containerapp update -g "$RG" -n "$APP" --set-env-vars \
  "OAuth__Audience=https://$FQDN" \
  "OAuth__Resource=https://$FQDN" \
  "OAuth__PublicBaseUrl=https://$FQDN" \
  "OAuth__SharedClientId=<staging client id>" \
  "OAuth__SharedClientSecret=secretref:oauth-shared-client-secret"
```

3. **Baseline gate.** Deploy the current `main` image; verify `/health` 200, unauthenticated
   `/mcp` 401, and a real client completing OAuth and listing tools.

4. **Change gate.** Deploy the branch image; verify the baseline checks plus `tools/list`
   differing by caller tier and `ttlMs` present.

5. **Teardown.** An orphaned Auth0 client and an unused managed identity are both standing security
   debt, so this is not optional:

```bash
set -euo pipefail
RG=vitally-prod-rg-uksouth
APP=vitally-staging-ca-uksouth
ID=vitally-staging-id-uksouth
ID_PRINCIPAL=$(az identity show -g "$RG" -n "$ID" --query principalId -o tsv)

# Role assignments first — deleting the identity leaves orphaned assignments behind otherwise.
az role assignment list --assignee "$ID_PRINCIPAL" --all --query "[].id" -o tsv \
  | xargs -r -n1 az role assignment delete --ids

az containerapp delete -g "$RG" -n "$APP" --yes
az identity delete -g "$RG" -n "$ID"
```

   Then in Auth0: delete the staging Resource Server (API) and the staging native client. Confirm
   the production client `VgB00WSYN2V0KkhtYx3WZXYH9XRBvK1D` and the API
   `https://vitally.fiscaltec.com/` are untouched.

   Finally verify production is unaffected:

```bash
curl -s -o /dev/null -w "%{http_code}\n" https://vitally.fiscaltec.com/health          # expect 200
curl -s -o /dev/null -w "%{http_code}\n" -X POST https://vitally.fiscaltec.com/mcp \
  -H "Content-Type: application/json" -d '{"jsonrpc":"2.0","id":1,"method":"initialize"}'  # expect 401
```

Note explicitly that the prerequisite networking check already passed on 2026-08-10 (the
environment is VNet-injected into `snet-app` and both private DNS zones are linked to
`vitally-prod-vnet-uksouth`), so no additional networking work is needed for a new app in that
environment.

- [ ] **Step 2: Commit**

```bash
sed -i 's/\r$//' docs/runbooks/mcp-sdk2-staging-validation.md
git add docs/runbooks/mcp-sdk2-staging-validation.md
git commit -m "docs: Add staging validation runbook for the MCP SDK 2.0 adoption"
```

---

## Verification

After all tasks:

- [ ] `dotnet build VitallyMcp.sln -c Release --nologo` → **0 warnings, 0 errors**
- [ ] `dotnet test VitallyMcp.sln -c Debug --nologo --verbosity minimal` → all green
- [ ] `git log --oneline main..HEAD` shows one commit per task
- [ ] Layer 2 and Layer 3 gates executed per the runbook before any production deploy
