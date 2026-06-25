# Tool-error surfacing — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Surface the real failure message (Vitally body, read-only/RBAC denial, validation) to the MCP client instead of the SDK's generic "An error occurred invoking 'X'.", via one CallTool request filter.

**Architecture:** A pure `ToolErrorResult` helper classifies which exceptions to surface and builds the `CallToolResult`; `Program.cs` registers an `AddCallToolFilter` that catches only those (via an exception filter) and returns the result, letting all other exceptions propagate to the SDK's existing protocol/cancellation/generic handling.

**Tech Stack:** .NET 10, C#, `ModelContextProtocol(.AspNetCore) 1.3.0` (`AddCallToolFilter` confirmed present in the installed assemblies), xUnit + FluentAssertions.

**Branch:** `feature/tool-error-surfacing` (created off `main`; spec committed there).

**Spec:** [tool-error-surfacing design](../specs/2026-06-25-tool-error-surfacing-design.md)

## Global Constraints

- UK English in comments/docs. Repo uses **LF** line endings (`.gitattributes` present).
- Surface `ex.Message` ONLY for `HttpRequestException`, `UnauthorizedAccessException`, `ArgumentException`. Everything else (incl. `McpException`, `OperationCanceledException`) must propagate untouched so the SDK keeps protocol/cancellation handling + the generic message.
- Do NOT change `VitallyService`/`ToolAuthorizer` to throw `McpException` (keep MCP-protocol concerns out of those layers).
- Only `ex.Message` is surfaced — never stack traces. Audit logging is unchanged.
- `CallToolResult` / `TextContentBlock` are `ModelContextProtocol.Protocol` types. If the exact `AddCallToolFilter` delegate signature or content-block shape differs in the installed 1.3.0 API, adjust to compile — the build will guide; the intent (catch surfaceable exceptions, return their message as an error result) is fixed.

---

### Task 1: `ToolErrorResult` helper

**Files:**
- Create: `VitallyMcp/ToolErrorResult.cs`
- Test: `VitallyMcp.Tests/ToolErrorResultTests.cs`

**Interfaces:**
- Produces: `static bool ToolErrorResult.IsSurfaceable(Exception ex)`; `static CallToolResult ToolErrorResult.Build(Exception ex)`.

- [ ] **Step 1: Write the failing tests**

Create `VitallyMcp.Tests/ToolErrorResultTests.cs`:

```csharp
using System.Net.Http;
using FluentAssertions;
using ModelContextProtocol.Protocol;
using VitallyMcp;

namespace VitallyMcp.Tests;

public class ToolErrorResultTests
{
    [Theory]
    [InlineData(typeof(HttpRequestException))]
    [InlineData(typeof(UnauthorizedAccessException))]
    [InlineData(typeof(ArgumentException))]
    public void IsSurfaceable_TrueForExpectedTypes(Type exType)
    {
        var ex = (Exception)Activator.CreateInstance(exType, "msg")!;
        ToolErrorResult.IsSurfaceable(ex).Should().BeTrue();
    }

    [Fact]
    public void IsSurfaceable_FalseForUnexpectedType()
    {
        ToolErrorResult.IsSurfaceable(new InvalidOperationException("x")).Should().BeFalse();
    }

    [Fact]
    public void Build_ReturnsErrorResultWithExceptionMessage()
    {
        var result = ToolErrorResult.Build(new ArgumentException("bad input"));

        result.IsError.Should().BeTrue();
        result.Content.OfType<TextContentBlock>().First().Text.Should().Be("bad input");
    }
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test --filter "FullyQualifiedName~ToolErrorResultTests" --nologo`
Expected: FAIL — `ToolErrorResult` not defined (compile error).

- [ ] **Step 3: Implement the helper**

Create `VitallyMcp/ToolErrorResult.cs`:

```csharp
using ModelContextProtocol.Protocol;

namespace VitallyMcp;

/// <summary>
/// Maps an "expected" tool exception to a <see cref="CallToolResult"/> whose text carries the real
/// message, so the MCP client (the calling LLM) sees the actual failure reason instead of the SDK's
/// generic "An error occurred invoking 'X'." Only a curated set of exception types is surfaced;
/// anything else is left to propagate so the SDK keeps its protocol/cancellation handling and the
/// generic message (we never leak unexpected internal exception detail).
/// </summary>
public static class ToolErrorResult
{
    /// <summary>
    /// The exceptions whose message we deliberately surface: Vitally API failures
    /// (<see cref="HttpRequestException"/>, body included by VitallyService.SendAsync), the
    /// read-only / RBAC denial (<see cref="UnauthorizedAccessException"/>), and our own client-side
    /// validation (<see cref="ArgumentException"/>).
    /// </summary>
    public static bool IsSurfaceable(Exception ex) =>
        ex is HttpRequestException or UnauthorizedAccessException or ArgumentException;

    public static CallToolResult Build(Exception ex) => new()
    {
        IsError = true,
        Content = [new TextContentBlock { Text = ex.Message }]
    };
}
```

(If the compiler does not already resolve `HttpRequestException`, add `using System.Net.Http;`.)

- [ ] **Step 4: Run to verify pass**

Run: `dotnet test --filter "FullyQualifiedName~ToolErrorResultTests" --nologo`
Expected: PASS (5 cases).

- [ ] **Step 5: Commit**

```powershell
git add VitallyMcp/ToolErrorResult.cs VitallyMcp.Tests/ToolErrorResultTests.cs
git commit -m @'
feat(errors): ToolErrorResult — surface curated exception messages to the client

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
'@
```

---

### Task 2: Wire the CallTool filter + end-to-end guard

**Files:**
- Modify: `VitallyMcp/Program.cs`
- Test: `VitallyMcp.Tests/ReadOnlyToolsListTests.cs`

**Interfaces:**
- Consumes: `ToolErrorResult.IsSurfaceable` / `ToolErrorResult.Build` (Task 1).

The end-to-end guard reuses the existing `ReadOnlyToolsListTests` harness (its `Factory` runs with `Authorization:ReadOnly=true`, and its private `BuildJsonRpc`/`PostMcpAsync` helpers): in read-only mode a `Create_organization` call is denied by `ToolAuthorizer` with `UnauthorizedAccessException("...read-only mode...")`; without the filter the client sees the generic message, with it the client sees the real one.

- [ ] **Step 1: Write the failing test**

Add to `VitallyMcp.Tests/ReadOnlyToolsListTests.cs` (inside the class, reusing its helpers):

```csharp
    [Fact]
    public async Task CallTool_InReadOnlyMode_SurfacesReadOnlyMessageNotGenericError()
    {
        using var client = _factory.CreateClient();

        var body = BuildJsonRpc("tools/call", id: 2, new
        {
            name = "Create_organization",
            arguments = new { jsonBody = "{}" }
        });
        var responseText = await PostMcpAsync(client, body);

        // The CallTool filter surfaces the UnauthorizedAccessException message from ToolAuthorizer,
        // instead of the SDK's generic "An error occurred invoking 'Create_organization'."
        responseText.Should().Contain("read-only mode");
        responseText.Should().NotContain("An error occurred invoking");
    }
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test --filter "FullyQualifiedName~CallTool_InReadOnlyMode_SurfacesReadOnlyMessageNotGenericError" --nologo`
Expected: FAIL — the response currently contains the generic "An error occurred invoking 'Create_organization'." and not "read-only mode".

- [ ] **Step 3: Wire the CallTool filter**

In `VitallyMcp/Program.cs`, replace the current MCP registration block (the `var readOnlyMode = …` line through the `if (readOnlyMode) { … WithRequestFilters … }` block) with:

```csharp
// Read the read-only flag from raw config at startup (same pattern as `noAuth` above).
var readOnlyMode = builder.Configuration.GetSection(ToolAuthorizationOptions.SectionName).GetValue<bool>("ReadOnly");

var mcpBuilder = builder.Services.AddMcpServer()
    .WithHttpTransport(options => options.Stateless = true)
    .WithToolsFromAssembly();

mcpBuilder.WithRequestFilters(filters =>
{
    // Surface the real failure reason (Vitally body / read-only / RBAC denial / validation) to the
    // client instead of the SDK's generic "An error occurred invoking 'X'." Unexpected exceptions
    // propagate so the SDK keeps its protocol-error / cancellation handling and generic message.
    filters.AddCallToolFilter(next => async (context, cancellationToken) =>
    {
        try
        {
            return await next(context, cancellationToken);
        }
        catch (Exception ex) when (ToolErrorResult.IsSurfaceable(ex))
        {
            return ToolErrorResult.Build(ex);
        }
    });

    // Read-only deployments: hide destructive tools from tools/list (enforcement is in ToolAuthorizer).
    if (readOnlyMode)
    {
        filters.AddListToolsFilter(next => async (context, cancellationToken) =>
        {
            var result = await next(context, cancellationToken);
            result.Tools = ReadOnlyToolFilter.FilterTools(result.Tools, readOnly: true);
            return result;
        });
    }
});
```

