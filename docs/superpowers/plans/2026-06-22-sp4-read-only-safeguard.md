# SP4 — Read-only safeguard — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** A deployment-level read-only kill switch — `Authorization:ReadOnly` denies every write/delete at the server choke point AND hides destructive tools from `tools/list`.

**Architecture:** Two layers. Layer 1: `ToolAuthorizer.EnsureAuthorizedAsync` rejects any non-GET verb when `ReadOnly` is set, before the `Enabled`/`NoAuth` gate (so it holds regardless of RBAC state). Layer 2: an `AddListToolsFilter` (MCP SDK request filter) post-filters `tools/list` to keep only read-only-hinted tools, via a pure `ReadOnlyToolFilter` helper. Defence-in-depth: a hidden tool invoked by name is still denied by Layer 1.

**Tech Stack:** .NET 10, C#, `ModelContextProtocol(.AspNetCore) 1.3.0` (filter API confirmed present in the installed assemblies), xUnit + FluentAssertions.

**Branch:** `feature/sp4-read-only-safeguard` (already created off `main`; spec committed there).

**Spec:** [SP4 design](../specs/2026-06-22-sp4-read-only-safeguard-design.md)

## Global Constraints

- UK English in comments/docs. Repo uses **LF** line endings (`.gitattributes` present).
- `Authorization:ReadOnly` default `false` (behaviour unchanged unless explicitly set).
- The read-only deny is checked **before** the `!Enabled || NoAuth` early-return in `EnsureAuthorizedAsync` — it must hold even when per-user RBAC is disabled or in NoAuth dev.
- A mutating verb = any HTTP method other than `GET`.
- Hidden set (Layer 2) must equal denied set (Layer 1): tools without `ReadOnlyHint == true`.
- All Vitally calls already funnel through `VitallyService.SendAsync` → `ToolAuthorizer`; do not add enforcement elsewhere.

---

### Task 1: `Authorization:ReadOnly` option + enforcement (Layer 1)

**Files:**
- Modify: `VitallyMcp/ToolAuthorizationOptions.cs`
- Modify: `VitallyMcp/ToolAuthorizer.cs`
- Test: `VitallyMcp.Tests/ToolAuthorizerTests.cs`

**Interfaces:**
- Produces: `ToolAuthorizationOptions.ReadOnly` (bool, default false); `EnsureAuthorizedAsync` denies non-GET when ReadOnly.

- [ ] **Step 1: Write the failing tests**

Add to `VitallyMcp.Tests/ToolAuthorizerTests.cs` (the file already has the private `Build(...)` helper used below):

```csharp
    [Theory]
    [InlineData("POST")]
    [InlineData("PUT")]
    [InlineData("PATCH")]
    [InlineData("DELETE")]
    public async Task ReadOnly_DeniesMutatingVerbs_EvenWhenAuthDisabled(string method)
    {
        // ReadOnly is independent of Enabled — denies writes even with RBAC off.
        var authorizer = Build(options: new ToolAuthorizationOptions { Enabled = false, ReadOnly = true });

        Func<Task> act = () => authorizer.EnsureAuthorizedAsync(new HttpMethod(method));

        await act.Should().ThrowAsync<UnauthorizedAccessException>().WithMessage("*read-only*");
    }

    [Fact]
    public async Task ReadOnly_AllowsGet()
    {
        var authorizer = Build(options: new ToolAuthorizationOptions { Enabled = false, ReadOnly = true });

        Func<Task> act = () => authorizer.EnsureAuthorizedAsync(HttpMethod.Get);

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task ReadOnly_DeniesWrite_EvenInNoAuthDev()
    {
        var authorizer = Build(noAuth: true, options: new ToolAuthorizationOptions { Enabled = false, ReadOnly = true });

        Func<Task> act = () => authorizer.EnsureAuthorizedAsync(HttpMethod.Delete);

        await act.Should().ThrowAsync<UnauthorizedAccessException>().WithMessage("*read-only*");
    }

    [Fact]
    public async Task ReadOnlyFalse_DoesNotBlockWrites()
    {
        // Default ReadOnly=false: with Enabled=false the write passes (unchanged behaviour).
        var authorizer = Build(options: new ToolAuthorizationOptions { Enabled = false, ReadOnly = false });

        Func<Task> act = () => authorizer.EnsureAuthorizedAsync(HttpMethod.Post);

        await act.Should().NotThrowAsync();
    }
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test --filter "FullyQualifiedName~ToolAuthorizerTests.ReadOnly" --nologo`
Expected: FAIL — `ToolAuthorizationOptions` has no `ReadOnly` property (compile error).

- [ ] **Step 3: Add the option**

In `VitallyMcp/ToolAuthorizationOptions.cs`, add after the `Enabled` property:

```csharp
    /// <summary>
    /// Deployment-level read-only kill switch. When true, every mutating tool call
    /// (create/update/delete) is denied regardless of RBAC state or token permissions, and the
    /// destructive tools are hidden from tools/list. A blunt safety net for read-only deployments
    /// that does not depend on the per-user Entra-group RBAC being configured. Default false.
    /// </summary>
    public bool ReadOnly { get; set; }
```

- [ ] **Step 4: Add the enforcement**

In `VitallyMcp/ToolAuthorizer.cs`, at the very start of `EnsureAuthorizedAsync` (before the `if (!_options.Enabled || _noAuth)` block):

```csharp
        // Deployment-level read-only kill switch: deny every mutating verb regardless of RBAC
        // state, NoAuth, or token permissions. Checked before the Enabled/NoAuth gate so a
        // read-only deployment is locked even when per-user RBAC isn't configured.
        if (_options.ReadOnly && method != HttpMethod.Get)
        {
            throw new UnauthorizedAccessException(
                "This server is deployed in read-only mode; create, update and delete operations are disabled.");
        }
```

- [ ] **Step 5: Run to verify pass**

Run: `dotnet test --filter "FullyQualifiedName~ToolAuthorizerTests" --nologo`
Expected: PASS (new tests + all existing authorizer tests).

- [ ] **Step 6: Commit**

```powershell
git add VitallyMcp/ToolAuthorizationOptions.cs VitallyMcp/ToolAuthorizer.cs VitallyMcp.Tests/ToolAuthorizerTests.cs
git commit -m @'
feat(safety): Authorization:ReadOnly deployment kill switch (SP4 Layer 1)

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
'@
```

---

### Task 2: `ReadOnlyToolFilter` helper (Layer 2 logic)

**Files:**
- Create: `VitallyMcp/ReadOnlyToolFilter.cs`
- Test: `VitallyMcp.Tests/ReadOnlyToolFilterTests.cs`

**Interfaces:**
- Produces: `public static IList<Tool> ReadOnlyToolFilter.FilterTools(IEnumerable<Tool> tools, bool readOnly)` — returns only tools with `Annotations?.ReadOnlyHint == true` when `readOnly` is true; all tools otherwise.
- `Tool` and `ToolAnnotations` are the MCP protocol types (namespace `ModelContextProtocol.Protocol`). Confirm the namespace against the installed SDK if the using doesn't resolve.

- [ ] **Step 1: Write the failing test**

Create `VitallyMcp.Tests/ReadOnlyToolFilterTests.cs`:

```csharp
using FluentAssertions;
using ModelContextProtocol.Protocol;
using VitallyMcp;

namespace VitallyMcp.Tests;

public class ReadOnlyToolFilterTests
{
    private static Tool ReadTool() => new() { Name = "Get_thing", Annotations = new ToolAnnotations { ReadOnlyHint = true } };
    private static Tool DeleteTool() => new() { Name = "Delete_thing", Annotations = new ToolAnnotations { DestructiveHint = true } };
    private static Tool CreateTool() => new() { Name = "Create_thing" }; // no annotations

    [Fact]
    public void ReadOnly_KeepsOnlyReadHintedTools()
    {
        var tools = new[] { ReadTool(), DeleteTool(), CreateTool() };

        var result = ReadOnlyToolFilter.FilterTools(tools, readOnly: true);

        result.Should().ContainSingle();
        result[0].Name.Should().Be("Get_thing");
    }

    [Fact]
    public void NotReadOnly_KeepsAllTools()
    {
        var tools = new[] { ReadTool(), DeleteTool(), CreateTool() };

        var result = ReadOnlyToolFilter.FilterTools(tools, readOnly: false);

        result.Should().HaveCount(3);
    }
```

```csharp
    [Fact]
    public void ReadOnly_DropsToolsWithNullAnnotations()
    {
        var tools = new[] { CreateTool() }; // null annotations -> not read-only -> dropped

        var result = ReadOnlyToolFilter.FilterTools(tools, readOnly: true);

        result.Should().BeEmpty();
    }
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test --filter "FullyQualifiedName~ReadOnlyToolFilterTests" --nologo`
Expected: FAIL — `ReadOnlyToolFilter` not defined.

- [ ] **Step 3: Implement the helper**

Create `VitallyMcp/ReadOnlyToolFilter.cs`:

```csharp
using ModelContextProtocol.Protocol;

namespace VitallyMcp;

/// <summary>
/// Filters a tools/list result for read-only deployments: keeps only tools whose annotation marks
/// them read-only (<c>ReadOnlyHint == true</c>) — i.e. drops every create/update/delete tool — so a
/// read-only server never advertises destructive operations. A pass-through when not read-only.
/// </summary>
public static class ReadOnlyToolFilter
{
    public static IList<Tool> FilterTools(IEnumerable<Tool> tools, bool readOnly)
    {
        if (!readOnly)
        {
            return tools.ToList();
        }
        return tools.Where(t => t.Annotations?.ReadOnlyHint == true).ToList();
    }
}
```

- [ ] **Step 4: Run to verify pass**

Run: `dotnet test --filter "FullyQualifiedName~ReadOnlyToolFilterTests" --nologo`
Expected: PASS (3 tests).

- [ ] **Step 5: Commit**

```powershell
git add VitallyMcp/ReadOnlyToolFilter.cs VitallyMcp.Tests/ReadOnlyToolFilterTests.cs
git commit -m @'
feat(safety): ReadOnlyToolFilter — keep only read-only tools (SP4 Layer 2 logic)

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
'@
```

---

### Task 3: Wire the `tools/list` filter + live-verify (Layer 2 wiring)

**Files:**
- Modify: `VitallyMcp/Program.cs`

**Interfaces:**
- Consumes: `ReadOnlyToolFilter.FilterTools` (Task 2); `ToolAuthorizationOptions.SectionName` / `ReadOnly`.