Add any `using` the compiler needs. If the installed 1.3.0 `AddCallToolFilter` delegate signature differs from the above (e.g. the context/result generic types), adjust to compile — the intent is fixed: catch `ToolErrorResult.IsSurfaceable` exceptions and return `ToolErrorResult.Build(ex)`, otherwise let it propagate.

- [ ] **Step 4: Run to verify pass**

Run: `dotnet test --filter "FullyQualifiedName~ReadOnlyToolsListTests" --nologo`
Expected: PASS (the new test + the existing read-only tools/list test).

- [ ] **Step 5: Full suite + commit**

Run: `dotnet test VitallyMcp.sln -c Debug --nologo --verbosity minimal` — all green.

```powershell
git add VitallyMcp/Program.cs VitallyMcp.Tests/ReadOnlyToolsListTests.cs
git commit -m @'
feat(errors): AddCallToolFilter surfaces curated tool-error messages to the client

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
'@
```

- [ ] **Step 6: Live check (optional, no Vitally key needed)**

Start read-only + NoAuth (`OAuth__NoAuth=true`, `Authorization__ReadOnly=true`, `Vitally__Region=EU`, `Vitally__DevelopmentApiKey=sk_live_placeholder`, `ASPNETCORE_URLS=http://localhost:5099`, `dotnet run --project VitallyMcp/VitallyMcp.csproj` in the background; poll `/mcp` for readiness). POST a `tools/call` for `Create_organization` and confirm the SSE response text contains "read-only mode" (not "An error occurred invoking"). Stop the server (`Get-Process -Name VitallyMcp | Stop-Process -Force`).

---

### Task 3: Docs (`CLAUDE.md`)

**Files:**
- Modify: `CLAUDE.md`

- [ ] **Step 1: Update the SendAsync error-handling note**

In `CLAUDE.md`, the sentence at line ~143 ends:

```
…and surfacing it gives the LLM something concrete to act on.
```

Append to that paragraph:

```markdown
 The MCP SDK only forwards an exception's own message to the client when it is an `McpException`, so a CallTool request filter (`ToolErrorResult` + `AddCallToolFilter` in `Program.cs`) is what actually delivers this body — and the read-only/RBAC denial and `ArgumentException` validation messages — to the client; other (unexpected) exceptions still yield the SDK's generic error.
```

- [ ] **Step 2: Update the Important Notes bullet**

In `CLAUDE.md`, the bullet at line ~302 reads:

```
- **Error handling**: `VitallyService.SendAsync` throws `HttpRequestException` with the Vitally response body included in the message on non-2xx responses, so MCP clients see the actual failure reason in JSON-RPC errors.
```

Replace it with:

```markdown
- **Error handling**: `VitallyService.SendAsync` throws `HttpRequestException` with the Vitally response body included in the message on non-2xx responses. A CallTool request filter (`ToolErrorResult` + `AddCallToolFilter`, `Program.cs`) surfaces the messages of `HttpRequestException`, `UnauthorizedAccessException` (read-only / RBAC denial) and `ArgumentException` (validation) to the client as the tool-call error text, so the LLM sees the actual failure reason rather than the SDK's generic "An error occurred invoking 'X'."; other exceptions keep the generic message.
```

- [ ] **Step 3: Commit**

```powershell
git add CLAUDE.md
git commit -m @'
docs: document the CallTool error-surfacing filter

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
'@
```

---

## Final verification

- [ ] `dotnet test VitallyMcp.sln -c Debug --nologo --verbosity minimal` — all green.
- [ ] Open a PR from `feature/tool-error-surfacing` into `main`; CI green; resolve any Copilot/CodeQL review threads (the `main` ruleset requires resolution); merge (squash).