This task has no clean in-process unit test (it wires the SDK host); its deliverable is verified by the live check in Step 3 (no Vitally key needed — `tools/list` and the read-only deny don't call Vitally).

- [ ] **Step 1: Wire the filter**

In `VitallyMcp/Program.cs`, replace the MCP server registration (currently lines ~90-92):

```csharp
builder.Services.AddMcpServer()
    .WithHttpTransport(options => options.Stateless = true)
    .WithToolsFromAssembly();
```

with:

```csharp
// Read the read-only flag from raw config at startup (same pattern as `noAuth` above) so the
// destructive tools can be filtered out of tools/list for read-only deployments.
var readOnlyMode = builder.Configuration.GetSection(ToolAuthorizationOptions.SectionName).GetValue<bool>("ReadOnly");

var mcpBuilder = builder.Services.AddMcpServer()
    .WithHttpTransport(options => options.Stateless = true)
    .WithToolsFromAssembly();

if (readOnlyMode)
{
    // Hide destructive tools from tools/list (enforcement is still done in ToolAuthorizer).
    mcpBuilder.WithRequestFilters(filters =>
    {
        filters.AddListToolsFilter(next => async (context, cancellationToken) =>
        {
            var result = await next(context, cancellationToken);
            result.Tools = ReadOnlyToolFilter.FilterTools(result.Tools, readOnly: true);
            return result;
        });
    });
}
```

Add any `using` the compiler requires for `WithRequestFilters`/`AddListToolsFilter` (the filter types live in the `ModelContextProtocol` packages already referenced). If the exact delegate signature or `ListToolsResult.Tools` setter differs from the above in the installed 1.3.0 API, adjust to match (the build will flag it) — the intent is: post-filter the list result through `ReadOnlyToolFilter.FilterTools`.

- [ ] **Step 2: Build**

Run: `dotnet build VitallyMcp/VitallyMcp.csproj -c Debug --nologo`
Expected: 0 errors.

- [ ] **Step 3: Live-verify (no Vitally key needed)**

Start the server in read-only NoAuth mode (writes are denied before any Vitally call, and `tools/list` needs no upstream call):

```powershell
$env:OAuth__NoAuth = "true"; $env:Authorization__ReadOnly = "true"
$env:Vitally__Region = "EU"; $env:Vitally__DevelopmentApiKey = "sk_live_placeholder"
$env:ASPNETCORE_URLS = "http://localhost:5099"
dotnet run --project VitallyMcp/VitallyMcp.csproj
```

Then in another shell — (a) `tools/list` must contain no `Create_`/`Update_`/`Delete_` tool, and (b) a write call must be denied:

```powershell
$h = @{ Accept = 'application/json, text/event-stream' }
$list = @{ jsonrpc='2.0'; id=1; method='tools/list' } | ConvertTo-Json -Compress
$r = Invoke-WebRequest -Method Post -Uri http://localhost:5099/mcp -ContentType 'application/json' -Headers $h -Body $list
"destructive tools present (want False): $($r.Content -match '(Create|Update|Delete)_')"

$call = @{ jsonrpc='2.0'; id=2; method='tools/call'; params=@{ name='Create_organization'; arguments=@{ jsonBody='{}' } } } | ConvertTo-Json -Depth 8 -Compress
$r2 = Invoke-WebRequest -Method Post -Uri http://localhost:5099/mcp -ContentType 'application/json' -Headers $h -Body $call
"write denied (want match): $($r2.Content -match 'read-only')"
```

Expected: destructive-tools-present = **False**; write-denied = **True**. Stop the server (`Get-Process -Name VitallyMcp | Stop-Process -Force`).

- [ ] **Step 4: Commit**

```powershell
git add VitallyMcp/Program.cs
git commit -m @'
feat(safety): hide destructive tools from tools/list in read-only mode (SP4 Layer 2)

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
'@
```

---

### Task 4: Docs — `CLAUDE.md` + read-only / RBAC-rollout runbook

**Files:**
- Modify: `CLAUDE.md`
- Create: `docs/runbooks/read-only-and-rbac-rollout.md`

- [ ] **Step 1: Document the flag in CLAUDE.md**

In `CLAUDE.md`, in the `ToolAuthorizationOptions` bullets, add:

```markdown
- `ReadOnly` (default `false`) — deployment-level read-only kill switch. When true, **every** mutating tool call (create/update/delete) is denied in `ToolAuthorizer` (checked before the `Enabled`/`NoAuth` gate, so it holds even with RBAC off), and the destructive tools are hidden from `tools/list` via an `AddListToolsFilter`. A blunt safety net for read-only deployments that doesn't depend on the per-user Entra-group RBAC. Denials are audited via `LogDenied`.
```

- [ ] **Step 2: Write the runbook**

Create `docs/runbooks/read-only-and-rbac-rollout.md`:

```markdown
# Read-only deployments & per-user RBAC rollout

## Deploy read-only (immediate safety net)

Set `Authorization__ReadOnly=true` on the Container App revision. Effect:
- All create/update/delete tool calls are denied (`ToolAuthorizer`, before the RBAC/NoAuth gate),
  audited via `LogDenied`.
- `tools/list` advertises only read tools (no `Create_*`/`Update_*`/`Delete_*`).
- Independent of `Authorization:Enabled` and of any Entra-group/Auth0 setup — a guaranteed lock.

Use this for CS-facing deployments until per-user RBAC (below) is rolled out and verified.

## Per-user RBAC rollout (finer-grained; out of the application repo)

The server-side RBAC backstop already exists (`ToolAuthorizer` maps HTTP verb → `vitally:read` /
`vitally:write` / `vitally:delete`). To grant tiers per user via Entra group membership:

1. **Entra:** create/confirm three security groups (Reader, Editor, Admin); collect their object ids.
2. **App config:** set `Authorization__ReaderGroupId` / `EditorGroupId` / `AdminGroupId` to those ids;
   set `Authorization__LiveGroupCheck=true` (resolves live membership via Microsoft Graph, so
   revocations take effect within the cache window). Requires the managed identity to hold Graph
   `GroupMember.Read.All`.
3. **Auth0 (alternative/auxiliary):** a post-login Action mapping Entra group membership to the
   `vitally:*` permissions, written to the namespaced `Authorization:CustomPermissionsClaim`.
4. **Verify on the live revision:** with a reader token, a write returns the RBAC denial; with an
   editor token, writes succeed but deletes are denied; with admin, all tiers succeed. Confirm
   denials appear in the audit log (`LogDenied`, by `sub`).
5. Once verified, `Authorization__ReadOnly` can be removed from editor/admin deployments while
   read-only stays the default for view-only consumers.

## Data-classification gate

Wider rollout remains gated on the pending data-classification review (customer data exposure).
Keep deployments read-only by default until that clears.
```

- [ ] **Step 3: Commit**

```powershell
git add CLAUDE.md docs/runbooks/read-only-and-rbac-rollout.md
git commit -m @'
docs: document Authorization:ReadOnly + read-only/RBAC rollout runbook

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
'@
```

---

## Final verification

- [ ] `dotnet test VitallyMcp.sln -c Debug --nologo --verbosity minimal` — all green.
- [ ] Re-confirm Task 3 Step 3 live check (read-only: destructive tools hidden + writes denied).
- [ ] Open a PR from `feature/sp4-read-only-safeguard` into `main`; CI green; resolve any Copilot/CodeQL review threads (the `main` ruleset requires thread resolution); merge (squash).
